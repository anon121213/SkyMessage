using System.Net;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace SkyMessage.Api.Calls;

/// <summary>
/// Reverse-proxies LiveKit signaling (WebSocket) under <c>/livekit</c> so clients only ever talk to
/// the API origin on 443. Owned by the Calls feature.
/// <para>
/// livekit-client 2.22.4 derives everything from the URL it is given (here <c>wss://host/livekit</c>) by
/// appending path segments: signaling WebSocket <c>/livekit/rtc/v1</c> (falls back to <c>/livekit/rtc</c>), on
/// connection errors <c>GET /livekit/rtc/v1/validate</c> (<c>/livekit/rtc/validate</c>) with the same query, and
/// <c>HEAD /livekit</c> as a reachability probe (its "online again" reconnect only fires on a 2xx). Exactly those
/// paths are forwarded, minus the <c>/livekit</c> prefix; LiveKit's own API (<c>/twirp</c>) is not exposed.
/// </para>
/// </summary>
public static class LiveKitProxySetup
{
    private const string ClusterId = "livekit";

    /// <summary>
    /// The access token and the gzip+base64 join request (with the SDP offer) travel in the signaling URL's query;
    /// Kestrel's default 8 KB request line is too tight for that.
    /// </summary>
    private const int MaxSignalingRequestLineBytes = 32 * 1024;

    /// <summary>Signaling sockets live as long as the call; LiveKit pings every few seconds, so only dead ones time out.</summary>
    private static readonly TimeSpan ActivityTimeout = TimeSpan.FromMinutes(15);

    public static IServiceCollection AddLiveKitProxy(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ReadOptions(configuration);
        if (!options.Enabled) return services;

        if (!Uri.TryCreate(options.InternalUrl, UriKind.Absolute, out var internalUri) || internalUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"{LiveKitOptions.Section}:InternalUrl must be an absolute http(s) URL of the LiveKit server, got '{options.InternalUrl}'.");

        services.Configure<KestrelServerOptions>(o =>
            o.Limits.MaxRequestLineSize = Math.Max(o.Limits.MaxRequestLineSize, MaxSignalingRequestLineBytes));

        services.AddReverseProxy().LoadFromMemory(
            [
                Route("livekit-probe", HubRoutes.LiveKitProxy),
                Route("livekit-rtc", HubRoutes.LiveKitProxy + "/rtc/{**remainder}"),
            ],
            [
                new ClusterConfig
                {
                    ClusterId = ClusterId,
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        ["livekit"] = new() { Address = internalUri.ToString() },
                    },
                    HttpRequest = new()
                    {
                        ActivityTimeout = ActivityTimeout,
                        Version = HttpVersion.Version11,
                        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                    },
                },
            ]);
        return services;
    }

    public static WebApplication MapLiveKitProxy(this WebApplication app)
    {
        var options = ReadOptions(app.Configuration);
        if (options.Enabled) app.MapReverseProxy();

        // Everything else under /livekit is not signaling: a clear answer instead of the SPA fallback.
        app.Map(HubRoutes.LiveKitProxy + "/{**remainder}", () => Results.Problem(
                statusCode: options.Enabled ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable,
                title: options.Enabled ? "Not found" : "Calls are disabled",
                extensions: new Dictionary<string, object?> { ["code"] = options.Enabled ? ErrorCodes.NotFound : ErrorCodes.CallsDisabled }))
            .AllowAnonymous()
            .WithMetadata(new DisableCorsAttribute());
        return app;
    }

    /// <summary>
    /// "anonymous": LiveKit authenticates with its own token in the query. CORS "disable": LiveKit answers with its
    /// own CORS headers (any origin, no credentials); ours on top would duplicate Access-Control-Allow-Origin and
    /// make browsers (the MAUI WebView origin) reject the validate/probe responses.
    /// </summary>
    private static RouteConfig Route(string routeId, string path) =>
        new RouteConfig
        {
            RouteId = routeId,
            ClusterId = ClusterId,
            AuthorizationPolicy = "anonymous",
            CorsPolicy = "disable",
            Match = new RouteMatch { Path = path },
        }.WithTransformPathRemovePrefix(HubRoutes.LiveKitProxy);

    private static LiveKitOptions ReadOptions(IConfiguration configuration) =>
        configuration.GetSection(LiveKitOptions.Section).Get<LiveKitOptions>() ?? new LiveKitOptions();
}
