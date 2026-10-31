using Android;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Microsoft.Extensions.Logging;
using View = Android.Views.View;
using WebView = Android.Webkit.WebView;

namespace SkyMessage.App.Droid;

/// <summary>
/// Wraps MAUI's chrome client (new windows, file chooser) and adds: getUserMedia grants for the app origin
/// after the matching runtime permissions, a transparent default video poster (no grey play icon), element
/// fullscreen and DEBUG console logging.
/// </summary>
internal sealed class PermissionManagingChromeClient(WebChromeClient inner, ILogger logger) : WebChromeClient
{
    private static readonly Lazy<Bitmap> TransparentPoster = new(() =>
    {
        var bitmap = Bitmap.CreateBitmap(1, 1, Bitmap.Config.Argb8888!)!;
        bitmap.EraseColor(Android.Graphics.Color.Transparent);
        return bitmap;
    });

    private View? _fullscreenView;
    private ICustomViewCallback? _fullscreenCallback;

    /// <summary>target=_blank opens externally (MAUI's behaviour) — only for user taps on http(s)/mailto/tel links.</summary>
    public override bool OnCreateWindow(WebView? view, bool isDialog, bool isUserGesture, Message? resultMsg)
    {
        var url = view?.GetHitTestResult()?.Extra;
        if (!isUserGesture || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto" or "tel"))
            return false;
        return inner.OnCreateWindow(view, isDialog, isUserGesture, resultMsg);
    }

    public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams) =>
        inner.OnShowFileChooser(webView, filePathCallback, fileChooserParams);

    public override Bitmap? DefaultVideoPoster => TransparentPoster.Value;

    public override void OnPermissionRequest(PermissionRequest? request)
    {
        if (request is null) return;
        var resources = request.GetResources() ?? [];
        if (!string.Equals(request.Origin?.Host, "0.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            request.Deny();
            return;
        }

        var wantsAudio = resources.Contains(PermissionRequest.ResourceAudioCapture);
        var wantsVideo = resources.Contains(PermissionRequest.ResourceVideoCapture);
        if (!wantsAudio && !wantsVideo)
        {
            request.Deny();
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var granted = new List<string>();
                if (wantsAudio && await EnsureAsync<Permissions.Microphone>(Manifest.Permission.RecordAudio))
                    granted.Add(PermissionRequest.ResourceAudioCapture);
                if (wantsVideo && await EnsureAsync<Permissions.Camera>(Manifest.Permission.Camera))
                    granted.Add(PermissionRequest.ResourceVideoCapture);

                if (granted.Count > 0) request.Grant([.. granted]);
                else request.Deny();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Media permission request failed");
                try
                {
                    request.Deny();
                }
                catch (Exception)
                {
                }
            }
        });
    }

    public override void OnPermissionRequestCanceled(PermissionRequest? request)
    {
    }

    public override void OnShowCustomView(View? view, ICustomViewCallback? callback)
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (view is null || activity?.Window?.DecorView is not FrameLayout decor)
        {
            callback?.OnCustomViewHidden();
            return;
        }
        if (_fullscreenView is not null) OnHideCustomView();
        _fullscreenView = view;
        _fullscreenCallback = callback;
        decor.AddView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        var controller = AndroidX.Core.View.WindowCompat.GetInsetsController(activity.Window!, decor);
        if (controller is null) return;
        controller.SystemBarsBehavior = AndroidX.Core.View.WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
        controller.Hide(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
    }

    public override void OnHideCustomView()
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (_fullscreenView is not null && activity?.Window?.DecorView is FrameLayout decor)
        {
            decor.RemoveView(_fullscreenView);
            AndroidX.Core.View.WindowCompat.GetInsetsController(activity.Window!, decor)
                ?.Show(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
        }
        _fullscreenView = null;
        var callback = _fullscreenCallback;
        _fullscreenCallback = null;
        callback?.OnCustomViewHidden();
    }

    /// <summary>True while an element is fullscreen (Back leaves fullscreen first).</summary>
    public bool ExitFullscreenIfShown()
    {
        if (_fullscreenView is null) return false;
        OnHideCustomView();
        return true;
    }

    public override bool OnConsoleMessage(ConsoleMessage? consoleMessage)
    {
#if DEBUG
        if (consoleMessage is not null)
            logger.LogDebug("[webview] {Level} {Message} ({Source}:{Line})", consoleMessage.InvokeMessageLevel(), consoleMessage.Message(),
                consoleMessage.SourceId(), consoleMessage.LineNumber());
#endif
        return base.OnConsoleMessage(consoleMessage);
    }

    private static async Task<bool> EnsureAsync<TPermission>(string androidPermission) where TPermission : Permissions.BasePermission, new()
    {
        if (AndroidPermissionService.IsGranted(androidPermission)) return true;
        return await Permissions.RequestAsync<TPermission>() == PermissionStatus.Granted;
    }
}
