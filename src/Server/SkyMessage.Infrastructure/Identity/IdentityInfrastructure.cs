using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Identity;
using SkyMessage.Infrastructure.Push;

namespace SkyMessage.Infrastructure.Identity;

/// <summary>JWT issuing, password hashing, presence tracking, session connection registry, push.</summary>
public static class IdentityInfrastructure
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IUserPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddSingleton<IIdentityDbFunctions, NpgsqlIdentityDbFunctions>();

        services.AddSingleton<PresenceTracker>();
        services.AddSingleton<IPresenceService>(sp => sp.GetRequiredService<PresenceTracker>());
        services.AddHostedService<PresenceWorker>();

        services.AddSingleton<SessionConnectionRegistry>();
        services.AddSingleton<ISessionConnections>(sp => sp.GetRequiredService<SessionConnectionRegistry>());

        services.AddHostedService<ServerInstanceInitializer>();
        services.AddPushNotifications();
        return services;
    }
}
