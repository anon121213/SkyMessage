using AndroidX.Core.Content;
using AndroidX.WebKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.App.Droid;

/// <summary>
/// Applies the user's proxy to the WebView's own network stack (LiveKit signaling WebSocket and any direct
/// loads) through androidx.webkit ProxyController. HTTP proxies are supported including credentials (answered
/// on 407 by <see cref="RecoveringWebViewClient"/>); SOCKS5 only without credentials — with credentials the
/// WebView stays direct and a warning is logged (the settings screen already says calls may not work through a
/// proxy). WebRTC media never uses a proxy.
/// </summary>
public sealed class WebViewProxyConfigurator(IServiceProvider services, ILogger<WebViewProxyConfigurator> logger)
{
    private ISettingsService? _settings;
    private string? _appliedKey;
    private ProxyCredentials? _credentials;

    internal ProxyCredentials? Credentials => _credentials;

    public void Start()
    {
        if (_settings is not null) return;
        _settings = services.GetService<ISettingsService>();
        if (_settings is null) return;
        _settings.Changed += Apply;
        Apply();
    }

    private void Apply()
    {
        var connection = _settings?.Current.Connection;
        if (connection is null) return;
        var proxy = HttpHandlerFactory.ProxyOf(connection);
        var key = proxy is null ? "none" : $"{proxy.Type}|{proxy.Host}|{proxy.Port}|{proxy.Username}|{proxy.Password?.GetHashCode()}";
        if (key == _appliedKey) return;
        _appliedKey = key;

        _credentials = proxy is { Type: ProxyType.Http } && !string.IsNullOrEmpty(proxy.Username)
            ? new ProxyCredentials(proxy.Host.Trim(), proxy.Username!, proxy.Password ?? "")
            : null;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (!WebViewFeature.IsFeatureSupported(WebViewFeature.ProxyOverride))
                {
                    if (proxy is not null) logger.LogWarning("This WebView cannot use an app proxy; calls connect directly");
                    return;
                }
                var context = Android.App.Application.Context;
                var executor = ContextCompat.GetMainExecutor(context)!;
                var done = new Java.Lang.Runnable(() => { });
                if (proxy is null)
                {
                    ProxyController.Instance!.ClearProxyOverride(executor, done);
                    return;
                }
                if (proxy.Type == ProxyType.Socks5 && !string.IsNullOrEmpty(proxy.Username))
                {
                    logger.LogWarning("SOCKS5 proxies with credentials cannot be applied to the WebView; call signaling connects directly");
                    ProxyController.Instance!.ClearProxyOverride(executor, done);
                    return;
                }
                var host = proxy.Host.Trim();
                if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
                var rule = proxy.Type == ProxyType.Socks5 ? $"socks5://{host}:{proxy.Port}" : $"http://{host}:{proxy.Port}";
                var config = new ProxyConfig.Builder().AddProxyRule(rule)!.Build()!;
                ProxyController.Instance!.SetProxyOverride(config, executor, done);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Applying the WebView proxy failed");
            }
        });
    }
}
