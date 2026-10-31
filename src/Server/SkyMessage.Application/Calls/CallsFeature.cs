using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkyMessage.Application.Common.Interfaces;

namespace SkyMessage.Application.Calls;

/// <summary>1:1 and group calls on top of LiveKit: signaling state, tokens, history.</summary>
public static class CallsFeature
{
    public static IServiceCollection AddCallsFeature(this IServiceCollection services)
    {
        services.AddSingleton<CallPresenceTracker>();
        services.AddScoped<CallLifecycle>();
        services.AddScoped<ICallGraceHandler>(sp => sp.GetRequiredService<CallLifecycle>());
        services.AddScoped<ICallService, CallService>();
        services.AddScoped<ICallWebhookService, CallWebhookService>();
        services.AddScoped<ICallMaintenance, CallMaintenance>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatMembershipObserver, CallMembershipObserver>());
        return services;
    }
}
