using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// Walks the real endpoint table: every route that is not on the SPEC's anonymous allow-list must answer 401 problem
/// details without (or with a bogus) token, and nothing else may be anonymous.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed partial class EndpointAuthorizationTests(ApiFactory factory)
{
    /// <summary>SPEC §3 ("exhaustive") plus the §6.2 failover helpers proof/probe and the app update check.</summary>
    private static readonly HashSet<string> AnonymousApi =
    [
        "POST " + ApiRoutes.Auth.Register,
        "POST " + ApiRoutes.Auth.Login,
        "POST " + ApiRoutes.Auth.Refresh,
        "GET " + ApiRoutes.Auth.UsernameAvailable,
        "GET " + ApiRoutes.ServerInfo,
        "GET " + ApiRoutes.ServerProof,
        "GET " + ApiRoutes.ServerProbe,
        "GET " + ApiRoutes.Media.Content,
        "GET " + ApiRoutes.Media.Thumbnail,
        "POST " + ApiRoutes.Calls.LiveKitWebhook,
        "GET " + ApiRoutes.AppVersion,
        "GET " + ApiRoutes.LatestApk,
        "HEAD " + ApiRoutes.LatestApk,
    ];

    /// <summary>Non-API anonymous plumbing: health, the SPA/API/updates fallbacks, the LiveKit proxy (own token) and dev-only docs.</summary>
    private static bool IsAllowedInfrastructure(string pattern) =>
        pattern == ApiRoutes.Health
        || pattern == "api/{**path}"
        || pattern == ApiRoutes.Updates + "/{**path}"
        || pattern == "{*path:nonfile}"
        || pattern == HubRoutes.LiveKitProxy.TrimStart('/')
        || pattern.StartsWith(HubRoutes.LiveKitProxy.TrimStart('/') + "/", StringComparison.Ordinal)
        || pattern.StartsWith("openapi/", StringComparison.Ordinal)
        || pattern.StartsWith("scalar", StringComparison.Ordinal);

    [Fact]
    public void Only_the_spec_allow_list_is_anonymous()
    {
        var endpoints = Endpoints();
        endpoints.Count.ShouldBeGreaterThan(50, "the endpoint table should contain every controller action");

        var anonymous = endpoints.Where(e => e.IsAnonymous).ToList();
        var unexpected = anonymous
            .Where(e => !IsAllowedInfrastructure(e.Pattern))
            .SelectMany(e => e.Methods.Select(m => $"{m} {e.Pattern}"))
            .Where(key => !AnonymousApi.Contains(key))
            .ToList();
        unexpected.ShouldBeEmpty("endpoints that are anonymous but not on the SPEC allow-list");

        var anonymousKeys = anonymous.SelectMany(e => e.Methods.Select(m => $"{m} {e.Pattern}")).ToHashSet();
        AnonymousApi.Where(key => !anonymousKeys.Contains(key)).ShouldBeEmpty("allow-listed endpoints that are missing or not anonymous");

        // The realtime hub is authenticated (its negotiate endpoint included).
        var hub = endpoints.Where(e => e.Pattern.StartsWith(HubRoutes.Realtime.TrimStart('/'), StringComparison.Ordinal)).ToList();
        hub.ShouldNotBeEmpty();
        hub.ShouldAllBe(e => !e.IsAnonymous);
    }

    [Fact]
    public async Task Every_protected_endpoint_answers_401_problem_without_a_token()
    {
        var client = factory.NewClient();
        var failures = await ProbeAllAsync(client);
        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_protected_endpoint_answers_401_problem_with_a_bogus_token()
    {
        // Structurally a JWT, but signed with a key the server never had.
        var forged = TokenKit.Create(factory, TokenKit.Claims(Guid.NewGuid(), Guid.NewGuid()), signingKey: "an-attacker-chosen-signing-key-of-sufficient-length!!");
        var client = factory.ClientWithToken(forged);
        var failures = await ProbeAllAsync(client);
        failures.ShouldBeEmpty();
    }

    private async Task<List<string>> ProbeAllAsync(HttpClient client)
    {
        var failures = new List<string>();
        var probed = 0;
        foreach (var endpoint in Endpoints().Where(e => !e.IsAnonymous))
        {
            foreach (var method in endpoint.Methods)
            {
                var path = "/" + Concrete(endpoint.Pattern);
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT" or "PATCH")
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, SecurityKit.Ct);
                probed++;
                var body = await response.Content.ReadAsStringAsync(SecurityKit.Ct);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    failures.Add($"{method} {path} → {(int)response.StatusCode}: {SecurityKit.Truncate(body, 200)}");
                    continue;
                }
                if (!body.Contains($"\"code\":\"{ErrorCodes.Unauthorized}\"", StringComparison.Ordinal)
                    && !body.Contains($"\"code\":\"{ErrorCodes.SessionRevoked}\"", StringComparison.Ordinal))
                    failures.Add($"{method} {path} → 401 without problem code: {SecurityKit.Truncate(body, 200)}");
            }
        }
        probed.ShouldBeGreaterThan(60, "every protected controller action should have been probed");
        return failures;
    }

    private List<EndpointInfo> Endpoints()
    {
        var source = factory.Services.GetRequiredService<EndpointDataSource>();
        return source.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => new EndpointInfo(
                (e.RoutePattern.RawText ?? "").TrimStart('/'),
                e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Where(m => m != "OPTIONS").ToList() is { Count: > 0 } methods
                    ? methods
                    : ["GET"],
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null))
            .ToList();
    }

    /// <summary>Fills route parameters with plausible values (constraints honored) so the request reaches its endpoint.</summary>
    private static string Concrete(string pattern) =>
        Parameter().Replace(pattern, match =>
        {
            var name = match.Groups["name"].Value;
            var constraints = match.Groups["constraints"].Value;
            if (match.Groups["star"].Success) return "x";
            if (constraints.Contains(":guid", StringComparison.Ordinal)) return Guid.NewGuid().ToString();
            if (constraints.Contains(":int", StringComparison.Ordinal)) return "0";
            return name switch
            {
                "token" => "dGVzdC10b2tlbi12YWx1ZQ",
                "username" => "someone_else",
                _ => match.Groups["optional"].Success ? "" : "x",
            };
        });

    [GeneratedRegex(@"\{(?<star>\*{1,2})?(?<name>[A-Za-z0-9_]+)(?<constraints>(:[^}?]+)*)(?<optional>\?)?\}")]
    private static partial Regex Parameter();

    private sealed record EndpointInfo(string Pattern, IReadOnlyList<string> Methods, bool IsAnonymous);
}
