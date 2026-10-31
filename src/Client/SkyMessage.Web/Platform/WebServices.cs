using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;

namespace SkyMessage.Web.Platform;

public sealed class WebPlatformInfo(WebBridge bridge) : IPlatformInfo
{
    private static readonly string Version = typeof(WebPlatformInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(WebPlatformInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public ClientPlatform Platform => ClientPlatform.Web;

    public string DeviceName => Truncate(bridge.State.DeviceName);

    public string? OsVersion => bridge.State.OsVersion;

    public string AppVersion => Version;

    public bool IsMobile => bridge.State.IsMobile;

    public bool IsNativeApp => false;

    public string? CountryCode => bridge.State.CountryCode;

    private static string Truncate(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "Browser" : name.Trim();
        return value.Length > Limits.DeviceNameMaxLength ? value[..Limits.DeviceNameMaxLength] : value;
    }
}

public sealed class WebPermissionService(WebBridge bridge) : IPermissionService
{
    public async Task<PermissionState> CheckAsync(AppPermission permission)
    {
        try
        {
            return permission switch
            {
                AppPermission.Microphone => Map(await bridge.Js.InvokeAsync<string>("skyPlatform.perm.query", "microphone").ConfigureAwait(false)),
                AppPermission.Camera => Map(await bridge.Js.InvokeAsync<string>("skyPlatform.perm.query", "camera").ConfigureAwait(false)),
                AppPermission.Notifications => MapNotification(await bridge.Js.InvokeAsync<string>("skyPlatform.notify.permission").ConfigureAwait(false)),
                _ => PermissionState.NotApplicable,
            };
        }
        catch (JSException)
        {
            return PermissionState.Unknown;
        }
    }

    public async Task<PermissionState> RequestAsync(AppPermission permission)
    {
        try
        {
            switch (permission)
            {
                case AppPermission.Microphone:
                case AppPermission.Camera:
                    var media = await bridge.Js.InvokeAsync<string>("skyPlatform.perm.requestMedia",
                        permission == AppPermission.Microphone, permission == AppPermission.Camera).ConfigureAwait(false);
                    return media switch
                    {
                        "granted" => PermissionState.Granted,
                        "denied" => PermissionState.Denied,
                        _ => PermissionState.Unknown,
                    };
                case AppPermission.Notifications:
                    return MapNotification(await bridge.Js.InvokeAsync<string>("skyPlatform.notify.request").ConfigureAwait(false));
                default:
                    return PermissionState.NotApplicable;
            }
        }
        catch (JSException)
        {
            return PermissionState.Unknown;
        }
    }

    /// <summary>Browsers expose no app settings page; the UI explains how to allow access in the address bar.</summary>
    public Task OpenAppSettingsAsync() => Task.CompletedTask;

    private static PermissionState Map(string? state) => state switch
    {
        "granted" => PermissionState.Granted,
        "denied" => PermissionState.Denied,
        _ => PermissionState.Unknown,
    };

    private static PermissionState MapNotification(string? state) => state switch
    {
        "granted" => PermissionState.Granted,
        "denied" => PermissionState.Denied,
        "unsupported" => PermissionState.NotApplicable,
        _ => PermissionState.Unknown,
    };
}

/// <summary>Page visibility (visibilitychange / pagehide / pageshow).</summary>
public sealed class WebAppLifecycle : IAppLifecycle
{
    private bool _foreground;

    public WebAppLifecycle(WebBridge bridge)
    {
        _foreground = bridge.State.Visible;
        bridge.VisibilityChanged += visible =>
        {
            if (visible == _foreground) return;
            _foreground = visible;
            ForegroundChanged?.Invoke(visible);
        };
    }

    public bool IsForeground => _foreground;

    public event Action<bool>? ForegroundChanged;
}

/// <summary>navigator.onLine plus the Network Information API where available (Chrome/Android).</summary>
public sealed class WebNetworkStatusService : INetworkStatusService
{
    private (bool Online, NetworkKind Kind, bool Metered) _state;

    public WebNetworkStatusService(WebBridge bridge)
    {
        _state = (bridge.State.Online, KindOf(bridge.State.Online, bridge.State.ConnectionType), bridge.State.Metered);
        bridge.NetworkChanged += (online, type, metered) =>
        {
            var next = (online, KindOf(online, type), metered);
            if (next == _state) return;
            _state = next;
            Changed?.Invoke();
        };
    }

    public bool IsConnected => _state.Online;

    public NetworkKind Kind => _state.Kind;

    public bool IsMetered => _state.Metered;

    public event Action? Changed;

    private static NetworkKind KindOf(bool online, string? type) => !online ? NetworkKind.None : type switch
    {
        "wifi" => NetworkKind.Wifi,
        "cellular" => NetworkKind.Cellular,
        "ethernet" => NetworkKind.Ethernet,
        "none" => NetworkKind.None,
        "bluetooth" or "wimax" or "other" or "mixed" => NetworkKind.Other,
        _ => NetworkKind.Unknown,
    };
}

/// <summary>The page's initial path is the deep link (/u/…, /join/…, /chat/…); notification clicks add more.</summary>
public sealed class WebDeepLinkSource : IDeepLinkSource
{
    private readonly ILogger<WebDeepLinkSource> _logger;
    private Uri? _pending;
    private Action<Uri>? _handlers;

    public WebDeepLinkSource(WebBridge bridge, ILogger<WebDeepLinkSource> logger)
    {
        _logger = logger;
        if (Uri.TryCreate(bridge.State.InitialUrl, UriKind.Absolute, out var initial) && DeepLinks.TryParse(initial, out _))
            _pending = initial;
    }

    public event Action<Uri>? DeepLinkReceived
    {
        add => _handlers += value;
        remove => _handlers -= value;
    }

    public Uri? ConsumePending()
    {
        var pending = _pending;
        _pending = null;
        return pending;
    }

    public void Publish(Uri uri)
    {
        if (!DeepLinks.TryParse(uri, out _)) return;
        var handlers = _handlers;
        if (handlers is null)
        {
            _pending = uri;
            return;
        }
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<Uri>)handler)(uri);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deep link handler failed");
            }
        }
    }
}

/// <summary>navigator.locks across tabs (a localStorage lease where unsupported).</summary>
public sealed class WebInterProcessLock(WebBridge bridge) : IInterProcessLock
{
    public async Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        await using (cancellationToken.Register(() => bridge.Sync?.InvokeVoid("skyPlatform.locks.abort", id)))
        {
            try
            {
                await bridge.Js.InvokeVoidAsync("skyPlatform.locks.acquire", name, id).ConfigureAwait(false);
            }
            catch (JSException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        if (cancellationToken.IsCancellationRequested)
        {
            bridge.Sync?.InvokeVoid("skyPlatform.locks.release", id);
            throw new OperationCanceledException(cancellationToken);
        }
        return new Releaser(bridge, id);
    }

    private sealed class Releaser(WebBridge bridge, string id) : IAsyncDisposable
    {
        private bool _released;

        public ValueTask DisposeAsync()
        {
            if (_released) return ValueTask.CompletedTask;
            _released = true;
            bridge.Sync?.InvokeVoid("skyPlatform.locks.release", id);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The browser owns networking (fetch / WebSocket): no app proxy, no custom sockets.</summary>
public sealed class WebHttpHandlerFactory : IHttpHandlerFactory
{
    public bool SupportsProxy => false;

    public HttpMessageHandler CreateHandler(ConnectionSettings settings) => new HttpClientHandler();

    public IWebProxy? CreateProxy(ConnectionSettings settings) => null;

    public Func<Uri, Action<ClientWebSocketOptions>?, CancellationToken, ValueTask<WebSocket>>? CreateWebSocketConnector(ConnectionSettings settings) => null;
}

public sealed class WebShareService(WebBridge bridge) : IShareService
{
    public async Task ShareTextAsync(string text, string? title = null)
    {
        try
        {
            if (bridge.State.CanShare && await bridge.Js.InvokeAsync<bool>("skyPlatform.share.text", text, title).ConfigureAwait(false)) return;
            await bridge.Js.InvokeAsync<bool>("skyPlatform.share.copy", text).ConfigureAwait(false);
        }
        catch (JSException)
        {
        }
    }

    public async Task<bool> CopyToClipboardAsync(string text)
    {
        try
        {
            return await bridge.Js.InvokeAsync<bool>("skyPlatform.share.copy", text).ConfigureAwait(false);
        }
        catch (JSException)
        {
            return false;
        }
    }
}

/// <summary>Saves through a download link (the browser decides the folder). No gallery access on the web.</summary>
public sealed class WebFileSaveService(WebBridge bridge, ILogger<WebFileSaveService> logger) : IFileSaveService
{
    public async Task<bool> SaveAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
    {
        try
        {
            using var streamReference = new DotNetStreamReference(content, leaveOpen: true);
            return await bridge.Js.InvokeAsync<bool>("skyPlatform.download.fromStream", cancellationToken, streamReference, fileName, contentType).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Download failed");
            return false;
        }
    }

    public bool CanSaveToGallery => false;

    public Task<bool> SaveToGalleryAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken = default) =>
        SaveAsync(fileName, contentType, content, cancellationToken);
}

/// <summary>Browsers cannot open local files in other apps; downloads go through signed URLs instead.</summary>
public sealed class WebFileOpener : IFileOpener
{
    public bool IsSupported => false;

    public Task<bool> OpenAsync(string localPath, string contentType) => Task.FromResult(false);
}

public sealed class WebExternalLauncher(WebBridge bridge, ILogger<WebExternalLauncher> logger) : IExternalLauncher
{
    public Task OpenUriAsync(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto" or "tel"))
        {
            logger.LogWarning("Refusing to open a link with scheme {Scheme}", uri.IsAbsoluteUri ? uri.Scheme : "(relative)");
            return Task.CompletedTask;
        }
        bridge.Sync?.InvokeVoid("skyPlatform.open.url", uri.AbsoluteUri);
        return Task.CompletedTask;
    }
}

/// <summary>Browser chrome colour (meta theme-color: Android address bar, installed PWA title bar).</summary>
public sealed class WebSystemUiService(WebBridge bridge) : ISystemUiService
{
    public Task SetSystemBarsStyleAsync(bool darkContent, string backgroundHex)
    {
        if (!string.IsNullOrWhiteSpace(backgroundHex)) bridge.Sync?.InvokeVoid("skyPlatform.ui.themeColor", backgroundHex);
        return Task.CompletedTask;
    }
}

/// <summary>A browser cannot keep a page out of screenshots: protected chats only hide their copy/save actions.</summary>
public sealed class WebScreenSecurityService : IScreenSecurityService
{
    public bool IsSupported => false;

    public void SetSecure(bool secure)
    {
    }
}

public sealed class WebHapticsService(WebBridge bridge) : IHapticsService
{
    public bool IsSupported => bridge.State.CanVibrate;

    public void Perform(HapticKind kind)
    {
        if (!IsSupported) return;
        int[] pattern = kind switch
        {
            HapticKind.Selection => [5],
            HapticKind.Light => [10],
            HapticKind.Medium => [20],
            HapticKind.Heavy => [35],
            HapticKind.Success => [15, 40, 15],
            HapticKind.Warning => [30, 60, 30],
            _ => [10],
        };
        bridge.Sync?.InvokeVoid("skyPlatform.haptics.vibrate", pattern);
    }
}

/// <summary>The Contact Picker API (Chrome on Android): the user picks which contacts to share; no blanket permission.</summary>
public sealed class WebDeviceContactsService(WebBridge bridge) : IDeviceContactsService
{
    public bool IsSupported => bridge.State.HasContactPicker;

    public Task<bool> RequestPermissionAsync() => Task.FromResult(IsSupported);

    public async Task<IReadOnlyList<DeviceContact>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return [];
        try
        {
            var picked = await bridge.Js.InvokeAsync<PickedContact[]>("skyPlatform.contacts.select", cancellationToken).ConfigureAwait(false);
            return picked
                .Where(c => c.Tel is { Length: > 0 })
                .Select(c => new DeviceContact(string.IsNullOrWhiteSpace(c.Name) ? c.Tel![0] : c.Name.Trim(), c.Tel!.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList()))
                .ToList();
        }
        catch (JSException)
        {
            return [];
        }
    }

    private sealed record PickedContact(string Name, string[]? Tel);
}

/// <summary>Media Session call actions (mute / camera / hang up from the OS media controls) and a screen wake lock for video.</summary>
public sealed class WebCallPlatformService : ICallPlatformService
{
    private readonly WebBridge _bridge;

    public WebCallPlatformService(WebBridge bridge)
    {
        _bridge = bridge;
        bridge.MediaSessionAction += action =>
        {
            CallCommand? command = action switch
            {
                "togglemicrophone" => CallCommand.ToggleMicrophone,
                "togglecamera" => CallCommand.ToggleCamera,
                "hangup" => CallCommand.Hangup,
                _ => null,
            };
            if (command is { } c) CommandRequested?.Invoke(c);
        };
    }

    public event Action<CallCommand>? CommandRequested;

    public async Task<bool> OnCallStartedAsync(CallDto call, string title, bool video)
    {
        try
        {
            await _bridge.Js.InvokeVoidAsync("skyPlatform.call.start", video).ConfigureAwait(false);
        }
        catch (JSException)
        {
        }
        return true;
    }

    public async Task<bool> OnCallMediaChangedAsync(bool cameraOn)
    {
        try
        {
            await _bridge.Js.InvokeVoidAsync("skyPlatform.call.start", cameraOn).ConfigureAwait(false);
        }
        catch (JSException)
        {
        }
        return true;
    }

    public Task OnCallEndedAsync()
    {
        _bridge.Sync?.InvokeVoid("skyPlatform.call.end");
        return Task.CompletedTask;
    }
}

public sealed class WebPhoneNumberService : IPhoneNumberService
{
    public bool IsSupported => false;

    public Task<PhoneDetectionResult> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PhoneDetectionResult.Of(PhoneDetectionStatus.Unsupported));
}

public sealed class WebBackgroundConnectionService : IBackgroundConnectionService
{
    public bool IsSupported => false;

    public bool IsEnabled => false;

    public Task SetEnabledAsync(bool enabled) => Task.CompletedTask;

    public Task<bool> RequestBatteryExemptionAsync() => Task.FromResult(false);
}

/// <summary>Output device selection on the web is per media element (setSinkId), handled by devices.js.</summary>
public sealed class WebAudioRouteService : IAudioRouteService
{
    public bool IsSupported => false;

    public Task<IReadOnlyList<AudioRoute>> GetRoutesAsync() => Task.FromResult<IReadOnlyList<AudioRoute>>([]);

    public Task SetRouteAsync(AudioRouteKind kind) => Task.CompletedTask;

    public Task SetRouteAsync(string routeId) => Task.CompletedTask;

    public event Action? RoutesChanged
    {
        add { }
        remove { }
    }
}

/// <summary>A page cannot pause other apps' or tabs' audio (the "pause music" setting is hidden in the browser).</summary>
public sealed class WebAudioFocusService : IAudioFocusService
{
    public bool IsSupported => false;

    public void SetFocus(AudioFocusKind kind)
    {
    }
}
