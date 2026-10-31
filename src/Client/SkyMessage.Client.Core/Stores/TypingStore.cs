using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// Who is typing/recording where. Remote entries expire after <see cref="Limits.TypingTimeoutSeconds"/> (a sweep
/// timer runs only while something is shown), end when the user's message arrives and are dropped on resync.
/// Local activity is throttled to one notification per action per 3 s; Cancel goes out at once.
/// </summary>
internal sealed class TypingStore : ITypingStore, IAccountScoped
{
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(Limits.TypingTimeoutSeconds);
    private static readonly TimeSpan LocalThrottle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(1);

    private readonly IRealtimeService _realtime;
    private readonly IAuthService _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<TypingStore> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Dictionary<Guid, (TypingEvent Event, long At)>> _remote = [];
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<TypingEvent>> _snapshots = new();
    private readonly Dictionary<(Guid ChatId, TypingAction Action), long> _lastSent = [];
    private readonly Dictionary<Guid, long> _lastActive = [];
    private ITimer? _sweeper;

    public TypingStore(IRealtimeService realtime, IAuthService auth, AccountLifecycle lifecycle, TimeProvider time, ILogger<TypingStore> logger)
    {
        _realtime = realtime;
        _auth = auth;
        _time = time;
        _logger = logger;
        realtime.Typing += OnTyping;
        realtime.MessageCreated += OnMessageCreated;
        realtime.Resynced += ClearRemote;
        lifecycle.Register(this);
    }

    public event Action<Guid>? Changed;

    public IReadOnlyList<TypingEvent> Get(Guid chatId) => _snapshots.TryGetValue(chatId, out var list) ? list : [];

    public void NotifyLocalActivity(Guid chatId, TypingAction action)
    {
        var now = _time.GetTimestamp();
        lock (_lock)
        {
            if (action == TypingAction.Cancel)
            {
                // Only worth a message when the others still see an indicator from us.
                if (!_lastActive.Remove(chatId, out var since) || _time.GetElapsedTime(since) > Expiry) return;
                foreach (var key in _lastSent.Keys.Where(k => k.ChatId == chatId).ToList()) _lastSent.Remove(key);
            }
            else
            {
                if (_lastSent.TryGetValue((chatId, action), out var last) && _time.GetElapsedTime(last) < LocalThrottle) return;
                _lastSent[(chatId, action)] = now;
                _lastActive[chatId] = now;
            }
        }
        _realtime.SendTypingAsync(chatId, action).Forget(_logger);
    }

    Task IAccountScoped.OnSignedInAsync(Guid userId) => Task.CompletedTask;

    Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        lock (_lock)
        {
            _lastSent.Clear();
            _lastActive.Clear();
        }
        ClearRemote();
        return Task.CompletedTask;
    }

    private void OnTyping(TypingEvent e)
    {
        if (_auth.UserId is { } me && e.UserId == me) return;
        lock (_lock)
        {
            if (e.Action == TypingAction.Cancel)
            {
                if (!_remote.TryGetValue(e.ChatId, out var users) || !users.Remove(e.UserId)) return;
            }
            else
            {
                if (!_remote.TryGetValue(e.ChatId, out var users)) _remote[e.ChatId] = users = [];
                users[e.UserId] = (e, _time.GetTimestamp());
                EnsureSweeper();
            }
            Rebuild(e.ChatId);
        }
        EventRaiser.Raise(Changed, e.ChatId, _logger);
    }

    private void OnMessageCreated(MessageDto message)
    {
        if (message.SenderId is not { } sender) return;
        lock (_lock)
        {
            if (!_remote.TryGetValue(message.ChatId, out var users) || !users.Remove(sender)) return;
            Rebuild(message.ChatId);
        }
        EventRaiser.Raise(Changed, message.ChatId, _logger);
    }

    private void ClearRemote()
    {
        List<Guid> chats;
        lock (_lock)
        {
            chats = [.. _remote.Keys];
            _remote.Clear();
            _snapshots.Clear();
            StopSweeper();
        }
        foreach (var chatId in chats) EventRaiser.Raise(Changed, chatId, _logger);
    }

    private void Sweep()
    {
        List<Guid> changed = [];
        lock (_lock)
        {
            foreach (var (chatId, users) in _remote.ToList())
            {
                var expired = users.Where(u => _time.GetElapsedTime(u.Value.At) >= Expiry).Select(u => u.Key).ToList();
                if (expired.Count == 0) continue;
                foreach (var userId in expired) users.Remove(userId);
                Rebuild(chatId);
                changed.Add(chatId);
            }
            if (_remote.Count == 0) StopSweeper();
        }
        foreach (var chatId in changed) EventRaiser.Raise(Changed, chatId, _logger);
    }

    // Callers hold _lock.
    private void Rebuild(Guid chatId)
    {
        if (!_remote.TryGetValue(chatId, out var users) || users.Count == 0)
        {
            _remote.Remove(chatId);
            _snapshots.TryRemove(chatId, out _);
            return;
        }
        _snapshots[chatId] = users.Values.OrderBy(u => u.At).Select(u => u.Event).ToList();
    }

    private void EnsureSweeper() => _sweeper ??= _time.CreateTimer(static s => ((TypingStore)s!).Sweep(), this, SweepPeriod, SweepPeriod);

    private void StopSweeper()
    {
        _sweeper?.Dispose();
        _sweeper = null;
    }
}
