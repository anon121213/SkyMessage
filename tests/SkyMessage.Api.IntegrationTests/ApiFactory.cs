using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace SkyMessage.Api.IntegrationTests;

/// <summary>Real API over a throwaway PostgreSQL container (schema created from the EF model).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        using var scope = Services.CreateScope();
        // Real migrations, so the tests also verify the migration matches the model.
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static int _clientCounter;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, TestClientIpStartupFilter>());
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("Server:MigrateOnStartup", "false");
        builder.UseSetting("LiveKit:InternalUrl", "http://127.0.0.1:1");
    }

    /// <summary>Each client gets its own client IP, so per-IP rate limits (auth) don't add up across tests.</summary>
    public HttpClient CreateJsonClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var n = Interlocked.Increment(ref _clientCounter);
        client.DefaultRequestHeaders.Add(TestClientIpStartupFilter.Header, $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}");
        return client;
    }

    /// <summary>Registers a fresh user and returns an authenticated client plus the auth response.</summary>
    public async Task<(HttpClient Client, AuthResponse Auth)> RegisterAsync(string? username = null, string displayName = "Test User")
    {
        username ??= "u" + Guid.NewGuid().ToString("N")[..12];
        var client = CreateJsonClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Register, new RegisterRequest
        {
            Username = username,
            Password = "correct-horse-battery",
            DisplayName = displayName,
            Device = new DeviceInfoDto { DeviceName = "tests", Platform = ClientPlatform.Linux, AppVersion = "test" },
        }, SkyJson.Options);
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(SkyJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth);
    }

    public HubConnection CreateHubConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, HubRoutes.Realtime.TrimStart('/')), o =>
            {
                o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .AddJsonProtocol(o => SkyJson.Configure(o.PayloadSerializerOptions))
            .Build();
}

/// <summary>TestServer has no client IP; lets a test client pick one (header only honored in this test host).</summary>
internal sealed class TestClientIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Client-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, nextMiddleware) =>
        {
            // Tests that simulate proxies send X-Forwarded-For; leave those to the forwarded-headers middleware.
            if (!ctx.Request.Headers.ContainsKey("X-Forwarded-For")
                && ctx.Request.Headers.TryGetValue(Header, out var value) && System.Net.IPAddress.TryParse(value, out var ip))
                ctx.Connection.RemoteIpAddress = ip;
            return nextMiddleware(ctx);
        });
        next(app);
    };
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
