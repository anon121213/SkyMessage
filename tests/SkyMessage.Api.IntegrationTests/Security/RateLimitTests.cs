using System.Net;
using System.Text;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// Rate limits of the auth surface (ApiSetup: 30/min per IP for login/register/refresh, 60/min per IP for the
/// username check, 10 per 10 min per user for phone lookups) and the per-login lockout (10 failures / 15 min).
/// Each test uses fresh client IPs, so its partitions start empty.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RateLimitTests(ApiFactory factory)
{
    private const int AuthPermits = 30;

    [Theory]
    [InlineData(ApiRoutes.Auth.Login)]
    [InlineData(ApiRoutes.Auth.Register)]
    [InlineData(ApiRoutes.Auth.Refresh)]
    public async Task Auth_endpoints_allow_30_requests_per_minute_per_ip(string route)
    {
        var attacker = factory.NewClient();
        // Invalid bodies are cheap (no password hashing) and still count: the limiter runs before the endpoint.
        for (var i = 0; i < AuthPermits; i++)
        {
            using var allowed = await attacker.PostAsync(route, Json("{}"), SecurityKit.Ct);
            allowed.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"request {i + 1} of {AuthPermits}");
        }

        using (var limited = await attacker.PostAsync(route, Json("{}"), SecurityKit.Ct))
            await limited.ShouldBeProblemAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);

        // Partitioned per client IP: someone else is not affected.
        using var neighbour = await factory.NewClient().PostAsync(route, Json("{}"), SecurityKit.Ct);
        neighbour.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ten_wrong_passwords_lock_the_account_whatever_the_ip()
    {
        var victim = await factory.RegisterUserAsync("victim");
        var bystander = await factory.RegisterUserAsync("bystander");

        for (var i = 0; i < 10; i++)
        {
            // A new IP for every guess: the per-IP limiter alone would never trigger.
            await factory.NewClient().ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
                new LoginRequest { Login = victim.Username, Password = $"guess-number-{i}", Device = SecurityKit.Device("attacker") },
                HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        }

        // Locked even for the right password, from any address, and for the '@' / upper-case spellings.
        foreach (var login in new[] { victim.Username, "@" + victim.Username, victim.Username.ToUpperInvariant() })
        {
            await factory.NewClient().ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
                new LoginRequest { Login = login, Password = SecurityKit.Password, Device = SecurityKit.Device("owner") },
                HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        }

        // The lockout is per account: other people sign in normally, and the victim's open sessions keep working.
        await factory.NewClient().PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Login,
            new LoginRequest { Login = bystander.Username, Password = SecurityKit.Password, Device = SecurityKit.Device("bystander") });
        (await victim.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(victim.Id);
    }

    [Fact]
    public async Task Current_password_checks_are_throttled_too()
    {
        var alice = await factory.RegisterUserAsync("alice");
        for (var i = 0; i < 10; i++)
        {
            await alice.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.ChangePassword,
                new ChangePasswordRequest { CurrentPassword = $"guess-{i}", NewPassword = "new-password-123" },
                HttpStatusCode.BadRequest, ErrorCodes.WrongPassword);
        }

        // A stolen session cannot brute-force the password through change-password or delete-account.
        await alice.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.ChangePassword,
            new ChangePasswordRequest { CurrentPassword = SecurityKit.Password, NewPassword = "new-password-123" },
            HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        await alice.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Users.MeDelete,
            new DeleteAccountRequest { Password = SecurityKit.Password }, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        (await alice.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(alice.Id);
    }

    [Fact]
    public async Task Username_checks_allow_60_per_minute_per_ip()
    {
        var client = factory.NewClient();
        for (var i = 0; i < 60; i++)
        {
            using var allowed = await client.GetAsync(ApiRoutes.Auth.UsernameAvailablePath($"probe_{i:D3}_name"), SecurityKit.Ct);
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK, $"request {i + 1}");
        }

        using var limited = await client.GetAsync(ApiRoutes.Auth.UsernameAvailablePath("probe_last_name"), SecurityKit.Ct);
        await limited.ShouldBeProblemAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task Phone_lookups_allow_10_per_10_minutes_per_user_whatever_the_ip()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var request = new FindByPhonesRequest { PhoneNumbers = ["+79991112233"], DefaultRegion = "RU" };
        for (var i = 0; i < 10; i++)
        {
            // Hopping IPs does not help: the partition is the user.
            var hopping = factory.ClientWithToken(alice.AccessToken);
            await hopping.PostJsonAsync<List<PhoneMatchDto>>(ApiRoutes.Users.FindByPhones, request);
        }

        await factory.ClientWithToken(alice.AccessToken).ProblemAsync(HttpMethod.Post, ApiRoutes.Users.FindByPhones, request,
            HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);

        var bob = await factory.RegisterUserAsync("bob");
        await bob.Client.PostJsonAsync<List<PhoneMatchDto>>(ApiRoutes.Users.FindByPhones, request);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
