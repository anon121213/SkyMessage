using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkyMessage.Application.Calls;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;

namespace SkyMessage.Infrastructure.Calls;

/// <summary>
/// LiveKit access tokens: HS256 JWTs signed with the API secret, <c>iss</c> = API key, grants in the
/// <c>video</c> object (same claim layout as LiveKit's server SDKs).
/// </summary>
internal sealed class LiveKitTokenService(IOptions<LiveKitOptions> options, IClock clock, ILogger<LiveKitTokenService> logger)
    : ILiveKitTokenService
{
    /// <summary>HS256 in Microsoft.IdentityModel needs a key of at least 256 bits (LiveKit recommends ≥ 32 chars too).</summary>
    internal const int MinSecretBytes = 32;

    private static readonly TimeSpan ServerTokenLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinJoinTokenLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxJoinTokenLifetime = TimeSpan.FromHours(24);

    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };
    private int _misconfigurationLogged;

    public bool IsConfigured
    {
        get
        {
            var o = options.Value;
            if (!o.Enabled || string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrEmpty(o.ApiSecret)) return false;
            if (Encoding.UTF8.GetByteCount(o.ApiSecret) >= MinSecretBytes) return true;

            if (Interlocked.Exchange(ref _misconfigurationLogged, 1) == 0)
                logger.LogError("LiveKit:ApiSecret is shorter than {MinBytes} bytes; calls are disabled until a longer secret is configured", MinSecretBytes);
            return false;
        }
    }

    public string CreateJoinToken(LiveKitJoinGrant grant)
    {
        var lifetime = TimeSpan.FromMinutes(options.Value.TokenTtlMinutes);
        if (lifetime < MinJoinTokenLifetime) lifetime = MinJoinTokenLifetime;
        if (lifetime > MaxJoinTokenLifetime) lifetime = MaxJoinTokenLifetime;

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = LiveKitNames.Identity(grant.UserId, grant.SessionId),
            ["name"] = grant.DisplayName,
            ["video"] = new Dictionary<string, object>
            {
                ["room"] = grant.RoomName,
                ["roomJoin"] = true,
                ["canPublish"] = true,
                ["canSubscribe"] = true,
                ["canPublishData"] = true,
            },
            // Identities are per device; clients group devices into people by this userId.
            ["metadata"] = JsonSerializer.Serialize(new ParticipantMetadata(grant.UserId, grant.AvatarMediaId), SkyJson.Options),
        };
        return Create(claims, lifetime);
    }

    /// <summary>Token for server-to-LiveKit Twirp calls, e.g. <c>{ "roomList": true }</c> or <c>{ "roomAdmin": true, "room": "…" }</c>.</summary>
    internal string CreateServerToken(IReadOnlyDictionary<string, object> videoGrant) =>
        Create(new Dictionary<string, object> { ["video"] = new Dictionary<string, object>(videoGrant) }, ServerTokenLifetime);

    private string Create(Dictionary<string, object> claims, TimeSpan lifetime)
    {
        var o = options.Value;
        var now = clock.UtcNow.UtcDateTime;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.ApiKey,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.ApiSecret)), SecurityAlgorithms.HmacSha256),
        });
    }

    private sealed record ParticipantMetadata(Guid UserId, Guid? AvatarMediaId);
}
