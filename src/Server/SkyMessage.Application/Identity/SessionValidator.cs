using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

/// <summary>What the validator needs to know about a session.</summary>
internal sealed record SessionSnapshot(Guid UserId, bool IsActive, byte[] MediaKey);

internal interface ISessionSnapshotStore
{
    /// <returns>Null when no such session exists.</returns>
    Task<SessionSnapshot?> FindAsync(Guid sessionId, CancellationToken ct);
    /// <summary>Stamps the session's refresh confirmation if its current refresh token hash starts with <paramref name="binding"/>.</summary>
    Task ConfirmRefreshAsync(Guid sessionId, string binding, CancellationToken ct);
}

internal sealed class DbSessionSnapshotStore(IAppDbContext db, IClock clock) : ISessionSnapshotStore
{
    public async Task<SessionSnapshot?> FindAsync(Guid sessionId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var row = await db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.UserId, s.RevokedAt, s.RefreshTokenExpiresAt, s.MediaKey })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : new SessionSnapshot(row.UserId, row.RevokedAt is null && row.RefreshTokenExpiresAt > now, row.MediaKey);
    }

    public Task ConfirmRefreshAsync(Guid sessionId, string binding, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return db.Sessions
            .Where(s => s.Id == sessionId && s.RefreshTokenConfirmedAt == null && s.RefreshTokenHash.StartsWith(binding))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefreshTokenConfirmedAt, now), ct);
    }
}

/// <summary>
/// Session checks for every authenticated request and every signed media URL. Results are cached briefly
/// (<see cref="ActiveTtl"/>, <see cref="MediaKeyTtl"/>); <see cref="Evict"/> runs on revocation.
/// </summary>
internal sealed class SessionValidator(ISessionSnapshotStore store, IMemoryCache cache) : ISessionValidator
{
    public static readonly TimeSpan ActiveTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MediaKeyTtl = TimeSpan.FromMinutes(5);
    /// <summary>Longer than any access token lives, so each binding reaches the database once.</summary>
    public static readonly TimeSpan ConfirmTtl = TimeSpan.FromHours(2);

    public async Task<bool> IsActiveAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        var key = IdentityCacheKeys.SessionActive(sessionId);
        if (!cache.TryGetValue(key, out ActiveEntry? entry) || entry is null)
        {
            var snapshot = await store.FindAsync(sessionId, cancellationToken);
            entry = new ActiveEntry(snapshot?.UserId ?? Guid.Empty, snapshot?.IsActive == true);
            cache.Set(key, entry, IdentityCacheKeys.Absolute(ActiveTtl));
        }
        return entry.IsActive && entry.UserId == userId;
    }

    public async Task<(Guid UserId, Guid SessionId)?> ValidateMediaSignatureAsync(string token, Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (!MediaSignature.TryParse(token, out var sessionId, out var signature))
            return null;

        var key = IdentityCacheKeys.SessionMediaKey(sessionId);
        if (!cache.TryGetValue(key, out MediaKeyEntry? entry) || entry is null)
        {
            var snapshot = await store.FindAsync(sessionId, cancellationToken);
            // Unknown session ids are not cached: random tokens must not grow the cache.
            if (snapshot is null) return null;
            entry = snapshot.IsActive && snapshot.MediaKey.Length == MediaSignature.KeyLength
                ? new MediaKeyEntry(snapshot.UserId, snapshot.MediaKey)
                : MediaKeyEntry.Inactive;
            cache.Set(key, entry, IdentityCacheKeys.Absolute(MediaKeyTtl));
        }

        if (entry.Key is null) return null;
        return MediaSignature.Verify(entry.Key, mediaId, signature) ? (entry.UserId, sessionId) : null;
    }

    public async Task ConfirmRefreshAsync(Guid sessionId, string binding, CancellationToken cancellationToken = default)
    {
        if (!RefreshTokens.IsBinding(binding)) return;
        var key = IdentityCacheKeys.RefreshConfirmed(sessionId, binding);
        if (cache.TryGetValue(key, out _)) return;
        await store.ConfirmRefreshAsync(sessionId, binding, cancellationToken);
        cache.Set(key, true, IdentityCacheKeys.Absolute(ConfirmTtl));
    }

    public static void Evict(IMemoryCache cache, Guid sessionId)
    {
        cache.Remove(IdentityCacheKeys.SessionActive(sessionId));
        cache.Remove(IdentityCacheKeys.SessionMediaKey(sessionId));
    }

    private sealed record ActiveEntry(Guid UserId, bool IsActive);

    private sealed record MediaKeyEntry(Guid UserId, byte[]? Key)
    {
        public static readonly MediaKeyEntry Inactive = new(Guid.Empty, null);
    }
}
