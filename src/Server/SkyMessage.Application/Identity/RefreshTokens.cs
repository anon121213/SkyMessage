using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace SkyMessage.Application.Identity;

/// <summary>Opaque refresh tokens: 32 random bytes (base64url). Only their SHA-256 (lowercase hex) is stored.</summary>
public static class RefreshTokens
{
    public const int ByteLength = 32;
    /// <summary>Upper bound for a plausible token (generated ones are 43 chars).</summary>
    public const int MaxLength = 128;

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Length of <see cref="Binding"/>: enough to tell rotations apart, useless for guessing the token.</summary>
    public const int BindingLength = 16;

    /// <summary>What access tokens carry (claim <c>rtb</c>) to show which refresh token they were issued with.</summary>
    public static string Binding(string tokenHash) => tokenHash[..BindingLength];

    public static bool IsBinding(string? value) =>
        value is { Length: BindingLength } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// The token that rotating <paramref name="token"/> hands out. Deterministic (HMAC under a server key), so a
    /// client that lost the response and presents <paramref name="token"/> again gets the same successor: retries
    /// are idempotent and never strand a successor that did arrive after all.
    /// </summary>
    public static string Successor(ReadOnlySpan<byte> key, Guid sessionId, string token)
    {
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{sessionId:N}:{token}"), mac);
        return Base64Url.EncodeToString(mac);
    }

    /// <summary>Key for <see cref="Successor"/>, derived from the JWT signing key (domain-separated).</summary>
    public static byte[] SuccessorKey(string signingKey) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), "skymessage/refresh-successor/v1"u8);
}

public enum RefreshDecision
{
    /// <summary>Unknown, expired or revoked: 401 refresh_token_invalid.</summary>
    Invalid = 0,
    /// <summary>The current token: rotate.</summary>
    Rotate = 1,
    /// <summary>The previous token while its successor is unconfirmed, or shortly after rotation: hand out the successor again.</summary>
    Retry = 2,
    /// <summary>The previous token after the client confirmed its successor and the grace window passed: a replay, revoke the session.</summary>
    Theft = 3,
}

/// <summary>What the server knows about a session's refresh tokens.</summary>
public readonly record struct RefreshTokenState(
    string CurrentHash,
    string? PreviousHash,
    DateTimeOffset? RotatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? ConfirmedAt = null);

/// <summary>Refresh token rotation with a lost-response grace window and reuse (theft) detection.</summary>
public static class RefreshTokenPolicy
{
    public static readonly TimeSpan LostResponseGrace = TimeSpan.FromMinutes(2);

    public static RefreshDecision Decide(string presentedHash, RefreshTokenState state, DateTimeOffset now)
    {
        if (state.RevokedAt is not null || state.ExpiresAt <= now)
            return RefreshDecision.Invalid;

        if (string.Equals(presentedHash, state.CurrentHash, StringComparison.Ordinal))
            return RefreshDecision.Rotate;

        if (state.PreviousHash is not null && string.Equals(presentedHash, state.PreviousHash, StringComparison.Ordinal))
        {
            // The current token is the previous one's successor and was never presented (that would have rotated it
            // away). Unconfirmed: no access token issued with it was ever used, so the client never got that response
            // (a flaky mobile link, the app killed mid-request) — a retry, however late. Confirmed: the client did get
            // it, and only a race right after the rotation explains the old token; anything later is a replay.
            if (state.ConfirmedAt is null)
                return RefreshDecision.Retry;
            return state.RotatedAt is { } rotatedAt && now - rotatedAt <= LostResponseGrace
                ? RefreshDecision.Retry
                : RefreshDecision.Theft;
        }

        return RefreshDecision.Invalid;
    }
}
