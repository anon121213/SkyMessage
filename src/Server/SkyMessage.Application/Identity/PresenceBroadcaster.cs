using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Persists online/offline transitions (<c>LastSeenAt</c>) and sends <c>PresenceChanged</c> to related,
/// connected users with each viewer's own view. Viewers the user blocked get nothing. Viewers who may not see
/// the last-seen time only get an update when their approximate bucket changes, so the event itself never
/// reveals the moment someone came online.
/// </summary>
public sealed class PresenceBroadcaster(IAppDbContext db, IRealtimeNotifier notifier, IPresenceService presence, IClock clock)
{
    /// <param name="at">Online: when the user came online. Offline: when the last active connection ended.</param>
    public async Task PublishTransitionAsync(Guid userId, bool isOnline, DateTimeOffset at, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsDeleted, u.LastSeenAt, Level = u.Privacy.LastSeen })
            .FirstOrDefaultAsync(ct);
        if (user is null || user.IsDeleted) return;

        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, at), ct);

        var viewers = (await RelatedUsers.GetAsync(db, userId, ct)).Where(presence.IsConnected).ToList();
        if (viewers.Count == 0) return;

        var blockedByUser = (await db.Blocks.AsNoTracking()
                .Where(b => b.BlockerId == userId && viewers.Contains(b.BlockedId))
                .Select(b => b.BlockedId)
                .ToListAsync(ct))
            .ToHashSet();
        var userContacts = user.Level == PrivacyLevel.Contacts
            ? (await db.Contacts.AsNoTracking()
                    .Where(c => c.OwnerId == userId && viewers.Contains(c.ContactUserId))
                    .Select(c => c.ContactUserId)
                    .ToListAsync(ct))
                .ToHashSet()
            : [];

        var visibleTo = new List<Guid>();
        var hiddenFrom = new List<Guid>();
        foreach (var viewer in viewers)
        {
            if (blockedByUser.Contains(viewer)) continue;
            var facts = new PresenceFacts(user.Level, IsSelf: false, IsDeleted: false, OwnerBlockedViewer: false,
                OwnerHasViewerInContacts: userContacts.Contains(viewer), isOnline, at);
            (PresenceCalculator.IsVisible(facts) ? visibleTo : hiddenFrom).Add(viewer);
        }

        if (visibleTo.Count > 0)
        {
            await notifier.Users(visibleTo).PresenceChanged(new PresenceChangedEvent
            {
                UserId = userId,
                Presence = PresenceCalculator.Visible(isOnline, at),
            });
        }

        if (hiddenFrom.Count > 0)
        {
            var now = clock.UtcNow;
            // Before this transition the user was offline since LastSeenAt (going online) or online (going offline).
            var before = PresenceCalculator.Approximate(isOnline ? user.LastSeenAt : now, now);
            var after = PresenceCalculator.Approximate(isOnline ? now : at, now);
            if (before != after)
            {
                await notifier.Users(hiddenFrom).PresenceChanged(new PresenceChangedEvent
                {
                    UserId = userId,
                    Presence = PresenceCalculator.Hidden(isOnline ? now : at, now),
                });
            }
        }
    }

    /// <summary>On shutdown: users still online were last seen now.</summary>
    public async Task PersistLastSeenAsync(IReadOnlyCollection<Guid> userIds, DateTimeOffset at, CancellationToken ct)
    {
        if (userIds.Count == 0) return;
        var ids = userIds.ToList();
        await db.Users.Where(u => ids.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, at), ct);
    }
}
