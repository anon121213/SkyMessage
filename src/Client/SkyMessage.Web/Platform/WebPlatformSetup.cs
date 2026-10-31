using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Web.Platform;

public static class WebPlatformSetup
{
    /// <summary>Every interface in SkyMessage.Client.Core.Platform for the browser (singletons; call after AddSkyMessageClientCore).</summary>
    public static IServiceCollection AddSkyMessageWebPlatform(this IServiceCollection services)
    {
        services.AddSingleton<WebBridge>();
        services.AddSingleton<WebBlobs>();

        services.AddSingleton<IPlatformInfo, WebPlatformInfo>();
        services.AddSingleton<ISecureStore, WebSecureStore>();
        services.AddSingleton<IPreferencesStore, WebPreferencesStore>();
        services.AddSingleton<ILocalCacheStore, WebLocalCacheStore>();
        services.AddSingleton<IOutboxFileStore, WebOutboxFileStore>();
        services.AddSingleton<IMediaFileStore, WebMediaFileStore>();
        services.AddSingleton<ICustomSoundStore, WebCustomSoundStore>();
        services.AddSingleton<IFilePickerService, WebFilePickerService>();
        services.AddSingleton<IMediaCompressor, WebMediaCompressor>();
        services.AddSingleton<IPhoneNumberService, WebPhoneNumberService>();
        services.AddSingleton<IDeviceContactsService, WebDeviceContactsService>();
        services.AddSingleton<IPermissionService, WebPermissionService>();
        services.AddSingleton<ILocalNotificationService, WebNotificationService>();
        services.AddSingleton<IAudioRouteService, WebAudioRouteService>();
        services.AddSingleton<IAudioFocusService, WebAudioFocusService>();
        services.AddSingleton<ICallPlatformService, WebCallPlatformService>();
        services.AddSingleton<IShareService, WebShareService>();
        services.AddSingleton<IFileSaveService, WebFileSaveService>();
        services.AddSingleton<IFileOpener, WebFileOpener>();
        services.AddSingleton<IAppLifecycle, WebAppLifecycle>();
        services.AddSingleton<IExternalLauncher, WebExternalLauncher>();
        services.AddSingleton<IDeepLinkSource, WebDeepLinkSource>();
        services.AddSingleton<INetworkStatusService, WebNetworkStatusService>();
        services.AddSingleton<ISystemUiService, WebSystemUiService>();
        services.AddSingleton<IScreenSecurityService, WebScreenSecurityService>();
        services.AddSingleton<IHapticsService, WebHapticsService>();
        services.AddSingleton<IBackgroundConnectionService, WebBackgroundConnectionService>();
        services.AddSingleton<IInterProcessLock, WebInterProcessLock>();
        services.AddSingleton<IHttpHandlerFactory, WebHttpHandlerFactory>();
        return services;
    }
}
