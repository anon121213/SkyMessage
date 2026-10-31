using System.Globalization;
using Android.Views;
using Android.Webkit;
using AndroidX.Core.View;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using View = Android.Views.View;
using WebView = Android.Webkit.WebView;

namespace SkyMessage.App.Droid;

/// <summary>
/// Per-WebView Android setup: settings, client wrappers, and system-bar/cutout/IME insets pushed into the page
/// as CSS custom properties (--sm-safe-top/right/bottom/left, --sm-kb-inset; CSS px). --sm-kb-inset is the full
/// keyboard height from the bottom edge (0 when hidden); the UI pads by max(safe-bottom, kb-inset).
/// </summary>
internal sealed class AndroidWebViewHost
{
    private readonly WebView _webView;
    private readonly ILogger _logger;
    private readonly PermissionManagingChromeClient _chrome;
    private Insets _last = new(-1, -1, -1, -1, -1);

    private AndroidWebViewHost(WebView webView, IServiceProvider services, Action<string?> onRendererGone)
    {
        _webView = webView;
        _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SkyMessage.WebView");
        var proxy = services.GetRequiredService<WebViewProxyConfigurator>();
        proxy.Start();

        var settings = webView.Settings;
        settings.MediaPlaybackRequiresUserGesture = false;
        settings.JavaScriptCanOpenWindowsAutomatically = false;
        settings.AllowFileAccess = false;
        settings.SetSupportZoom(false);
        settings.BuiltInZoomControls = false;
        settings.DisplayZoomControls = false;
        settings.TextZoom = 100;
#if DEBUG
        // Development servers are plain http (ws://10.0.2.2:8080/livekit) while the page is https://0.0.0.1.
        settings.MixedContentMode = MixedContentHandling.AlwaysAllow;
        WebView.SetWebContentsDebuggingEnabled(true);
#else
        settings.MixedContentMode = MixedContentHandling.NeverAllow;
#endif
        webView.OverScrollMode = OverScrollMode.Never;
        var background = services.GetService<SkyMessage.Client.Core.Platform.ISystemUiService>() is AndroidSystemUiService ui
            ? ui.Background
            : Android.Graphics.Color.White;
        webView.SetBackgroundColor(background);

        _chrome = new PermissionManagingChromeClient(webView.WebChromeClient ?? new WebChromeClient(), _logger);
        webView.SetWebChromeClient(_chrome);
        var inner = webView.WebViewClient;
        webView.SetWebViewClient(new RecoveringWebViewClient(inner, () => proxy.Credentials, OnPageFinished, onRendererGone, _logger));

        ViewCompat.SetOnApplyWindowInsetsListener(webView, new InsetsListener(this));
        ViewCompat.SetWindowInsetsAnimationCallback(webView, new KeyboardAnimationCallback(this));
        webView.ViewTreeObserver?.AddOnGlobalLayoutListener(new LayoutListener(this));
        ViewCompat.RequestApplyInsets(webView);
    }

    public static AndroidWebViewHost Attach(WebView webView, IServiceProvider services, Action<string?> onRendererGone) =>
        new(webView, services, onRendererGone);

    public WebView WebView => _webView;

    /// <summary>Back: leave element fullscreen, else WebView history (closes overlays via popstate). False = nothing left.</summary>
    public bool TryGoBack()
    {
        if (_chrome.ExitFullscreenIfShown()) return true;
        if (!_webView.CanGoBack()) return false;
        _webView.GoBack();
        return true;
    }

    private void OnPageFinished(WebView view)
    {
        _last = new Insets(-1, -1, -1, -1, -1);
        Refresh(null);
    }

    private void Refresh(WindowInsetsCompat? insets)
    {
        try
        {
            insets ??= ViewCompat.GetRootWindowInsets(_webView);
            if (insets is null) return;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime());
            if (bars is null || ime is null) return;
            var density = _webView.Resources?.DisplayMetrics?.Density ?? 1f;
            var current = new Insets(
                Css(bars.Top, density),
                Css(bars.Right, density),
                Css(bars.Bottom, density),
                Css(bars.Left, density),
                Css(Math.Max(0, ime.Bottom), density));
            if (current == _last) return;
            _last = current;
            var script = string.Create(CultureInfo.InvariantCulture,
                $"(function(t,r,b,l,k){{var s=document.documentElement&&document.documentElement.style;if(!s)return;" +
                $"s.setProperty('--sm-safe-top',t+'px');s.setProperty('--sm-safe-right',r+'px');s.setProperty('--sm-safe-bottom',b+'px');" +
                $"s.setProperty('--sm-safe-left',l+'px');s.setProperty('--sm-kb-inset',k+'px');" +
                $"window.__smNativeInsets={{top:t,right:r,bottom:b,left:l,keyboard:k}};" +
                $"try{{window.dispatchEvent(new CustomEvent('sm-insets',{{detail:window.__smNativeInsets}}));}}catch(e){{}}}})" +
                $"({current.Top},{current.Right},{current.Bottom},{current.Left},{current.Keyboard});");
            _webView.EvaluateJavascript(script, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pushing insets failed");
        }
    }

    private static double Css(int px, float density) => Math.Round(px / (double)density, 1);

    private readonly record struct Insets(double Top, double Right, double Bottom, double Left, double Keyboard);

    private sealed class InsetsListener(AndroidWebViewHost host) : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(View? v, WindowInsetsCompat? insets)
        {
            if (insets is not null) host.Refresh(insets);
            return v is null || insets is null ? insets : ViewCompat.OnApplyWindowInsets(v, insets);
        }
    }

    private sealed class KeyboardAnimationCallback(AndroidWebViewHost host)
        : WindowInsetsAnimationCompat.Callback(WindowInsetsAnimationCompat.Callback.DispatchModeContinueOnSubtree)
    {
        public override WindowInsetsCompat OnProgress(WindowInsetsCompat? insets, IList<WindowInsetsAnimationCompat>? runningAnimations) => insets!;

        public override void OnEnd(WindowInsetsAnimationCompat? animation)
        {
            base.OnEnd(animation);
            host.Refresh(null);
        }
    }

    private sealed class LayoutListener(AndroidWebViewHost host) : Java.Lang.Object, ViewTreeObserver.IOnGlobalLayoutListener
    {
        public void OnGlobalLayout() => host.Refresh(null);
    }
}
