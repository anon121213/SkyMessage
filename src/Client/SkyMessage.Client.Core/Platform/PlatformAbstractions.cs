using System.Net;
using System.Net.WebSockets;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Platform;

// Implemented by each host: SkyMessage.App (MAUI: Android/iOS/Mac) and SkyMessage.Web (browser).
// Everything here must have a safe implementation on every host ("IsSupported = false" is fine).
// Lifetimes: register as singletons; implementations must not depend on scoped Blazor services.

public interface IPlatformInfo
{
    ClientPlatform Platform { get; }
    string DeviceName { get; }
    string? OsVersion { get; }
    string AppVersion { get; }
    /// <summary>Phone/tablet, touch-first UI.</summary>
    bool IsMobile { get; }
    /// <summary>MAUI app (true) vs. browser (false).</summary>
    bool IsNativeApp { get; }
    /// <summary>ISO 3166 alpha-2 from SIM/network country, else device locale (e.g. "TH", "RU"); used to parse phone numbers.</summary>
    string? CountryCode { get; }
}

public interface ISecureStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}

public interface IPreferencesStore
{
    Task<string?> GetAsync(string key);
    /// <summary>Null removes the key.</summary>
    Task SetAsync(string key, string? value);
}

/// <summary>
/// Best-effort key/value store for offline data (chat list, recent messages, users, outbox, drafts).
/// MAUI: files in the app data dir; browser: IndexedDB (not localStorage — quota and sync writes).
/// </summary>
public interface ILocalCacheStore
{
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAsync(string key, string content, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>Deletes every key starting with <paramref name="prefix"/> (empty = everything).</summary>
    Task ClearAsync(string prefix = "", CancellationToken cancellationToken = default);
    Task<long> GetSizeBytesAsync(CancellationToken cancellationToken = default);
}

/// <summary>A file chosen or captured by the user. The stream may be opened more than once.</summary>
public sealed class PickedFile
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    /// <summary>-1 when unknown.</summary>
    public long Size { get; init; } = -1;
    public required Func<CancellationToken, Task<Stream>> OpenReadAsync { get; init; }
    /// <summary>Native file path when available (MAUI).</summary>
    public string? LocalPath { get; init; }
    /// <summary>Opaque host handle (e.g. a browser File kept in JS) so the host can persist the original without streaming it through .NET.</summary>
    public string? PlatformRef { get; init; }

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public bool IsVideo => ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}

public interface IFilePickerService
{
    /// <summary>Photos and videos from the gallery.</summary>
    Task<IReadOnlyList<PickedFile>> PickMediaAsync(bool allowMultiple, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PickedFile>> PickFilesAsync(bool allowMultiple, CancellationToken cancellationToken = default);
    /// <summary>Single image (avatars).</summary>
    Task<PickedFile?> PickImageAsync(CancellationToken cancellationToken = default);
    bool CanCapture { get; }
    Task<PickedFile?> CapturePhotoAsync(CancellationToken cancellationToken = default);
    Task<PickedFile?> CaptureVideoAsync(CancellationToken cancellationToken = default);
}

public sealed class ImageCompressionOptions
{
    public int MaxDimension { get; init; } = 2560;
    public int Quality { get; init; } = 82;
    /// <summary>Also removes EXIF/GPS metadata (privacy).</summary>
    public bool StripMetadata { get; init; } = true;
}

public sealed class VideoCompressionOptions
{
    /// <summary>Longest side, e.g. 1280 for 720p.</summary>
    public int MaxDimension { get; init; } = 1280;
    public int VideoBitrateKbps { get; init; } = 2500;
    public int AudioBitrateKbps { get; init; } = 128;
}

/// <summary>Result of client-side compression or probing, ready to upload.</summary>
public sealed class CompressedMedia
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long Size { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationMs { get; init; }
    public required Func<CancellationToken, Task<Stream>> OpenReadAsync { get; init; }
    /// <summary>Small JPEG (≤ 320px) for an instant local preview.</summary>
    public byte[]? PreviewJpeg { get; init; }
    /// <summary>Temporary file to delete after upload, if any.</summary>
    public string? TempFilePath { get; init; }
}

public interface IMediaCompressor
{
    bool CanCompressImages { get; }
    bool CanCompressVideos { get; }
    Task<CompressedMedia> CompressImageAsync(PickedFile file, ImageCompressionOptions options, CancellationToken cancellationToken = default);
    Task<CompressedMedia> CompressVideoAsync(PickedFile file, VideoCompressionOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Dimensions/duration/preview without re-encoding. Fast; Core calls it first for an instant preview.</summary>
    Task<CompressedMedia> ProbeAsync(PickedFile file, CancellationToken cancellationToken = default);
}

public enum PhoneDetectionStatus
{
    Detected,
    NotAvailable,
    PermissionDenied,
    Unsupported,
    Cancelled,
}

/// <summary>A number found on the device (dual-SIM phones yield two). Number in E.164 when known.</summary>
public sealed record PhoneCandidate(string Number, string? CountryCode, string? CarrierName);

public sealed record PhoneDetectionResult(PhoneDetectionStatus Status, IReadOnlyList<PhoneCandidate> Candidates)
{
    public static PhoneDetectionResult Of(PhoneDetectionStatus status) => new(status, []);
}

/// <summary>
/// Reads the device's own phone number(s): Android uses the Phone Number Hint API first (no permission,
/// system dual-SIM chooser, E.164), then SubscriptionManager as a fallback. iOS/Mac/Web: unsupported.
/// </summary>
public interface IPhoneNumberService
{
    bool IsSupported { get; }
    Task<PhoneDetectionResult> DetectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Address-book entry. <see cref="E164Numbers"/> holds OS-normalized numbers when available (Android NORMALIZED_NUMBER).</summary>
public sealed record DeviceContact(string DisplayName, IReadOnlyList<string> PhoneNumbers, IReadOnlyList<string>? E164Numbers = null);

public interface IDeviceContactsService
{
    bool IsSupported { get; }
    Task<bool> RequestPermissionAsync();
    Task<IReadOnlyList<DeviceContact>> GetContactsAsync(CancellationToken cancellationToken = default);
}

public enum AppPermission
{
    Microphone,
    Camera,
    Contacts,
    Notifications,
    PhoneNumber,
    Photos,
    /// <summary>Android 12+ runtime permission for Bluetooth audio routing.</summary>
    Bluetooth,
    /// <summary>Android 14+: full-screen incoming-call notifications (user-revocable).</summary>
    FullScreenIntent,
}

public enum PermissionState
{
    Unknown,
    Granted,
    Denied,
    Restricted,
    NotApplicable,
    /// <summary>Partial access (iOS limited photos/contacts).</summary>
    Limited,
}

public interface IPermissionService
{
    Task<PermissionState> CheckAsync(AppPermission permission);
    Task<PermissionState> RequestAsync(AppPermission permission);
    Task OpenAppSettingsAsync();
}

public sealed record LocalNotification(Guid ChatId, Guid? MessageId, string Title, string Body, bool Silent);

public enum CallNotificationAction
{
    Accept,
    AcceptVideo,
    Decline,
    Hangup,
}

/// <summary>
/// OS notifications while the app is in background. Tapping a message notification must open
/// <see cref="DeepLinks.AppChatUri"/> through <see cref="IDeepLinkSource"/> (works on cold start too).
/// </summary>
public interface ILocalNotificationService
{
    Task<bool> RequestPermissionAsync();
    Task ShowMessageAsync(LocalNotification notification);
    Task CancelForChatAsync(Guid chatId);
    Task ShowIncomingCallAsync(CallDto call, string callerName);
    Task CancelCallAsync(Guid callId);
    /// <summary>App icon badge (total unread); no-op where unsupported.</summary>
    Task SetBadgeCountAsync(int count);
    /// <summary>Accept/decline pressed on an incoming-call notification.</summary>
    event Action<Guid, CallNotificationAction>? CallActionInvoked;
}

public enum AudioRouteKind
{
    Earpiece,
    Speaker,
    Bluetooth,
    Wired,
}

/// <summary>An audio output. <see cref="Id"/> distinguishes devices of the same kind (car vs. earbuds).</summary>
public sealed record AudioRoute(AudioRouteKind Kind, string Name, bool IsActive, string? Id = null);

/// <summary>
/// Native audio output routing for calls on phones (speaker / earpiece / Bluetooth). The WebView's WebRTC
/// stack may re-pick a device when capture restarts, so the preferred route is re-applied after the local
/// audio track is published and whenever devices change.
/// </summary>
public interface IAudioRouteService
{
    bool IsSupported { get; }
    Task<IReadOnlyList<AudioRoute>> GetRoutesAsync();
    Task SetRouteAsync(AudioRouteKind kind);
    Task SetRouteAsync(string routeId);
    event Action? RoutesChanged;
}

public enum CallCommand
{
    ToggleMicrophone,
    ToggleCamera,
    SwitchCamera,
    Hangup,
}

/// <summary>
/// Keeps calls alive natively (Android foreground service, wake lock, iOS audio session). Core requests the
/// Microphone (and, for video, Camera) permission through IPermissionService first and calls
/// <see cref="OnCallStartedAsync"/> only while the app is in foreground (Android 14+ forbids starting a
/// microphone service otherwise). Implementations never throw.
/// </summary>
public interface ICallPlatformService
{
    /// <summary>Returns true when the call is being kept alive (service type derived from granted permissions).</summary>
    Task<bool> OnCallStartedAsync(CallDto call, string title, bool video);
    /// <summary>Camera turned on/off mid-call (Android 14 needs the service type updated; only in foreground).</summary>
    Task<bool> OnCallMediaChangedAsync(bool cameraOn);
    Task OnCallEndedAsync();
    /// <summary>Buttons on the ongoing-call notification / headset (mute, hang up...).</summary>
    event Action<CallCommand>? CommandRequested;
}

/// <summary>Why other apps' audio should pause right now (see <see cref="IAudioFocusService"/>).</summary>
public enum AudioFocusKind
{
    /// <summary>Nothing of ours is playing or recording: other apps may play.</summary>
    None,
    /// <summary>An incoming call rings.</summary>
    Ringtone,
    /// <summary>A call is being set up, in progress or showing its end.</summary>
    Call,
    /// <summary>A voice message or a round video note is being recorded.</summary>
    Recording,
}

/// <summary>
/// Android: transient audio focus, so music and videos in other apps pause while a call rings or runs and while a
/// voice or video note is recorded, and resume when focus is given back. Other hosts: <see cref="IsSupported"/> =
/// false (the setting is hidden). Core decides when (<c>AudioFocusPolicy</c>). Implementations never throw.
/// </summary>
public interface IAudioFocusService
{
    bool IsSupported { get; }
    /// <summary>
    /// Holds focus for <paramref name="kind"/>; <see cref="AudioFocusKind.None"/> gives it back. Switching from one
    /// kind to another never lets other apps resume in between.
    /// </summary>
    void SetFocus(AudioFocusKind kind);
}

public interface IShareService
{
    Task ShareTextAsync(string text, string? title = null);
    Task<bool> CopyToClipboardAsync(string text);
}

public interface IFileSaveService
{
    /// <summary>Saves to Downloads / shows a save dialog. Returns false if cancelled.</summary>
    Task<bool> SaveAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken = default);
    bool CanSaveToGallery { get; }
    /// <summary>Photos/videos into the device gallery (MediaStore / PHPhotoLibrary).</summary>
    Task<bool> SaveToGalleryAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken = default);
}

/// <summary>Opens a downloaded local file in another app (PDF viewer, etc.).</summary>
public interface IFileOpener
{
    bool IsSupported { get; }
    Task<bool> OpenAsync(string localPath, string contentType);
}

/// <summary>
/// Durable copies of outbox attachments, so a voice note recorded offline survives the app being killed.
/// MAUI: app-private outbox/ dir excluded from backup (Android Photo Picker URIs may use persistable
/// permissions instead of copying); browser: IndexedDB blobs (PickedFile.PlatformRef avoids copying via .NET).
/// Core persists the (compressed) payload before an item becomes Queued and deletes it after send/cancel.
/// </summary>
public interface IOutboxFileStore
{
    bool IsSupported { get; }
    Task<PickedFile> PersistAsync(Guid itemKey, PickedFile source, CancellationToken cancellationToken = default);
    Task<PickedFile?> TryOpenAsync(Guid itemKey);
    Task DeleteAsync(Guid itemKey);
    Task<long> GetSizeBytesAsync();
}

/// <summary>
/// On-device cache of downloaded media bytes (MAUI: files). Browser: <see cref="IsSupported"/> = false
/// (the HTTP cache is used instead).
/// </summary>
public interface IMediaFileStore
{
    bool IsSupported { get; }
    Task<string?> GetLocalPathAsync(Guid mediaId, bool thumbnail = false);
    Task<string> SaveAsync(Guid mediaId, bool thumbnail, string fileName, Stream content, CancellationToken cancellationToken = default);
    Task<long> GetSizeBytesAsync();
    /// <summary>Deletes least recently used files until the cache fits <paramref name="maxBytes"/>.</summary>
    Task TrimAsync(long maxBytes);
    Task ClearAsync();
}

public interface IAppLifecycle
{
    bool IsForeground { get; }
    event Action<bool>? ForegroundChanged;
}

public interface IExternalLauncher
{
    /// <summary>Opens http(s)/mailto/tel links outside the app. Never pass media content URLs here.</summary>
    Task OpenUriAsync(Uri uri);
}

/// <summary>Deep links that started or resumed the app (skymessage://..., https://host/u/..., notification taps).</summary>
public interface IDeepLinkSource
{
    event Action<Uri>? DeepLinkReceived;
    /// <summary>Link that launched the app before the UI subscribed; returns it once.</summary>
    Uri? ConsumePending();
}

public enum NetworkKind
{
    None,
    Wifi,
    Cellular,
    Ethernet,
    Other,
    Unknown,
}

/// <summary>
/// <see cref="IsConnected"/> means "some network interface is up" — NOT OS internet validation (which fails
/// on filtered Russian networks while our server is reachable). Core never gates connection attempts on it;
/// it only uses changes to trigger reconnects and to drive the status bar.
/// </summary>
public interface INetworkStatusService
{
    bool IsConnected { get; }
    NetworkKind Kind { get; }
    bool IsMetered { get; }
    event Action? Changed;
}

/// <summary>
/// Status/navigation bar appearance (follow the in-app theme, not the OS). On Android 15+ (edge-to-edge)
/// only the icon color applies — <c>backgroundHex</c> is ignored and the CSS must paint behind the bars.
/// </summary>
public interface ISystemUiService
{
    Task SetSystemBarsStyleAsync(bool darkContent, string backgroundHex);
}

public enum HapticKind
{
    Light,
    Medium,
    Heavy,
    Selection,
    Success,
    Warning,
}

public interface IHapticsService
{
    bool IsSupported { get; }
    void Perform(HapticKind kind);
}

/// <summary>
/// Android: optional persistent foreground service that keeps the realtime connection alive so messages
/// and calls arrive while the app is closed (until push lands). Other platforms: not supported.
/// </summary>
public interface IBackgroundConnectionService
{
    bool IsSupported { get; }
    bool IsEnabled { get; }
    Task SetEnabledAsync(bool enabled);
    /// <summary>Asks the OS to exempt the app from battery optimizations (Android). False if declined/unsupported.</summary>
    Task<bool> RequestBatteryExemptionAsync();
}

/// <summary>
/// Named lock shared by all instances of the client on this device (browser tabs: navigator.locks;
/// MAUI: in-process). Used to serialize token refresh.
/// </summary>
public interface IInterProcessLock
{
    Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the HTTP stack for API/SignalR traffic: proxy settings, TLS policy, connect timeouts and
/// Happy Eyeballs (IPv6/IPv4 racing) on native; iOS prefers NSUrlSessionHandler (wakes the cellular radio).
/// </summary>
public interface IHttpHandlerFactory
{
    /// <summary>False in the browser (the app proxy setting must be hidden there).</summary>
    bool SupportsProxy { get; }
    HttpMessageHandler CreateHandler(ConnectionSettings settings);
    /// <summary>Proxy for SignalR's own WebSocket (HttpConnectionOptions.Proxy). Null when none or unsupported (browser).</summary>
    IWebProxy? CreateProxy(ConnectionSettings settings);
    /// <summary>
    /// Opens WebSockets through the same native stack (proxy, TLS, Happy Eyeballs, 10–15 s connect timeout) for
    /// SignalR's HttpConnectionOptions.WebSocketFactory; the callback configures headers (Authorization).
    /// Null in the browser (default WebSocket).
    /// </summary>
    Func<Uri, Action<ClientWebSocketOptions>?, CancellationToken, ValueTask<WebSocket>>? CreateWebSocketConnector(ConnectionSettings settings);
}
