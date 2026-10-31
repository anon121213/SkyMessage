using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// The chat list (SPEC §8.1 Stores, StoreContracts rules). Starts instantly from the per-account cache, then
/// refreshes from the server. Mutations run through one queue; realtime events received while a snapshot is
/// loading are buffered and replayed after it is applied. Summaries never move backwards (LastMessageSeq).
/// Pushes <see cref="TotalUnread"/> to the app badge and sends read marks debounced.
/// </summary>
internal sealed class ChatListStore : IChatListStore, IAccountScoped
{
    private const string CacheName = "chats";
    private static readonly TimeSpan PersistDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MarkReadDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RefetchDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PeerChangeDelay = TimeSpan.FromMilliseconds(150);

    private readonly ISkyApi _api;
    private readonly IRealtimeService _realtime;
    private readonly IAuthService _auth;
    private readonly IUserStore _users;
    private readonly ILocalNotificationService _notifications;
    private readonly LocalCache _cache;
    private readonly AccountLifecycle _lifecycle;
    private readonly ISettingsService _settings;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<ChatListStore> _logger;
    private readonly SerialQueue _queue = new();
    private readonly CoalescingAction _persist;
    private readonly CoalescingAction _sendReads;
    private readonly CoalescingAction _refetch;
    private readonly CoalescingAction _peerChanged;

    // Guarded by _queue.
    private Dictionary<Guid, ChatSummaryDto> _chats = [];
    private readonly List<Func<bool>> _buffer = [];
    private int _loadDepth;

    // Guarded by _lock.
    private readonly object _lock = new();
    private readonly Dictionary<Guid, long> _unsentReads = [];
    private readonly HashSet<Guid> _refetchIds = [];
    private Task? _load;
    private bool _reloadAfterLoad;

    private volatile IReadOnlyList<ChatSummaryDto> _list = [];
    private volatile IReadOnlyList<ChatSummaryDto> _archived = [];
    private volatile bool _isLoaded;
    private volatile bool _isLoading;
    private volatile bool _loadedFromServer;
    private int _totalUnread;
    private int _badge = -1;
    private Guid? _activeChatId;
    private volatile int _generation;
    private IMessagePreviewFormatter? _formatter;
    private bool _formatterResolved;

    public ChatListStore(
        ISkyApi api,
        IRealtimeService realtime,
        IAuthService auth,
        IUserStore users,
        ILocalNotificationService notifications,
        LocalCache cache,
        AccountLifecycle lifecycle,
        ISettingsService settings,
        IServiceProvider services,
        TimeProvider time,
        ILogger<ChatListStore> logger)
    {
        _api = api;
        _realtime = realtime;
        _auth = auth;
        _users = users;
        _notifications = notifications;
        _cache = cache;
        _lifecycle = lifecycle;
        _settings = settings;
        _services = services;
        _time = time;
        _logger = logger;
        _persist = new CoalescingAction(PersistDelay, PersistAsync, time, logger);
        _sendReads = new CoalescingAction(MarkReadDelay, SendReadsAsync, time, logger);
        _refetch = new CoalescingAction(RefetchDelay, RefetchAsync, time, logger);
        _peerChanged = new CoalescingAction(PeerChangeDelay, _ =>
        {
            EventRaiser.Raise(Changed, _logger);
            return Task.CompletedTask;
        }, time, logger);

        realtime.MessageCreated += m => Apply(() => OnMessageCreated(m));
        realtime.MessageUpdated += m => Apply(() => OnMessageUpdated(m));
        realtime.MessagesDeleted += e => ScheduleRefetch(e.ChatId);
        realtime.MessagesRead += e => Apply(() => OnMessagesRead(e));
        realtime.ChatAdded += e => FetchAndUpsertAsync(e.ChatId).Forget(_logger);
        realtime.ChatUpdated += info => Apply(() => OnChatUpdated(info));
        realtime.ChatRemoved += e => Apply(() => _chats.Remove(e.ChatId));
        realtime.ChatMembersChanged += e => ScheduleRefetch(e.ChatId);
        realtime.ChatSettingsChanged += e => Apply(() => Update(e.ChatId, c => ChatRules.WithSettings(c, e.Settings)));
        realtime.HistoryCleared += e => Apply(() => OnHistoryCleared(e.ChatId, e.UpToSeq));
        realtime.Resynced += OnResynced;
        users.UserChanged += OnUserChanged;
        lifecycle.Register(this);
    }

    public IReadOnlyList<ChatSummaryDto> Chats => _list;

    public IReadOnlyList<ChatSummaryDto> ArchivedChats => _archived;

    public bool IsLoaded => _isLoaded;

    public bool IsLoading => _isLoading;

    public int TotalUnread => Volatile.Read(ref _totalUnread);

    public Guid? ActiveChatId
    {
        get
        {
            lock (_lock) return _activeChatId;
        }
        set
        {
            bool changed;
            lock (_lock)
            {
                changed = _activeChatId != value;
                _activeChatId = value;
            }
            if (value is { } chatId) _notifications.CancelForChatAsync(chatId).Forget(_logger);
            if (changed) EventRaiser.Raise(ActiveChatChanged, _logger);
        }
    }

    public event Action? Changed;

    public event Action? ActiveChatChanged;

    public Task LoadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && _loadedFromServer) return Task.CompletedTask;
        Task load;
        lock (_lock)
        {
            if (_load is { IsCompleted: false } running)
            {
                if (force) _reloadAfterLoad = true;
                load = running;
            }
            else
            {
                load = _load = LoadCoreAsync(_generation);
            }
        }
        return load.WaitAsync(cancellationToken);
    }

    private async Task LoadCoreAsync(int generation)
    {
        await Task.Yield();
        while (true)
        {
            lock (_lock) _reloadAfterLoad = false;
            using var sync = _realtime.BeginSync();
            await _queue.RunAsync(() =>
            {
                _loadDepth++;
                _isLoading = true;
            }).ConfigureAwait(false);
            RaiseChanged();

            IReadOnlyList<ChatSummaryDto>? fresh = null;
            try
            {
                fresh = await _api.GetChatsAsync(_lifecycle.AccountToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException)
            {
                _logger.LogInformation("Chat list refresh failed ({Error})", (ex as ApiException)?.Code ?? "cancelled");
            }
            catch (Exception ex)
            {
                // Anything else (e.g. an HTTP stack that cannot be built from a malformed proxy) must not leave the
                // load depth raised: every later event would be buffered forever.
                _logger.LogWarning(ex, "Chat list refresh failed");
            }

            var stale = generation != _generation;
            await MutateAsync(() =>
            {
                _loadDepth--;
                if (!stale && fresh is not null) ApplySnapshot(fresh);
                if (_loadDepth == 0)
                {
                    _isLoading = false;
                    var replay = _buffer.ToList();
                    _buffer.Clear();
                    if (!stale) foreach (var mutation in replay) mutation();
                }
                return true;
            }).ConfigureAwait(false);

            if (stale) return;
            if (fresh is not null)
            {
                _loadedFromServer = true;
                _users.Upsert(fresh.SelectMany(Embedded));
                RetryUnsentReads();
            }

            lock (_lock)
            {
                if (!_reloadAfterLoad) return;
            }
        }
    }

    public ChatSummaryDto? Get(Guid chatId)
    {
        foreach (var chat in _list) if (chat.Id == chatId) return chat;
        foreach (var chat in _archived) if (chat.Id == chatId) return chat;
        return null;
    }

    public async Task<ChatSummaryDto?> GetOrFetchAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        if (Get(chatId) is { } known) return known;
        try
        {
            var chat = await _api.GetChatAsync(chatId, cancellationToken).ConfigureAwait(false);
            if (chat is null) return null;
            await UpsertAsync(chat).ConfigureAwait(false);
            return Get(chatId) ?? chat;
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 403)
        {
            return null;
        }
    }

    public string TitleOf(ChatSummaryDto chat)
    {
        if (Formatter() is { } formatter)
        {
            try
            {
                return formatter.FormatChatTitle(chat);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Preview formatter failed");
            }
        }

        switch (chat.Type)
        {
            case ChatType.Saved:
                return CoreStrings.SavedMessages(CoreStrings.IsRussian(_settings));
            case ChatType.Private when chat.Peer is { } embedded:
                var peer = _users.Get(embedded.Id) ?? embedded;
                var name = peer.ContactName ?? peer.DisplayName;
                return string.IsNullOrWhiteSpace(name) ? chat.Title : name;
            default:
                return chat.Title;
        }
    }

    public Guid? AvatarOf(ChatSummaryDto chat) => chat.Type switch
    {
        ChatType.Private when chat.Peer is { } peer => (_users.Get(peer.Id) ?? peer).AvatarMediaId,
        ChatType.Saved => null,
        _ => chat.AvatarMediaId,
    };

    public async Task<ChatSummaryDto> OpenPrivateChatAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var chat = await _api.CreatePrivateChatAsync(userId, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(chat).ConfigureAwait(false);
        return Get(chat.Id) ?? chat;
    }

    public async Task<ChatSummaryDto> GetSavedMessagesAsync(CancellationToken cancellationToken = default)
    {
        if (_auth.Me is { SavedMessagesChatId: var savedId } && savedId != Guid.Empty
            && await GetOrFetchAsync(savedId, cancellationToken).ConfigureAwait(false) is { } saved)
        {
            return saved;
        }
        var me = _auth.UserId ?? throw new ApiException(401, ErrorCodes.Unauthorized, "Not signed in");
        return await OpenPrivateChatAsync(me, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreateGroupResultDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _api.CreateGroupAsync(request, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(result.Chat).ConfigureAwait(false);
        return result;
    }

    public async Task<ChatSummaryDto> JoinByInviteAsync(string token, CancellationToken cancellationToken = default)
    {
        var chat = await _api.JoinByInviteAsync(token, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(chat).ConfigureAwait(false);
        return Get(chat.Id) ?? chat;
    }

    public Task SetPinnedAsync(Guid chatId, bool pinned) =>
        UpdateSettingsAsync(chatId, new UpdateChatSettingsRequest { IsPinned = pinned },
            c => c with { IsPinned = pinned, PinnedAt = pinned ? c.PinnedAt ?? _time.GetUtcNow() : null });

    public Task SetArchivedAsync(Guid chatId, bool archived) =>
        UpdateSettingsAsync(chatId, new UpdateChatSettingsRequest { IsArchived = archived }, c => c with { IsArchived = archived });

    public Task SetMutedAsync(Guid chatId, int muteForSeconds) =>
        UpdateSettingsAsync(chatId, new UpdateChatSettingsRequest { MuteForSeconds = muteForSeconds }, c => c with
        {
            MutedUntil = muteForSeconds switch
            {
                0 => null,
                < 0 => new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero),
                _ => _time.GetUtcNow().AddSeconds(muteForSeconds),
            },
        });

    public async Task LeaveAsync(Guid chatId)
    {
        await _api.LeaveChatAsync(chatId).ConfigureAwait(false);
        await MutateAsync(() => _chats.Remove(chatId)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid chatId, bool forEveryone)
    {
        await _api.DeleteChatAsync(chatId, forEveryone).ConfigureAwait(false);
        await MutateAsync(() => _chats.Remove(chatId)).ConfigureAwait(false);
        _notifications.CancelForChatAsync(chatId).Forget(_logger);
    }

    public async Task ClearHistoryAsync(Guid chatId, bool forEveryone)
    {
        await _api.ClearHistoryAsync(chatId, forEveryone).ConfigureAwait(false);
        await MutateAsync(() => _chats.TryGetValue(chatId, out var chat) && OnHistoryCleared(chatId, chat.LastMessageSeq)).ConfigureAwait(false);
        _notifications.CancelForChatAsync(chatId).Forget(_logger);
    }

    public async Task MarkReadAsync(Guid chatId, long upToSeq)
    {
        if (upToSeq <= 0) return;
        // Not listed yet (deep link before the first load): the server still gets the mark.
        if (Get(chatId) is { } listed && upToSeq <= listed.MyLastReadSeq) return;
        // Buffered like events while a snapshot loads, so the (older) snapshot cannot undo the local mark.
        await MutateAsync(() =>
        {
            bool ApplyRead()
            {
                if (!_chats.TryGetValue(chatId, out var chat) || upToSeq <= chat.MyLastReadSeq) return false;
                // Refetched once the server has the mark (SendReadsAsync): a copy fetched earlier still has the old one.
                _chats[chatId] = WithReadUpTo(chat, upToSeq, refetch: false);
                return true;
            }

            if (_loadDepth > 0)
            {
                _buffer.Add(ApplyRead);
                return false;
            }
            return ApplyRead();
        }).ConfigureAwait(false);

        lock (_lock)
        {
            _unsentReads[chatId] = Math.Max(upToSeq, _unsentReads.GetValueOrDefault(chatId));
        }
        _sendReads.Request();
        _notifications.CancelForChatAsync(chatId).Forget(_logger);
    }

    async Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        var generation = _generation;
        var cached = await _cache.ReadAsync<List<ChatSummaryDto>>(LocalCache.AccountKey(userId, CacheName)).ConfigureAwait(false);
        if (cached is null || cached.Count == 0 || generation != _generation) return;
        _users.Upsert(cached.SelectMany(Embedded));
        await MutateAsync(() =>
        {
            if (generation != _generation || _chats.Count > 0) return false;
            foreach (var chat in cached)
                if (chat is not null && chat.Id != Guid.Empty) _chats[chat.Id] = chat;
            _isLoaded = true;
            return true;
        }).ConfigureAwait(false);
    }

    async Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        Interlocked.Increment(ref _generation);
        _persist.Cancel();
        _sendReads.Cancel();
        _refetch.Cancel();
        lock (_lock)
        {
            _unsentReads.Clear();
            _refetchIds.Clear();
            _activeChatId = null;
        }
        _loadedFromServer = false;
        await MutateAsync(() =>
        {
            _chats = [];
            _buffer.Clear();
            _isLoaded = false;
            _isLoading = false;
            return true;
        }).ConfigureAwait(false);
        Interlocked.Exchange(ref _badge, -1);
        await _notifications.SetBadgeCountAsync(0).ConfigureAwait(false);
    }

    // Event handlers (run inside the queue, buffered during snapshot loads)

    private bool OnMessageCreated(MessageDto message)
    {
        if (!_chats.TryGetValue(message.ChatId, out var chat))
        {
            // A chat we did not know (first message of a new private chat, unhidden chat).
            FetchAndUpsertAsync(message.ChatId).Forget(_logger);
            return false;
        }
        if (message.Seq <= chat.LastMessageSeq)
        {
            if (chat.LastMessage?.Id == message.Id && message.UpdatedAt >= chat.LastMessage.UpdatedAt)
            {
                _chats[chat.Id] = chat with { LastMessage = message };
                return true;
            }
            // Two sends committed close together can be published out of order: an unread message older than the
            // newest one may be missing from the counters (unread, mentions). The server copy has both.
            if (message.Seq < chat.LastMessageSeq && message.Seq > Math.Max(chat.MyLastReadSeq, chat.ClearedUpToSeq) && !message.IsDeleted)
                ScheduleRefetch(chat.Id);
            return false;
        }

        var me = _auth.UserId;
        var mine = MessageRules.IsMine(message, me);
        var next = chat with
        {
            LastMessage = message,
            LastMessageSeq = message.Seq,
            LastMessageSender = message.SenderId is { } sender ? _users.Get(sender) ?? (chat.LastMessageSender?.Id == sender ? chat.LastMessageSender : null) : null,
            LastActivityAt = message.CreatedAt > chat.LastActivityAt ? message.CreatedAt : chat.LastActivityAt,
        };

        if (mine)
        {
            // Sending (or acting, for service messages) reads the chat up to the new message (server: LastReadSeq = seq).
            next = WithReadUpTo(next, message.Seq);
        }
        else if (message.Seq > Math.Max(chat.MyLastReadSeq, chat.ClearedUpToSeq) && !message.IsDeleted)
        {
            next = next with { UnreadCount = chat.UnreadCount + 1 };
            if (me is { } myId && MessageRules.MentionsMe(message, myId, _auth.Me?.Username))
            {
                next = next with
                {
                    UnreadMentionCount = chat.UnreadMentionCount + 1,
                    FirstUnreadMentionSeq = chat.FirstUnreadMentionSeq ?? message.Seq,
                };
            }
        }
        _chats[chat.Id] = next;
        return true;
    }

    private bool OnMessageUpdated(MessageDto message)
    {
        if (!_chats.TryGetValue(message.ChatId, out var chat) || chat.LastMessage?.Id != message.Id) return false;
        if (message.UpdatedAt < chat.LastMessage.UpdatedAt) return false;
        if (message.IsDeleted) ScheduleRefetch(chat.Id);
        _chats[chat.Id] = chat with { LastMessage = message };
        return true;
    }

    private bool OnMessagesRead(MessagesReadEvent e)
    {
        if (!_chats.TryGetValue(e.ChatId, out var chat)) return false;
        if (_auth.UserId is { } me && e.UserId == me)
        {
            if (e.UpToSeq <= chat.MyLastReadSeq) return false;
            _chats[chat.Id] = WithReadUpTo(chat, e.UpToSeq);
            _notifications.CancelForChatAsync(chat.Id).Forget(_logger);
            return true;
        }
        if (e.UpToSeq <= chat.OthersLastReadSeq) return false;
        _chats[chat.Id] = chat with { OthersLastReadSeq = e.UpToSeq };
        return true;
    }

    private bool OnChatUpdated(ChatInfoDto info) => Update(info.Id, chat => chat.Type == ChatType.Group
        ? chat with
        {
            Title = info.Title ?? chat.Title,
            AvatarMediaId = info.AvatarMediaId,
            MemberCount = info.MemberCount,
            Permissions = info.Permissions,
            ActiveCallId = info.ActiveCallId,
        }
        : chat with { MemberCount = info.MemberCount, ActiveCallId = info.ActiveCallId });

    private bool OnHistoryCleared(Guid chatId, long upToSeq)
    {
        if (!_chats.TryGetValue(chatId, out var chat)) return false;
        var allCleared = upToSeq >= chat.LastMessageSeq;
        _chats[chatId] = chat with
        {
            ClearedUpToSeq = Math.Max(chat.ClearedUpToSeq, upToSeq),
            LastMessage = allCleared ? null : chat.LastMessage,
            LastMessageSender = allCleared ? null : chat.LastMessageSender,
            UnreadCount = allCleared ? 0 : chat.UnreadCount,
            UnreadMentionCount = allCleared ? 0 : chat.UnreadMentionCount,
            FirstUnreadMentionSeq = allCleared ? null : chat.FirstUnreadMentionSeq,
        };
        if (!allCleared) ScheduleRefetch(chatId);
        return true;
    }

    private bool Update(Guid chatId, Func<ChatSummaryDto, ChatSummaryDto> change)
    {
        if (!_chats.TryGetValue(chatId, out var chat)) return false;
        var next = change(chat);
        if (next == chat) return false;
        _chats[chatId] = next;
        return true;
    }

    /// <summary>
    /// Read up to <paramref name="upToSeq"/>: exact when everything is read, an upper bound otherwise, corrected by a
    /// refetch (unless <paramref name="refetch"/> is false: the caller refetches once the server has the mark).
    /// </summary>
    private ChatSummaryDto WithReadUpTo(ChatSummaryDto chat, long upToSeq, bool refetch = true)
    {
        if (refetch && upToSeq < chat.LastMessageSeq) ScheduleRefetch(chat.Id);
        return ReadUpTo(chat, upToSeq);
    }

    /// <summary>Counters after reading up to <paramref name="upToSeq"/>: exact when everything is read, an upper bound otherwise.</summary>
    private static ChatSummaryDto ReadUpTo(ChatSummaryDto chat, long upToSeq)
    {
        if (upToSeq >= chat.LastMessageSeq)
            return chat with { MyLastReadSeq = Math.Max(chat.MyLastReadSeq, upToSeq), UnreadCount = 0, UnreadMentionCount = 0, FirstUnreadMentionSeq = null };

        var remaining = (int)Math.Min(chat.UnreadCount, Math.Max(0, chat.LastMessageSeq - upToSeq));
        var mentionsCleared = chat.FirstUnreadMentionSeq is { } first && first <= upToSeq;
        return chat with
        {
            MyLastReadSeq = Math.Max(chat.MyLastReadSeq, upToSeq),
            UnreadCount = remaining,
            UnreadMentionCount = mentionsCleared ? Math.Min(chat.UnreadMentionCount, remaining) : chat.UnreadMentionCount,
        };
    }

    /// <summary>Applies a server list: merge per chat, never moving LastMessageSeq or my read mark backwards.</summary>
    private void ApplySnapshot(IReadOnlyList<ChatSummaryDto> fresh)
    {
        var next = new Dictionary<Guid, ChatSummaryDto>(fresh.Count);
        List<(Guid ChatId, long Seq)>? unsent = null;
        foreach (var incoming in fresh)
        {
            if (incoming is null || incoming.Id == Guid.Empty) continue;
            if (!_chats.TryGetValue(incoming.Id, out var held))
            {
                next[incoming.Id] = incoming;
                continue;
            }
            next[incoming.Id] = Merge(held, incoming);
            // A read the server never got: one in flight, or one whose sending ended with the app (read offline, the
            // mark kept only in the cached list). Sent (again) after this load; server marks only move forward.
            if (held.MyLastReadSeq > incoming.MyLastReadSeq) (unsent ??= []).Add((incoming.Id, held.MyLastReadSeq));
        }
        _chats = next;
        _isLoaded = true;
        if (unsent is null) return;
        lock (_lock)
        {
            foreach (var (chatId, seq) in unsent) _unsentReads[chatId] = Math.Max(seq, _unsentReads.GetValueOrDefault(chatId));
        }
    }

    /// <summary>
    /// Merges a server copy into the held one. LastMessageSeq never moves backwards, and neither does my read mark: the
    /// server's only moves forward too, so a held one ahead of it is a read of this device the server has not received
    /// yet (a copy fetched before the read was sent must not bring the counters back).
    /// </summary>
    internal static ChatSummaryDto Merge(ChatSummaryDto held, ChatSummaryDto incoming)
    {
        var merged = held.LastMessageSeq <= incoming.LastMessageSeq
            ? incoming
            : incoming with
            {
                LastMessage = held.LastMessage,
                LastMessageSeq = held.LastMessageSeq,
                LastMessageSender = held.LastMessageSender,
                LastActivityAt = held.LastActivityAt > incoming.LastActivityAt ? held.LastActivityAt : incoming.LastActivityAt,
                UnreadCount = Math.Max(held.UnreadCount, incoming.UnreadCount),
            };
        return held.MyLastReadSeq > merged.MyLastReadSeq ? ReadUpTo(merged, held.MyLastReadSeq) : merged;
    }

    // Plumbing

    private void Apply(Func<bool> mutation) => MutateAsync(() =>
    {
        if (_loadDepth > 0)
        {
            _buffer.Add(mutation);
            return false;
        }
        return mutation();
    }).Forget(_logger);

    private async Task MutateAsync(Func<bool> mutation)
    {
        var changed = await _queue.RunAsync(() =>
        {
            var c = mutation();
            if (c) Publish();
            return c;
        }).ConfigureAwait(false);
        if (!changed) return;
        RaiseChanged();
        _persist.Request();
        UpdateBadge();
    }

    private Task UpsertAsync(ChatSummaryDto chat)
    {
        _users.Upsert(Embedded(chat));
        return MutateAsync(() =>
        {
            if (_loadDepth > 0)
            {
                _buffer.Add(() => UpsertCore(chat));
                return false;
            }
            return UpsertCore(chat);
        });
    }

    private bool UpsertCore(ChatSummaryDto chat)
    {
        var next = _chats.TryGetValue(chat.Id, out var held) ? Merge(held, chat) : chat;
        if (held == next) return false;
        _chats[chat.Id] = next;
        return true;
    }

    private void Publish()
    {
        var now = _time.GetUtcNow();
        var all = _chats.Values;
        _list = ChatRules.Sort(all.Where(c => !c.IsArchived));
        _archived = ChatRules.Sort(all.Where(c => c.IsArchived));
        Volatile.Write(ref _totalUnread, all.Where(c => !ChatRules.IsMuted(c, now)).Sum(c => Math.Max(0, c.UnreadCount)));
    }

    private void UpdateBadge()
    {
        var total = TotalUnread;
        if (Interlocked.Exchange(ref _badge, total) == total) return;
        _notifications.SetBadgeCountAsync(total).Forget(_logger);
    }

    private async Task UpdateSettingsAsync(Guid chatId, UpdateChatSettingsRequest request, Func<ChatSummaryDto, ChatSummaryDto> optimistic)
    {
        ChatSummaryDto? before = null;
        await MutateAsync(() =>
        {
            if (!_chats.TryGetValue(chatId, out var chat)) return false;
            before = chat;
            _chats[chatId] = optimistic(chat);
            return true;
        }).ConfigureAwait(false);

        try
        {
            var settings = await _api.UpdateChatSettingsAsync(chatId, request).ConfigureAwait(false);
            await MutateAsync(() => Update(chatId, c => ChatRules.WithSettings(c, settings))).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            if (before is { } original)
            {
                await MutateAsync(() => Update(chatId, c => ChatRules.WithSettings(c, new ChatUserSettingsDto
                {
                    IsPinned = original.IsPinned,
                    PinnedAt = original.PinnedAt,
                    IsArchived = original.IsArchived,
                    MutedUntil = original.MutedUntil,
                }))).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task FetchAndUpsertAsync(Guid chatId)
    {
        var generation = _generation;
        try
        {
            var chat = await _api.GetChatAsync(chatId, _lifecycle.AccountToken).ConfigureAwait(false);
            if (chat is not null && generation == _generation) await UpsertAsync(chat).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 403)
        {
            if (generation == _generation) await MutateAsync(() => _chats.Remove(chatId)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            _logger.LogDebug("Chat {ChatId} could not be fetched", chatId);
        }
    }

    private void ScheduleRefetch(Guid chatId)
    {
        lock (_lock) _refetchIds.Add(chatId);
        _refetch.Request();
    }

    private async Task RefetchAsync(CancellationToken ct)
    {
        List<Guid> ids;
        lock (_lock)
        {
            ids = [.. _refetchIds];
            _refetchIds.Clear();
        }
        foreach (var chatId in ids)
        {
            if (ct.IsCancellationRequested) return;
            await FetchAndUpsertAsync(chatId).ConfigureAwait(false);
        }
    }

    private async Task SendReadsAsync(CancellationToken ct)
    {
        List<KeyValuePair<Guid, long>> reads;
        lock (_lock) reads = [.. _unsentReads];
        foreach (var (chatId, seq) in reads)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await _api.MarkReadAsync(chatId, seq, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_unsentReads.TryGetValue(chatId, out var pending) && pending <= seq) _unsentReads.Remove(chatId);
                }
                // A partial read left estimated counters: the server has the mark now, so its copy is exact.
                if (Get(chatId) is { } chat && seq < chat.LastMessageSeq) ScheduleRefetch(chatId);
            }
            catch (ApiException ex) when (!ex.IsTransient)
            {
                lock (_lock) _unsentReads.Remove(chatId);
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException)
            {
                // Kept for the next reconnect.
            }
        }
    }

    private void RetryUnsentReads()
    {
        bool any;
        lock (_lock) any = _unsentReads.Count > 0;
        if (any) _sendReads.Request();
    }

    private void OnResynced() => LoadAsync(force: true).Forget(_logger);

    private void OnUserChanged(Guid userId)
    {
        var list = _list;
        foreach (var chat in list)
        {
            if (chat.Peer?.Id == userId || chat.LastMessageSender?.Id == userId)
            {
                _peerChanged.Request();
                return;
            }
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_lifecycle.ActiveUserId is not { } userId) return;
        var chats = _list.Concat(_archived).ToList();
        await _cache.WriteAsync(LocalCache.AccountKey(userId, CacheName), chats, ct).ConfigureAwait(false);
    }

    private IMessagePreviewFormatter? Formatter()
    {
        if (_formatterResolved) return _formatter;
        try
        {
            _formatter = _services.GetService<IMessagePreviewFormatter>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No preview formatter available");
        }
        _formatterResolved = true;
        return _formatter;
    }

    private static IEnumerable<UserSummaryDto> Embedded(ChatSummaryDto chat)
    {
        if (chat.Peer is { } peer) yield return peer;
        if (chat.LastMessageSender is { } sender) yield return sender;
    }

    private void RaiseChanged() => EventRaiser.Raise(Changed, _logger);
}
