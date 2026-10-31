using System.Globalization;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Services;
using UIKit;
using WebKit;

namespace SkyMessage.App.Apple;

/// <summary>
/// Per-WKWebView setup on iOS/Mac: wraps MAUI's UI delegate (alert/confirm forwarded; the UI never uses
/// window.prompt, which therefore returns null) to grant getUserMedia for
/// the app origin, wraps its navigation delegate to recover when the WebContent process is killed, and pushes
/// the safe-area insets into the page as --sm-safe-* (--sm-kb-inset stays 0 because WKWebView resizes for
/// the keyboard itself). On iOS/Mac 17+ the user's proxy also applies to the WebView's own connections (LiveKit
/// signaling, direct media) through WKWebsiteDataStore proxy configurations. Never uses AppHostAddressAlways0000
/// (app://0.0.0.0 is refused by WebKit).
/// </summary>
public sealed class AppleWebViewHost
{
    private readonly WKWebView _webView;
    private readonly ContentPage _page;
    private readonly Action _onTerminated;
    private readonly ILogger _logger;
    private readonly ISettingsService? _settings;
    private UIEdgeInsets _last = new(-1, -1, -1, -1);
    private string? _proxyKey;

    private AppleWebViewHost(WKWebView webView, ContentPage page, IServiceProvider services, Action onTerminated)
    {
        _webView = webView;
        _page = page;
        _onTerminated = onTerminated;
        _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SkyMessage.WebView");

        webView.ScrollView.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
        webView.ScrollView.Bounces = false;
        webView.AllowsBackForwardNavigationGestures = false;
#if DEBUG
        if (OperatingSystem.IsIOSVersionAtLeast(16, 4) || OperatingSystem.IsMacCatalystVersionAtLeast(16, 4)) webView.Inspectable = true;
#endif
        page.SizeChanged += OnPageSizeChanged;
        _settings = services.GetService<ISettingsService>();
        if (_settings is not null) _settings.Changed += OnSettingsChanged;
        ApplyProxy();
        // MAUI assigns its delegates right after this event; wrap them on the next main-loop turn.
        DispatchQueue.MainQueue.DispatchAsync(WrapDelegates);
    }

    public static AppleWebViewHost Attach(WKWebView webView, ContentPage page, IServiceProvider services, Action onTerminated) =>
        new(webView, page, services, onTerminated);

    public void Detach()
    {
        _page.SizeChanged -= OnPageSizeChanged;
        if (_settings is not null) _settings.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged() => DispatchQueue.MainQueue.DispatchAsync(ApplyProxy);

    private void ApplyProxy()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(17) && !OperatingSystem.IsMacCatalystVersionAtLeast(17)) return;
        try
        {
            var connection = _settings?.Current.Connection;
            var proxy = connection is null ? null : HttpHandlerFactory.ProxyOf(connection);
            var key = proxy is null ? "none" : $"{proxy.Type}|{proxy.Host}|{proxy.Port}|{proxy.Username}|{proxy.Password?.GetHashCode()}";
            if (key == _proxyKey) return;
            _proxyKey = key;
            var store = _webView.Configuration.WebsiteDataStore;
            store.ProxyConfigurations = proxy is not null && HttpHandlerFactory.CreateNetworkProxy(proxy) is { } config ? [config] : [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Applying the WebView proxy failed");
        }
    }

    private void WrapDelegates()
    {
        try
        {
            if (_webView.UIDelegate is WKUIDelegate ui && ui is not MediaCaptureUIDelegate)
                _webView.UIDelegate = new MediaCaptureUIDelegate(ui);
            if (_webView.NavigationDelegate is WKNavigationDelegate navigation && navigation is not RecoveringNavigationDelegate)
                _webView.NavigationDelegate = new RecoveringNavigationDelegate(navigation, OnNavigationFinished, OnTerminated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wrapping the WKWebView delegates failed");
        }
        PushInsets();
    }

    private void OnNavigationFinished()
    {
        _last = new UIEdgeInsets(-1, -1, -1, -1);
        PushInsets();
    }

    private void OnTerminated()
    {
        _logger.LogWarning("WebContent process terminated; rebuilding the UI");
        _onTerminated();
    }

    private void OnPageSizeChanged(object? sender, EventArgs e) =>
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            _last = new UIEdgeInsets(-1, -1, -1, -1);
            PushInsets();
        });

    private void PushInsets()
    {
        try
        {
            var insets = _webView.SafeAreaInsets;
            if (insets == _last) return;
            _last = insets;
            var script = string.Create(CultureInfo.InvariantCulture,
                $"(function(t,r,b,l){{var s=document.documentElement&&document.documentElement.style;if(!s)return;" +
                $"s.setProperty('--sm-safe-top',t+'px');s.setProperty('--sm-safe-right',r+'px');s.setProperty('--sm-safe-bottom',b+'px');" +
                $"s.setProperty('--sm-safe-left',l+'px');s.setProperty('--sm-kb-inset','0px');" +
                $"window.__smNativeInsets={{top:t,right:r,bottom:b,left:l,keyboard:0}};" +
                $"try{{window.dispatchEvent(new CustomEvent('sm-insets',{{detail:window.__smNativeInsets}}));}}catch(e){{}}}})" +
                $"({Math.Round(insets.Top, 1)},{Math.Round(insets.Right, 1)},{Math.Round(insets.Bottom, 1)},{Math.Round(insets.Left, 1)});");
            _webView.EvaluateJavaScript(script, (_, _) => { });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pushing insets failed");
        }
    }

    /// <summary>
    /// Forwards to MAUI's delegate only what it implements: calling a protocol method a [Model] subclass did not
    /// override throws inside a native callback.
    /// </summary>
    private static bool Implements(NSObject target, string selector) =>
        target.RespondsToSelector(new ObjCRuntime.Selector(selector));

    private sealed class MediaCaptureUIDelegate(WKUIDelegate inner) : WKUIDelegate
    {
        public override void RunJavaScriptAlertPanel(WKWebView webView, string message, WKFrameInfo frame, Action completionHandler)
        {
            if (Implements(inner, "webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:"))
                inner.RunJavaScriptAlertPanel(webView, message, frame, completionHandler);
            else
                completionHandler();
        }

        public override void RunJavaScriptConfirmPanel(WKWebView webView, string message, WKFrameInfo frame, Action<bool> completionHandler)
        {
            if (Implements(inner, "webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:"))
                inner.RunJavaScriptConfirmPanel(webView, message, frame, completionHandler);
            else
                completionHandler(false);
        }

        public override void RequestMediaCapturePermission(WKWebView webView, WKSecurityOrigin origin, WKFrameInfo frame, WKMediaCaptureType type,
            Action<WKPermissionDecision> decisionHandler) =>
            decisionHandler(string.Equals(origin.Host, "0.0.0.1", StringComparison.OrdinalIgnoreCase) ? WKPermissionDecision.Grant : WKPermissionDecision.Deny);
    }

    private sealed class RecoveringNavigationDelegate(WKNavigationDelegate inner, Action onFinished, Action onTerminated) : WKNavigationDelegate
    {
        private bool _terminated;

        public override void DidStartProvisionalNavigation(WKWebView webView, WKNavigation navigation)
        {
            if (Implements(inner, "webView:didStartProvisionalNavigation:")) inner.DidStartProvisionalNavigation(webView, navigation);
        }

        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            if (Implements(inner, "webView:decidePolicyForNavigationAction:decisionHandler:"))
                inner.DecidePolicy(webView, navigationAction, decisionHandler);
            else
                decisionHandler(WKNavigationActionPolicy.Allow);
        }

        public override void DidReceiveServerRedirectForProvisionalNavigation(WKWebView webView, WKNavigation navigation)
        {
            if (Implements(inner, "webView:didReceiveServerRedirectForProvisionalNavigation:"))
                inner.DidReceiveServerRedirectForProvisionalNavigation(webView, navigation);
        }

        public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error)
        {
            if (Implements(inner, "webView:didFailNavigation:withError:")) inner.DidFailNavigation(webView, navigation, error);
        }

        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error)
        {
            if (Implements(inner, "webView:didFailProvisionalNavigation:withError:")) inner.DidFailProvisionalNavigation(webView, navigation, error);
        }

        public override void DidCommitNavigation(WKWebView webView, WKNavigation navigation)
        {
            if (Implements(inner, "webView:didCommitNavigation:")) inner.DidCommitNavigation(webView, navigation);
        }

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
        {
            if (Implements(inner, "webView:didFinishNavigation:")) inner.DidFinishNavigation(webView, navigation);
            onFinished();
        }

        public override void ContentProcessDidTerminate(WKWebView webView)
        {
            if (_terminated) return;
            _terminated = true;
            onTerminated();
        }
    }
}
