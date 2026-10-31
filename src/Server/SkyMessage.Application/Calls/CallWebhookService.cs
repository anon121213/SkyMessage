using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Calls;

/// <summary>LiveKit webhooks (<c>POST api/livekit/webhook</c>, anonymous, own signature).</summary>
public interface ICallWebhookService
{
    /// <summary>Verifies the request (401 when the signature is wrong) and applies the event.</summary>
    Task HandleAsync(ReadOnlyMemory<byte> body, string? authorization, CancellationToken cancellationToken = default);
}

internal sealed class CallWebhookService(
    ILiveKitWebhookVerifier verifier,
    CallLifecycle lifecycle,
    IMemoryCache cache,
    ILogger<CallWebhookService> logger) : ICallWebhookService
{
    /// <summary>protojson: camelCase names, int64 as strings, unknown fields ignored.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>LiveKit retries deliveries; events carry an id.</summary>
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(10);

    public async Task HandleAsync(ReadOnlyMemory<byte> body, string? authorization, CancellationToken cancellationToken = default)
    {
        if (!await verifier.VerifyAsync(body, authorization))
        {
            logger.LogWarning("Rejected a LiveKit webhook with a missing or invalid signature");
            throw new UnauthorizedException();
        }
        if (!TryParse(body.Span, out var webhook))
            throw new BadRequestException(ErrorCodes.Validation, "Malformed LiveKit webhook.");

        var dedupeKey = string.IsNullOrEmpty(webhook.Id) ? null : $"livekit-webhook:{webhook.Id}";
        if (dedupeKey is not null && cache.TryGetValue(dedupeKey, out _)) return;

        var room = webhook.Room?.Name;
        var participant = webhook.Participant;
        logger.LogDebug("LiveKit webhook {Event} for room {Room}", webhook.Event, room);

        switch (webhook.Event)
        {
            case "participant_joined" when room is not null && participant?.Identity is { } joined:
                await lifecycle.OnParticipantJoinedAsync(room, joined, cancellationToken);
                break;
            // A duplicate identity means the same device connected again: it did not really leave.
            case "participant_left" or "participant_connection_aborted"
                when room is not null && participant?.Identity is { } left && !participant.IsDuplicateIdentity:
                await lifecycle.OnParticipantLeftAsync(room, left, participant.Sid, cancellationToken);
                break;
            case "room_finished" when room is not null:
                await lifecycle.OnRoomFinishedAsync(room, cancellationToken);
                break;
        }

        if (dedupeKey is not null) cache.Set(dedupeKey, true, DedupeWindow);
    }

    internal static bool TryParse(ReadOnlySpan<byte> body, [NotNullWhen(true)] out LiveKitWebhookEvent? webhook)
    {
        try
        {
            webhook = JsonSerializer.Deserialize<LiveKitWebhookEvent>(body, Json);
            return webhook is not null;
        }
        catch (JsonException)
        {
            webhook = null;
            return false;
        }
    }
}

/// <summary>The parts of LiveKit's <c>WebhookEvent</c> (protojson) the calls feature uses.</summary>
internal sealed record LiveKitWebhookEvent
{
    public string? Event { get; init; }
    public string? Id { get; init; }
    public LiveKitWebhookRoom? Room { get; init; }
    public LiveKitWebhookParticipant? Participant { get; init; }
}

internal sealed record LiveKitWebhookRoom
{
    public string? Sid { get; init; }
    public string? Name { get; init; }
}

internal sealed record LiveKitWebhookParticipant
{
    public string? Sid { get; init; }
    public string? Identity { get; init; }

    /// <summary>Enum <c>DisconnectReason</c>: a name in protojson, a number with other encoders.</summary>
    public JsonElement DisconnectReason { get; init; }

    public bool IsDuplicateIdentity => DisconnectReason.ValueKind switch
    {
        JsonValueKind.String => DisconnectReason.GetString() == "DUPLICATE_IDENTITY",
        JsonValueKind.Number => DisconnectReason.TryGetInt32(out var value) && value == 2,
        _ => false,
    };
}
