using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace SkyMessage.Web.Platform;

/// <summary>Initial browser state reported by <c>skyPlatform.init</c>.</summary>
public sealed record WebInitState
{
    public string InitialUrl { get; init; } = "";
    public bool Online { get; init; } = true;
    public string ConnectionType { get; init; } = "unknown";
    public bool Metered { get; init; }
    public bool Visible { get; init; } = true;
    public string DeviceName { get; init; } = "Browser";
    public string? OsVersion { get; init; }
    public bool IsMobile { get; init; }
    public string? CountryCode { get; init; }
    /// <summary>"accept:{callId}" when a call notification opened a new window.</summary>
    public string? CallAction { get; init; }
    public bool CanShare { get; init; }
    public bool HasContactPicker { get; init; }
    public bool CanVibrate { get; init; }
}

/// <summary>
/// The single JS → .NET channel: <c>wwwroot/web-platform.js</c> reports network, visibility, notification
/// clicks and media-session actions here; platform services subscribe. WebAssembly is single-threaded and
/// IJSRuntime is a singleton there, so singletons may hold it.
/// </summary>
public sealed class WebBridge : IDisposable
{
    private readonly ILogger<WebBridge> _logger;
    private readonly DotNetObjectReference<WebBridge> _reference;

    public WebBridge(IJSRuntime js, ILogger<WebBridge> logger)
    {
        _logger = logger;
        Js = js;
        _reference = DotNetObjectReference.Create(this);
        try
        {
            State = ((IJSInProcessRuntime)js).Invoke<WebInitState>("skyPlatform.init", _reference) ?? new WebInitState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "web-platform.js is not loaded; browser features are limited");
            State = new WebInitState();
        }
    }

    public IJSRuntime Js { get; }

    public IJSInProcessRuntime? Sync => Js as IJSInProcessRuntime;

    public WebInitState State { get; }

    public event Action<bool, string, bool>? NetworkChanged;

    public event Action<bool>? VisibilityChanged;

    /// <summary>(action, data JSON) from a notification click.</summary>
    public event Action<string, string>? NotificationAction;

    public event Action<string>? MediaSessionAction;

    [JSInvokable]
    public void OnNetworkChanged(bool online, string type, bool metered) => Raise(NetworkChanged, h => h(online, type, metered));

    [JSInvokable]
    public void OnVisibilityChanged(bool visible) => Raise(VisibilityChanged, h => h(visible));

    [JSInvokable]
    public void OnNotificationAction(string action, string data) => Raise(NotificationAction, h => h(action, data));

    [JSInvokable]
    public void OnMediaSessionAction(string action) => Raise(MediaSessionAction, h => h(action));

    private void Raise<T>(T? handlers, Action<T> invoke) where T : Delegate
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                invoke((T)handler);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Browser event handler failed");
            }
        }
    }

    public void Dispose() => _reference.Dispose();
}
