using Android;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Activity.Result.Contract;
using AndroidX.Core.App;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using AndroidSettings = Android.Provider.Settings;
using AndroidUri = Android.Net.Uri;

namespace SkyMessage.App.Droid;

public sealed class AndroidPermissionService(ILogger<AndroidPermissionService> logger) : IPermissionService
{
    public async Task<PermissionState> CheckAsync(AppPermission permission)
    {
        try
        {
            return permission switch
            {
                AppPermission.Microphone => Map(await Permissions.CheckStatusAsync<Permissions.Microphone>().ConfigureAwait(false)),
                AppPermission.Camera => Map(await Permissions.CheckStatusAsync<Permissions.Camera>().ConfigureAwait(false)),
                AppPermission.Contacts => Map(await Permissions.CheckStatusAsync<Permissions.ContactsRead>().ConfigureAwait(false)),
                AppPermission.Notifications => CheckNotifications(),
                AppPermission.PhoneNumber => Map(await Permissions.CheckStatusAsync<ReadPhoneNumbersPermission>().ConfigureAwait(false)),
                AppPermission.Photos => await CheckPhotosAsync().ConfigureAwait(false),
                AppPermission.Bluetooth => OperatingSystem.IsAndroidVersionAtLeast(31)
                    ? Map(await Permissions.CheckStatusAsync<BluetoothConnectPermission>().ConfigureAwait(false))
                    : PermissionState.NotApplicable,
                AppPermission.FullScreenIntent => CheckFullScreenIntent(),
                _ => PermissionState.Unknown,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Permission check failed for {Permission}", permission);
            return PermissionState.Unknown;
        }
    }

    public async Task<PermissionState> RequestAsync(AppPermission permission)
    {
        try
        {
            switch (permission)
            {
                case AppPermission.Microphone:
                    return Map(await RequestOnMainAsync<Permissions.Microphone>().ConfigureAwait(false));
                case AppPermission.Camera:
                    return Map(await RequestOnMainAsync<Permissions.Camera>().ConfigureAwait(false));
                case AppPermission.Contacts:
                    return Map(await RequestOnMainAsync<Permissions.ContactsRead>().ConfigureAwait(false));
                case AppPermission.PhoneNumber:
                    return Map(await RequestOnMainAsync<ReadPhoneNumbersPermission>().ConfigureAwait(false));
                case AppPermission.Bluetooth:
                    return OperatingSystem.IsAndroidVersionAtLeast(31)
                        ? Map(await RequestOnMainAsync<BluetoothConnectPermission>().ConfigureAwait(false))
                        : PermissionState.NotApplicable;
                case AppPermission.Notifications:
                    if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckNotifications() != PermissionState.Granted)
                        await RequestOnMainAsync<Permissions.PostNotifications>().ConfigureAwait(false);
                    return CheckNotifications();
                case AppPermission.Photos:
                    if (!OperatingSystem.IsAndroidVersionAtLeast(29))
                        return Map(await RequestOnMainAsync<Permissions.StorageWrite>().ConfigureAwait(false));
                    return PermissionState.NotApplicable;
                case AppPermission.FullScreenIntent:
                    if (OperatingSystem.IsAndroidVersionAtLeast(34) && CheckFullScreenIntent() == PermissionState.Denied)
                    {
                        var intent = new Intent(AndroidSettings.ActionManageAppUseFullScreenIntent,
                            AndroidUri.Parse("package:" + Android.App.Application.Context.PackageName));
                        await ActivityResults.LaunchAsync(new ActivityResultContracts.StartActivityForResult(), intent).ConfigureAwait(false);
                    }
                    return CheckFullScreenIntent();
                default:
                    return PermissionState.Unknown;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Permission request failed for {Permission}", permission);
            return await CheckAsync(permission).ConfigureAwait(false);
        }
    }

    public Task OpenAppSettingsAsync()
    {
        try
        {
            AppInfo.Current.ShowSettingsUI();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the app settings");
        }
        return Task.CompletedTask;
    }

    internal static bool IsGranted(string permission) =>
        AndroidX.Core.Content.ContextCompat.CheckSelfPermission(Android.App.Application.Context, permission) == Android.Content.PM.Permission.Granted;

    private static PermissionState CheckNotifications() =>
        NotificationManagerCompat.From(Android.App.Application.Context)?.AreNotificationsEnabled() == true
            ? PermissionState.Granted
            : PermissionState.Denied;

    private static PermissionState CheckFullScreenIntent()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(34)) return PermissionState.NotApplicable;
        var manager = (NotificationManager?)Android.App.Application.Context.GetSystemService(Context.NotificationService);
        return manager?.CanUseFullScreenIntent() == true ? PermissionState.Granted : PermissionState.Denied;
    }

    private static async Task<PermissionState> CheckPhotosAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) return PermissionState.NotApplicable;
        return Map(await Permissions.CheckStatusAsync<Permissions.StorageWrite>().ConfigureAwait(false));
    }

    private static Task<PermissionStatus> RequestOnMainAsync<T>() where T : Permissions.BasePermission, new() =>
        MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<T>);

    internal static PermissionState Map(PermissionStatus status) => status switch
    {
        PermissionStatus.Granted => PermissionState.Granted,
        PermissionStatus.Limited => PermissionState.Limited,
        PermissionStatus.Restricted => PermissionState.Restricted,
        PermissionStatus.Denied or PermissionStatus.Disabled => PermissionState.Denied,
        _ => PermissionState.Unknown,
    };

    /// <summary>Fallback phone-number detection only (SubscriptionManager / line number).</summary>
    public sealed class ReadPhoneNumbersPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            [(Manifest.Permission.ReadPhoneNumbers, true)];
    }

    /// <summary>Android 12+: Bluetooth headset routing and names. No scan/advertise permissions.</summary>
    public sealed class BluetoothConnectPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            OperatingSystem.IsAndroidVersionAtLeast(31) ? [(Manifest.Permission.BluetoothConnect, true)] : [];
    }
}
