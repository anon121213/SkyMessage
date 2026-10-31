using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Ends sessions: <c>RevokedAt</c> + push registration dropped, validator caches evicted, <c>SessionRevoked</c>
/// sent to the session's connections, then those connections are aborted.
/// </summary>
public sealed class SessionRevoker(
    IAppDbContext db,
    IMemoryCache cache,
    IRealtimeNotifier notifier,
    ISessionConnections connections,
    IClock clock,
    ILogger<SessionRevoker> logger)
{
    /// <summary>
    /// Marks the matching active sessions revoked. Joins an ambient transaction if there is one; call
    /// <see cref="NotifyRevokedAsync"/> after it committed.
    /// </summary>
    public async Task<List<Guid>> MarkRevokedAsync(Expression<Func<Session, bool>> predicate, CancellationToken ct)
    {
        var ids = await db.Sessions.Where(predicate).Where(s => s.RevokedAt == null).Select(s => s.Id).ToListAsync(ct);
        if (ids.Count == 0) return ids;

        var now = clock.UtcNow;
        await db.Sessions
            .Where(s => ids.Contains(s.Id) && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.PushProvider, PushProvider.None)
                .SetProperty(x => x.PushToken, (string?)null)
                .SetProperty(x => x.PushEncryptionKey, (byte[]?)null), ct);

        foreach (var id in ids)
            SessionValidator.Evict(cache, id);
        return ids;
    }

    /// <summary>Post-commit half of a revocation. Never throws.</summary>
    public async Task NotifyRevokedAsync(IReadOnlyCollection<Guid> sessionIds)
    {
        foreach (var sessionId in sessionIds)
        {
            // Again after commit: a request racing the transaction may have re-cached "active".
            SessionValidator.Evict(cache, sessionId);
            try
            {
                await notifier.Session(sessionId).SessionRevoked(new SessionRevokedEvent { SessionId = sessionId });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send SessionRevoked to session {SessionId}", sessionId);
            }
            connections.AbortSession(sessionId);
        }
    }

    public async Task<List<Guid>> RevokeAsync(Expression<Func<Session, bool>> predicate, CancellationToken ct)
    {
        var ids = await MarkRevokedAsync(predicate, ct);
        if (ids.Count > 0)
        {
            await NotifyRevokedAsync(ids);
            logger.LogInformation("Revoked sessions {SessionIds}", ids);
        }
        return ids;
    }
}
