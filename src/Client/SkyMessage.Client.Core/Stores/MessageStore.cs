using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// Message windows per chat, the persisted outbox and message actions (SPEC §8.1 Stores, StoreContracts rules).
/// Every mutation runs through one queue and publishes an immutable <see cref="ChatMessagesState"/> per chat.
/// Realtime events for a chat whose window is loading are buffered and replayed afterwards; merges follow the
/// UpdatedAt rule; pending items are deduped by ClientMessageId. This file: state, snapshots, realtime and lifecycle;
/// the partial files hold loading/sync, the outbox and message actions.
/// </summary>
internal sealed partial class MessageStore : IMessageStore, IAccountScoped
{
    private const string OutboxCacheName = "outbox";
    private const int CachedMessagesPerChat = 50;
    /// <summary>A window off screen for <see cref="IdleTrimDelay"/> that grows past this is cut back to <see cref="IdleWindowMessages"/>.</summary>
    private const int IdleWindowLimit = 300;
    private const int IdleWindowMessages = 100;
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PersistDelay = TimeSpan.FromMilliseconds(800);

    private readonly ISkyApi _api;
    private readonly IRealtimeService _realtime;
    private readonly IAuthService _auth;
    private readonly IChatListStore _chats;
    private readonly IUserStore _users;
    private readonly ITypingStore _typing;
    private readonly MediaUploader _uploader;
    private readonly IMediaCompressor _compressor;
    private readonly IOutboxFileStore _outboxFiles;
    private readonly INetworkStatusService _network;
    private readonly ISettingsService _settings;
    private readonly LocalCache _cache;
    private readonly AccountLifecycle _lifecycle;
    private readonly TimeProvider _time;
    private readonly ILogger<MessageStore> _logger;
    private readonly SerialQueue _queue = new();
    private readonly object _lock = new();
    private readonly CoalescingAction _persistOutbox;
    private readonly CoalescingAction _persistWindows;

    // Guarded by _queue.
    private readonly Dictionary<Guid, MessageWindow> _windows = [];
    private readonly Dictionary<Guid, List<Action<Changes>>> _buffers = [];
    private readonly Dictionary<Guid, Dictionary<Guid, MessageItem>> _itemCache = [];
    private List<OutboxEntry> _outbox = [];
    /// <summary>Resyncs so far: a catch-up that started before one does not count as done.</summary>
    private int _resyncs;

    // Read without the queue (immutable values).
    private readonly ConcurrentDictionary<Guid, ChatMessagesState> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<MessageDto>> _pinned = new();

    // Guarded by _lock.
    private readonly HashSet<Guid> _dirtyWindows = [];
    private readonly HashSet<Guid> _pinnedLoaded = [];
    /// <summary>The chat on screen as of the last ActiveChatChanged (to tell which one left the screen).</summary>
    private Guid? _shownChatId;

    private volatile int _generation;

    public MessageStore(
        ISkyApi api,
        IRealtimeService realtime,
        IAuthService auth,
        IChatListStore chats,
        IUserStore users,
        ITypingStore typing,
        MediaUploader uploader,
        IMediaCompressor compressor,
        IOutboxFileStore outboxFiles,
        INetworkStatusService network,
        ISettingsService settings,
        LocalCache cache,
        AccountLifecycle lifecycle,
        TimeProvider time,
        ILogger<MessageStore> logger)
    {
        _api = api;
        _realtime = realtime;
        _auth = auth;
        _chats = chats;
        _users = users;
        _typing = typing;
        _uploader = uploader;
        _compressor = compressor;
        _outboxFiles = outboxFiles;
        _network = network;
        _settings = settings;
        _cache = cache;
        _lifecycle = lifecycle;
        _time = time;
        _logger = logger;
        _persistOutbox = new CoalescingAction(PersistDelay, PersistOutboxAsync, time, logger);
        _persistWindows = new CoalescingAction(PersistDelay, PersistWindowsAsync, time, logger);

        realtime.MessageCreated += OnMessageCreated;
        realtime.MessageUpdated += OnMessageUpdated;
        realtime.MessagesDeleted += OnMessagesDeleted;
        realtime.HistoryCleared += OnHistoryCleared;
        realtime.ChatRemoved += OnChatRemoved;
        realtime.PinnedMessagesChanged += OnPinnedMessagesChanged;
        realtime.Resynced += OnResynced;
        realtime.StateChanged += OnRealtimeStateChanged;
        network.Changed += OnNetworkChanged;
        chats.ActiveChatChanged += OnActiveChatChanged;
        lifecycle.Register(this);
    }

    public event Action<Guid>? ChatChanged;

    public event Action<Guid, long>? JumpRequested;

    public ChatMessagesState GetState(Guid chatId) =>
        _snapshots.TryGetValue(chatId, out var state) ? state : ChatMessagesState.Empty(chatId);

    public IReadOnlyList<MessageDto> GetPinned(Guid chatId) => _pinned.TryGetValue(chatId, out var pinned) ? pinned : [];

    public MessageItem? Find(Guid chatId, Guid messageId)
    {
        if (_snapshots.TryGetValue(chatId, out var state))
        {
            foreach (var item in state.Items)
                if (item.Message.Id == messageId || item.Key == messageId) return item;
        }
        if (_pinned.TryGetValue(chatId, out var pinned))
        {
            foreach (var message in pinned)
                if (message.Id == messageId) return new MessageItem { Message = message, Key = message.Id };
        }
        return null;
    }

    // Lifecycle

    async Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        var generation = _generation;
        var records = await _cache.ReadAsync<List<OutboxRecord>>(LocalCache.AccountKey(userId, OutboxCacheName)).ConfigureAwait(false);
        var restored = new List<OutboxEntry>();
        foreach (var record in records ?? [])
        {
            if (record is null || record.Key == Guid.Empty || record.ChatId == Guid.Empty) continue;
            restored.Add(await RestoreEntryAsync(record).ConfigureAwait(false));
        }
        if (generation != _generation) return;

        var chats = new HashSet<Guid>();
        await MutateAsync(changes =>
        {
            foreach (var entry in restored)
            {
                if (_outbox.Any(e => e.Key == entry.Key)) continue;
                _outbox.Add(entry);
                EnsureWindow(entry.ChatId);
                changes.Chats.Add(entry.ChatId);
                chats.Add(entry.ChatId);
            }
        }).ConfigureAwait(false);
        foreach (var chatId in chats) Kick(chatId);
    }

    async Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        Interlocked.Increment(ref _generation);
        _persistOutbox.Cancel();
        _persistWindows.Cancel();
        lock (_lock)
        {
            _dirtyWindows.Clear();
            _pinnedLoaded.Clear();
            _shownChatId = null;
            _draining.Clear();
            _drainAgain.Clear();
            _gapFills.Clear();
            _catchUps.Clear();
            _latestLoads.Clear();
        }

        List<OutboxEntry> entries = [];
        List<Guid> chats = [];
        await _queue.RunAsync(() =>
        {
            entries = _outbox;
            _outbox = [];
            chats = [.. _windows.Keys];
            _windows.Clear();
            _buffers.Clear();
            _itemCache.Clear();
        }).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            entry.Removed = true;
            entry.Cancellation.Cancel();
        }
        _snapshots.Clear();
        _pinned.Clear();
        foreach (var chatId in chats) EventRaiser.Raise(ChatChanged, chatId, _logger);

        // The account's cache prefix is wiped by the lifecycle; durable attachment copies are ours to delete.
        foreach (var entry in entries)
            await DeleteFilesAsync(entry).ConfigureAwait(false);
    }

    // Realtime

    private void OnMessageCreated(MessageDto message) => MutateAsync(changes =>
    {
        if (!_windows.TryGetValue(message.ChatId, out var window))
        {
            ConfirmPendingByServerCopy(message, null, changes);
            return;
        }
        RunOrBuffer(window, changes, c => ApplyCreated(window, message, c));
    }).Forget(_logger);

    private void OnMessageUpdated(MessageDto message) => MutateAsync(changes =>
    {
        UpdatePinnedCopy(message, changes);
        if (!_windows.TryGetValue(message.ChatId, out var window)) return;
        RunOrBuffer(window, changes, c => ApplyUpdated(window, message, c));
    }).Forget(_logger);

    private void OnMessagesDeleted(MessagesDeletedEvent e) => MutateAsync(changes =>
    {
        RemovePinnedCopies(e.ChatId, e.MessageIds, changes);
        if (!_windows.TryGetValue(e.ChatId, out var window)) return;
        RunOrBuffer(window, changes, c =>
        {
            var removed = false;
            foreach (var id in e.MessageIds) removed |= window.Remove(id);
            if (e.ForEveryone) removed |= MarkReplyPreviewsDeleted(window, e.MessageIds);
            if (removed) Touch(window, c);
        });
    }).Forget(_logger);

    private void OnHistoryCleared(HistoryClearedEvent e) => MutateAsync(changes =>
    {
        if (_pinned.TryGetValue(e.ChatId, out var pinned))
        {
            _pinned[e.ChatId] = pinned.Where(m => m.Seq > e.UpToSeq).ToList();
            changes.Chats.Add(e.ChatId);
        }
        if (!_windows.TryGetValue(e.ChatId, out var window)) return;
        RunOrBuffer(window, changes, c =>
        {
            if (window.ClearUpTo(e.UpToSeq)) Touch(window, c);
        });
    }).Forget(_logger);

    private void OnChatRemoved(ChatRefEvent e) => DropChatAsync(e.ChatId).Forget(_logger);

    private async Task DropChatAsync(Guid chatId)
    {
        List<OutboxEntry> dropped = [];
        await MutateAsync(changes =>
        {
            _windows.Remove(chatId);
            _buffers.Remove(chatId);
            _itemCache.Remove(chatId);
            dropped = _outbox.Where(o => o.ChatId == chatId).ToList();
            foreach (var entry in dropped) RemoveEntry(entry, changes);
            changes.Chats.Add(chatId);
        }).ConfigureAwait(false);
        _snapshots.TryRemove(chatId, out _);
        _pinned.TryRemove(chatId, out _);
        lock (_lock)
        {
            _pinnedLoaded.Remove(chatId);
            _dirtyWindows.Remove(chatId);
        }
        if (_lifecycle.ActiveUserId is { } userId)
            await _cache.DeleteAsync(LocalCache.AccountKey(userId, WindowCacheName(chatId))).ConfigureAwait(false);
        // Left or removed: nothing pending there can be sent any more.
        foreach (var entry in dropped)
        {
            await entry.Cancellation.CancelAsync().ConfigureAwait(false);
            await DeleteFilesAsync(entry).ConfigureAwait(false);
        }
    }

    private void OnPinnedMessagesChanged(ChatRefEvent e)
    {
        bool tracked;
        lock (_lock) tracked = _pinnedLoaded.Contains(e.ChatId);
        if (tracked || _snapshots.ContainsKey(e.ChatId))
            LoadPinnedAsync(e.ChatId).Forget(_logger);
    }

    private void OnResynced()
    {
        var active = _chats.ActiveChatId;
        MutateAsync(changes =>
        {
            // Lazy resync: chats not on screen catch up when they are opened or come back on screen.
            _resyncs++;
            foreach (var window in _windows.Values)
                if (window.IsInitialized) window.NeedsCatchUp = true;
            ResetBackoff();
        }).ContinueWith(_ =>
        {
            if (active is { } chatId)
            {
                CatchUpAsync(chatId, fromResync: true, CancellationToken.None).Forget(_logger);
                LoadPinnedAsync(chatId).Forget(_logger);
            }
            KickAll();
        }, TaskScheduler.Default).Forget(_logger);
    }

    private void OnRealtimeStateChanged()
    {
        if (_realtime.State == ConnectionState.Connected) KickAll();
    }

    private void OnNetworkChanged()
    {
        if (!_network.IsConnected) return;
        MutateAsync(_ => ResetBackoff()).ContinueWith(_ => KickAll(), TaskScheduler.Default).Forget(_logger);
        // A catch-up of the chat on screen that failed while the network was gone runs again.
        if (_chats.ActiveChatId is { } chatId) CatchUpIfPendingAsync(chatId).Forget(_logger);
    }

    private void OnActiveChatChanged()
    {
        var active = _chats.ActiveChatId;
        Guid? left;
        lock (_lock)
        {
            left = _shownChatId;
            _shownChatId = active;
        }
        if (left is { } leftId && leftId != active)
        {
            MutateAsync(_ =>
            {
                if (_windows.TryGetValue(leftId, out var window)) window.LeftScreenAt = _time.GetTimestamp();
            }).Forget(_logger);
        }

        if (active is not { } chatId) return;
        ResumeAsync().Forget(_logger);

        // Back on screen (uncovered, reopened): a resync while the chat was not on screen left its catch-up for later,
        // and the chat is about to be read. A first open restores and catches up through OpenAsync/JumpToAsync.
        async Task ResumeAsync()
        {
            await Task.Yield();
            if (!await IsCatchUpPendingAsync(chatId).ConfigureAwait(false)) return;
            LoadPinnedAsync(chatId).Forget(_logger);
            await CatchUpAsync(chatId, fromResync: false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Applying confirmed messages (inside the queue)

    private void ApplyCreated(MessageWindow window, MessageDto message, Changes changes)
    {
        var confirmedHere = ConfirmPendingByServerCopy(message, window, changes);
        if (!window.IsInitialized)
        {
            if (confirmedHere) changes.Chats.Add(window.ChatId);
            return;
        }
        if (window.Contains(message.Id) || window.BelongsInWindow(message.Seq))
        {
            var gap = !window.Contains(message.Id) && window.HasGapBefore(message.Seq);
            if (window.Upsert(message) || confirmedHere) Touch(window, changes);
            if (gap && !window.NeedsCatchUp) ScheduleGapFill(window.ChatId);
            // The catch-up still due fills the hole (/changes); on screen it must not wait for the next open.
            else if (gap && _chats.ActiveChatId == window.ChatId) CatchUpIfPendingAsync(window.ChatId).Forget(_logger);
            TrimIfIdle(window, changes);
        }
        else if (confirmedHere)
        {
            changes.Chats.Add(window.ChatId);
        }
    }

    private void ApplyUpdated(MessageWindow window, MessageDto message, Changes changes)
    {
        if (!window.Contains(message.Id)) return;
        var previous = window.MyReactions.GetValueOrDefault(message.Id);
        var changed = window.Upsert(message);
        if (window.Contains(message.Id) && Me is { } me)
        {
            var mine = MessageRules.MyReaction(window.Get(message.Id)!, me, previous);
            if (mine != previous)
            {
                if (mine is null) window.MyReactions.Remove(message.Id);
                else window.MyReactions[message.Id] = mine;
                changed = true;
            }
        }
        changed |= RefreshReplyPreviews(window, message);
        if (changed) Touch(window, changes);
    }

    /// <summary>
    /// Every chat opened once keeps a window that takes each new message, and the process lives on in the background
    /// for days: a window off screen for a while is cut back to its newest messages (older ones load again when
    /// scrolled to). One left only moments ago is spared (covered by its info page, it comes back where it was).
    /// </summary>
    private void TrimIfIdle(MessageWindow window, Changes changes)
    {
        if (window.Count <= IdleWindowLimit || window.LoadDepth > 0 || window.HasMoreAfter || _chats.ActiveChatId == window.ChatId) return;
        if (window.LeftScreenAt is { } left && _time.GetElapsedTime(left) < IdleTrimDelay) return;
        if (window.TrimToNewest(IdleWindowMessages)) Touch(window, changes);
    }

    /// <summary>Removes the pending copy a server message confirms (same ClientMessageId). True when one was removed.</summary>
    private bool ConfirmPendingByServerCopy(MessageDto message, MessageWindow? window, Changes changes)
    {
        if (message.ClientMessageId is not { } clientId) return false;
        var entry = _outbox.FirstOrDefault(e => e.Key == clientId && e.ChatId == message.ChatId);
        if (entry is null) return false;
        if (window is not null) window.LocalKeys[message.Id] = clientId;
        RemoveEntry(entry, changes);
        DeleteFilesAsync(entry).Forget(_logger);
        return true;
    }

    private bool RefreshReplyPreviews(MessageWindow window, MessageDto original)
    {
        var changed = false;
        foreach (var held in window.Messages.ToList())
        {
            if (held.ReplyTo?.MessageId != original.Id) continue;
            var preview = MessageRules.ReplyPreview(original);
            if (held.ReplyTo == preview) continue;
            window.Upsert(held with { ReplyTo = preview });
            changed = true;
        }
        return changed;
    }

    private static bool MarkReplyPreviewsDeleted(MessageWindow window, IReadOnlyList<Guid> deleted)
    {
        var changed = false;
        var set = deleted.ToHashSet();
        foreach (var held in window.Messages.ToList())
        {
            if (held.ReplyTo is not { } reply || !set.Contains(reply.MessageId) || reply.IsDeleted) continue;
            window.Upsert(held with { ReplyTo = reply with { IsDeleted = true, Text = null, Media = null } });
            changed = true;
        }
        return changed;
    }

    private void UpdatePinnedCopy(MessageDto message, Changes changes)
    {
        if (!_pinned.TryGetValue(message.ChatId, out var pinned)) return;
        var index = -1;
        for (var i = 0; i < pinned.Count; i++)
            if (pinned[i].Id == message.Id) index = i;

        List<MessageDto> next;
        if (message.IsDeleted || !message.IsPinned)
        {
            if (index < 0) return;
            next = pinned.Where(m => m.Id != message.Id).ToList();
        }
        else if (index >= 0)
        {
            if (message.UpdatedAt < pinned[index].UpdatedAt) return;
            next = [.. pinned];
            next[index] = message;
        }
        else
        {
            next = pinned.Prepend(message).ToList();
        }
        _pinned[message.ChatId] = next;
        changes.Chats.Add(message.ChatId);
    }

    private void RemovePinnedCopies(Guid chatId, IReadOnlyList<Guid> ids, Changes changes)
    {
        if (!_pinned.TryGetValue(chatId, out var pinned) || !pinned.Any(m => ids.Contains(m.Id))) return;
        _pinned[chatId] = pinned.Where(m => !ids.Contains(m.Id)).ToList();
        changes.Chats.Add(chatId);
    }

    // Plumbing

    private Guid? Me => _auth.UserId;

    /// <summary>What a mutation touched: chats to republish/notify, windows to persist, whether the outbox changed.</summary>
    private sealed class Changes
    {
        public HashSet<Guid> Chats { get; } = [];
        public HashSet<Guid> Windows { get; } = [];
        public bool Outbox { get; set; }
        public List<(Guid ChatId, long Seq)> Jumps { get; } = [];
    }

    private async Task MutateAsync(Action<Changes> mutation)
    {
        var changes = new Changes();
        await _queue.RunAsync(() =>
        {
            mutation(changes);
            foreach (var chatId in changes.Chats) Publish(chatId);
        }).ConfigureAwait(false);
        Flush(changes);
    }

    private async Task<T> MutateAsync<T>(Func<Changes, T> mutation)
    {
        var changes = new Changes();
        var result = await _queue.RunAsync(() =>
        {
            var value = mutation(changes);
            foreach (var chatId in changes.Chats) Publish(chatId);
            return value;
        }).ConfigureAwait(false);
        Flush(changes);
        return result;
    }

    private void Flush(Changes changes)
    {
        if (changes.Outbox) _persistOutbox.Request();
        if (changes.Windows.Count > 0)
        {
            lock (_lock) _dirtyWindows.UnionWith(changes.Windows);
            _persistWindows.Request();
        }
        foreach (var chatId in changes.Chats) EventRaiser.Raise(ChatChanged, chatId, _logger);
        foreach (var (chatId, seq) in changes.Jumps) EventRaiser.Raise(JumpRequested, chatId, seq, _logger);
    }

    private void RunOrBuffer(MessageWindow window, Changes changes, Action<Changes> apply)
    {
        if (window.LoadDepth > 0)
        {
            if (!_buffers.TryGetValue(window.ChatId, out var buffer)) _buffers[window.ChatId] = buffer = [];
            buffer.Add(apply);
            return;
        }
        apply(changes);
    }

    /// <summary>Called when a load finished: replays what arrived meanwhile once no load is in flight.</summary>
    private void EndLoad(MessageWindow window, Changes changes)
    {
        window.LoadDepth = Math.Max(0, window.LoadDepth - 1);
        if (window.LoadDepth > 0 || !_buffers.Remove(window.ChatId, out var buffer)) return;
        foreach (var apply in buffer) apply(changes);
    }

    private static void Touch(MessageWindow window, Changes changes)
    {
        changes.Chats.Add(window.ChatId);
        changes.Windows.Add(window.ChatId);
    }

    private MessageWindow EnsureWindow(Guid chatId)
    {
        if (!_windows.TryGetValue(chatId, out var window))
        {
            window = new MessageWindow(chatId);
            _windows[chatId] = window;
        }
        return window;
    }

    /// <summary>Rebuilds the immutable snapshot of one chat (inside the queue).</summary>
    private void Publish(Guid chatId)
    {
        _windows.TryGetValue(chatId, out var window);
        var pending = _outbox.Where(e => e.ChatId == chatId && !e.Removed).ToList();
        if (window is null && pending.Count == 0)
        {
            _snapshots.TryRemove(chatId, out _);
            return;
        }

        var items = new List<MessageItem>((window?.Count ?? 0) + pending.Count);
        if (window is not null)
        {
            if (!_itemCache.TryGetValue(chatId, out var cache)) _itemCache[chatId] = cache = [];
            foreach (var message in window.Messages)
            {
                var key = window.LocalKeys.TryGetValue(message.Id, out var local) ? local : message.Id;
                if (!cache.TryGetValue(message.Id, out var item) || !ReferenceEquals(item.Message, message) || item.Key != key)
                {
                    item = new MessageItem { Message = message, Key = key };
                    cache[message.Id] = item;
                }
                items.Add(item);
            }
            if (cache.Count > window.Count * 2 + 64)
            {
                foreach (var stale in cache.Keys.Where(id => !window.Contains(id)).ToList()) cache.Remove(stale);
            }
        }
        foreach (var entry in pending) items.Add(entry.CachedItem ??= BuildPendingItem(entry));

        _snapshots[chatId] = new ChatMessagesState
        {
            ChatId = chatId,
            Items = items,
            IsInitialized = window?.IsInitialized ?? false,
            HasMoreBefore = window?.HasMoreBefore ?? false,
            HasMoreAfter = window?.HasMoreAfter ?? false,
            IsLoadingBefore = window?.IsLoadingBefore ?? false,
            IsLoadingAfter = window?.IsLoadingAfter ?? false,
            FirstUnreadSeq = window?.FirstUnreadSeq,
            MyReactions = window is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string>(window.MyReactions),
        };
    }

    private MessageItem BuildPendingItem(OutboxEntry entry)
    {
        var record = entry.Record;
        var forward = record.Kind == OutboxKind.Forward;
        var source = record.ForwardSource;
        var message = new MessageDto
        {
            Id = record.Key,
            ChatId = record.ChatId,
            Seq = 0,
            SenderId = Me,
            Type = record.Type,
            Text = record.Text,
            Entities = record.Entities,
            Attachments = forward && source is not null ? source.Attachments : [],
            ReplyTo = record.ReplyPreview,
            Forward = forward ? ForwardInfoFor(source, record.ForwardFromChatId) : null,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.CreatedAt,
            ClientMessageId = record.Key,
        };
        return new MessageItem
        {
            Message = message,
            Key = record.Key,
            SendState = entry.State,
            Progress = entry.Progress,
            Error = entry.Error,
            LocalPreviews = entry.Attachments.Select(a => a.ToPreview()).ToList(),
        };
    }

    private ForwardInfoDto? ForwardInfoFor(MessageDto? source, Guid? fromChatId)
    {
        if (source is null) return new ForwardInfoDto { FromChatId = fromChatId, SenderName = "", OriginalDate = _time.GetUtcNow() };
        if (source.Forward is { } original) return original;
        return new ForwardInfoDto
        {
            FromUserId = source.SenderId,
            FromChatId = fromChatId,
            SenderName = source.SenderId is { } sender ? _users.DisplayNameOf(sender) : "",
            OriginalDate = source.CreatedAt,
        };
    }

    // Window cache ("msgs/{chatId}": the newest messages + their sync cursor)

    private static string WindowCacheName(Guid chatId) => $"msgs/{chatId:N}";

    private sealed class CachedWindow
    {
        public List<MessageDto> Messages { get; set; } = [];
        public string? SyncCursor { get; set; }
        public bool HasMoreBefore { get; set; }
        public Dictionary<Guid, string> MyReactions { get; set; } = [];
        public List<MessageDto>? Pinned { get; set; }
    }

    private async Task PersistWindowsAsync(CancellationToken ct)
    {
        if (_lifecycle.ActiveUserId is not { } userId) return;
        List<Guid> ids;
        lock (_lock)
        {
            ids = [.. _dirtyWindows];
            _dirtyWindows.Clear();
        }
        foreach (var chatId in ids)
        {
            var cached = await _queue.RunAsync(() =>
            {
                if (!_windows.TryGetValue(chatId, out var window) || !window.IsInitialized || window.HasMoreAfter || window.SyncCursor is null)
                    return null;
                var messages = window.Messages.Skip(Math.Max(0, window.Count - CachedMessagesPerChat)).ToList();
                var ids = messages.Select(m => m.Id).ToHashSet();
                return new CachedWindow
                {
                    Messages = messages,
                    SyncCursor = window.SyncCursor,
                    HasMoreBefore = window.HasMoreBefore || window.Count > CachedMessagesPerChat,
                    MyReactions = window.MyReactions.Where(r => ids.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value),
                    Pinned = _pinned.TryGetValue(chatId, out var pinned) ? [.. pinned] : null,
                };
            }).ConfigureAwait(false);
            if (cached is null || ct.IsCancellationRequested) continue;
            await _cache.WriteAsync(LocalCache.AccountKey(userId, WindowCacheName(chatId)), cached, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Creates the window and restores it from the cache once (no network).</summary>
    private async Task EnsureWindowAsync(Guid chatId)
    {
        var needsCache = await _queue.RunAsync(() => !EnsureWindow(chatId).CacheChecked).ConfigureAwait(false);
        if (!needsCache) return;

        CachedWindow? cached = null;
        if (_lifecycle.ActiveUserId is { } userId)
            cached = await _cache.ReadAsync<CachedWindow>(LocalCache.AccountKey(userId, WindowCacheName(chatId))).ConfigureAwait(false);
        var clearedUpTo = _chats.Get(chatId)?.ClearedUpToSeq ?? 0;

        await MutateAsync(changes =>
        {
            var window = EnsureWindow(chatId);
            if (window.CacheChecked) return;
            window.CacheChecked = true;
            if (cached is { Messages.Count: > 0 } && !window.IsInitialized && window.LoadDepth == 0)
            {
                foreach (var message in cached.Messages) window.Upsert(message);
                foreach (var (id, emoji) in cached.MyReactions)
                    if (window.Contains(id)) window.MyReactions[id] = emoji;
                window.ClearUpTo(clearedUpTo);
                window.HasMoreBefore = cached.HasMoreBefore;
                window.HasMoreAfter = false;
                window.SyncCursor = cached.SyncCursor;
                window.IsInitialized = true;
                window.NeedsCatchUp = true;
            }
            if (cached?.Pinned is { } pinned) _pinned.TryAdd(chatId, pinned);
            changes.Chats.Add(chatId);
        }).ConfigureAwait(false);
    }
}
