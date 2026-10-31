using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;
using SkyMessage.Infrastructure.Calls;
using SkyMessage.Infrastructure.Identity;
using SkyMessage.Infrastructure.MediaFiles;
using SkyMessage.Infrastructure.Messaging;
using SkyMessage.Infrastructure.Persistence;
using SkyMessage.Infrastructure.Updates;

namespace SkyMessage.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServerOptions>().Bind(configuration.GetSection(ServerOptions.Section));
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section));
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.Section));
        services.AddOptions<MediaProcessingOptions>().Bind(configuration.GetSection(MediaProcessingOptions.Section));
        services.AddOptions<LiveKitOptions>().Bind(configuration.GetSection(LiveKitOptions.Section));
        services.AddOptions<PushOptions>().Bind(configuration.GetSection(PushOptions.Section));
        services.AddOptions<UpdatesOptions>().Bind(configuration.GetSection(UpdatesOptions.Section));

        services.AddSingleton<IClock, SystemClock>();
        services.AddMemoryCache();
        services.AddPersistence(configuration);

        services.AddIdentityInfrastructure(configuration);
        services.AddMessagingInfrastructure(configuration);
        services.AddMediaInfrastructure(configuration);
        services.AddCallsInfrastructure(configuration);
        services.AddUpdatesInfrastructure();

        return services;
    }
}

internal sealed class SystemClock : IClock
{
    // PostgreSQL stores microseconds; truncating keeps in-memory values equal to what is persisted
    // (sync cursors and DTO timestamps compare exactly).
    public DateTimeOffset UtcNow
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        }
    }
}
