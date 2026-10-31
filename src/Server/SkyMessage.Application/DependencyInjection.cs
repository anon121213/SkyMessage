using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Application.Calls;
using SkyMessage.Application.Identity;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Application.Messaging;

namespace SkyMessage.Application;

public static class DependencyInjection
{
    /// <summary>Registers use-case services of every feature. Options are bound by Infrastructure.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        services.AddIdentityFeature();
        services.AddMessagingFeature();
        services.AddMediaFeature();
        services.AddCallsFeature();

        return services;
    }
}
