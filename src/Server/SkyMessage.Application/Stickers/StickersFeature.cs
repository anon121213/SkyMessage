using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SkyMessage.Application.Stickers;

/// <summary>Sticker packs, my installed packs, favorite and recent stickers, saved GIFs (SPEC §6.6).</summary>
public static class StickersFeature
{
    public static IServiceCollection AddStickersFeature(this IServiceCollection services)
    {
        // The sticker images (IStickerMediaFactory) are made in Infrastructure (SkiaSharp, chunk storage).
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<StickerRecentsPublisher>();
        services.AddScoped<StickerStates>();
        services.AddScoped<StickerService>();
        services.AddScoped<IStickerService>(sp => sp.GetRequiredService<StickerService>());
        services.AddScoped<IStickerRecents>(sp => sp.GetRequiredService<StickerService>());
        services.AddScoped<IStickerPackService, StickerPackService>();
        return services;
    }
}
