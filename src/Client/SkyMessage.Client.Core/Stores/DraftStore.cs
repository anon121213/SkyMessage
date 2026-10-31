using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;

namespace SkyMessage.Client.Core.Stores;

/// <summary>Composer drafts per chat, persisted per account ("drafts", debounced) so they survive restarts.</summary>
internal sealed class DraftStore : IDraftStore, IAccountScoped
{
    private const string CacheName = "drafts";
    private static readonly TimeSpan PersistDelay = TimeSpan.FromMilliseconds(500);

    private readonly LocalCache _cache;
    private readonly AccountLifecycle _lifecycle;
    private readonly ILogger<DraftStore> _logger;
    private readonly ConcurrentDictionary<Guid, DraftDto> _drafts = new();
    private readonly CoalescingAction _persist;
    private volatile int _generation;

    public DraftStore(LocalCache cache, AccountLifecycle lifecycle, TimeProvider time, ILogger<DraftStore> logger)
    {
        _cache = cache;
        _lifecycle = lifecycle;
        _logger = logger;
        _persist = new CoalescingAction(PersistDelay, PersistAsync, time, logger);
        lifecycle.Register(this);
    }

    public event Action<Guid>? Changed;

    public DraftDto? Get(Guid chatId) => _drafts.TryGetValue(chatId, out var draft) ? draft : null;

    public void Set(Guid chatId, DraftDto? draft)
    {
        bool changed;
        if (draft is null || (string.IsNullOrWhiteSpace(draft.Text) && draft.ReplyToMessageId is null))
        {
            changed = _drafts.TryRemove(chatId, out _);
        }
        else
        {
            changed = !_drafts.TryGetValue(chatId, out var existing) || !SameContent(existing, draft);
            _drafts[chatId] = draft;
        }
        if (!changed) return;
        _persist.Request();
        EventRaiser.Raise(Changed, chatId, _logger);
    }

    async Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        var generation = _generation;
        var cached = await _cache.ReadAsync<Dictionary<Guid, DraftDto>>(LocalCache.AccountKey(userId, CacheName)).ConfigureAwait(false);
        if (cached is null || generation != _generation) return;
        foreach (var (chatId, draft) in cached)
        {
            if (draft is null || !_drafts.TryAdd(chatId, draft)) continue;
            EventRaiser.Raise(Changed, chatId, _logger);
        }
    }

    Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        Interlocked.Increment(ref _generation);
        _persist.Cancel();
        var chats = _drafts.Keys.ToList();
        _drafts.Clear();
        foreach (var chatId in chats) EventRaiser.Raise(Changed, chatId, _logger);
        return Task.CompletedTask;
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_lifecycle.ActiveUserId is not { } userId) return;
        var snapshot = new Dictionary<Guid, DraftDto>(_drafts);
        await _cache.WriteAsync(LocalCache.AccountKey(userId, CacheName), snapshot, ct).ConfigureAwait(false);
    }

    private static bool SameContent(DraftDto a, DraftDto b) =>
        a.Text == b.Text && a.ReplyToMessageId == b.ReplyToMessageId
        && (a.Entities ?? []).SequenceEqual(b.Entities ?? []);
}
