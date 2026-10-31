using System.Collections.Concurrent;
using System.Threading.Channels;
using SkyMessage.Application.Common.Interfaces;

namespace SkyMessage.Infrastructure.Identity;

/// <summary>An online/offline change to persist and broadcast (see <see cref="PresenceWorker"/>).</summary>
public readonly record struct PresenceTransition(Guid UserId, bool IsOnline, DateTimeOffset At);

/// <summary>
/// In-memory presence fed by the realtime hub (single API instance). Online = at least one active
/// connection; connections start active and the app reports background/foreground via SetActive. Going
/// offline is debounced by <see cref="OfflineDelay"/> so reconnects and quick app switches do not flap.
/// Transitions are queued in order per user and consumed by <see cref="PresenceWorker"/>.
/// </summary>
public sealed class PresenceTracker(IClock clock) : IPresenceService
{
    public static readonly TimeSpan OfflineDelay = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, UserPresence> _users = new();
    private readonly Channel<PresenceTransition> _transitions =
        Channel.CreateUnbounded<PresenceTransition>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<PresenceTransition> Transitions => _transitions.Reader;

    /// <summary>True while any connection is active, and for <see cref="OfflineDelay"/> after the last one stopped.</summary>
    public bool IsOnline(Guid userId)
    {
        if (!_users.TryGetValue(userId, out var user)) return false;
        lock (user) return user.IsOnline;
    }

    public bool IsConnected(Guid userId)
    {
        if (!_users.TryGetValue(userId, out var user)) return false;
        lock (user) return user.Connections.Count > 0;
    }

    public void Connected(Guid userId, string connectionId) =>
        Update(userId, user => user.Connections[connectionId] = true);

    public void Disconnected(Guid userId, string connectionId) =>
        Update(userId, user => user.Connections.Remove(connectionId));

    public void SetActive(Guid userId, string connectionId, bool isActive) =>
        Update(userId, user =>
        {
            if (user.Connections.ContainsKey(connectionId))
                user.Connections[connectionId] = isActive;
        });

    /// <summary>Completes debounced offline transitions that are due. Called every second by the worker.</summary>
    public void SweepExpired()
    {
        var now = clock.UtcNow;
        foreach (var (userId, user) in _users)
        {
            bool due;
            lock (user) due = user.OfflineDeadline is { } deadline && deadline <= now;
            if (due) Update(userId, static _ => { });
        }
    }

    public IReadOnlyList<Guid> OnlineUserIds()
    {
        var result = new List<Guid>();
        foreach (var (userId, user) in _users)
        {
            lock (user)
            {
                if (user.IsOnline) result.Add(userId);
            }
        }
        return result;
    }

    private void Update(Guid userId, Action<UserPresence> mutate)
    {
        while (true)
        {
            var user = _users.GetOrAdd(userId, static _ => new UserPresence());
            lock (user)
            {
                // Lost a race with the cleanup below: retry on a fresh entry.
                if (user.Removed) continue;

                mutate(user);
                Recompute(userId, user, clock.UtcNow);
                if (user.Connections.Count == 0 && !user.IsOnline)
                {
                    user.Removed = true;
                    _users.TryRemove(new KeyValuePair<Guid, UserPresence>(userId, user));
                }
                return;
            }
        }
    }

    // Runs under the user's lock, so transitions of one user are queued in order.
    private void Recompute(Guid userId, UserPresence user, DateTimeOffset now)
    {
        if (user.Connections.Values.Any(active => active))
        {
            user.OfflineDeadline = null;
            if (!user.IsOnline)
            {
                user.IsOnline = true;
                _transitions.Writer.TryWrite(new PresenceTransition(userId, true, now));
            }
            return;
        }

        if (!user.IsOnline) return;
        if (user.OfflineDeadline is null)
        {
            user.OfflineDeadline = now + OfflineDelay;
            user.InactiveSince = now;
        }
        else if (user.OfflineDeadline <= now)
        {
            user.IsOnline = false;
            user.OfflineDeadline = null;
            _transitions.Writer.TryWrite(new PresenceTransition(userId, false, user.InactiveSince ?? now));
        }
    }

    private sealed class UserPresence
    {
        /// <summary>Connection id → app in foreground.</summary>
        public readonly Dictionary<string, bool> Connections = new(StringComparer.Ordinal);
        public bool IsOnline;
        public DateTimeOffset? OfflineDeadline;
        /// <summary>When the last active connection stopped: the user's last-seen time if they go offline.</summary>
        public DateTimeOffset? InactiveSince;
        public bool Removed;
    }
}
