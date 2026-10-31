using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkyMessage.Application.Calls;

namespace SkyMessage.Infrastructure.Calls;

/// <summary>
/// LiveKit RoomService over Twirp (JSON): <c>POST {InternalUrl}/twirp/livekit.RoomService/{Method}</c> with a
/// short-lived server token. LiveKit answers with protobuf field names (snake_case); errors are
/// <c>{"code": "not_found", "msg": "…"}</c>.
/// </summary>
internal sealed class LiveKitRoomService(IHttpClientFactory httpClients, LiveKitTokenService tokens) : ILiveKitRoomService
{
    public const string HttpClientName = "livekit";
    private const string ServicePath = "twirp/livekit.RoomService/";

    public async Task<IReadOnlyList<LiveKitRoomInfo>> ListRoomsAsync(IReadOnlyCollection<string>? names, CancellationToken cancellationToken = default)
    {
        var request = new Dictionary<string, object>();
        if (names is { Count: > 0 }) request["names"] = names;

        using var response = await CallAsync("ListRooms", request, new Dictionary<string, object> { ["roomList"] = true }, cancellationToken);
        var rooms = new List<LiveKitRoomInfo>();
        if (response is null || !response.RootElement.TryGetProperty("rooms", out var array) || array.ValueKind != JsonValueKind.Array)
            return rooms;

        foreach (var room in array.EnumerateArray())
        {
            var name = GetString(room, "name");
            if (string.IsNullOrEmpty(name)) continue;
            rooms.Add(new LiveKitRoomInfo(name, GetString(room, "sid"), GetInt(room, "num_participants", "numParticipants")));
        }
        return rooms;
    }

    public async Task<IReadOnlyList<LiveKitParticipantInfo>> ListParticipantsAsync(string roomName, CancellationToken cancellationToken = default)
    {
        using var response = await CallAsync("ListParticipants", new Dictionary<string, object> { ["room"] = roomName }, RoomAdmin(roomName), cancellationToken);
        var participants = new List<LiveKitParticipantInfo>();
        if (response is null || !response.RootElement.TryGetProperty("participants", out var array) || array.ValueKind != JsonValueKind.Array)
            return participants;

        foreach (var participant in array.EnumerateArray())
        {
            var identity = GetString(participant, "identity");
            if (string.IsNullOrEmpty(identity)) continue;
            participants.Add(new LiveKitParticipantInfo(identity, GetString(participant, "sid"), GetString(participant, "state")));
        }
        return participants;
    }

    public async Task<bool> DeleteRoomAsync(string roomName, CancellationToken cancellationToken = default)
    {
        using var response = await CallAsync("DeleteRoom", new Dictionary<string, object> { ["room"] = roomName },
            new Dictionary<string, object> { ["roomCreate"] = true }, cancellationToken);
        return response is not null;
    }

    public async Task<bool> RemoveParticipantAsync(string roomName, string identity, CancellationToken cancellationToken = default)
    {
        using var response = await CallAsync("RemoveParticipant", new Dictionary<string, object> { ["room"] = roomName, ["identity"] = identity },
            RoomAdmin(roomName), cancellationToken);
        return response is not null;
    }

    private static Dictionary<string, object> RoomAdmin(string roomName) => new() { ["roomAdmin"] = true, ["room"] = roomName };

    /// <summary>The parsed response; null for Twirp <c>not_found</c>.</summary>
    private async Task<JsonDocument?> CallAsync(
        string method,
        Dictionary<string, object> body,
        IReadOnlyDictionary<string, object> grant,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClients.CreateClient(HttpClientName);
            if (client.BaseAddress is null)
                throw new LiveKitUnavailableException("LiveKit:InternalUrl is not an absolute http(s) URL.");

            using var request = new HttpRequestMessage(HttpMethod.Post, ServicePath + method)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CreateServerToken(grant));

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }

            var code = await ReadErrorCodeAsync(response, cancellationToken);
            if (code == "not_found") return null;
            throw new LiveKitUnavailableException($"LiveKit {method} answered {(int)response.StatusCode} ({code ?? "no error code"}).");
        }
        catch (LiveKitUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network errors, timeouts, unusable keys, unexpected payloads: the room state is unknown.
            throw new LiveKitUnavailableException($"LiveKit {method} failed: {ex.Message}", ex);
        }
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var error = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return GetString(error.RootElement, "code");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        }
        return 0;
    }
}
