using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using UserNotifications;

namespace SkyMessage.App.Apple;

public sealed class ApplePermissionService(ILogger<ApplePermissionService> logger) : IPermissionService
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
                AppPermission.Photos => Map(await Permissions.CheckStatusAsync<Permissions.PhotosAddOnly>().ConfigureAwait(false)),
                AppPermission.Notifications => await NotificationStateAsync().ConfigureAwait(false),
                _ => PermissionState.NotApplicable,
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
                    return Map(await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.Microphone>).ConfigureAwait(false));
                case AppPermission.Camera:
                    return Map(await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.Camera>).ConfigureAwait(false));
                case AppPermission.Contacts:
                    return Map(await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.ContactsRead>).ConfigureAwait(false));
                case AppPermission.Photos:
                    return Map(await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.PhotosAddOnly>).ConfigureAwait(false));
                case AppPermission.Notifications:
                    var (granted, _) = await UNUserNotificationCenter.Current
                        .RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound)
                        .ConfigureAwait(false);
                    return granted ? PermissionState.Granted : await NotificationStateAsync().ConfigureAwait(false);
                default:
                    return PermissionState.NotApplicable;
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

    private static async Task<PermissionState> NotificationStateAsync()
    {
        var settings = await UNUserNotificationCenter.Current.GetNotificationSettingsAsync().ConfigureAwait(false);
        return settings.AuthorizationStatus switch
        {
            UNAuthorizationStatus.Authorized or UNAuthorizationStatus.Provisional or UNAuthorizationStatus.Ephemeral => PermissionState.Granted,
            UNAuthorizationStatus.Denied => PermissionState.Denied,
            _ => PermissionState.Unknown,
        };
    }

    internal static PermissionState Map(PermissionStatus status) => status switch
    {
        PermissionStatus.Granted => PermissionState.Granted,
        PermissionStatus.Limited => PermissionState.Limited,
        PermissionStatus.Restricted => PermissionState.Restricted,
        PermissionStatus.Denied or PermissionStatus.Disabled => PermissionState.Denied,
        _ => PermissionState.Unknown,
    };
}
