using Android.Graphics;
using Android.Net.Http;
using Android.Webkit;
using Microsoft.Extensions.Logging;
using WebView = Android.Webkit.WebView;

namespace SkyMessage.App.Droid;

/// <summary>
/// Wraps BlazorWebView's own client (static assets, media interception, Blazor startup, back state) and adds:
/// answers to HTTP proxy authentication (407) with the user's proxy credentials, and recovery when the
/// renderer process dies — return true so the app survives, then MainPage rebuilds the BlazorWebView
/// (Core singletons keep running; an active call re-joins).
/// </summary>
internal sealed class RecoveringWebViewClient(
    WebViewClient inner,
    Func<ProxyCredentials?> proxyCredentials,
    Action<WebView> onPageFinished,
    Action<string?> onRendererGone,
    ILogger logger) : WebViewClient
{
    private readonly Dictionary<string, int> _authAttempts = new(StringComparer.OrdinalIgnoreCase);
    private ProxyCredentials? _lastCredentials;
    private bool _rendererGone;

    public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request) =>
        inner.ShouldInterceptRequest(view, request);

    public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) =>
        inner.ShouldOverrideUrlLoading(view, request);

#pragma warning disable CA1422, CS0672 // Deprecated by Android; forwarded so the wrapped client sees every call.
    public override bool ShouldOverrideUrlLoading(WebView? view, string? url) =>
        inner.ShouldOverrideUrlLoading(view, url);
#pragma warning restore CA1422, CS0672

    public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon) =>
        inner.OnPageStarted(view, url, favicon);

    public override void OnPageFinished(WebView? view, string? url)
    {
        inner.OnPageFinished(view, url);
        if (view is not null) onPageFinished(view);
    }

    public override void DoUpdateVisitedHistory(WebView? view, string? url, bool isReload) =>
        inner.DoUpdateVisitedHistory(view, url, isReload);

    public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error) =>
        inner.OnReceivedError(view, request, error);

    public override void OnReceivedHttpError(WebView? view, IWebResourceRequest? request, WebResourceResponse? errorResponse) =>
        inner.OnReceivedHttpError(view, request, errorResponse);

    public override void OnReceivedSslError(WebView? view, SslErrorHandler? handler, SslError? error)
    {
        // Never proceed on certificate errors.
        handler?.Cancel();
    }

    public override void OnReceivedHttpAuthRequest(WebView? view, HttpAuthHandler? handler, string? host, string? realm)
    {
        if (handler is null) return;
        var credentials = proxyCredentials();
        if (!ReferenceEquals(credentials, _lastCredentials))
        {
            // New proxy settings: earlier rejections no longer count.
            _lastCredentials = credentials;
            _authAttempts.Clear();
        }
        if (credentials is not null && host is not null && string.Equals(StripPort(host), credentials.Host, StringComparison.OrdinalIgnoreCase))
        {
            var key = $"{host}|{realm}";
            var attempts = _authAttempts.GetValueOrDefault(key);
            if (attempts < 2)
            {
                _authAttempts[key] = attempts + 1;
                handler.Proceed(credentials.Username, credentials.Password);
                return;
            }
            logger.LogWarning("Proxy rejected the configured credentials");
        }
        inner.OnReceivedHttpAuthRequest(view, handler, host, realm);
    }

    public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
    {
        if (_rendererGone) return true;
        _rendererGone = true;
        var crashed = OperatingSystem.IsAndroidVersionAtLeast(26) && detail?.DidCrash() == true;
        logger.LogWarning("WebView renderer {Reason}; rebuilding the UI", crashed ? "crashed" : "was killed by the system");
        string? url = null;
        try
        {
            url = view?.Url;
        }
        catch (Exception)
        {
        }
        onRendererGone(url);
        return true;
    }

    private static string StripPort(string host)
    {
        var index = host.LastIndexOf(':');
        return index > 0 && !host.EndsWith(']') && host.Count(c => c == ':') == 1 ? host[..index] : host;
    }
}

internal sealed record ProxyCredentials(string Host, string Username, string Password);
