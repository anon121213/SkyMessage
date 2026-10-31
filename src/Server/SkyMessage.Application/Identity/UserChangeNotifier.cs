using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

/// <summary>Publishes <c>UserChanged</c> to everyone who displays a user plus the user's own devices.</summary>
public sealed class UserChangeNotifier(
    IAppDbContext db,
    IRealtimeNotifier notifier,
    IPresenceService presence,
    ILogger<UserChangeNotifier> logger)
{
    /// <summary>Call after the change was saved. Never throws (the change itself already succeeded).</summary>
    /// <param name="recipients">Precomputed recipients (e.g. captured before memberships were removed); null = look them up.</param>
    public async Task NotifyAsync(Guid userId, IEnumerable<Guid>? recipients = null)
    {
        try
        {
            var targets = recipients?.ToHashSet() ?? await RelatedUsers.GetAsync(db, userId, CancellationToken.None);
            targets.Add(userId);
            var connected = targets.Where(presence.IsConnected).ToList();
            if (connected.Count > 0)
                await notifier.Users(connected).UserChanged(new UserRefEvent { UserId = userId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish UserChanged for {UserId}", userId);
        }
    }
}
