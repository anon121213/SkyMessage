using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Application.Messaging;

namespace SkyMessage.Infrastructure.Messaging;

public static class MessagingInfrastructure
{
    public static IServiceCollection AddMessagingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IMessagingDbFunctions, NpgsqlMessagingDbFunctions>();
        return services;
    }
}
