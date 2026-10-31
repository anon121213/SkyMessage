using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using SkyMessage.Api.Calls;
using SkyMessage.Api.Hubs;
using SkyMessage.Api.Infrastructure;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.Common.Security;
using SkyMessage.Contracts;

namespace SkyMessage.Api;

public static class RateLimitPolicies
{
    /// <summary>Login / register / refresh: per client IP. Apply per action, not per controller.</summary>
    public const string Auth = "auth";
    /// <summary>As-you-type username availability checks: per client IP.</summary>
    public const string UsernameCheck = "username-check";
    /// <summary>User / message search: per user.</summary>
    public const string Search = "search";
    /// <summary>Phone-number lookups (contact discovery): per user, strict.</summary>
    public const string PhoneLookup = "phone-lookup";
}

public static class ApiSetup
{
    public const string CorsPolicy = "clients";
    private const string AuthErrorItem = "sky.auth_error";

    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        EnsureNoDevSecretsOutsideDevelopment(configuration, environment);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
        services.AddSingleton<IUserIdProvider, SubClaimUserIdProvider>();

        services.AddControllers(o => o.Filters.Add<ValidationFilter>())
            .AddJsonOptions(o => SkyJson.Configure(o.JsonSerializerOptions));
        services.ConfigureHttpJsonOptions(o => SkyJson.Configure(o.SerializerOptions));
        services.Configure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = ValidationFilter.InvalidModelState);

        services.AddSignalR(o =>
            {
                o.MaximumReceiveMessageSize = 64 * 1024;
                o.EnableDetailedErrors = environment.IsDevelopment();
                o.KeepAliveInterval = TimeSpan.FromSeconds(15);
                o.ClientTimeoutInterval = TimeSpan.FromSeconds(40);
            })
            .AddJsonProtocol(o => SkyJson.Configure(o.PayloadSerializerOptions));

        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
            ctx.ProblemDetails.Extensions.TryAdd("traceId", ctx.HttpContext.TraceIdentifier));
        services.AddExceptionHandler<AppExceptionHandler>();
        services.AddOpenApi();
        services.AddHealthChecks();

        var server = configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // TLS terminates at the reverse proxy (Caddy). Only explicitly trusted proxies may set these headers.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var cidr in server.TrustedProxyNetworks)
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        });

        AddAuth(services, configuration, environment);
        AddCors(services, server, environment);
        AddRateLimiting(services);

        services.AddLiveKitProxy(configuration);
        return services;
    }

    public static WebApplication UseApi(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        if (!app.Environment.IsDevelopment())
            app.UseHsts();
        app.Use(AddSecurityHeaders);

        // Blazor WebAssembly web client served from the same origin as the API.
        app.UseBlazorFrameworkFiles();
        app.UseStaticFiles(RevalidatedStaticFiles);

        app.UseRouting();
        app.UseCors(CorsPolicy);
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseMiddleware<HubConnectionOwnershipMiddleware>();

        app.MapControllers();
        // Stateful reconnect replays buffered messages after short drops (no full resync on mobile blips).
        // Revoked sessions are cut off by the hub itself (it aborts their connections on revoke), and every
        // connection also ends with its access token, so a leaked token cannot keep a socket open forever
        // (clients reconnect with a fresh token; resync is lazy, so this costs a couple of small requests).
        app.MapHub<RealtimeHub>(HubRoutes.Realtime, o =>
        {
            o.AllowStatefulReconnects = true;
            o.CloseOnAuthenticationExpiration = true;
        });
        app.MapHealthChecks("/" + ApiRoutes.Health).AllowAnonymous();
        app.MapLiveKitProxy();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }

        // Unknown API routes must 404 instead of falling through to the SPA.
        app.MapFallback("/api/{**path}", (HttpContext ctx) => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound }))
            .AllowAnonymous();
        app.MapFallbackToFile("index.html", RevalidatedStaticFiles).AllowAnonymous();
        return app;
    }

    /// <summary>
    /// index.html, our JS modules and stylesheets keep fixed names (only _framework is fingerprinted), so browsers
    /// must revalidate them (cheap 304s): otherwise after a server update new .NET code runs against cached scripts.
    /// </summary>
    private static readonly StaticFileOptions RevalidatedStaticFiles = new()
    {
        OnPrepareResponse = ctx =>
        {
            var extension = Path.GetExtension(ctx.File.Name);
            if (extension is ".html" or ".js" or ".mjs" or ".css" or ".json" or ".webmanifest")
                ctx.Context.Response.Headers.CacheControl = "no-cache";
        },
    };

    private static void EnsureNoDevSecretsOutsideDevelopment(IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;
        var offending = new[]
            {
                ($"{JwtOptions.Section}:SigningKey", configuration[$"{JwtOptions.Section}:SigningKey"]),
                ($"{StorageOptions.Section}:MasterKey", configuration[$"{StorageOptions.Section}:MasterKey"]),
                ($"{LiveKitOptions.Section}:ApiSecret", configuration[$"{LiveKitOptions.Section}:ApiSecret"]),
            }
            .Where(x => DevSecrets.IsDevSecret(x.Item2))
            .Select(x => x.Item1)
            .ToList();
        if (offending.Count > 0)
            throw new InvalidOperationException(
                $"Refusing to start in '{environment.EnvironmentName}' with development secrets: {string.Join(", ", offending)}. Generate real secrets (see .env.example).");
    }

    private static async Task AddSecurityHeaders(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path;
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        var isSpa = !(path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs") || path.StartsWithSegments("/livekit")
                      || path.StartsWithSegments("/scalar") || path.StartsWithSegments("/openapi"));
        if (isSpa)
        {
            headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self' ws: wss:; " +
                "img-src 'self' data: blob:; media-src 'self' blob: data:; style-src 'self' 'unsafe-inline'; " +
                "font-src 'self' data:; worker-src 'self' blob:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            headers["X-Frame-Options"] = "DENY";
        }
        await next();
    }

    private static void AddAuth(IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var jwt = configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        var signingKey = jwt.SigningKey;
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Jwt:SigningKey must be configured.");
            signingKey = DevSecrets.JwtSigningKey;
            services.PostConfigure<JwtOptions>(o => o.SigningKey = DevSecrets.JwtSigningKey);
        }
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = SkyClaims.Username,
                };
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        // Browsers cannot set headers on WebSocket/SSE requests.
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments(HubRoutes.Realtime))
                            ctx.Token = token;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async ctx =>
                    {
                        var sub = ctx.Principal?.FindFirst(SkyClaims.UserId)?.Value;
                        var sid = ctx.Principal?.FindFirst(SkyClaims.SessionId)?.Value;
                        var validator = ctx.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
                        if (!Guid.TryParse(sub, out var userId) || !Guid.TryParse(sid, out var sessionId)
                            || !await validator.IsActiveAsync(sessionId, userId, ctx.HttpContext.RequestAborted))
                        {
                            ctx.HttpContext.Items[AuthErrorItem] = ErrorCodes.SessionRevoked;
                            ctx.Fail("Session is no longer active.");
                        }
                        else if (ctx.Principal?.FindFirst(SkyClaims.RefreshBinding)?.Value is { } binding)
                        {
                            // The client got the refresh response this token came with: see RefreshTokenPolicy.
                            await validator.ConfirmRefreshAsync(sessionId, binding, ctx.HttpContext.RequestAborted);
                        }
                    },
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        var code = ctx.HttpContext.Items[AuthErrorItem] as string ?? ErrorCodes.Unauthorized;
                        await WriteProblemAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, "Unauthorized", code);
                    },
                    OnForbidden = ctx => WriteProblemAsync(ctx.HttpContext, StatusCodes.Status403Forbidden, "Forbidden", ErrorCodes.Forbidden),
                };
            });

        services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }

    private static Task WriteProblemAsync(HttpContext ctx, int status, string title, string code)
    {
        if (ctx.Response.HasStarted) return Task.CompletedTask;
        ctx.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Extensions = { ["code"] = code, ["traceId"] = ctx.TraceIdentifier },
        };
        // The content-type overload: WriteAsJsonAsync would otherwise reset it to application/json.
        return ctx.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }

    private static void AddCors(IServiceCollection services, ServerOptions server, IWebHostEnvironment environment)
    {
        // Origins of the MAUI BlazorWebView (Android: https://0.0.0.1, iOS/Mac: app://0.0.0.1).
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "https://0.0.0.1", "http://0.0.0.1", "app://0.0.0.1", "https://0.0.0.0", "app://0.0.0.0",
        };
        foreach (var origin in server.CorsOrigins) allowed.Add(origin.TrimEnd('/'));

        services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
            .SetIsOriginAllowed(origin =>
            {
                if (allowed.Contains(origin)) return true;
                if (!environment.IsDevelopment()) return false;
                return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                       && (uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var ip) && IsPrivate(ip)));
            })
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders("Content-Range", "Accept-Ranges", "Content-Length", "ETag")));
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }

    private static void AddRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) => new ValueTask(
                WriteProblemAsync(ctx.HttpContext, StatusCodes.Status429TooManyRequests, "Too many requests", ErrorCodes.RateLimited));

            o.AddPolicy(RateLimitPolicies.Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));

            o.AddPolicy(RateLimitPolicies.UsernameCheck, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));

            o.AddPolicy(RateLimitPolicies.Search, ctx => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));

            o.AddPolicy(RateLimitPolicies.PhoneLookup, ctx => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
        });
    }

    private static string PartitionKey(HttpContext ctx) =>
        ctx.User.FindFirst(SkyClaims.UserId)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
