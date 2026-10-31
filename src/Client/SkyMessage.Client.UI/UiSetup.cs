using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.UI.Features.ChatList;
using SkyMessage.Client.UI.Features.Search;
using SkyMessage.Client.UI.Kit.Emoji;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI;

public static class UiSetup
{
    /// <summary>
    /// Registers the UI services. Called by both hosts after <c>AddSkyMessageClientCore</c> and the platform
    /// services. Singletons have no JS/NavigationManager dependencies (Core and native services may use them);
    /// everything touching JS or navigation is scoped (one scope per WebView / browser tab).
    /// </summary>
    public static IServiceCollection AddSkyMessageUI(this IServiceCollection services)
    {
        // Singletons (no JS)
        services.TryAddSingleton<ILocalizer, Localizer>();
        services.TryAddSingleton<FormatService>();
        services.TryAddSingleton<MessagePreviewFormatter>();
        // Core resolves the formatter lazily (optional dependency); Replace makes sure the UI one is the one in use.
        services.Replace(ServiceDescriptor.Singleton<IMessagePreviewFormatter>(sp => sp.GetRequiredService<MessagePreviewFormatter>()));
        services.TryAddSingleton<ThemeCatalog>();
        services.TryAddSingleton<ToneCatalog>();
        services.TryAddSingleton<EmojiCatalog>();
        services.TryAddSingleton<EmojiRecents>();
        services.TryAddSingleton<RecentSearches>();
        services.TryAddSingleton<HapticFeedback>();
        services.TryAddSingleton<AvatarUploader>();

        // Scoped (JS wrappers, navigation, overlays)
        services.TryAddScoped<AppJs>();
        services.TryAddScoped<UiDispatcher>();
        services.TryAddScoped<LayoutService>();
        services.TryAddScoped<ThemeService>();
        services.TryAddScoped<NavigationService>();
        services.TryAddScoped<DialogService>();
        services.TryAddScoped<ToastService>();
        services.TryAddScoped<SoundService>();
        services.TryAddScoped<ISoundService>(sp => sp.GetRequiredService<SoundService>());
        services.TryAddScoped<LinkService>();
        services.TryAddScoped<ShareHelper>();
        services.TryAddScoped<ChatActions>();
        services.TryAddScoped<AppUpdatePrompts>();
        return services;
    }
}
