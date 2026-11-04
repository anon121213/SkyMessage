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
using SkyMessage.Client.Core.Services;
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
        // A recreated activity has a fresh window: a protected chat still on screen (or the passcode lock) keeps it out of
        // screenshots, and with a passcode set the recents picture stays blank.
        (AppServices.Get<IScreenSecurityService>() as AndroidScreenSecurityService)?.Apply(this);
        (AppServices.Get<IAppLockPlatform>() as AndroidAppLockPlatform)?.Apply(this);

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

    protected override void OnStart()
    {
        base.OnStart();
        LockIfOverKeyguard();
    }

    protected override void OnPause()
    {
        // Android 12 and older with a passcode set: secure while paused, so the recents picture shows no chats.
        (AppServices.Get<IScreenSecurityService>() as AndroidScreenSecurityService)?.SetPaused(this, true);
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        LockIfOverKeyguard();
        (AppServices.Get<IScreenSecurityService>() as AndroidScreenSecurityService)?.SetPaused(this, false);
        // In the foreground a foreground service may start: revive the background connection if its process died.
        AndroidBackgroundConnectionService.EnsureRunning(ApplicationContext ?? this);
        // A call showing a picture keeps the screen on, also on a window created meanwhile.
        if (AppServices.Get<ICallPlatformService>() is AndroidCallPlatformService { KeepsScreenAwake: true } calls)
            calls.ApplyKeepAwake(this);
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
        LockIfOverKeyguard();
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

    /// <summary>
    /// Over the phone's own lock screen only the call may show: with an app passcode set, the chats stay locked behind it
    /// for whoever holds the phone (minimizing the call shows our lock screen). Checked whenever the activity comes back,
    /// not only when it starts showing over the lock screen: a call accepted on an unlocked phone stays showable after
    /// the phone locks during it, and the screen coming on again must not reopen the chats.
    /// </summary>
    private void LockIfOverKeyguard()
    {
        if (!_showingOverLockScreen) return;
        try
        {
            if (GetSystemService(KeyguardService) is KeyguardManager { IsKeyguardLocked: true })
                AppServices.Get<IAppLockService>()?.LockNow();
        }
        catch (Exception)
        {
            // The keyguard state could not be read: the auto-lock still applies.
        }
    }

    private void HookCallChanges(ICallService calls)
    {
        if (_calls is not null) return;
        _calls = calls;
        _callChanged = () => RunOnUiThread(UpdateLockScreen);
        calls.Changed += _callChanged;
    }

    /// <summary>
    /// Back closes overlays/screens through WebView history; at the root the app goes to background (never finishes). The
    /// passcode lock screen has nothing behind it to go back to: Back closes its "Forgot the passcode?" question, else it
    /// leaves the app (the call screen shown over it still minimizes first).
    /// </summary>
    private sealed class BackCallback(MainActivity activity) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
            if (AppServices.Get<IAppLockService>() is { } appLock && (appLock.IsLocked || !appLock.IsInitialized)
                && !SkyMessage.Client.UI.Features.Calls.CallUi.IsCovering)
            {
                if (SkyMessage.Client.UI.Features.Passcode.LockUi.TryBack()) return;
                activity.MoveTaskToBack(true);
                return;
            }
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page as MainPage;
            if (page?.TryGoBack() == true) return;
            activity.MoveTaskToBack(true);
        }
    }
}
