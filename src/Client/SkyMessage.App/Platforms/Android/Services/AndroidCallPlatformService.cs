using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
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
/// only for video while in the foreground, as Android 14+ requires), a partial wake lock (plus a proximity lock
/// for audio calls) and an ongoing-call notification with Mute and Hang up. When the call ends the audio routing
/// it requested is released, so later media playback is not stuck on the earpiece or a headset.
/// </summary>
public sealed class AndroidCallPlatformService(IServiceProvider services, IAppLifecycle lifecycle, NativeStrings strings,
    ILogger<AndroidCallPlatformService> logger) : ICallPlatformService
{
    private static readonly int AccentColor = unchecked((int)0xFF6A4BFF);

    private readonly object _gate = new();
    private Action<CallCommand>? _handlers;
    private ICallService? _callService;
    private bool? _notifiedMuted;

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
            HookCallService();
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
        try
        {
            Call = null;
            _notifiedMuted = null;
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

    private void HookCallService()
    {
        if (_callService is not null) return;
        _callService = services.GetService<ICallService>();
        if (_callService is null) return;
        _callService.Changed += () =>
        {
            var current = _callService.Current;
            if (Call is null || current is null) return;
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
    private static volatile bool _isForeground;
    private PowerManager.WakeLock? _cpuLock;
    private PowerManager.WakeLock? _proximityLock;

    /// <summary>True between a successful startForeground() and the service's destruction.</summary>
    internal static bool IsForeground => _isForeground;

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
        AcquireLocks(audioOnly: !platform.CameraOn);
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
        Release(ref _cpuLock);
        Release(ref _proximityLock);
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

    private void AcquireLocks(bool audioOnly)
    {
        if (GetSystemService(PowerService) is not PowerManager power) return;
        try
        {
            if (_cpuLock is null)
            {
                _cpuLock = power.NewWakeLock(WakeLockFlags.Partial, "SkyMessage:call");
                _cpuLock?.SetReferenceCounted(false);
                _cpuLock?.Acquire(4 * 60 * 60 * 1000L);
            }
            if (audioOnly && _proximityLock is null && power.IsWakeLockLevelSupported((int)WakeLockFlags.ProximityScreenOff))
            {
                _proximityLock = power.NewWakeLock(WakeLockFlags.ProximityScreenOff, "SkyMessage:proximity");
                _proximityLock?.SetReferenceCounted(false);
                _proximityLock?.Acquire(4 * 60 * 60 * 1000L);
            }
            else if (!audioOnly)
            {
                Release(ref _proximityLock);
            }
        }
        catch (Exception)
        {
            // Wake locks are best effort.
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
