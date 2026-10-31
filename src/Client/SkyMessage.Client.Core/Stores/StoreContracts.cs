using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

// Store rules (all stores are singletons):
// • State is published as immutable snapshots (records with init-only members, read-only lists). The UI
//   re-reads the latest snapshot on every render and never mutates it.
// • Each store serializes its mutations through one async queue, swaps the snapshot, then raises events.
//   Events may fire on any thread (components marshal with InvokeAsync(StateHasChanged)); each
//   subscriber is invoked in isolation (a throwing handler does not break others).
// • Merge rules: a MessageDto replaces the held one only if incoming.UpdatedAt >= held.UpdatedAt; chat
//   summaries merge by LastMessageSeq (never move backwards); realtime events received while a resync
//   snapshot is loading are buffered and replayed after it is applied. Events can arrive out of seq
//   order: fill gaps with afterSeq pages.
// • Progress notifications are throttled to ~5 Hz.
// • IUserStore is the single source of truth for people: every store upserts embedded UserSummaryDto
//   copies into it, and the UI renders people via IUserStore.Get(id) ?? embedded copy.
// • UI rule: when a store method exists, use it instead of calling ISkyApi directly.

public interface IChatListStore
{
    /// <summary>Non-archived chats: pinned first (in my pin order, PinOrder desc), then by LastActivityAt desc. Hidden chats excluded.</summary>
    IReadOnlyList<ChatSummaryDto> Chats { get; }
    IReadOnlyList<ChatSummaryDto> ArchivedChats { get; }
    bool IsLoaded { get; }
    bool IsLoading { get; }
    /// <summary>Unread messages in non-muted chats (also pushed to the app badge).</summary>
    int TotalUnread { get; }
    /// <summary>
    /// Chat currently on screen. ChatPage sets it on open and clears it on dispose. It suppresses
    /// notifications (while the app is in foreground) and lets the message store run a catch-up the chat still
    /// waits for (resync while it was covered); Core never marks messages read by itself. The message store keeps
    /// the window of this chat whole; a window off screen for minutes is cut back to its newest messages.
    /// </summary>
    Guid? ActiveChatId { get; set; }
    event Action? Changed;
    /// <summary>Raised when <see cref="ActiveChatId"/> takes a different value (any thread).</summary>
    event Action? ActiveChatChanged;

    Task LoadAsync(bool force = false, CancellationToken cancellationToken = default);
    ChatSummaryDto? Get(Guid chatId);
    Task<ChatSummaryDto?> GetOrFetchAsync(Guid chatId, CancellationToken cancellationToken = default);
    /// <summary>Private chat title/avatar derived from IUserStore (contact names, privacy) — use this, not Title.</summary>
    string TitleOf(ChatSummaryDto chat);
    Guid? AvatarOf(ChatSummaryDto chat);
    Task<ChatSummaryDto> OpenPrivateChatAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<ChatSummaryDto> GetSavedMessagesAsync(CancellationToken cancellationToken = default);
    Task<CreateGroupResultDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default);
    Task<ChatSummaryDto> JoinByInviteAsync(string token, CancellationToken cancellationToken = default);
    /// <summary>A new pin goes on top of my other pins.</summary>
    Task SetPinnedAsync(Guid chatId, bool pinned);
    /// <summary>
    /// Rearranges my pinned chats (top first; usually the pinned chats of one list): applied at once, stored on the
    /// server and synced to my other devices (PinnedChatsReordered). Put back and rethrown when the server refuses.
    /// </summary>
    Task ReorderPinnedAsync(IReadOnlyList<Guid> chatIdsTopFirst);
    /// <summary>Closes the "add to contacts / block" bar of a private chat (on all my devices).</summary>
    Task SetContactBarDismissedAsync(Guid chatId);
    /// <summary>
    /// Content protection on/off (<see cref="ContentProtectionRules"/>): groups for everyone (admins), private chats my
    /// own part. Optimistic; put back and rethrown when the server refuses.
    /// </summary>
    Task SetContentProtectionAsync(Guid chatId, bool enabled);
    Task SetArchivedAsync(Guid chatId, bool archived);
    /// <summary>0 = unmute, -1 = forever, &gt;0 seconds.</summary>
    Task SetMutedAsync(Guid chatId, int muteForSeconds);
    Task LeaveAsync(Guid chatId);
    Task DeleteAsync(Guid chatId, bool forEveryone);
    Task ClearHistoryAsync(Guid chatId, bool forEveryone);
    /// <summary>
    /// Called by the chat screen with the highest *confirmed* seq visible on screen while the app is in
    /// foreground. Debounced; updates unread/mention counters locally and cancels the chat's notifications.
    /// </summary>
    Task MarkReadAsync(Guid chatId, long upToSeq);
}

public enum MessageSendState
{
    Sent,
    /// <summary>Compressing media before upload.</summary>
    Preparing,
    Uploading,
    Sending,
    /// <summary>Permanent failure (4xx that retrying cannot fix); user can retry or delete.</summary>
    Failed,
    /// <summary>Waiting for network; resent automatically from the persisted outbox.</summary>
    Queued,
}

/// <summary>Local preview of a pending attachment (shown until the server copy arrives).</summary>
public sealed record LocalAttachmentPreview(
    MediaKind Kind,
    string FileName,
    long Size,
    string? PreviewUrl,
    int? Width,
    int? Height,
    int? DurationMs,
    byte[]? Waveform);

/// <summary>
/// A message on screen: either confirmed by the server or a pending local copy. Pending copies have
/// Message.Id = ClientMessageId, Seq = 0, SenderId = me, CreatedAt = local time, Type inferred like the
/// server does, ReplyTo built locally, Attachments empty (render <see cref="LocalPreviews"/>).
/// Pending items sort after confirmed ones in send order and never count for read tracking.
/// </summary>
public sealed record MessageItem
{
    public required MessageDto Message { get; init; }
    /// <summary>Stable render key: ClientMessageId for messages sent from this device, otherwise Message.Id.</summary>
    public required Guid Key { get; init; }
    public MessageSendState SendState { get; init; } = MessageSendState.Sent;
    /// <summary>0..1 while Preparing/Uploading.</summary>
    public double Progress { get; init; }
    /// <summary>Error code when Failed.</summary>
    public string? Error { get; init; }
    public IReadOnlyList<LocalAttachmentPreview> LocalPreviews { get; init; } = [];
    public bool IsPending => SendState != MessageSendState.Sent;
}

public sealed record ChatMessagesState
{
    public required Guid ChatId { get; init; }
    /// <summary>Ascending by seq; pending messages are at the end in send order.</summary>
    public IReadOnlyList<MessageItem> Items { get; init; } = [];
    public bool IsInitialized { get; init; }
    public bool HasMoreBefore { get; init; }
    /// <summary>True when the window is not at the newest message (after a jump).</summary>
    public bool HasMoreAfter { get; init; }
    public bool IsLoadingBefore { get; init; }
    public bool IsLoadingAfter { get; init; }
    /// <summary>Recomputed from MyLastReadSeq on every OpenAsync (for the "Unread messages" divider).</summary>
    public long? FirstUnreadSeq { get; init; }
    /// <summary>My reaction per message (reaction user lists are capped server-side).</summary>
    public IReadOnlyDictionary<Guid, string> MyReactions { get; init; } = new Dictionary<Guid, string>();

    public static ChatMessagesState Empty(Guid chatId) => new() { ChatId = chatId };
}

/// <summary>Something to send. For voice/video notes the recorder supplies duration (and waveform).</summary>
public sealed class OutgoingAttachment
{
    public required MediaKind Kind { get; init; }
    public required PickedFile Source { get; init; }
    /// <summary>
    /// False = "send without compression": photos/videos are uploaded as <see cref="MediaKind.File"/> with
    /// the original bytes (no client or server re-encoding).
    /// </summary>
    public bool Compress { get; init; } = true;
    public int? DurationMs { get; init; }
    public byte[]? Waveform { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    /// <summary>Optional data: URL for an instant preview.</summary>
    public string? PreviewUrl { get; init; }
}

/// <summary>
/// Message windows per chat plus sending. Send methods return as soon as the pending item exists (and is
/// persisted) and never throw network/server errors (they surface in the item's SendState). Sending while
/// viewing an older window then jumps to the latest in the background before the item goes out. The outbox
/// is persisted per account and resent (same ClientMessageId) on start, on Resynced and when the network returns.
/// </summary>
public interface IMessageStore
{
    /// <summary>Raised with the chat id whose <see cref="ChatMessagesState"/> changed.</summary>
    event Action<Guid>? ChatChanged;
    /// <summary>Scroll to and highlight (chatId, seq) once — raised by JumpToAsync after the window loaded.</summary>
    event Action<Guid, long>? JumpRequested;

    ChatMessagesState GetState(Guid chatId);
    /// <summary>Loads around the first unread message (or the latest) and recomputes FirstUnreadSeq.</summary>
    Task OpenAsync(Guid chatId, CancellationToken cancellationToken = default);
    Task LoadOlderAsync(Guid chatId, CancellationToken cancellationToken = default);
    Task LoadNewerAsync(Guid chatId, CancellationToken cancellationToken = default);
    Task JumpToAsync(Guid chatId, long seq, CancellationToken cancellationToken = default);
    Task JumpToLatestAsync(Guid chatId, CancellationToken cancellationToken = default);

    Task SendTextAsync(Guid chatId, string text, Guid? replyToMessageId = null, IReadOnlyList<MessageEntityDto>? entities = null);
    /// <summary>
    /// Photos/videos become one album message (≤10 per message), files one message with up to 10 files,
    /// voice/video notes one message each. Probe (instant preview) → compression → chunked upload → send.
    /// </summary>
    Task SendAttachmentsAsync(Guid chatId, IReadOnlyList<OutgoingAttachment> attachments, string? caption = null, Guid? replyToMessageId = null);
    Task RetryAsync(Guid chatId, Guid key);
    /// <summary>Cancels an upload in progress / removes a pending or failed item.</summary>
    Task CancelAsync(Guid chatId, Guid key);
    Task EditAsync(Guid chatId, Guid messageId, string text, IReadOnlyList<MessageEntityDto>? entities = null);
    Task DeleteAsync(Guid chatId, IReadOnlyList<Guid> messageIds, bool forEveryone);
    Task ForwardAsync(Guid fromChatId, IReadOnlyList<Guid> messageIds, Guid toChatId);
    /// <summary>Null removes my reaction.</summary>
    Task SetReactionAsync(Guid chatId, Guid messageId, string? emoji);
    Task SetPinnedAsync(Guid chatId, Guid messageId, bool pinned);
    /// <summary>Pinned messages of a chat (newest first), cached and refreshed on PinnedMessagesChanged.</summary>
    IReadOnlyList<MessageDto> GetPinned(Guid chatId);
    Task LoadPinnedAsync(Guid chatId, CancellationToken cancellationToken = default);
    /// <summary>Seqs of my unread mentions in the chat (for "jump to next mention").</summary>
    Task<IReadOnlyList<long>> GetUnreadMentionsAsync(Guid chatId, CancellationToken cancellationToken = default);
    Task<MessagesPageDto> SearchAsync(Guid chatId, string query, CancellationToken cancellationToken = default);
    MessageItem? Find(Guid chatId, Guid messageId);
}

public interface IUserStore
{
    event Action<Guid>? UserChanged;
    UserSummaryDto? Get(Guid userId);
    /// <summary>Unknown ids are collected for ~50 ms and fetched with one POST api/users/batch (no duplicate in-flight requests).</summary>
    Task<UserSummaryDto?> GetOrFetchAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<UserProfileDto> GetProfileAsync(Guid userId, bool refresh = false, CancellationToken cancellationToken = default);
    Task<UserProfileDto?> GetProfileByUsernameAsync(string username, CancellationToken cancellationToken = default);
    void Upsert(IEnumerable<UserSummaryDto> users);
    /// <summary>ContactName ?? DisplayName, or a placeholder while unknown (and triggers a batched fetch).</summary>
    string DisplayNameOf(Guid userId);
}

/// <summary>A phone-book match. Only matches the user confirms are imported; the list shows @username and avatar.</summary>
public sealed record PhoneBookMatch(string DeviceContactName, string PhoneNumber, UserSummaryDto User, bool AlreadyContact);

public interface IContactStore
{
    /// <summary>Sorted by display name.</summary>
    IReadOnlyList<ContactDto> Contacts { get; }
    IReadOnlyList<UserSummaryDto> Blocked { get; }
    bool IsLoaded { get; }
    event Action? Changed;

    Task LoadAsync(bool force = false, CancellationToken cancellationToken = default);
    bool IsContact(Guid userId);
    bool IsBlocked(Guid userId);
    Task AddAsync(Guid userId, string? customName = null);
    Task RenameAsync(Guid userId, string? customName);
    Task RemoveAsync(Guid userId);
    Task BlockAsync(Guid userId);
    Task UnblockAsync(Guid userId);
    /// <summary>Reads the phone book (asks permission) and returns registered users found by number (DefaultRegion from IPlatformInfo.CountryCode).</summary>
    Task<IReadOnlyList<PhoneBookMatch>> FindDeviceContactsAsync(CancellationToken cancellationToken = default);
    Task ImportAsync(IReadOnlyList<PhoneBookMatch> confirmedMatches);
}

public interface ITypingStore
{
    /// <summary>Raised with a chat id when its typing set changes.</summary>
    event Action<Guid>? Changed;
    /// <summary>Other users currently typing/recording in the chat (expired after <see cref="Limits.TypingTimeoutSeconds"/>).</summary>
    IReadOnlyList<TypingEvent> Get(Guid chatId);
    /// <summary>Throttled (≤ 1 per 3 s per action); Cancel is sent immediately.</summary>
    void NotifyLocalActivity(Guid chatId, TypingAction action);
}

public sealed record DraftDto(string Text, IReadOnlyList<MessageEntityDto>? Entities, Guid? ReplyToMessageId, DateTimeOffset UpdatedAt);

/// <summary>Per-chat composer drafts, persisted per account (survive app restarts; shown in the chat list).</summary>
public interface IDraftStore
{
    event Action<Guid>? Changed;
    DraftDto? Get(Guid chatId);
    /// <summary>Null or empty text with no reply clears the draft. Persisting is debounced.</summary>
    void Set(Guid chatId, DraftDto? draft);
}

/// <summary>
/// Small per-account local data (recent emoji, recent searches, played voice/video notes...). Namespaced by
/// user id and wiped on sign-out. Keys are free-form strings owned by the feature using them.
/// </summary>
public interface IAccountLocalStore
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value);
    Task RemoveAsync(string key);
}

public enum CallPhase
{
    Idle,
    OutgoingRinging,
    IncomingRinging,
    Connecting,
    Active,
    Reconnecting,
    Ended,
}

public enum CallMediaState
{
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
    Failed,
}

/// <summary>
/// Remote participant as reported by the media engine (LiveKit identity = "{userId:N}.{sessionId:N}"). HasVideo: their
/// camera; HasScreen: a screen they share.
/// </summary>
public sealed record CallParticipantState(Guid UserId, bool IsSpeaking, bool HasVideo, bool IsMuted, int ConnectionQuality, bool HasScreen = false);

public sealed record CallSession
{
    public required CallDto Call { get; init; }
    public CallPhase Phase { get; init; }
    public bool IsOutgoing { get; init; }
    /// <summary>The user started/accepted with camera on.</summary>
    public bool StartWithVideo { get; init; }
    /// <summary>Set once the UI should connect its media engine (LiveKit) to the room.</summary>
    public CallJoinInfoDto? JoinInfo { get; init; }
    /// <summary>JoinInfo.LiveKitUrl resolved to an absolute ws(s) URL.</summary>
    public string? LiveKitUrl { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    /// <summary>Peer name or group title.</summary>
    public string Title { get; init; } = "";
    public Guid? PeerUserId { get; init; }
    public Guid? AvatarMediaId { get; init; }
    /// <summary>Final status after the call ended ("Call ended / Declined / Busy / Missed").</summary>
    public CallStatus? EndStatus { get; init; }

    // Local and remote media state, reported by the UI's media engine.
    public bool IsMicMuted { get; init; }
    public bool IsCameraOn { get; init; }
    public bool IsScreenSharing { get; init; }
    public AudioRouteKind? AudioRoute { get; init; }
    public IReadOnlyList<CallParticipantState> Participants { get; init; } = [];
}

/// <summary>
/// Call signaling and state. Media (LiveKit JS in CallOverlay) reports back via Report* and executes
/// <see cref="CommandRequested"/> (from notification buttons, headset, ICallPlatformService).
/// StartAsync: call_busy → Current with Phase Ended / EndStatus Busy; other errors throw ApiException.
/// Incoming is cleared when my participant stops ringing on another device (accepted/declined there).
/// A second incoming call while in a call is declined as busy.
/// </summary>
public interface ICallService
{
    /// <summary>The call I'm in or dialing.</summary>
    CallSession? Current { get; }
    /// <summary>Ringing incoming call.</summary>
    CallSession? Incoming { get; }
    event Action? Changed;
    event Action<CallCommand>? CommandRequested;

    Task StartAsync(Guid chatId, bool video);
    Task AcceptAsync(bool withVideo);
    Task DeclineAsync();
    Task HangupAsync();
    /// <summary>Join a group call, or re-join an unfinished private call after an app/WebView restart.</summary>
    Task JoinAsync(Guid callId, bool video);
    void ReportMediaState(CallMediaState state);
    void ReportLocalMedia(bool micMuted, bool cameraOn, bool screenSharing, AudioRouteKind? audioRoute);
    void ReportParticipants(IReadOnlyList<CallParticipantState> participants);
    void RequestCommand(CallCommand command);
    Task<IReadOnlyList<CallHistoryItemDto>> GetHistoryAsync(DateTimeOffset? before = null, CancellationToken cancellationToken = default);
}
