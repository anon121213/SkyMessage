using Microsoft.Extensions.DependencyInjection;

namespace SkyMessage.Application.Updates;

/// <summary>App releases: api/app/version from the manifest CI publishes (the APK files themselves are static).</summary>
public static class UpdatesFeature
{
    public static IServiceCollection AddUpdatesFeature(this IServiceCollection services)
    {
        services.AddSingleton<AppVersionService>();
        return services;
    }
}
