using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity.Result.Contract;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using AndroidSettings = Android.Provider.Settings;
using AndroidUri = Android.Net.Uri;

namespace SkyMessage.App.Droid;

/// <summary>
/// Optional persistent foreground service (type remoteMessaging, minimum-importance notification) that keeps
/// the process — and with it Core's realtime connection — alive while the app is closed, so messages and calls
/// still arrive until push delivery exists. Restarted after reboot/update and whenever the app comes to the
/// foreground while enabled but not running.
/// </summary>
public sealed class AndroidBackgroundConnectionService(IServiceProvider services, ILogger<AndroidBackgroundConnectionService> logger)
    : IBackgroundConnectionService
{
    internal const string EnabledKey = "background.enabled";

    public bool IsSupported => true;

    public bool IsEnabled => IsEnabledStored();

    public Task SetEnabledAsync(bool enabled)
    {
        try
        {
            Preferences.Default.Set(EnabledKey, enabled, PreferencesStore.SharedName);
            var context = Android.App.Application.Context;
            if (enabled) Start(context, services, logger);
            // A start still on its way to startForeground() must not be stopped from outside (Android kills the
            // app for that): it reads the stored flag and stops itself.
            else if (BackgroundConnectionForegroundService.IsForeground)
                context.StopService(new Intent(context, typeof(BackgroundConnectionForegroundService)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Toggling the background connection failed");
        }
        return Task.CompletedTask;
    }

    public async Task<bool> RequestBatteryExemptionAsync()
    {
        var context = Android.App.Application.Context;
        try
        {
            if (IsExempt(context)) return true;
            var intent = new Intent(AndroidSettings.ActionRequestIgnoreBatteryOptimizations, AndroidUri.Parse("package:" + context.PackageName));
            await ActivityResults.LaunchAsync(new ActivityResultContracts.StartActivityForResult(), intent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Battery optimization prompt unavailable; opening the list instead");
            try
            {
                var list = new Intent(AndroidSettings.ActionIgnoreBatteryOptimizationSettings);
                await ActivityResults.LaunchAsync(new ActivityResultContracts.StartActivityForResult(), list).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }
        }
        return IsExempt(context);
    }

    internal static bool IsEnabledStored()
    {
        try
        {
            return Preferences.Default.Get(EnabledKey, false, PreferencesStore.SharedName);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Called while the app is in the foreground (MainActivity resumed): brings the service back when it is enabled
    /// but not running — its process was killed or force-stopped, or a sticky restart could not re-enter the
    /// foreground (Android 12+ refuses that from the background). Core only applies the setting when it changes.
    /// </summary>
    internal static void EnsureRunning(Context context)
    {
        if (BackgroundConnectionForegroundService.IsForeground || !IsEnabledStored()) return;
        Start(context, AppServices.Provider, AppServices.Get<ILogger<AndroidBackgroundConnectionService>>());
    }

    internal static void Start(Context context, IServiceProvider? services, ILogger? logger)
    {
        try
        {
            (services?.GetService<NotificationChannels>())?.EnsureBase();
            ContextCompat.StartForegroundService(context, new Intent(context, typeof(BackgroundConnectionForegroundService)));
        }
        catch (Exception ex)
        {
            // Android 12+ refuses foreground-service starts from the background outside the allowed cases.
            logger?.LogWarning(ex, "Background connection service could not start");
        }
    }

    private static bool IsExempt(Context context) =>
        context.GetSystemService(Context.PowerService) is PowerManager power && power.IsIgnoringBatteryOptimizations(context.PackageName);
}

[Service(Name = "com.skymessage.messenger.ConnectionService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeRemoteMessaging)]
public sealed class BackgroundConnectionForegroundService : Service
{
    private static volatile bool _isForeground;

    /// <summary>True between a successful startForeground() and the service's destruction.</summary>
    internal static bool IsForeground => _isForeground;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        _isForeground = false;
        base.OnDestroy();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Enter the foreground first, even when about to stop: a service started with startForegroundService()
        // that stops before calling startForeground() crashes the app.
        try
        {
            var strings = AppServices.Get<NativeStrings>();
            EnsureChannel();
            var content = AppIntents.ActivityPendingIntent(this, 41, AppIntents.ActivityIntent(this, null));
            var notification = new NotificationCompat.Builder(this, NotificationChannels.Background)
                .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
                .SetColor(unchecked((int)0xFF6A4BFF))!
                .SetContentTitle(strings?["platform.background.title"] ?? "SkyMessage")!
                .SetContentText(strings?["platform.background.text"] ?? "")!
                .SetContentIntent(content)!
                .SetOngoing(true)!
                .SetShowWhen(false)!
                .SetPriority(NotificationCompat.PriorityMin)!
                .SetCategory(NotificationCompat.CategoryService)!
                .SetVisibility(NotificationCompat.VisibilitySecret)!
                .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceDeferred)!
                .Build()!;
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                ServiceCompat.StartForeground(this, AppIntents.BackgroundNotificationId, notification, (int)ForegroundService.TypeRemoteMessaging);
            else
                StartForeground(AppIntents.BackgroundNotificationId, notification);
            _isForeground = true;
        }
        catch (Exception)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (!AndroidBackgroundConnectionService.IsEnabledStored())
        {
            try
            {
                ServiceCompat.StopForeground(this, ServiceCompat.StopForegroundRemove);
            }
            catch (Exception)
            {
            }
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Started without UI (boot, update, restart): bring Core up so realtime connects.
        _ = Task.Run(async () =>
        {
            try
            {
                if (AppServices.Get<IClientBootstrapper>() is { } bootstrapper) await bootstrapper.StartAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Core logs its own failures; the service stays up and retries on the next start.
            }
        });
        return StartCommandResult.Sticky;
    }

    private void EnsureChannel()
    {
        if (AppServices.Get<NotificationChannels>() is { } channels)
        {
            channels.EnsureBase();
            return;
        }
        // No service provider (process shutting down): a bare channel keeps startForeground() valid.
        if (GetSystemService(NotificationService) is NotificationManager manager && manager.GetNotificationChannel(NotificationChannels.Background) is null)
            manager.CreateNotificationChannel(new NotificationChannel(NotificationChannels.Background, "SkyMessage", NotificationImportance.Min));
    }
}

/// <summary>Restarts the background connection after reboot or an app update when the user enabled it.</summary>
[BroadcastReceiver(Name = "com.skymessage.messenger.BootReceiver", Exported = false, Enabled = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced, "android.intent.action.QUICKBOOT_POWERON"])]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || !AndroidBackgroundConnectionService.IsEnabledStored()) return;
        AndroidBackgroundConnectionService.Start(context, AppServices.Provider, null);
    }
}
