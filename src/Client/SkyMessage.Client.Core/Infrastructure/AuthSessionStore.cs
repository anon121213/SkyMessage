using System.Buffers.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>The persisted session ("auth.v1" in <see cref="ISecureStore"/>). Never logged.</summary>
internal sealed record StoredSession
{
    public Guid UserId { get; init; }
    public Guid SessionId { get; init; }
    public string AccessToken { get; init; } = "";
    public DateTimeOffset AccessTokenExpiresAt { get; init; }
    /// <summary>
    /// Expiry on this device's clock (receipt time + token lifetime), so a wrong device clock neither causes a
    /// refresh storm nor lets expired tokens through.
    /// </summary>
    public DateTimeOffset LocalAccessExpiresAt { get; init; }
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset RefreshTokenExpiresAt { get; init; }
    public string MediaKey { get; init; } = "";
    /// <summary>Last known profile so the UI can render offline.</summary>
    public MeDto? Me { get; init; }

    public static StoredSession From(AuthResponse response, DateTimeOffset localNow, MeDto? me = null) => new()
    {
        UserId = response.User.Id != Guid.Empty ? response.User.Id : me?.Id ?? Guid.Empty,
        SessionId = response.SessionId,
        AccessToken = response.AccessToken,
        AccessTokenExpiresAt = response.AccessTokenExpiresAt,
        LocalAccessExpiresAt = localNow + AccessTokenLifetime(response.AccessToken, response.AccessTokenExpiresAt, localNow),
        RefreshToken = response.RefreshToken,
        RefreshTokenExpiresAt = response.RefreshTokenExpiresAt,
        MediaKey = response.MediaKey,
        Me = response.User.Id != Guid.Empty ? response.User : me,
    };

    /// <summary>exp − iat (or nbf) from the JWT payload; falls back to the server-stated expiry.</summary>
    internal static TimeSpan AccessTokenLifetime(string accessToken, DateTimeOffset expiresAt, DateTimeOffset localNow)
    {
        var fallback = expiresAt - localNow;
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2) return Clamp(fallback);
            using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var root = doc.RootElement;
            if (!root.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expSeconds)) return Clamp(fallback);
            long issued;
            if (root.TryGetProperty("iat", out var iat) && iat.TryGetInt64(out var iatSeconds)) issued = iatSeconds;
            else if (root.TryGetProperty("nbf", out var nbf) && nbf.TryGetInt64(out var nbfSeconds)) issued = nbfSeconds;
            else return Clamp(fallback);
            return Clamp(TimeSpan.FromSeconds(expSeconds - issued));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return Clamp(fallback);
        }
    }

    private static TimeSpan Clamp(TimeSpan lifetime) =>
        lifetime < TimeSpan.Zero ? TimeSpan.Zero : lifetime > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : lifetime;
}

/// <summary>
/// Holds the current session in memory and in secure storage. Shared by the auth service (owner), the
/// endpoint service (session id + media key for server proofs) and anything that must know whether
/// tokens exist without depending on the auth service itself.
/// </summary>
internal sealed class AuthSessionStore(ISecureStore secureStore, ILogger<AuthSessionStore> logger)
{
    public const string Key = "auth.v1";

    private volatile StoredSession? _current;

    public StoredSession? Current => _current;

    public event Action? Changed;

    /// <summary>Reads the persisted session without touching <see cref="Current"/> (another tab/process may have changed it).</summary>
    public async Task<StoredSession?> ReadAsync() => (await TryReadAsync().ConfigureAwait(false)).Session;

    /// <summary>
    /// Like <see cref="ReadAsync"/>, but tells "nothing stored" (Ok, null) apart from "the store failed" (not Ok):
    /// a keychain hiccup must not sign the user out.
    /// </summary>
    public async Task<(bool Ok, StoredSession? Session)> TryReadAsync()
    {
        string? json;
        try
        {
            json = await secureStore.GetAsync(Key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The secure store could not be read");
            return (false, null);
        }

        if (string.IsNullOrEmpty(json)) return (true, null);
        try
        {
            var session = JsonSerializer.Deserialize<StoredSession>(json, SkyJson.Options);
            return (true, session is { SessionId: var sid, UserId: var uid, RefreshToken.Length: > 0 } && sid != Guid.Empty && uid != Guid.Empty
                ? session
                : null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The stored session is unreadable; treating it as signed out");
            return (true, null);
        }
    }

    public async Task<StoredSession?> LoadAsync()
    {
        var session = await ReadAsync().ConfigureAwait(false);
        Set(session);
        return session;
    }

    public async Task SaveAsync(StoredSession session)
    {
        Set(session);
        try
        {
            await secureStore.SetAsync(Key, JsonSerializer.Serialize(session, SkyJson.Options)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Keep working in memory; the user will have to sign in again after a restart.
            logger.LogError(ex, "Could not persist the session");
        }
    }

    /// <summary>
    /// Drops the session. The stored copy is removed only when it is the same session (another tab may have
    /// signed in to a different account meanwhile; its session must survive).
    /// </summary>
    public async Task ClearAsync(Guid? onlyIfSessionId = null)
    {
        Set(null);
        try
        {
            if (onlyIfSessionId is { } sid)
            {
                var stored = await ReadAsync().ConfigureAwait(false);
                if (stored is not null && stored.SessionId != sid) return;
            }
            await secureStore.RemoveAsync(Key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove the stored session");
        }
    }

    /// <summary>Swaps the in-memory session only.</summary>
    public void Set(StoredSession? session)
    {
        var previous = _current;
        _current = session;
        if (!ReferenceEquals(previous, session))
            EventRaiser.Raise(Changed, logger);
    }
}
