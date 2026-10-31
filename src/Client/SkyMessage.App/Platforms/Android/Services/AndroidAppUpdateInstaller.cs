using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Activity.Result.Contract;
using AndroidX.Core.App;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using AndroidSettings = Android.Provider.Settings;
using AndroidUri = Android.Net.Uri;

namespace SkyMessage.App.Droid;

/// <summary>
/// Self-updates through PackageInstaller sessions. Core downloads packages into the cache dir (updates/); a session
/// installs one over the running app. On Android 12+ the session asks for USER_ACTION_NOT_REQUIRED (the manifest
/// declares UPDATE_PACKAGES_WITHOUT_USER_ACTION), so once the user allowed "Install unknown apps" an update of SkyMessage
/// by itself installs without a confirmation. Otherwise Android answers STATUS_PENDING_USER_ACTION and its confirmation
/// is shown — only from the foreground, since background activity starts are blocked; Core then asks again later.
/// Replacing the package ends this process; an install the user started from the app posts a notification afterwards
/// so the new build is one tap away (the app cannot relaunch itself).
/// </summary>
public sealed class AndroidAppUpdateInstaller(IAppLifecycle lifecycle, ILogger<AndroidAppUpdateInstaller> logger) : IAppUpdateInstaller
{
    internal const string ActionInstallStatus = "com.skymessage.action.UPDATE_INSTALL_STATUS";
    private const string RelaunchKey = "update.relaunch.ticks";
    private static readonly TimeSpan FirstStatusTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RelaunchWindow = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<AppInstallResult>> _firstStatus = [];

    public event Action<AppInstallResult>? InstallStatusChanged;

    public bool IsSupported => true;

    public int InstalledVersionCode => int.TryParse(AppInfo.Current.BuildString, out var code) ? code : 0;

    public bool CanInstallPackages
    {
        get
        {
            try
            {
                return Android.App.Application.Context.PackageManager?.CanRequestPackageInstalls() == true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public bool CanInstallSilently => OperatingSystem.IsAndroidVersionAtLeast(31) && CanInstallPackages;

    public async Task<bool> RequestInstallPermissionAsync()
    {
        if (CanInstallPackages) return true;
        var context = Android.App.Application.Context;
        try
        {
            var intent = new Intent(AndroidSettings.ActionManageUnknownAppSources, AndroidUri.Parse("package:" + context.PackageName));
            await ActivityResults.LaunchAsync(new ActivityResultContracts.StartActivityForResult(), intent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Some vendors have no per-app page: the app's own settings page leads there too.
            logger.LogInformation(ex, "The install permission page is unavailable; opening the app settings");
            try
            {
                var details = new Intent(AndroidSettings.ActionApplicationDetailsSettings, AndroidUri.Parse("package:" + context.PackageName));
                await ActivityResults.LaunchAsync(new ActivityResultContracts.StartActivityForResult(), details).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }
        }
        return CanInstallPackages;
    }

    // ---- Package storage (cache dir: the OS may clear it, Core then downloads again) ----

    private static string Folder
    {
        get
        {
            var folder = Path.Combine(FileSystem.CacheDirectory, "updates");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static string PathOf(string fileName) => Path.Combine(Folder, Path.GetFileName(fileName));

    public Task<long> GetPackageLengthAsync(string fileName)
    {
        var file = new FileInfo(PathOf(fileName));
        return Task.FromResult(file.Exists ? file.Length : -1L);
    }

    public Task<Stream> OpenPackageWriteAsync(string fileName, long offset, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(PathOf(fileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
        try
        {
            stream.SetLength(offset);
            stream.Position = offset;
            return Task.FromResult<Stream>(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public Task<Stream?> OpenPackageReadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = PathOf(fileName);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);
    }

    public Task DeletePackagesAsync(string? keep = null)
    {
        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            if (keep is not null && string.Equals(Path.GetFileName(file), keep, StringComparison.Ordinal)) continue;
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
        return Task.CompletedTask;
    }

    // ---- Installing ----

    public async Task<AppInstallResult> InstallAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = PathOf(fileName);
        if (!File.Exists(path)) return new AppInstallResult(AppInstallStatus.Failed, AppUpdateErrors.Invalid);

        var first = new TaskCompletionSource<AppInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Started while the user looks at the app: it vanishes when replaced, so a notification brings it back.
        if (lifecycle.IsForeground) RememberRelaunch();
        try
        {
            await Task.Run(() => Commit(path, first), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ForgetRelaunch();
            logger.LogWarning(ex, "Could not hand the update to the package installer");
            return new AppInstallResult(AppInstallStatus.Failed, ex is IOException ? AppUpdateErrors.Storage : AppUpdateErrors.InstallFailed);
        }

        // The first status usually arrives within seconds; a silent install may end the process before any.
        var finished = await Task.WhenAny(first.Task, Task.Delay(FirstStatusTimeout, cancellationToken)).ConfigureAwait(false);
        return finished == first.Task ? await first.Task.ConfigureAwait(false) : new AppInstallResult(AppInstallStatus.Pending);
    }

    private void Commit(string path, TaskCompletionSource<AppInstallResult> first)
    {
        var context = Android.App.Application.Context;
        var installer = context.PackageManager!.PackageInstaller;
        AbandonStaleSessions(installer);

        var length = new FileInfo(path).Length;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName);
        parameters.SetSize(length);
        parameters.SetInstallReason(PackageInstallReason.User);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            parameters.SetRequireUserAction(PackageInstaller.SessionParams.UserActionNotRequired);

        var sessionId = installer.CreateSession(parameters);
        lock (_gate) _firstStatus[sessionId] = first;
        using var session = installer.OpenSession(sessionId);
        try
        {
            using (var input = File.OpenRead(path))
            using (var output = session.OpenWrite("skymessage.apk", 0, length))
            {
                input.CopyTo(output);
                session.Fsync(output);
            }

            var status = new Intent(context, typeof(UpdateInstallReceiver)).SetAction(ActionInstallStatus).SetPackage(context.PackageName);
            // Mutable: the installer adds the status extras to it.
            var flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) flags |= PendingIntentFlags.Mutable;
            var pending = PendingIntent.GetBroadcast(context, sessionId, status, flags)!;
            session.Commit(pending.IntentSender);
            logger.LogInformation("Update committed to the package installer (session {Session})", sessionId);
        }
        catch
        {
            lock (_gate) _firstStatus.Remove(sessionId);
            try
            {
                session.Abandon();
            }
            catch (Exception)
            {
            }
            throw;
        }
    }

    /// <summary>Sessions left over from earlier attempts (a confirmation never answered) would only pile up.</summary>
    private void AbandonStaleSessions(PackageInstaller installer)
    {
        try
        {
            foreach (var info in installer.MySessions)
            {
                if (info.IsActive) continue;
                try
                {
                    installer.AbandonSession(info.SessionId);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list earlier install sessions");
        }
    }

    /// <summary>A status broadcast of one of our sessions (<see cref="UpdateInstallReceiver"/>).</summary>
    internal void OnStatus(Intent intent)
    {
        var sessionId = intent.GetIntExtra(PackageInstaller.ExtraSessionId, -1);
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        logger.LogInformation("Package installer: {Status} (session {Session}) {Message}", status, sessionId,
            intent.GetStringExtra(PackageInstaller.ExtraStatusMessage));

        var result = status switch
        {
            PackageInstallStatus.PendingUserAction => ShowConfirmation(intent)
                ? new AppInstallResult(AppInstallStatus.Pending)
                : new AppInstallResult(AppInstallStatus.NeedsConfirmation),
            PackageInstallStatus.Success => new AppInstallResult(AppInstallStatus.Succeeded),
            PackageInstallStatus.FailureAborted => new AppInstallResult(AppInstallStatus.Cancelled),
            _ => new AppInstallResult(AppInstallStatus.Failed, ErrorOf(status)),
        };
        if (result.Status is AppInstallStatus.Cancelled or AppInstallStatus.Failed or AppInstallStatus.NeedsConfirmation) ForgetRelaunch();

        TaskCompletionSource<AppInstallResult>? first;
        lock (_gate)
        {
            if (_firstStatus.Remove(sessionId, out first) && first.TrySetResult(result)) return;
        }
        var handlers = InstallStatusChanged;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<AppInstallResult>)handler)(result);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "An install status handler failed");
            }
        }
    }

    /// <summary>Shows Android's "Update this app?" when the app is in front; false when it has to wait for the foreground.</summary>
    private bool ShowConfirmation(Intent intent)
    {
        Intent? confirm;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            confirm = intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent))) as Intent;
        else
            confirm = intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
        if (confirm is null || !lifecycle.IsForeground) return false;
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null || activity.IsFinishing) return false;
        try
        {
            activity.StartActivity(confirm);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show the install confirmation");
            return false;
        }
    }

    private static string ErrorOf(PackageInstallStatus status) => status switch
    {
        PackageInstallStatus.FailureConflict => AppUpdateErrors.Conflict,
        PackageInstallStatus.FailureIncompatible => AppUpdateErrors.Incompatible,
        PackageInstallStatus.FailureBlocked => AppUpdateErrors.Blocked,
        PackageInstallStatus.FailureInvalid => AppUpdateErrors.Invalid,
        PackageInstallStatus.FailureStorage => AppUpdateErrors.Storage,
        _ => AppUpdateErrors.InstallFailed,
    };

    // ---- After the replacement (runs in the new process) ----

    private static void RememberRelaunch()
    {
        try
        {
            Preferences.Default.Set(RelaunchKey, DateTime.UtcNow.Ticks, PreferencesStore.SharedName);
        }
        catch (Exception)
        {
        }
    }

    private static void ForgetRelaunch()
    {
        try
        {
            Preferences.Default.Remove(RelaunchKey, PreferencesStore.SharedName);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The package was replaced (MY_PACKAGE_REPLACED). When the user started the install from the app, they just saw it
    /// close: a notification opens the new build.
    /// </summary>
    internal static void OnPackageReplaced(Context context)
    {
        long ticks;
        try
        {
            ticks = Preferences.Default.Get(RelaunchKey, 0L, PreferencesStore.SharedName);
        }
        catch (Exception)
        {
            return;
        }
        ForgetRelaunch();
        if (ticks <= 0 || DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > RelaunchWindow) return;

        try
        {
            var channels = AppServices.Get<NotificationChannels>();
            channels?.EnsureBase();
            var strings = AppServices.Get<NativeStrings>();
            var open = AppIntents.ActivityPendingIntent(context, AppIntents.UpdatedNotificationId, AppIntents.ActivityIntent(context, null));
            var notification = new NotificationCompat.Builder(context, NotificationChannels.Updates)
                .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
                .SetColor(unchecked((int)0xFF6A4BFF))!
                .SetContentTitle(strings?.Format("platform.update.installed", AppInfo.Current.VersionString) ?? "SkyMessage")!
                .SetContentText(strings?["platform.update.tapToOpen"] ?? "")!
                .SetContentIntent(open)!
                .SetAutoCancel(true)!
                .SetPriority(NotificationCompat.PriorityHigh)!
                .SetCategory(NotificationCompat.CategoryStatus)!
                .Build()!;
            NotificationManagerCompat.From(context).Notify(AppIntents.UpdatedNotificationId, notification);
        }
        catch (Exception)
        {
            // Without notification permission the user simply opens the app again.
        }
    }
}

/// <summary>Status of our install sessions (the PendingIntent given to <c>Session.commit</c>).</summary>
[BroadcastReceiver(Name = "com.skymessage.messenger.UpdateInstallReceiver", Exported = false, Enabled = true)]
public sealed class UpdateInstallReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action != AndroidAppUpdateInstaller.ActionInstallStatus) return;
        (AppServices.Get<IAppUpdateInstaller>() as AndroidAppUpdateInstaller)?.OnStatus(intent);
    }
}

/// <summary>The new build after a self-update (MY_PACKAGE_REPLACED is delivered to it).</summary>
[BroadcastReceiver(Name = "com.skymessage.messenger.UpdateReplacedReceiver", Exported = false, Enabled = true)]
[IntentFilter([Intent.ActionMyPackageReplaced])]
public sealed class UpdateReplacedReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Intent.ActionMyPackageReplaced) return;
        AndroidAppUpdateInstaller.OnPackageReplaced(context);
    }
}
