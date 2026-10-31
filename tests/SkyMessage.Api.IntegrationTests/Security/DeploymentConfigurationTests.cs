using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// Settings a real deployment relies on: client IPs cannot be spoofed past the rate limiter, development secrets are
/// refused outside Development, and the registration modes really gate account creation.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DeploymentConfigurationTests(ApiFactory factory)
{
    [Fact]
    public async Task Direct_clients_cannot_spoof_X_Forwarded_For_to_escape_the_auth_limit()
    {
        // A client connecting straight to the API from a public address (not a trusted proxy).
        var publicIp = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var direct = ClientFrom(publicIp);
        for (var i = 0; i < 30; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Auth.Login)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", SecurityKit.NextIp());
            using var response = await direct.SendAsync(request, SecurityKit.Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"request {i + 1}");
        }

        using var spoofed = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Auth.Login)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        spoofed.Headers.TryAddWithoutValidation("X-Forwarded-For", SecurityKit.NextIp());
        using var limited = await direct.SendAsync(spoofed, SecurityKit.Ct);
        await limited.ShouldBeProblemAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task Session_ip_comes_from_trusted_proxies_only()
    {
        // Through the local reverse proxy (loopback is trusted): the forwarded client address is recorded.
        var viaProxy = ClientFrom("127.0.0.1");
        viaProxy.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.77");
        var proxied = await viaProxy.PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Register, new RegisterRequest
        {
            Username = SecurityKit.NewUsername("proxied"), Password = SecurityKit.Password, DisplayName = "Proxied", Device = SecurityKit.Device("x"),
        });

        // Straight from the internet: a forged header is ignored.
        var publicIp = $"198.18.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(1, 254)}";
        var direct = ClientFrom(publicIp);
        direct.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", "10.0.0.1");
        var forged = await direct.PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Register, new RegisterRequest
        {
            Username = SecurityKit.NewUsername("direct"), Password = SecurityKit.Password, DisplayName = "Direct", Device = SecurityKit.Device("x"),
        });

        (await factory.ClientWithToken(proxied.AccessToken).GetJsonAsync<List<SessionDto>>(ApiRoutes.Sessions.Root)).Single().IpAddress.ShouldBe("198.51.100.77");
        (await factory.ClientWithToken(forged.AccessToken).GetJsonAsync<List<SessionDto>>(ApiRoutes.Sessions.Root)).Single().IpAddress.ShouldBe(publicIp);
    }

    [Theory]
    [InlineData("Jwt:SigningKey", DevSecrets.JwtSigningKey, "development secrets")]
    [InlineData("Storage:MasterKey", DevSecrets.StorageMasterKey, "development secrets")]
    [InlineData("LiveKit:ApiSecret", DevSecrets.LiveKitApiSecret, "development secrets")]
    [InlineData("Jwt:SigningKey", "too-short-key", "at least 32 bytes")]
    [InlineData("Jwt:SigningKey", "", "must be configured")]
    public async Task Production_refuses_to_start_with_development_or_weak_secrets(string key, string value, string reason)
    {
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:SigningKey", "production-like-signing-key-" + Guid.NewGuid().ToString("N"));
            builder.UseSetting(key, value);
        });
        try
        {
            var error = Should.Throw<Exception>(() => host.CreateClient());
            error.ToString().ShouldContain(reason);
        }
        finally
        {
            await DisposeQuietlyAsync(host);
        }
    }

    [Fact]
    public async Task Invite_code_mode_requires_the_code_and_never_reveals_it()
    {
        const string code = "family-only-7731";
        await using var host = ProductionHost.Create(factory, ("Server:RegistrationMode", "InviteCode"), ("Server:RegistrationCode", code));
        var client = Client(host);

        using (var info = await client.GetAsync(ApiRoutes.ServerInfo, SecurityKit.Ct))
        {
            var json = await info.Content.ReadAsStringAsync(SecurityKit.Ct);
            info.StatusCode.ShouldBe(HttpStatusCode.OK, json);
            json.ShouldContain("\"registrationMode\":\"InviteCode\"");
            json.ShouldNotContain(code);
        }

        foreach (var attempt in new string?[] { null, "", "family-only-7730", code + "x", code.ToUpperInvariant() })
        {
            await client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Register, Register(attempt), HttpStatusCode.Forbidden, ErrorCodes.RegistrationClosed);
        }
        (await client.PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Register, Register(code))).AccessToken.ShouldNotBeNullOrEmpty();
        // Surrounding whitespace (copy/paste) is tolerated.
        (await client.PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Register, Register($"  {code} "))).AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Closed_mode_and_invite_mode_without_a_code_let_nobody_in()
    {
        await using var closed = ProductionHost.Create(factory, ("Server:RegistrationMode", "Closed"), ("Server:RegistrationCode", "ignored-code"));
        await Client(closed).ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Register, Register("ignored-code"), HttpStatusCode.Forbidden, ErrorCodes.RegistrationClosed);

        await using var misconfigured = ProductionHost.Create(factory, ("Server:RegistrationMode", "InviteCode"));
        var client = Client(misconfigured);
        await client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Register, Register(null), HttpStatusCode.Forbidden, ErrorCodes.RegistrationClosed);
        await client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Register, Register(""), HttpStatusCode.Forbidden, ErrorCodes.RegistrationClosed);

        // Existing users still sign in while registration is closed.
        var alice = await factory.RegisterUserAsync("alice");
        (await Client(closed).PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Login,
            new LoginRequest { Login = alice.Username, Password = SecurityKit.Password, Device = SecurityKit.Device("x") })).User.Id.ShouldBe(alice.Id);
    }

    private HttpClient ClientFrom(string remoteIp)
    {
        var handler = factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp));
        return new HttpClient(handler) { BaseAddress = factory.Server.BaseAddress };
    }

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", SecurityKit.NextIp());
        return client;
    }

    private static RegisterRequest Register(string? inviteCode) => new()
    {
        Username = SecurityKit.NewUsername("newbie"),
        Password = SecurityKit.Password,
        DisplayName = "Newbie",
        Device = SecurityKit.Device("x"),
        InviteCode = inviteCode,
    };

    private static async Task DisposeQuietlyAsync(WebApplicationFactory<Program> host)
    {
        try
        {
            await host.DisposeAsync();
        }
        catch (Exception)
        {
            // The host never started; nothing to clean up.
        }
    }
}
