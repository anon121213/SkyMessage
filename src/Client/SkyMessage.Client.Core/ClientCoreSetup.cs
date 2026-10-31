using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;

namespace SkyMessage.Client.Core;

public enum MediaUrlMode
{
    /// <summary>Server URLs with per-file signatures (browser).</summary>
    Direct,
    /// <summary>App-origin URLs served natively through IMediaContentSource (MAUI).</summary>
    AppProxy,
}

public sealed class ClientCoreOptions
{
    /// <summary>Server used until the user picks another one (e.g. http://10.0.2.2:8080/ on the Android emulator).</summary>
    public string DefaultServerUrl { get; set; } = "http://localhost:8080/";
    /// <summary>Built-in fallback addresses of the same deployment, tried when the main one is unreachable.</summary>
    public List<string> FallbackServerUrls { get; set; } = [];
    /// <summary>Web client served by the API: always use the page's own origin.</summary>
    public bool UseHostOrigin { get; set; }
    public string? HostOrigin { get; set; }
    public MediaUrlMode MediaUrlMode { get; set; } = MediaUrlMode.Direct;
    /// <summary>AppProxy mode: kinds served through the app-origin proxy (null = all); others use Direct URLs.</summary>
    public HashSet<SkyMessage.Contracts.MediaKind>? AppProxyKinds { get; set; }
}

public static class ClientCoreSetup
{
    /// <summary>
    /// Registers all Client.Core services (singletons). The host must also register every interface in
    /// <c>SkyMessage.Client.Core.Platform</c>.
    /// </summary>
    public static IServiceCollection AddSkyMessageClientCore(this IServiceCollection services, Action<ClientCoreOptions>? configure = null)
    {
        var options = new ClientCoreOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // Infrastructure
        services.AddSingleton<LocalCache>();
        services.AddSingleton<AuthSessionStore>();
        services.AddSingleton<AccountLifecycle>();
        services.AddSingleton<HttpClientProvider>();
        services.AddSingleton<ApiTransport>();
        services.AddSingleton<AuthorizedSender>();
        services.AddSingleton<MediaFetcher>();
        services.AddSingleton<IAuthChannel, AuthChannel>();

        // Services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<ServerEndpointService>();
        services.AddSingleton<IServerEndpointService>(sp => sp.GetRequiredService<ServerEndpointService>());
        services.AddSingleton<IEndpointRouting>(sp => sp.GetRequiredService<ServerEndpointService>());
        services.AddSingleton<AuthService>();
        services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<AuthService>());
        services.AddSingleton<ITokenSource>(sp => sp.GetRequiredService<AuthService>());
        services.AddSingleton<SkyApi>();
        services.AddSingleton<ISkyApi>(sp => sp.GetRequiredService<SkyApi>());
        services.AddSingleton<RealtimeService>();
        services.AddSingleton<IRealtimeService>(sp => sp.GetRequiredService<RealtimeService>());
        services.AddSingleton<MediaUrlBuilder>();
        services.AddSingleton<IMediaUrlBuilder>(sp => sp.GetRequiredService<MediaUrlBuilder>());
        services.AddSingleton<MediaCacheTrimmer>();
        services.AddSingleton<IMediaContentSource, MediaContentSource>();
        services.AddSingleton<MediaUploader>();
        services.AddSingleton<IMediaUploader>(sp => sp.GetRequiredService<MediaUploader>());
        services.AddSingleton<IMediaDownloadService, MediaDownloadService>();
        services.AddSingleton<ILinkBuilder, LinkBuilder>();
        services.AddSingleton<INotificationCoordinator, NotificationCoordinator>();
        services.AddSingleton<IClientBootstrapper, ClientBootstrapper>();
        services.AddSingleton<IAppUpdateFeed, AppUpdateFeed>();
        services.AddSingleton<AppUpdateService>();
        services.AddSingleton<IAppUpdateService>(sp => sp.GetRequiredService<AppUpdateService>());

        // Stores
        services.AddSingleton<IUserStore, UserStore>();
        services.AddSingleton<IChatListStore, ChatListStore>();
        services.AddSingleton<IMessageStore, MessageStore>();
        services.AddSingleton<IContactStore, ContactStore>();
        services.AddSingleton<ITypingStore, TypingStore>();
        services.AddSingleton<IDraftStore, DraftStore>();
        services.AddSingleton<IAccountLocalStore, AccountLocalStore>();
        services.AddSingleton<ICallService, CallService>();
        return services;
    }
}
