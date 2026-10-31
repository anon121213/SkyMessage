using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Client.Core.Platform;
#if ANDROID
using SkyMessage.App.Droid;
#elif IOS || MACCATALYST
using SkyMessage.App.Apple;
#endif

namespace SkyMessage.App.Services;

public static class PlatformServices
{
    /// <summary>Every interface in SkyMessage.Client.Core.Platform as a singleton (call after AddSkyMessageClientCore).</summary>
    public static IServiceCollection AddSkyMessagePlatform(this IServiceCollection services)
    {
        services.AddSingleton<NativeStrings>();
        services.AddSingleton<MediaProxy>();

        services.AddSingleton<IPlatformInfo, PlatformInfo>();
        services.AddSingleton<ISecureStore, SecureStore>();
        services.AddSingleton<IPreferencesStore, PreferencesStore>();
        services.AddSingleton<ILocalCacheStore, LocalCacheStore>();
        services.AddSingleton<IOutboxFileStore, OutboxFileStore>();
        services.AddSingleton<IMediaFileStore, MediaFileStore>();
        services.AddSingleton<ICustomSoundStore, CustomSoundStore>();
        services.AddSingleton<IShareService, ShareService>();
        services.AddSingleton<IFileOpener, FileOpener>();
        services.AddSingleton<IExternalLauncher, ExternalLauncher>();
        services.AddSingleton<IDeepLinkSource, DeepLinkSource>();
        services.AddSingleton<IAppLifecycle, AppLifecycle>();
        services.AddSingleton<IInterProcessLock, InterProcessLock>();
        services.AddSingleton<IHttpHandlerFactory, HttpHandlerFactory>();

#if ANDROID
        services.AddSingleton<NotificationChannels>();
        services.AddSingleton<WebViewProxyConfigurator>();
        services.AddSingleton<IPermissionService, AndroidPermissionService>();
        services.AddSingleton<INetworkStatusService, AndroidNetworkStatusService>();
        services.AddSingleton<ISystemUiService, AndroidSystemUiService>();
        services.AddSingleton<IHapticsService, AndroidHapticsService>();
        services.AddSingleton<IFilePickerService, AndroidFilePickerService>();
        services.AddSingleton<IMediaCompressor, AndroidMediaCompressor>();
        services.AddSingleton<IPhoneNumberService, AndroidPhoneNumberService>();
        services.AddSingleton<IDeviceContactsService, AndroidDeviceContactsService>();
        services.AddSingleton<ILocalNotificationService, AndroidNotificationService>();
        services.AddSingleton<IAudioRouteService, AndroidAudioRouteService>();
        services.AddSingleton<ICallPlatformService, AndroidCallPlatformService>();
        services.AddSingleton<IBackgroundConnectionService, AndroidBackgroundConnectionService>();
        services.AddSingleton<IFileSaveService, AndroidFileSaveService>();
#elif IOS || MACCATALYST
        services.AddSingleton<IPermissionService, ApplePermissionService>();
        services.AddSingleton<INetworkStatusService, AppleNetworkStatusService>();
        services.AddSingleton<ISystemUiService, AppleSystemUiService>();
        services.AddSingleton<IFilePickerService, AppleFilePickerService>();
        services.AddSingleton<IMediaCompressor, AppleMediaCompressor>();
        services.AddSingleton<IPhoneNumberService, UnsupportedPhoneNumberService>();
        services.AddSingleton<IDeviceContactsService, AppleDeviceContactsService>();
        services.AddSingleton<ILocalNotificationService, AppleNotificationService>();
        services.AddSingleton<ICallPlatformService, AppleCallPlatformService>();
        services.AddSingleton<IBackgroundConnectionService, UnsupportedBackgroundConnectionService>();
        services.AddSingleton<IFileSaveService, AppleFileSaveService>();
#if IOS
        services.AddSingleton<IHapticsService, AppleHapticsService>();
        services.AddSingleton<IAudioRouteService, AppleAudioRouteService>();
#else
        services.AddSingleton<IHapticsService, UnsupportedHapticsService>();
        services.AddSingleton<IAudioRouteService, UnsupportedAudioRouteService>();
#endif
#endif
        return services;
    }
}
