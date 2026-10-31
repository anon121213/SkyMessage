using Android.App;
using Android.Content;
using Android.Runtime;
using Android.OS;
using Android.Views;
using AndroidX.Activity;
using ActivityFlags = Android.Content.ActivityFlags;
using SkyMessage.App.Droid;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;

namespace SkyMessage.App;

// Declared in AndroidManifest.xml (launch mode, full configChanges incl. colorMode, intent filters).
[Register("com.skymessage.messenger.MainActivity")]
public class MainActivity : MauiAppCompatActivity
{
    private bool _showingOverLockScreen;
    private Guid? _lockScreenCallId;
    private BackCallback? _backCallback;
    private ICallService? _calls;
    private Action? _callChanged;
    private (Guid CallId, CallNotificationAction Action)? _pendingCallAction;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _backCallback = new BackCallback(this);
        OnBackPressedDispatcher.AddCallback(this, _backCallback);
        (AppServices.Get<ISystemUiService>() as AndroidSystemUiService)?.Apply(this);

        // A recreated activity (or a relaunch from recents) must not replay an old deep link / call action.
        var intent = Intent;
        var fromHistory = intent is not null && (intent.Flags & ActivityFlags.LaunchedFromHistory) != 0;
        if (savedInstanceState is null && !fromHistory) HandleIntent(intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is null) return;
        Intent = intent;
        HandleIntent(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        // In the foreground a foreground service may start: revive the background connection if its process died.
        AndroidBackgroundConnectionService.EnsureRunning(ApplicationContext ?? this);
        // Accept actions are delivered once the activity is resumed, i.e. the app is in the foreground, so the
        // call's microphone/camera foreground service is allowed to start (Android 14+ rule).
        if (_pendingCallAction is { } pending)
        {
            _pendingCallAction = null;
            (AppServices.Get<ILocalNotificationService>() as AndroidNotificationService)?.OnCallAction(pending.CallId, pending.Action);
        }
    }

    protected override void OnDestroy()
    {
        if (_calls is not null && _callChanged is not null) _calls.Changed -= _callChanged;
        _calls = null;
        _callChanged = null;
        base.OnDestroy();
    }

    /// <summary>
    /// The dispatcher is LIFO and every BlazorWebView handler registers its own Back callback when it connects;
    /// after the WebView is rebuilt ours is re-added so overlays/fullscreen keep closing first.
    /// </summary>
    internal void KeepBackCallbackOnTop()
    {
        if (_backCallback is null || IsFinishing || IsDestroyed) return;
        _backCallback.Remove();
        OnBackPressedDispatcher.AddCallback(this, _backCallback);
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        (AppServices.Get<ISystemUiService>() as AndroidSystemUiService)?.Apply(this);
    }

    private void HandleIntent(Intent? intent)
    {
        if (intent is null) return;
        var notifications = AppServices.Get<ILocalNotificationService>() as AndroidNotificationService;
        switch (intent.Action)
        {
            case AppIntents.ActionCallAccept when AppIntents.CallIdOf(intent) is { } callId:
                ShowOverLockScreenFor(callId);
                _ = notifications?.CancelCallAsync(callId);
                _pendingCallAction = (callId, CallNotificationAction.Accept);
                break;
            case AppIntents.ActionCallAcceptVideo when AppIntents.CallIdOf(intent) is { } callId:
                ShowOverLockScreenFor(callId);
                _ = notifications?.CancelCallAsync(callId);
                _pendingCallAction = (callId, CallNotificationAction.AcceptVideo);
                break;
            case AppIntents.ActionIncomingCall:
            case AppIntents.ActionOngoingCall:
                ShowOverLockScreenFor(AppIntents.CallIdOf(intent));
                break;
            default:
                if (intent.Data is { } data) (AppServices.Get<IDeepLinkSource>() as DeepLinkSource)?.Publish(data.ToString());
                break;
        }
    }

    /// <summary>
    /// Incoming/ongoing calls are shown over the lock screen. MainActivity is exported and a notification can outlive
    /// its call, so a call intent alone proves nothing: the call it names must ring or run in ICallService (after a
    /// cold start from the notification it gets there a moment later). Without one the lock screen stays in charge.
    /// </summary>
    private void ShowOverLockScreenFor(Guid? callId)
    {
        if (AppServices.Get<ICallService>() is not { } calls) return;
        HookCallChanges(calls);
        _lockScreenCallId = callId ?? calls.Current?.Call.Id;
        UpdateLockScreen();
    }

    /// <summary>UI thread. On once the named call is here; off when no call remains (one ringing right after keeps it on).</summary>
    private void UpdateLockScreen()
    {
        if (_calls is not { } calls) return;
        if (_showingOverLockScreen)
        {
            if (calls.Current is not null || calls.Incoming is not null) return;
            _lockScreenCallId = null;
            ShowOverLockScreen(false);
        }
        else if (_lockScreenCallId is { } id && id != Guid.Empty && (calls.Incoming?.Call.Id == id || calls.Current?.Call.Id == id))
        {
            ShowOverLockScreen(true);
        }
    }

    private void ShowOverLockScreen(bool show)
    {
        if (show == _showingOverLockScreen) return;
        _showingOverLockScreen = show;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(27))
            {
                SetShowWhenLocked(show);
                SetTurnScreenOn(show);
            }
            else if (show)
            {
#pragma warning disable CA1422
                Window?.AddFlags(WindowManagerFlags.ShowWhenLocked | WindowManagerFlags.TurnScreenOn);
#pragma warning restore CA1422
            }
            else
            {
#pragma warning disable CA1422
                Window?.ClearFlags(WindowManagerFlags.ShowWhenLocked | WindowManagerFlags.TurnScreenOn);
#pragma warning restore CA1422
            }
        }
        catch (Exception)
        {
        }
    }

    private void HookCallChanges(ICallService calls)
    {
        if (_calls is not null) return;
        _calls = calls;
        _callChanged = () => RunOnUiThread(UpdateLockScreen);
        calls.Changed += _callChanged;
    }

    /// <summary>Back closes overlays/screens through WebView history; at the root the app goes to background (never finishes).</summary>
    private sealed class BackCallback(MainActivity activity) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page as MainPage;
            if (page?.TryGoBack() == true) return;
            activity.MoveTaskToBack(true);
        }
    }
}
