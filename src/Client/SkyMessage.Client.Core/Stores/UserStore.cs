using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// The single source of truth for people (SPEC §8.1 Stores). Every store upserts embedded summaries here; unknown
/// ids are collected for 50 ms and resolved with one POST api/users/batch (no duplicate in-flight requests, failed
/// ids are not retried for a while). Presence events patch the cached summaries; UserChanged refetches. Cached per
/// account ("users").
/// </summary>
internal sealed class UserStore : IUserStore, IAccountScoped
{
    internal static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfterMissing = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PersistDelay = TimeSpan.FromSeconds(2);
    private const string CacheName = "users";
    private const int MaxCachedUsers = 3000;

    private readonly ISkyApi _api;
    private readonly IAuthService _auth;
    private readonly LocalCache _cache;
    private readonly AccountLifecycle _lifecycle;
    private readonly TimeProvider _time;
    private readonly ILogger<UserStore> _logger;
    private readonly ConcurrentDictionary<Guid, UserSummaryDto> _users = new();
    private readonly ConcurrentDictionary<Guid, UserProfileDto> _profiles = new();
    private readonly ConcurrentDictionary<Guid, long> _retryAfter = new();
    private readonly object _batchLock = new();
    private readonly Dictionary<Guid, TaskCompletionSource<UserSummaryDto?>> _waiters = [];
    private readonly HashSet<Guid> _pending = [];
    private readonly CoalescingAction _persist;
    private bool _flushScheduled;
    private volatile int _generation;

    public UserStore(ISkyApi api, IRealtimeService realtime, IAuthService auth, LocalCache cache, AccountLifecycle lifecycle,
        TimeProvider time, ILogger<UserStore> logger)
    {
        _api = api;
        _auth = auth;
        _cache = cache;
        _lifecycle = lifecycle;
        _time = time;
        _logger = logger;
        _persist = new CoalescingAction(PersistDelay, PersistAsync, time, logger);

        realtime.PresenceChanged += OnPresenceChanged;
        realtime.UserChanged += OnUserChanged;
        realtime.Resynced += OnResynced;
        auth.Changed += OnAuthChanged;
        lifecycle.Register(this);
    }

    public event Action<Guid>? UserChanged;

    public UserSummaryDto? Get(Guid userId) => _users.TryGetValue(userId, out var user) ? user : null;

    public Task<UserSummaryDto?> GetOrFetchAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) return Task.FromResult<UserSummaryDto?>(null);
        if (_users.TryGetValue(userId, out var known)) return Task.FromResult<UserSummaryDto?>(known);
        return Enqueue(userId, force: true).WaitAsync(cancellationToken);
    }

    public async Task<UserProfileDto> GetProfileAsync(Guid userId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && _profiles.TryGetValue(userId, out var cached)) return cached;
        var profile = await _api.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        _profiles[userId] = profile;
        Upsert([profile.User]);
        return profile;
    }

    public async Task<UserProfileDto?> GetProfileByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var name = username.Trim().TrimStart('@');
        if (name.Length == 0) return null;
        try
        {
            var profile = await _api.GetUserByUsernameAsync(name, cancellationToken).ConfigureAwait(false);
            _profiles[profile.User.Id] = profile;
            Upsert([profile.User]);
            return profile;
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public void Upsert(IEnumerable<UserSummaryDto> users)
    {
        List<Guid>? changed = null;
        foreach (var user in users)
        {
            if (user is null || user.Id == Guid.Empty) continue;
            var stored = _users.AddOrUpdate(user.Id, user, (_, existing) => existing == user ? existing : user);
            if (!ReferenceEquals(stored, user)) continue;
            _retryAfter.TryRemove(user.Id, out _);
            (changed ??= []).Add(user.Id);
        }
        if (changed is null) return;
        _persist.Request();
        foreach (var id in changed) EventRaiser.Raise(UserChanged, id, _logger);
    }

    public string DisplayNameOf(Guid userId)
    {
        if (_users.TryGetValue(userId, out var user))
        {
            var name = user.ContactName ?? user.DisplayName;
            return string.IsNullOrWhiteSpace(name) ? user.Username : name;
        }
        if (userId != Guid.Empty) Enqueue(userId, force: false).Forget(_logger);
        return CoreStrings.UnknownUser;
    }

    async Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        var generation = _generation;
        var cached = await _cache.ReadAsync<List<UserSummaryDto>>(LocalCache.AccountKey(userId, CacheName)).ConfigureAwait(false);
        if (cached is null || generation != _generation) return;
        foreach (var user in cached)
            if (user is not null && user.Id != Guid.Empty) _users.TryAdd(user.Id, user);
        OnAuthChanged();
    }

    Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        Interlocked.Increment(ref _generation);
        _persist.Cancel();
        List<TaskCompletionSource<UserSummaryDto?>> waiters;
        lock (_batchLock)
        {
            waiters = [.. _waiters.Values];
            _waiters.Clear();
            _pending.Clear();
        }
        foreach (var waiter in waiters) waiter.TrySetResult(null);
        _users.Clear();
        _profiles.Clear();
        _retryAfter.Clear();
        return Task.CompletedTask;
    }

    private Task<UserSummaryDto?> Enqueue(Guid userId, bool force)
    {
        if (!force && _retryAfter.TryGetValue(userId, out var until) && _time.GetTimestamp() < until)
            return Task.FromResult<UserSummaryDto?>(null);

        lock (_batchLock)
        {
            if (_waiters.TryGetValue(userId, out var existing)) return existing.Task;
            var waiter = new TaskCompletionSource<UserSummaryDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters[userId] = waiter;
            _pending.Add(userId);
            if (!_flushScheduled)
            {
                _flushScheduled = true;
                FlushLaterAsync(_generation).Forget(_logger);
            }
            return waiter.Task;
        }
    }

    private async Task FlushLaterAsync(int generation)
    {
        await Task.Delay(BatchDelay, _time).ConfigureAwait(false);
        List<Guid> ids;
        lock (_batchLock)
        {
            _flushScheduled = false;
            ids = [.. _pending];
            _pending.Clear();
        }
        if (ids.Count == 0) return;

        IReadOnlyList<UserSummaryDto>? found = null;
        var failed = false;
        try
        {
            found = await _api.GetUsersAsync(ids, _lifecycle.AccountToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            _logger.LogDebug(ex, "User batch of {Count} failed", ids.Count);
        }

        if (generation != _generation) return;
        if (found is not null) Upsert(found);

        var now = _time.GetTimestamp();
        var delay = (long)((failed ? RetryAfterFailure : RetryAfterMissing).TotalSeconds * _time.TimestampFrequency);
        List<(TaskCompletionSource<UserSummaryDto?> Waiter, UserSummaryDto? User)> completions = [];
        lock (_batchLock)
        {
            foreach (var id in ids)
            {
                var user = Get(id);
                if (user is null) _retryAfter[id] = now + delay;
                if (_waiters.Remove(id, out var waiter)) completions.Add((waiter, user));
            }
        }
        foreach (var (waiter, user) in completions) waiter.TrySetResult(user);
    }

    private void OnPresenceChanged(PresenceChangedEvent e)
    {
        if (_users.TryGetValue(e.UserId, out var user))
            Upsert([user with { Presence = e.Presence }]);
        if (_profiles.TryGetValue(e.UserId, out var profile))
            _profiles[e.UserId] = profile with { User = profile.User with { Presence = e.Presence } };
    }

    private void OnUserChanged(UserRefEvent e)
    {
        _profiles.TryRemove(e.UserId, out _);
        _retryAfter.TryRemove(e.UserId, out _);
        if (_users.ContainsKey(e.UserId) || e.UserId == _auth.UserId)
            Enqueue(e.UserId, force: true).Forget(_logger);
    }

    private void OnResynced()
    {
        // Presence changes were missed while disconnected: refresh everyone shown as online.
        foreach (var user in _users.Values)
            if (user.Presence.IsOnline) Enqueue(user.Id, force: true).Forget(_logger);
    }

    private void OnAuthChanged()
    {
        if (_auth.Me is not { } me || me.Id == Guid.Empty) return;
        var existing = Get(me.Id);
        var summary = new UserSummaryDto
        {
            Id = me.Id,
            Username = me.Username,
            DisplayName = me.DisplayName,
            AvatarMediaId = me.AvatarMediaId,
            Presence = existing?.Presence ?? new PresenceDto { IsOnline = true, Approx = LastSeenApprox.Online },
            ContactName = existing?.ContactName,
            IsContact = existing?.IsContact ?? false,
        };
        if (existing is null || existing.Username != summary.Username || existing.DisplayName != summary.DisplayName
            || existing.AvatarMediaId != summary.AvatarMediaId)
        {
            Upsert([summary]);
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_lifecycle.ActiveUserId is not { } userId) return;
        var users = _users.Values.Take(MaxCachedUsers).ToList();
        await _cache.WriteAsync(LocalCache.AccountKey(userId, CacheName), users, ct).ConfigureAwait(false);
    }
}
