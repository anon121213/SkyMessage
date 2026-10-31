using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>CORS for the MAUI WebView origins and the security headers of SPA, API and media responses.</summary>
[Collection(ApiCollection.Name)]
public sealed class CorsAndHeadersTests(ApiFactory factory)
{
    /// <summary>Origins of the MAUI BlazorWebView: Android https://0.0.0.1, iOS/Mac app://0.0.0.1 (and the 0.0.0.0 variants).</summary>
    public static readonly string[] MauiOrigins = ["https://0.0.0.1", "http://0.0.0.1", "app://0.0.0.1", "https://0.0.0.0", "app://0.0.0.0"];

    /// <summary>Never allowed: foreign sites, look-alikes of the MAUI origins, the opaque "null" origin.</summary>
    public static readonly string[] HostileOrigins =
    [
        "https://evil.example", "https://0.0.0.1.evil.example", "https://evil.example?https://0.0.0.1", "null", "http://8.8.8.8",
        "https://0.0.0.2", "https://0.0.0.1:8443",
    ];

    [Fact]
    public async Task Development_allows_maui_and_local_dev_origins_but_not_foreign_sites()
    {
        var client = factory.NewClient();
        foreach (var origin in MauiOrigins.Concat(["http://localhost:5173", "http://127.0.0.1:8080", "http://192.168.1.34:5000", "http://10.0.2.2:8080"]))
            await AssertAllowedAsync(client, origin);
        foreach (var origin in HostileOrigins)
            await AssertRejectedAsync(client, origin);
    }

    [Fact]
    public async Task Production_allows_only_the_maui_origins_and_configured_web_origins()
    {
        await using var production = ProductionHost.Create(factory);
        var client = production.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", SecurityKit.NextIp());

        foreach (var origin in MauiOrigins.Append(ProductionHost.WebOrigin))
            await AssertAllowedAsync(client, origin);
        foreach (var origin in HostileOrigins.Concat(["http://localhost:5173", "http://127.0.0.1:8080", "http://192.168.1.34:5000", "http://10.0.2.2:8080"]))
            await AssertRejectedAsync(client, origin);
    }

    [Fact]
    public async Task Production_sends_hsts_over_https_and_hides_the_dev_api_explorer()
    {
        await using var production = ProductionHost.Create(factory);
        var client = production.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://chat.family.example/"),
        });
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", SecurityKit.NextIp());

        using (var info = await client.GetAsync(ApiRoutes.ServerInfo, SecurityKit.Ct))
        {
            await info.ShouldHaveStatusAsync(HttpStatusCode.OK);
            info.Headers.TryGetValues("Strict-Transport-Security", out var hsts).ShouldBeTrue("HSTS on https responses in production");
            hsts!.Single().ShouldContain("max-age=");
            (await info.Content.ReadFromJsonAsync<ServerInfoDto>(SkyJson.Options, SecurityKit.Ct))!.CallsEnabled.ShouldBeFalse();
        }

        foreach (var path in new[] { "openapi/v1.json", "scalar/v1" })
        {
            using var response = await client.GetAsync(path, SecurityKit.Ct);
            var body = await response.Content.ReadAsStringAsync(SecurityKit.Ct);
            body.ShouldNotContain("\"openapi\"", Case.Insensitive, $"{path} must not serve the API description in production");
            body.ShouldNotContain("scalar", Case.Insensitive, path);
        }
    }

    [Fact]
    public async Task Api_responses_get_nosniff_and_no_referrer_but_no_page_policies()
    {
        var alice = await factory.RegisterUserAsync("alice");
        foreach (var path in new[] { ApiRoutes.ServerInfo, ApiRoutes.Users.Me, "api/unknown-route", "hubs/realtime/negotiate" })
        {
            using var response = path.StartsWith("hubs", StringComparison.Ordinal)
                ? await factory.NewClient().PostAsync(path, null, SecurityKit.Ct)
                : await alice.Client.GetAsync(path, SecurityKit.Ct);
            Header(response, "X-Content-Type-Options").ShouldBe("nosniff", path);
            Header(response, "Referrer-Policy").ShouldBe("no-referrer", path);
            Header(response, "Content-Security-Policy").ShouldBeNull(path);
            Header(response, "X-Frame-Options").ShouldBeNull(path);
            Header(response, "Strict-Transport-Security").ShouldBeNull($"{path}: no HSTS in Development / over http");
        }
    }

    [Fact]
    public async Task Spa_responses_get_a_strict_content_security_policy()
    {
        var client = factory.NewClient();
        foreach (var path in new[] { "/", "/chat/" + Guid.NewGuid(), "/join/some-token", "/u/someone" })
        {
            using var response = await client.GetAsync(path, SecurityKit.Ct);
            var csp = Header(response, "Content-Security-Policy");
            csp.ShouldNotBeNull(path);
            csp.ShouldContain("default-src 'self'");
            csp.ShouldContain("frame-ancestors 'none'");
            csp.ShouldContain("base-uri 'self'");
            csp.ShouldContain("form-action 'self'");
            csp.ShouldNotContain("'unsafe-eval'", Case.Sensitive, "only 'wasm-unsafe-eval' is needed for Blazor");
            csp.Split(';').Single(d => d.Trim().StartsWith("script-src", StringComparison.Ordinal)).ShouldNotContain("unsafe-inline");
            csp.ShouldNotContain("script-src *");
            Header(response, "X-Frame-Options").ShouldBe("DENY", path);
            Header(response, "X-Content-Type-Options").ShouldBe("nosniff", path);
            Header(response, "Referrer-Policy").ShouldBe("no-referrer", path);
        }
    }

    [Fact]
    public async Task Cross_origin_actual_requests_expose_only_the_media_headers()
    {
        var client = factory.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.ServerInfo);
        request.Headers.TryAddWithoutValidation("Origin", "https://0.0.0.1");
        using var response = await client.SendAsync(request, SecurityKit.Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        Header(response, "Access-Control-Allow-Origin").ShouldBe("https://0.0.0.1");
        Header(response, "Access-Control-Allow-Credentials").ShouldBe("true");
        var exposed = Header(response, "Access-Control-Expose-Headers")!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        exposed.ShouldBe(["Content-Range", "Accept-Ranges", "Content-Length", "ETag"], ignoreOrder: true);
    }

    private static async Task AssertAllowedAsync(HttpClient client, string origin)
    {
        using var response = await PreflightAsync(client, origin);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, origin);
        Header(response, "Access-Control-Allow-Origin").ShouldBe(origin, $"{origin} should be allowed");
        Header(response, "Access-Control-Allow-Credentials").ShouldBe("true", origin);
        // Never a wildcard together with credentials.
        Header(response, "Access-Control-Allow-Origin").ShouldNotBe("*");
    }

    private static async Task AssertRejectedAsync(HttpClient client, string origin)
    {
        using var response = await PreflightAsync(client, origin);
        Header(response, "Access-Control-Allow-Origin").ShouldBeNull($"{origin} must not be allowed");
        Header(response, "Access-Control-Allow-Credentials").ShouldBeNull(origin);

        using var actual = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.ServerInfo);
        actual.Headers.TryAddWithoutValidation("Origin", origin);
        using var actualResponse = await client.SendAsync(actual, SecurityKit.Ct);
        Header(actualResponse, "Access-Control-Allow-Origin").ShouldBeNull($"{origin} (actual request) must not be allowed");
    }

    private static Task<HttpResponseMessage> PreflightAsync(HttpClient client, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, ApiRoutes.Users.Me);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "PATCH");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization,content-type");
        return client.SendAsync(request, SecurityKit.Ct);
    }

    internal static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : null;
}

/// <summary>
/// The same API on the shared test database, but configured like a production deployment (Production environment,
/// real secrets, no dev CORS relaxations) plus one configured web origin.
/// </summary>
internal static class ProductionHost
{
    public const string WebOrigin = "https://web.family.example";

    public static WebApplicationFactory<Program> Create(ApiFactory factory, params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:SigningKey", "production-like-signing-key-" + Guid.NewGuid().ToString("N"));
            builder.UseSetting("Server:CorsOrigins:0", WebOrigin + "/");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });
}
