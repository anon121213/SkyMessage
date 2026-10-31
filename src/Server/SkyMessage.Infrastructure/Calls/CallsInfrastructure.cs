using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Calls;
using SkyMessage.Application.Common.Options;

namespace SkyMessage.Infrastructure.Calls;

/// <summary>LiveKit access tokens, webhook verification, ring-timeout background service.</summary>
public static class CallsInfrastructure
{
    /// <summary>Twirp calls are tiny; a LiveKit that does not answer within this is treated as unreachable.</summary>
    private static readonly TimeSpan TwirpTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddCallsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<LiveKitTokenService>();
        services.AddSingleton<ILiveKitTokenService>(sp => sp.GetRequiredService<LiveKitTokenService>());
        services.AddSingleton<ILiveKitWebhookVerifier, LiveKitWebhookVerifier>();

        services.AddHttpClient(LiveKitRoomService.HttpClientName, (sp, client) =>
            {
                var internalUrl = sp.GetRequiredService<IOptions<LiveKitOptions>>().Value.InternalUrl ?? "";
                if (Uri.TryCreate(internalUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
                    && baseUri.Scheme is "http" or "https")
                    client.BaseAddress = baseUri;
                client.Timeout = TwirpTimeout;
            })
            // The default handlers would log every reconciliation request at Information.
            .RemoveAllLoggers();
        services.AddSingleton<ILiveKitRoomService, LiveKitRoomService>();

        services.AddSingleton<ICallGraceScheduler, CallGraceScheduler>();
        services.AddHostedService<CallMaintenanceService>();
        return services;
    }
}
