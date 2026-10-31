using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Person = AndroidX.Core.App.Person;

namespace SkyMessage.App.Droid;

/// <summary>
/// Keeps calls alive: a foreground service whose type is derived from granted permissions (microphone; camera
/// only for video while in the foreground, as Android 14+ requires), a partial wake lock, a proximity lock while the
/// call is held to the ear (<see cref="WantsProximityLock"/>) and an ongoing-call notification with Mute and Hang up.
/// When the call ends the audio routing it requested is released, so later media playback is not stuck on the
/// earpiece or a headset.
/// </summary>
public sealed class AndroidCallPlatformService(IServiceProvider services, IAppLifecycle lifecycle, NativeStrings strings,
    ILogger<AndroidCallPlatformService> logger) : ICallPlatformService
{
    private static readonly int AccentColor = unchecked((int)0xFF6A4BFF);

    private readonly object _gate = new();
    private Action<CallCommand>? _handlers;
    private ICallService? _callService;
    private AndroidAudioRouteService? _routes;
    private bool? _notifiedMuted;
    private bool? _seenCameraOn;
    private bool _keepAwake;

    internal CallDto? Call { get; private set; }

    internal string Title { get; private set; } = "";

    internal bool IsVideo { get; private set; }

    internal bool CameraOn { get; private set; }

    internal DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;

    public event Action<CallCommand>? CommandRequested
    {
        add { lock (_gate) _handlers += value; }
        remove { lock (_gate) _handlers -= value; }
    }

    public Task<bool> OnCallStartedAsync(CallDto call, string title, bool video)
    {
        try
        {
            if (!AndroidPermissionService.IsGranted(Manifest.Permission.RecordAudio)) return Task.FromResult(false);
            Call = call;
            Title = string.IsNullOrWhiteSpace(title) ? "SkyMessage" : title;
            IsVideo = video;
            CameraOn = video;
            StartedAt = DateTimeOffset.UtcNow;
            _seenCameraOn = null;
            HookCallService();
            HookRoutes();
            return Task.FromResult(StartService());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Starting the call service failed");
            return Task.FromResult(false);
        }
    }

    public Task<bool> OnCallMediaChangedAsync(bool cameraOn)
    {
        try
        {
            if (Call is null) return Task.FromResult(false);
            CameraOn = cameraOn;
            if (cameraOn) IsVideo = true;
            UpdateProximity();
            if (!lifecycle.IsForeground) return Task.FromResult(false);
            return Task.FromResult(StartService());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Updating the call service failed");
            return Task.FromResult(false);
        }
    }

    public Task OnCallEndedAsync()
    {
        UpdateKeepAwake(false);
        try
        {
            Call = null;
            _notifiedMuted = null;
            _seenCameraOn = null;
            var context = Android.App.Application.Context;
            // A start that has not reached startForeground() yet must not be stopped from outside (Android kills
            // the app for that); the service sees Call == null and stops itself right after entering foreground.
            if (CallForegroundService.IsForeground) context.StopService(new Intent(context, typeof(CallForegroundService)));
            NotificationManagerCompat.From(context)?.Cancel(AppIntents.OngoingCallNotificationId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Stopping the call service failed");
        }
        try
        {
            (services.GetService<IAudioRouteService>() as AndroidAudioRouteService)?.Reset();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Releasing the call audio route failed");
        }
        return Task.CompletedTask;
    }

    internal void RaiseCommand(CallCommand command)
    {
        Action<CallCommand>? handlers;
        lock (_gate) handlers = _handlers;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<CallCommand>)handler)(command);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Call command handler failed");
            }
        }
    }

    /// <summary>Foreground service types allowed right now (typed foreground services exist from Android 11).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    internal ForegroundService ServiceTypes()
    {
        var types = ForegroundService.TypeMicrophone;
        if (Call is not null && CameraOn && lifecycle.IsForeground && AndroidPermissionService.IsGranted(Manifest.Permission.Camera))
            types |= ForegroundService.TypeCamera;
        return types;
    }

    internal bool IsMuted => _callService?.Current?.IsMicMuted ?? false;

    /// <summary>
    /// Whether the proximity screen-off lock belongs to the call right now
    /// (<see cref="CallScreenPolicy.HoldsProximityLock"/>): call audio on the earpiece and my camera off. Route and
    /// audio mode are read from the system, so the speaker button, a headset plugged in or the WebView picking another
    /// device all count.
    /// </summary>
    internal bool WantsProximityLock
    {
        get
        {
            if (Call is null || _routes is not { } routes) return false;
            var cameraOn = _callService?.Current?.IsCameraOn ?? CameraOn;
            return CallScreenPolicy.HoldsProximityLock(routes.CurrentKind, cameraOn, routes.IsInCommunication);
        }
    }

    /// <summary>Re-checks the proximity lock (route or camera changed); the service applies it on the main thread.</summary>
    internal void UpdateProximity()
    {
        if (Call is null && !CallForegroundService.IsForeground) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                CallForegroundService.ApplyProximity(WantsProximityLock);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Updating the proximity lock failed");
            }
        });
    }

    /// <summary>
    /// Keeps the app's window from timing out while the call shows a picture
    /// (<see cref="CallScreenPolicy.KeepsScreenAwake"/>): call video plays muted (the voice comes through separate
    /// audio elements) and the WebView's own screen wake handling cannot be relied on for it, so someone watching a
    /// face or a shared screen would see the screen go dark.
    /// </summary>
    private void UpdateKeepAwake(bool keep)
    {
        lock (_gate)
        {
            if (keep == _keepAwake) return;
            _keepAwake = keep;
        }
        MainThread.BeginInvokeOnMainThread(() => ApplyKeepAwake(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity));
    }

    /// <summary>Whether the call keeps the screen on right now (<see cref="UpdateKeepAwake"/>).</summary>
    internal bool KeepsScreenAwake => _keepAwake;

    /// <summary>Sets or clears the keep-screen-on flag of <paramref name="activity"/>'s window (main thread).</summary>
    internal void ApplyKeepAwake(Activity? activity)
    {
        try
        {
            if (activity?.Window is not { } window) return;
            if (_keepAwake) window.AddFlags(WindowManagerFlags.KeepScreenOn);
            else window.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Keeping the screen on failed");
        }
    }

    /// <summary>The user swiped the app away mid-call: the WebView (media) is gone, so end the call cleanly.</summary>
    internal void OnTaskRemoved()
    {
        try
        {
            var callService = services.GetService<ICallService>();
            if (callService?.Current is not null) _ = callService.HangupAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Hang up on task removal failed");
        }
    }

    internal Notification BuildOngoingNotification(Context context)
    {
        var muted = IsMuted;
        _notifiedMuted = muted;
        var hangup = AppIntents.BroadcastPendingIntent(context, 31, AppIntents.ActionCallHangup);
        var mute = AppIntents.BroadcastPendingIntent(context, 32, AppIntents.ActionCallToggleMute);
        var content = AppIntents.ActivityPendingIntent(context, 33, AppIntents.ActivityIntent(context, AppIntents.ActionOngoingCall, Call?.Id));
        var person = new Person.Builder().SetName(Title)!.SetImportant(true)!.Build()!;

        return new NotificationCompat.Builder(context, NotificationChannels.OngoingCall)
            .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
            .SetColor(AccentColor)!
            .SetContentTitle(Title)!
            .SetContentText(strings[IsVideo ? "platform.call.ongoingVideo" : "platform.call.ongoing"])!
            .SetSubText(strings["platform.call.tapToReturn"])!
            .SetContentIntent(content)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetCategory(NotificationCompat.CategoryCall)!
            .SetVisibility(NotificationCompat.VisibilityPublic)!
            .SetUsesChronometer(true)!
            .SetWhen(StartedAt.ToUnixTimeMilliseconds())!
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)!
            .SetStyle(NotificationCompat.CallStyle.ForOngoingCall(person, hangup)!.SetIsVideo(IsVideo))!
            .AddAction(new NotificationCompat.Action(0, strings[muted ? "platform.call.unmute" : "platform.call.mute"], mute))!
            .Build()!;
    }

    /// <summary>Notification for a start that arrives after the call already ended (shown for an instant).</summary>
    internal Notification BuildEndedNotification(Context context) =>
        new NotificationCompat.Builder(context, NotificationChannels.OngoingCall)
            .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
            .SetColor(AccentColor)!
            .SetContentTitle(string.IsNullOrWhiteSpace(Title) ? "SkyMessage" : Title)!
            .SetOngoing(false)!
            .SetSilent(true)!
            .SetCategory(NotificationCompat.CategoryCall)!
            .Build()!;

    internal void EnsureChannels()
    {
        try
        {
            services.GetRequiredService<NotificationChannels>().EnsureBase();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Notification channels unavailable");
        }
    }

    private bool StartService()
    {
        var context = Android.App.Application.Context;
        try
        {
            EnsureChannels();
            ContextCompat.StartForegroundService(context, new Intent(context, typeof(CallForegroundService)));
            return true;
        }
        catch (Exception ex)
        {
            // ForegroundServiceStartNotAllowedException (background start) or missing permission.
            logger.LogWarning(ex, "The call foreground service could not start");
            return false;
        }
    }

    /// <summary>
    /// Route changes (the in-call speaker button, headsets coming and going) and the WebView entering or leaving
    /// communication mode (microphone opened or closed) move the proximity lock.
    /// </summary>
    private void HookRoutes()
    {
        if (_routes is not null) return;
        _routes = services.GetService<IAudioRouteService>() as AndroidAudioRouteService;
        if (_routes is null) return;
        _routes.RoutesChanged += UpdateProximity;
        _routes.ModeChanged += UpdateProximity;
    }

    private void HookCallService()
    {
        if (_callService is not null) return;
        _callService = services.GetService<ICallService>();
        if (_callService is null) return;
        _callService.Changed += () =>
        {
            var current = _callService.Current;
            UpdateKeepAwake(Call is not null && CallScreenPolicy.KeepsScreenAwake(current));
            if (Call is null || current is null) return;
            // The camera turned on or off (also in background, where OnCallMediaChangedAsync is not called).
            if (current.IsCameraOn != _seenCameraOn)
            {
                _seenCameraOn = current.IsCameraOn;
                UpdateProximity();
            }
            if (current.IsMicMuted == _notifiedMuted) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    if (Call is null || !CallForegroundService.IsForeground) return;
                    var context = Android.App.Application.Context;
                    NotificationManagerCompat.From(context)?.Notify(AppIntents.OngoingCallNotificationId, BuildOngoingNotification(context));
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Ongoing call notification update failed");
                }
            });
        };
    }
}

[Service(Name = "com.skymessage.messenger.CallService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeMicrophone | ForegroundService.TypeCamera)]
public sealed class CallForegroundService : Service
{
    /// <summary>How often Android 11 and older look at the proximity lock again (see <see cref="StartRechecks"/>).</summary>
    private const long RecheckMs = 2000;

    private static volatile bool _isForeground;
    private static CallForegroundService? _running;
    private PowerManager.WakeLock? _cpuLock;
    private PowerManager.WakeLock? _proximityLock;
    private Handler? _handler;
    private Action? _recheck;

    /// <summary>True between a successful startForeground() and the service's destruction.</summary>
    internal static bool IsForeground => _isForeground;

    /// <summary>Takes or lets go of the proximity lock of the running call service (main thread).</summary>
    internal static void ApplyProximity(bool hold) => _running?.SetProximity(hold);

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var platform = AppServices.Get<ICallPlatformService>() as AndroidCallPlatformService;
        // Every start must reach startForeground(), even one for a call that already ended: a service started with
        // startForegroundService() that stops before that call crashes the app.
        try
        {
            platform?.EnsureChannels();
            var active = platform?.Call is not null;
            var notification = active ? platform!.BuildOngoingNotification(this) : BuildFallback(platform);
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var types = platform?.ServiceTypes() ?? ForegroundService.TypeMicrophone;
                ServiceCompat.StartForeground(this, AppIntents.OngoingCallNotificationId, notification, (int)types);
            }
            else
            {
                StartForeground(AppIntents.OngoingCallNotificationId, notification);
            }
            _isForeground = true;
            _running = this;
        }
        catch (Exception)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (platform?.Call is null)
        {
            StopNow();
            return StartCommandResult.NotSticky;
        }
        AcquireCpuLock();
        SetProximity(platform.WantsProximityLock);
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) StartRechecks();
        return StartCommandResult.NotSticky;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        (AppServices.Get<ICallPlatformService>() as AndroidCallPlatformService)?.OnTaskRemoved();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        _isForeground = false;
        if (ReferenceEquals(_running, this)) _running = null;
        if (_recheck is { } recheck) _handler?.RemoveCallbacks(recheck);
        _recheck = null;
        Release(ref _cpuLock);
        ReleaseProximity();
        base.OnDestroy();
    }

    private Notification BuildFallback(AndroidCallPlatformService? platform)
    {
        if (platform is not null) return platform.BuildEndedNotification(this);
        // The app is shutting down (no services): a bare channel keeps startForeground() valid.
        if (GetSystemService(NotificationService) is NotificationManager manager && manager.GetNotificationChannel(NotificationChannels.OngoingCall) is null)
            manager.CreateNotificationChannel(new NotificationChannel(NotificationChannels.OngoingCall, "SkyMessage", NotificationImportance.Low));
        return new NotificationCompat.Builder(this, NotificationChannels.OngoingCall)
            .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
            .SetContentTitle("SkyMessage")!
            .SetSilent(true)!
            .Build()!;
    }

    private void StopNow()
    {
        try
        {
            ServiceCompat.StopForeground(this, ServiceCompat.StopForegroundRemove);
        }
        catch (Exception)
        {
        }
        StopSelf();
    }

    private void AcquireCpuLock()
    {
        if (_cpuLock is not null || GetSystemService(PowerService) is not PowerManager power) return;
        try
        {
            _cpuLock = power.NewWakeLock(WakeLockFlags.Partial, "SkyMessage:call");
            _cpuLock?.SetReferenceCounted(false);
            _cpuLock?.Acquire(4 * 60 * 60 * 1000L);
        }
        catch (Exception)
        {
            // Wake locks are best effort.
        }
    }

    /// <summary>
    /// The proximity lock turns the screen off while something is near the sensor (the phone at the ear). Only a call
    /// held to the ear has it (AndroidCallPlatformService.WantsProximityLock): on the speaker, a headset or with the
    /// camera on, the screen stays on.
    /// </summary>
    private void SetProximity(bool hold)
    {
        if (!hold)
        {
            ReleaseProximity();
            return;
        }
        if (_proximityLock is not null || GetSystemService(PowerService) is not PowerManager power) return;
        try
        {
            if (!power.IsWakeLockLevelSupported((int)WakeLockFlags.ProximityScreenOff)) return;
            _proximityLock = power.NewWakeLock(WakeLockFlags.ProximityScreenOff, "SkyMessage:proximity");
            _proximityLock?.SetReferenceCounted(false);
            _proximityLock?.Acquire(4 * 60 * 60 * 1000L);
        }
        catch (Exception)
        {
            // Wake locks are best effort.
        }
    }

    /// <summary>
    /// Android 11 and older tell nobody when the WebView turns the speakerphone on or off or enters communication mode
    /// (Android 12+ report both, see AndroidAudioRouteService), so while the call runs the proximity lock is looked at
    /// again every <see cref="RecheckMs"/> on top of the events: a lock taken or dropped by such a switch follows within
    /// two seconds instead of staying wrong for the rest of the call.
    /// </summary>
    private void StartRechecks()
    {
        if (_recheck is not null) return;
        _handler ??= new Handler(Looper.MainLooper!);
        _recheck = Recheck;
        _handler.PostDelayed(_recheck, RecheckMs);
    }

    private void Recheck()
    {
        var recheck = _recheck;
        if (recheck is null) return;
        var platform = AppServices.Get<ICallPlatformService>() as AndroidCallPlatformService;
        if (!_isForeground || platform?.Call is null)
        {
            // The call is over (the service stops next); a later start schedules the checks anew.
            _recheck = null;
            return;
        }
        SetProximity(platform.WantsProximityLock);
        _handler?.PostDelayed(recheck, RecheckMs);
    }

    /// <summary>
    /// Lets go of the proximity lock. A screen that is dark because the phone is at the ear stays dark until it is taken
    /// away, so switching to a headset (or the call ending) never lights it up against a cheek or in a pocket.
    /// </summary>
    private void ReleaseProximity()
    {
        var wakeLock = _proximityLock;
        _proximityLock = null;
        try
        {
            if (wakeLock?.IsHeld == true) wakeLock.Release(WakeLockFlags.ReleaseFlagWaitForNoProximity);
        }
        catch (Exception)
        {
        }
    }

    private static void Release(ref PowerManager.WakeLock? wakeLock)
    {
        try
        {
            if (wakeLock?.IsHeld == true) wakeLock.Release();
        }
        catch (Exception)
        {
        }
        wakeLock = null;
    }
}
