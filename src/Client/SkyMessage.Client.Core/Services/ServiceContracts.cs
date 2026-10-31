using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

// All Core services are singletons. Events may fire on any thread; Blazor components must marshal with
// InvokeAsync(StateHasChanged). Event invocation is isolated per subscriber (one throwing handler does
// not stop the others).

public static class ClientErrorCodes
{
    public const string Network = "network_error";
    public const string Timeout = "timeout";
    public const string Unknown = "unknown";
    /// <summary>Microphone/camera permission denied (ICallService start/accept/join).</summary>
    public const string PermissionDenied = "permission_denied";
}

/// <summary>Thrown by <see cref="ISkyApi"/> for non-success responses (StatusCode 0 = network failure).</summary>
public sealed class ApiException(int statusCode, string code, string? message = null, IDictionary<string, string[]>? errors = null)
    : Exception(message ?? code)
{
    public int StatusCode { get; } = statusCode;
    /// <summary>One of <see cref="ErrorCodes"/> or <see cref="ClientErrorCodes"/>.</summary>
    public string Code { get; } = code;
    public IDictionary<string, string[]>? Errors { get; } = errors;
    public bool IsNetworkError => StatusCode == 0;
    /// <summary>Worth retrying later (network, timeouts, 5xx, 429) as opposed to permanent 4xx failures.</summary>
    public bool IsTransient => StatusCode is 0 or 408 or 429 or >= 500;
}

public sealed record ApiResult(bool Success, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static ApiResult Ok { get; } = new(true);
    public static ApiResult Fail(ApiException ex) => new(false, ex.Code, ex.Message);
}

public enum AuthState
{
    Initializing,
    SignedOut,
    SignedIn,
}

/// <summary>
/// Session handling. Tokens live in ISecureStore ("auth.v1"). Refresh is single-flight within the process
/// and serialized across processes/tabs with <see cref="Platform.IInterProcessLock"/>; inside the lock the
/// stored tokens are re-read first (another tab may already have refreshed).
/// </summary>
public interface IAuthService
{
    AuthState State { get; }
    MeDto? Me { get; }
    Guid? UserId { get; }
    Guid? SessionId { get; }
    /// <summary>Per-session media signing key; see <see cref="AuthResponse.MediaKey"/>.</summary>
    string? MediaKey { get; }
    event Action? Changed;

    /// <summary>Restores the session from secure storage (no network required).</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<ApiResult> LoginAsync(string login, string password, CancellationToken cancellationToken = default);
    Task<ApiResult> RegisterAsync(string username, string password, string displayName, string? inviteCode = null, CancellationToken cancellationToken = default);
    Task<UsernameAvailabilityDto> CheckUsernameAsync(string username, CancellationToken cancellationToken = default);
    /// <summary>
    /// Clears all local state (stores, caches, per-account data). <paramref name="revokeOnServer"/> = false
    /// when the server already revoked the session (SessionRevoked, refresh_token_invalid).
    /// </summary>
    Task LogoutAsync(bool revokeOnServer = true);
    /// <summary>Valid access token, refreshing it if needed. Null when signed out.</summary>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    /// <summary>Call after profile/avatar/phone/privacy edits (and on UserChanged for me) so <see cref="Me"/> stays current.</summary>
    void UpdateMe(MeDto me);
    /// <summary>Refetches api/users/me.</summary>
    Task RefreshMeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Which server the client talks to, with fallbacks (resilience against blocked addresses). Listens to
/// ISettingsService.Changed for Connection.* edits (rebuilds HTTP handlers and reconnects realtime).
/// Candidates: user/custom address, user extras, build fallbacks and server-advertised
/// <see cref="ServerInfoDto.MirrorUrls"/> (remembered per InstanceId). Before any token is sent to a
/// non-primary candidate it must report the same InstanceId AND pass <see cref="ServerProofDto"/>
/// verification with the session's media key. Probes download ≥ 64 KB from api/server/probe with a minimum
/// throughput, and repeated stalls (no bytes for 15 s) on uploads/media/downloads also trigger failover —
/// some networks pass handshakes but freeze real transfers.
/// </summary>
public interface IServerEndpointService
{
    /// <summary>Active server base URI, always with a trailing slash.</summary>
    Uri BaseUri { get; }
    IReadOnlyList<Uri> Candidates { get; }
    /// <summary>Last server info read from <see cref="BaseUri"/> (null until first contact).</summary>
    ServerInfoDto? Info { get; }
    /// <summary>
    /// Deployment this client is bound to (remembered across restarts; known even while the server is unreachable
    /// and <see cref="Info"/> is null). <see cref="SetCustomServerAsync"/> signs out when the new server differs.
    /// </summary>
    Guid? KnownInstanceId { get; }
    event Action? Changed;
    /// <summary>
    /// Probes GET api/server/info on <paramref name="baseUri"/>, optionally through candidate connection
    /// settings (e.g. a proxy being edited) instead of the current ones.
    /// </summary>
    Task<ServerInfoDto?> TestAsync(Uri baseUri, ConnectionSettings? candidate = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Saves a user-entered server (null = back to default) and switches to it. When the new server is a
    /// different deployment (InstanceId differs) the user is signed out first.
    /// </summary>
    Task SetCustomServerAsync(string? url);
    /// <summary>Switches to the next reachable candidate of the same deployment. False if none answered.</summary>
    Task<bool> FailoverAsync(CancellationToken cancellationToken = default);
    Uri Resolve(string relativePath);
    /// <summary>"/livekit" → ws(s)://host/livekit (relative to BaseUri); absolute URLs are returned unchanged.</summary>
    string ResolveLiveKitUrl(string liveKitUrl);
}

/// <summary>
/// URLs usable directly in &lt;img&gt;/&lt;video&gt;/&lt;audio&gt;. Always build media URLs through this
/// service and re-render on <see cref="Changed"/> (server failover, sign-in). Two modes
/// (<see cref="ClientCoreOptions.MediaUrlMode"/>): Direct (browser: server URL + per-file signature) and
/// AppProxy (MAUI: app-origin "/_sm/media/{id}/..." served natively through <see cref="IMediaContentSource"/>,
/// so proxy/failover/caching apply to media too).
/// </summary>
public interface IMediaUrlBuilder
{
    /// <summary>Preferred: carries size/type so the app-origin proxy can answer Range requests without I/O.</summary>
    string ContentUrl(MediaDto media, bool download = false);
    /// <summary>When only the id is known (served as a plain 200 without Range support in AppProxy mode).</summary>
    string ContentUrl(Guid mediaId, bool download = false);
    string ThumbnailUrl(Guid mediaId);
    /// <summary>Thumbnail URL of an avatar media, null when there is no avatar.</summary>
    string? AvatarUrl(Guid? avatarMediaId);
    /// <summary>data:image/jpeg;base64,... of <see cref="MediaDto.MiniThumbnail"/>, null if absent.</summary>
    string? MiniThumbnailDataUrl(MediaDto media);
    event Action? Changed;
}

/// <summary>Status line + headers for an app-origin media response (ReasonPhrase is never empty: Android rejects it).</summary>
public sealed record MediaResponseHead(int StatusCode, string ReasonPhrase, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Serves app-origin media for the MAUI WebView. The host's WebResourceRequested handler is synchronous
/// and never awaits: it calls <c>SetResponse(head..., OpenBodyAsync(...))</c>. URLs from
/// <see cref="IMediaUrlBuilder.ContentUrl(MediaDto, bool)"/> embed size and content type
/// ("/_sm/media/{id}/content?s=123&amp;ct=video%2Fmp4"), so <see cref="GetHead"/> computes 200/206 and
/// Content-Range without any I/O. The body comes from <see cref="Platform.IMediaFileStore"/> or a ranged
/// server fetch (Authorization header, failover, stall detection).
/// </summary>
public interface IMediaContentSource
{
    /// <summary>Prefix of app-origin media paths, "/_sm/media/".</summary>
    string PathPrefix { get; }
    bool CanHandle(string appRelativePath);
    /// <summary>Synchronous, no I/O, never throws (404 head for unknown paths).</summary>
    MediaResponseHead GetHead(string appRelativePath, string? rangeHeader);
    /// <summary>Errors simply end the stream; uses ConfigureAwait(false) throughout.</summary>
    Task<Stream> OpenBodyAsync(string appRelativePath, string? rangeHeader, CancellationToken cancellationToken = default);
}

public sealed class MediaUploadRequest
{
    public required MediaKind Kind { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long Size { get; init; }
    public required Func<CancellationToken, Task<Stream>> OpenReadAsync { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationMs { get; init; }
    public byte[]? Waveform { get; init; }
}

/// <summary>Chunked, resumable upload; waits until the server reports the media Ready. Used for avatars too.</summary>
public interface IMediaUploader
{
    Task<MediaDto> UploadAsync(MediaUploadRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public enum MediaDownloadPhase
{
    NotDownloaded,
    Downloading,
    Downloaded,
    Failed,
}

public sealed record MediaDownloadState(MediaDownloadPhase Phase, double Progress = 0, string? LocalPath = null, string? Error = null);

/// <summary>
/// Downloads for files/"save media", auto-download policy (DataSettings × network type) and the media
/// cache (CacheLimitMb). Never open media content URLs outside the app.
/// </summary>
public interface IMediaDownloadService
{
    MediaDownloadState GetState(Guid mediaId);
    /// <summary>Raised with the media id whose state changed (progress throttled to ~5 Hz).</summary>
    event Action<Guid>? StateChanged;
    /// <summary>Deduped per media; returns the local path (MAUI) or null in the browser (the browser downloads it).</summary>
    Task<string?> DownloadAsync(MediaDto media, CancellationToken cancellationToken = default);
    void Cancel(Guid mediaId);
    /// <summary>Downloads (if needed) and opens the file in an external app / triggers a browser download.</summary>
    Task OpenAsync(MediaDto media, CancellationToken cancellationToken = default);
    Task<bool> SaveToDeviceAsync(MediaDto media, CancellationToken cancellationToken = default);
    /// <summary>Should this attachment be fetched automatically on the current network?</summary>
    bool ShouldAutoDownload(MediaDto media);
    Task<long> GetCacheSizeAsync();
    Task ClearCacheAsync();
}

/// <summary>Shareable https links (PublicBaseUrl ?? BaseUri) for profiles and invites.</summary>
public interface ILinkBuilder
{
    string ProfileLink(string username);
    string InviteLink(string token);
}

/// <summary>
/// Settings. <see cref="Current"/> is an immutable-by-contract snapshot: read it, never mutate it.
/// <see cref="UpdateAsync"/> mutates a clone, persists it, swaps the snapshot and raises <see cref="Changed"/>.
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    event Action? Changed;
    Task LoadAsync();
    Task UpdateAsync(Action<AppSettings> mutate);
    Task ResetAsync();
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>SignalR connection to <see cref="HubRoutes.Realtime"/>; stores subscribe to its events.</summary>
public interface IRealtimeService
{
    ConnectionState State { get; }
    /// <summary>"WebSockets", "ServerSentEvents" or "LongPolling" when connected.</summary>
    string? TransportName { get; }
    /// <summary>True from <see cref="Resynced"/> until every store finished catching up.</summary>
    bool IsSyncing { get; }
    event Action? StateChanged;
    event Action? SyncStateChanged;
    /// <summary>
    /// Raised after a (re)connect that lost messages; stores and direct subscribers must refetch. Short drops
    /// are covered by SignalR stateful reconnect (buffered messages are replayed, no Resynced). Resync is
    /// lazy: eagerly only the chat list and the open chat; other cached chats catch up via /changes when
    /// opened. Stores register catch-up work with <see cref="BeginSync"/>.
    /// </summary>
    event Action? Resynced;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    /// <summary>Skip the backoff and reconnect right away (network came back, app resumed).</summary>
    void ReconnectNow();
    /// <summary>Marks one catch-up job; <see cref="IsSyncing"/> stays true until all returned handles are disposed.</summary>
    IDisposable BeginSync();
    Task SendTypingAsync(Guid chatId, TypingAction action);
    Task SetActiveAsync(bool isActive);

    event Action<MessageDto>? MessageCreated;
    event Action<MessageDto>? MessageUpdated;
    event Action<MessagesDeletedEvent>? MessagesDeleted;
    event Action<MessagesReadEvent>? MessagesRead;
    event Action<TypingEvent>? Typing;
    event Action<ChatRefEvent>? ChatAdded;
    event Action<ChatInfoDto>? ChatUpdated;
    event Action<ChatRefEvent>? ChatRemoved;
    event Action<ChatRefEvent>? ChatMembersChanged;
    event Action<ChatSettingsChangedEvent>? ChatSettingsChanged;
    event Action<HistoryClearedEvent>? HistoryCleared;
    event Action<ChatRefEvent>? PinnedMessagesChanged;
    event Action<PresenceChangedEvent>? PresenceChanged;
    event Action<UserRefEvent>? UserChanged;
    event Action<ContactsChangedEvent>? ContactsChanged;
    event Action<MediaDto>? MediaProcessed;
    event Action<CallDto>? IncomingCall;
    event Action<CallDto>? CallUpdated;
    event Action<SessionRevokedEvent>? SessionRevoked;
}

/// <summary>
/// App start. Routes.razor calls <see cref="StartAsync"/> once; it completes after settings and the
/// session are restored locally (no network wait), then realtime, loads and the outbox continue in the
/// background. It owns sign-in/out: on sign-out it stops realtime, ends calls, cancels notifications and
/// clears stores, caches and per-account data. Idempotent.
/// </summary>
public interface IClientBootstrapper
{
    bool IsStarted { get; }
    Task StartAsync();
}

/// <summary>
/// Localized one-line previews ("Photo", "🎤 Voice message 0:12", "Alice joined the group").
/// Implemented by the UI as a singleton without JS dependencies; Core resolves it lazily (optional).
/// </summary>
public interface IMessagePreviewFormatter
{
    string FormatPreview(MessageDto message);
    string FormatChatTitle(ChatSummaryDto chat);
}

public sealed record InAppNotification(Guid ChatId, Guid? SenderId, string Title, string Body, Guid? AvatarMediaId);

/// <summary>
/// Decides whether an incoming message notifies: skips my own messages, muted chats (mentions bypass mute
/// when enabled), disabled settings, and the active chat while the app is in foreground. Foreground →
/// <see cref="InAppNotificationRaised"/> (UI banner + tone); background → local OS notification.
/// </summary>
public interface INotificationCoordinator
{
    event Action<InAppNotification>? InAppNotificationRaised;
}
