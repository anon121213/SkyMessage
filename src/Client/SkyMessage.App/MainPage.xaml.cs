using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI;
using SkyMessage.Contracts;

namespace SkyMessage.App;

public partial class MainPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly MediaProxy _mediaProxy;
    private readonly ILogger<MainPage> _logger;
    private BlazorWebView? _webView;
    private CancellationTokenSource _webViewLifetime = new();
    private bool _rebuilding;
#if ANDROID
    private SkyMessage.App.Droid.AndroidWebViewHost? _androidHost;
#elif IOS || MACCATALYST
    private SkyMessage.App.Apple.AppleWebViewHost? _appleHost;
#endif

    public MainPage(IServiceProvider services, MediaProxy mediaProxy, ILogger<MainPage> logger)
    {
        _services = services;
        _mediaProxy = mediaProxy;
        _logger = logger;
        InitializeComponent();
        CreateWebView("/");
    }

    /// <summary>Android Back: element fullscreen → WebView history (overlays close on popstate). False = at the root.</summary>
    public bool TryGoBack()
    {
#if ANDROID
        return _androidHost?.TryGoBack() ?? false;
#else
        return false;
#endif
    }

    private void CreateWebView(string startPath)
    {
        var webView = new BlazorWebView
        {
            HostPage = "wwwroot/index.html",
            StartPath = startPath,
        };
        webView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        webView.WebResourceRequested += OnWebResourceRequested;
        webView.UrlLoading += OnUrlLoading;
        webView.BlazorWebViewInitializing += OnWebViewInitializing;
        webView.BlazorWebViewInitialized += OnWebViewInitialized;
        _webView = webView;
        Root.Children.Add(webView);
    }

    private void OnWebResourceRequested(object? sender, WebViewWebResourceRequestedEventArgs e) =>
        _mediaProxy.TryHandle(e, _webViewLifetime.Token);

    private void OnUrlLoading(object? sender, UrlLoadingEventArgs e)
    {
        var url = e.Url;
        if (string.Equals(url.Scheme, DeepLinks.AppScheme, StringComparison.OrdinalIgnoreCase))
        {
            e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
            (_services.GetService<IDeepLinkSource>() as DeepLinkSource)?.Publish(url);
            return;
        }
        if (string.Equals(url.Host, "0.0.0.1", StringComparison.OrdinalIgnoreCase)) return;

        // Profile/invite links of our own server open in-app.
        if (IsOwnServer(url) && DeepLinks.TryParse(url, out _))
        {
            e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
            (_services.GetService<IDeepLinkSource>() as DeepLinkSource)?.Publish(url);
            return;
        }

        // Everything else leaves the app through the allow-listed launcher (never media URLs, never other schemes).
        e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
        if (url.Scheme is "http" or "https" or "mailto" or "tel")
            _ = _services.GetRequiredService<IExternalLauncher>().OpenUriAsync(url);
    }

    private void OnWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
#if IOS || MACCATALYST
        e.Configuration.AllowsInlineMediaPlayback = true;
        e.Configuration.MediaTypesRequiringUserActionForPlayback = WebKit.WKAudiovisualMediaTypes.None;
#endif
    }

    private void OnWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        try
        {
#if ANDROID
            _androidHost = SkyMessage.App.Droid.AndroidWebViewHost.Attach(e.WebView, _services,
                lastUrl => MainThread.BeginInvokeOnMainThread(() => RebuildWebView(lastUrl)));
#elif IOS || MACCATALYST
            _appleHost = SkyMessage.App.Apple.AppleWebViewHost.Attach(e.WebView, this, _services,
                () => MainThread.BeginInvokeOnMainThread(() => RebuildWebView(null)));
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebView platform setup failed");
        }
    }

    /// <summary>
    /// The WebView's content process died: replace the BlazorWebView. Core singletons keep their state; the UI
    /// restarts on the same route and an unfinished call re-joins with fresh LiveKit credentials.
    /// </summary>
    private void RebuildWebView(string? lastUrl)
    {
        if (_rebuilding) return;
        _rebuilding = true;
        try
        {
            var old = _webView;
            _webViewLifetime.Cancel();
            _webViewLifetime.Dispose();
            _webViewLifetime = new CancellationTokenSource();
            if (old is not null)
            {
                old.WebResourceRequested -= OnWebResourceRequested;
                old.UrlLoading -= OnUrlLoading;
                old.BlazorWebViewInitializing -= OnWebViewInitializing;
                old.BlazorWebViewInitialized -= OnWebViewInitialized;
                var platformView = old.Handler?.PlatformView;
                try
                {
                    old.Handler?.DisconnectHandler();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Disconnecting the old WebView failed");
                }
                Root.Children.Remove(old);
#if ANDROID
                try
                {
                    (platformView as Android.Webkit.WebView)?.Destroy();
                }
                catch (Exception)
                {
                }
                _androidHost = null;
#elif IOS || MACCATALYST
                _appleHost?.Detach();
                _appleHost = null;
                (platformView as UIKit.UIView)?.RemoveFromSuperview();
#endif
            }

            CreateWebView(StartPathOf(lastUrl));
#if ANDROID
            // The new handler registered its own Back callback while connecting (synchronously on Add); re-add ours
            // now and once more after this main-loop turn in case the platform view was created lazily.
            (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity)?.KeepBackCallbackOnTop();
            MainThread.BeginInvokeOnMainThread(() =>
                (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity)?.KeepBackCallbackOnTop());
#endif
            RejoinCall();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rebuilding the WebView failed");
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void RejoinCall()
    {
        try
        {
            var calls = _services.GetService<ICallService>();
            var session = calls?.Current;
            if (calls is null || session is null || session.Phase is CallPhase.Ended or CallPhase.Idle) return;
            _ = calls.JoinAsync(session.Call.Id, session.IsCameraOn);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Re-joining the call after a WebView restart failed");
        }
    }

    private bool IsOwnServer(Uri url)
    {
        try
        {
            var endpoints = _services.GetService<SkyMessage.Client.Core.Services.IServerEndpointService>();
            if (endpoints is null) return false;
            if (string.Equals(url.Host, endpoints.BaseUri.Host, StringComparison.OrdinalIgnoreCase)) return true;
            return endpoints.Info?.PublicBaseUrl is { } publicBase
                   && Uri.TryCreate(publicBase, UriKind.Absolute, out var parsed)
                   && string.Equals(url.Host, parsed.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string StartPathOf(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "/";
        if (!string.Equals(uri.Host, "0.0.0.1", StringComparison.OrdinalIgnoreCase)) return "/";
        var path = uri.AbsolutePath;
        return string.IsNullOrEmpty(path) || path.StartsWith("/_", StringComparison.Ordinal) ? "/" : path;
    }
}
