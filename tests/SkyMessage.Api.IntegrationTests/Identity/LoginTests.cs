using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class LoginTests(ApiFactory factory)
{
    [Theory]
    [InlineData("{0}")]
    [InlineData("@{0}")]
    [InlineData("  {0}  ")]
    [InlineData("UPPER")]
    public async Task Username_login_is_case_insensitive_and_accepts_a_leading_at(string format)
    {
        var user = await factory.NewUserAsync(NewUsername("Login"));
        var login = format == "UPPER" ? user.Username.ToUpperInvariant() : string.Format(format, user.Username);

        var response = await factory.TryLoginAsync(login, user.Password, device: Device("laptop", ClientPlatform.MacOS));
        var auth = await response.ReadAsync<AuthResponse>();

        auth.User.Id.ShouldBe(user.Id);
        auth.User.Username.ShouldBe(user.Username);
        auth.User.SavedMessagesChatId.ShouldBe(user.Auth.User.SavedMessagesChatId);
        auth.SessionId.ShouldNotBe(user.SessionId);
        auth.MediaKey.ShouldNotBe(user.Auth.MediaKey);
        var session = await factory.LoadSessionAsync(auth.SessionId);
        session.DeviceName.ShouldBe("laptop");
        session.Platform.ShouldBe(ClientPlatform.MacOS);
        (await factory.NewClient(accessToken: auth.AccessToken).GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_answer()
    {
        var user = await factory.NewUserAsync();

        var wrongPassword = await factory.TryLoginAsync(user.Username, "not-the-password");
        var unknownUser = await factory.TryLoginAsync(NewUsername("nobody"), "not-the-password");
        var unknownPhone = await factory.TryLoginAsync(TestPhones.Thai().E164, "not-the-password");

        var titles = new HashSet<string?>();
        foreach (var response in new[] { wrongPassword, unknownUser, unknownPhone })
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            await response.ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            titles.Add(doc.RootElement.GetProperty("title").GetString());
        }
        titles.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Empty_login_or_password_is_a_validation_error()
    {
        await (await factory.TryLoginAsync("", "whatever-password")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await factory.TryLoginAsync(NewUsername(), "")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Phone_login_works_in_international_and_national_form_with_the_default_region()
    {
        var user = await factory.NewUserAsync();
        var phone = TestPhones.Thai();
        await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164 });

        // "+" numbers need no region.
        (await (await factory.TryLoginAsync(phone.E164, user.Password)).ReadAsync<AuthResponse>()).User.Id.ShouldBe(user.Id);
        var spaced = $"+66 {phone.E164[3..5]} {phone.E164[5..8]} {phone.E164[8..]}";
        (await (await factory.TryLoginAsync(spaced, user.Password)).ReadAsync<AuthResponse>()).User.Id.ShouldBe(user.Id);

        // A Thai national number is read in the client's region...
        var national = await factory.TryLoginAsync(phone.National, user.Password, defaultRegion: "th");
        (await national.ReadAsync<AuthResponse>()).User.Id.ShouldBe(user.Id);

        // ...but without it the RU fallback cannot read it.
        await (await factory.TryLoginAsync(phone.National, user.Password))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await (await factory.TryLoginAsync(phone.E164, "wrong-password"))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task Russian_national_number_falls_back_to_RU_even_with_another_default_region()
    {
        var user = await factory.NewUserAsync();
        var phone = TestPhones.Russian();
        await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164 });

        (await (await factory.TryLoginAsync(phone.National, user.Password)).ReadAsync<AuthResponse>()).User.Id.ShouldBe(user.Id);
        (await (await factory.TryLoginAsync(phone.National, user.Password, defaultRegion: "TH")).ReadAsync<AuthResponse>()).User.Id.ShouldBe(user.Id);
        var digitsOnly = "8" + phone.E164[2..];
        (await (await factory.TryLoginAsync(digitsOnly, user.Password, defaultRegion: "XX")).ReadAsync<AuthResponse>()).User.Id.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Ten_failures_lock_the_login_out_even_for_the_right_password()
    {
        var user = await factory.NewUserAsync();
        var other = await factory.NewUserAsync();
        var ip = NewIp();

        for (var i = 0; i < 10; i++)
            await (await factory.TryLoginAsync(user.Username, "wrong-password-" + i, ip))
                .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);

        await (await factory.TryLoginAsync(user.Username, user.Password, ip))
            .ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        // The lockout is per login (any spelling of it), not per client address.
        await (await factory.TryLoginAsync("@" + user.Username.ToUpperInvariant(), user.Password))
            .ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        await (await factory.TryLoginAsync(other.Username, other.Password, ip)).ShouldBeStatusAsync(HttpStatusCode.OK);

        // Signed-in sessions are not affected.
        (await user.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Phone_logins_are_locked_out_per_number()
    {
        var user = await factory.NewUserAsync();
        var phone = TestPhones.Russian();
        await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164 });
        var ip = NewIp();

        for (var i = 0; i < 10; i++)
            await (await factory.TryLoginAsync(i % 2 == 0 ? phone.E164 : phone.National, "wrong-password", ip))
                .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);

        await (await factory.TryLoginAsync(phone.E164, user.Password, ip))
            .ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        await (await factory.TryLoginAsync(phone.National, user.Password, ip))
            .ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_count()
    {
        var user = await factory.NewUserAsync();
        var ip = NewIp();

        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 9; i++)
                await (await factory.TryLoginAsync(user.Username, "wrong-password", ip))
                    .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
            await (await factory.TryLoginAsync(user.Username, user.Password, ip)).ShouldBeStatusAsync(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Outdated_password_hashes_are_upgraded_on_login()
    {
        var user = await factory.NewUserAsync();
        var legacyHasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
        }));
        var legacyHash = legacyHasher.HashPassword(new User(), user.Password);
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, legacyHash), Ct));

        await (await factory.TryLoginAsync(user.Username, user.Password)).ShouldBeStatusAsync(HttpStatusCode.OK);

        var stored = (await factory.LoadUserAsync(user.Id)).PasswordHash;
        stored.ShouldNotBe(legacyHash);
        Convert.FromBase64String(stored)[0].ShouldBe((byte)0x01); // Identity V3 format marker
        new PasswordHasher<User>().VerifyHashedPassword(new User(), stored, user.Password).ShouldBe(PasswordVerificationResult.Success);
        await (await factory.TryLoginAsync(user.Username, user.Password)).ShouldBeStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Per_ip_rate_limit_applies_to_authentication_endpoints()
    {
        var ip = NewIp();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
            statuses.Add((await factory.TryLoginAsync(NewUsername("rl"), "wrong-password", ip)).StatusCode);

        statuses.Take(30).ShouldAllBe(s => s == HttpStatusCode.Unauthorized);
        var limited = await factory.TryLoginAsync(NewUsername("rl"), "wrong-password", ip);
        await limited.ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
    }
}
