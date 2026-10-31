using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class RegistrationTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_creates_the_user_a_session_and_the_saved_messages_chat()
    {
        var username = NewUsername("Reg");
        var user = await factory.NewUserAsync(username, displayName: "  Alice Example  ");

        var auth = user.Auth;
        auth.User.Username.ShouldBe(username);
        auth.User.DisplayName.ShouldBe("Alice Example");
        auth.User.PhoneNumber.ShouldBeNull();
        auth.User.PhoneSource.ShouldBe(PhoneSource.None);
        auth.User.Privacy.ShouldBe(new PrivacySettingsDto());
        auth.SessionId.ShouldNotBe(Guid.Empty);
        auth.AccessToken.ShouldNotBeNullOrWhiteSpace();
        auth.AccessTokenExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(10));
        auth.RefreshToken.Length.ShouldBe(43);
        auth.RefreshTokenExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(170));
        MediaSignature.DecodeKey(auth.MediaKey).Length.ShouldBe(MediaSignature.KeyLength);
        auth.User.SavedMessagesChatId.ShouldNotBe(Guid.Empty);

        var me = await user.Client.GetAs<MeDto>(ApiRoutes.Users.Me);
        me.ShouldBe(auth.User);

        var (saved, members, session) = await factory.WithDbAsync(async db => (
            await db.Chats.AsNoTracking().SingleAsync(c => c.Id == auth.User.SavedMessagesChatId, Ct),
            await db.ChatMembers.AsNoTracking().Where(m => m.ChatId == auth.User.SavedMessagesChatId).ToListAsync(Ct),
            await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == auth.SessionId, Ct)));
        saved.Type.ShouldBe(ChatType.Saved);
        saved.PrivateKey.ShouldBe(Chat.SavedKeyFor(user.Id));
        saved.MemberCount.ShouldBe(1);
        members.ShouldHaveSingleItem().UserId.ShouldBe(user.Id);
        members[0].Role.ShouldBe(ChatRole.Owner);
        session.UserId.ShouldBe(user.Id);
        session.IpAddress.ShouldBe(user.Ip);
        session.DeviceName.ShouldBe("tests");
        session.RefreshTokenHash.ShouldNotContain(auth.RefreshToken);
        session.RefreshTokenHash.Length.ShouldBe(64);
        session.MediaKey.ShouldBe(MediaSignature.DecodeKey(auth.MediaKey));
    }

    [Fact]
    public async Task Username_is_cleaned_and_unique_regardless_of_case()
    {
        var username = NewUsername("Case");
        var first = await factory.NewClient().PostAs<AuthResponse>(ApiRoutes.Auth.Register, RegisterRequest("  @" + username + " "));
        first.User.Username.ShouldBe(username);

        var duplicate = await factory.NewClient().Post(ApiRoutes.Auth.Register, RegisterRequest(username.ToUpperInvariant()));
        await duplicate.ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.UsernameTaken);

        var withAt = await factory.NewClient().Post(ApiRoutes.Auth.Register, RegisterRequest("@" + username.ToLowerInvariant()));
        await withAt.ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.UsernameTaken);
    }

    [Fact]
    public async Task Concurrent_registrations_of_one_username_create_exactly_one_user()
    {
        var username = NewUsername("Race");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            factory.NewClient().Post(ApiRoutes.Auth.Register, RegisterRequest(username))));

        attempts.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        foreach (var loser in attempts.Where(r => r.StatusCode != HttpStatusCode.OK))
            await loser.ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.UsernameTaken);
        var count = await factory.WithDbAsync(db => db.Users.CountAsync(u => u.UsernameNormalized == username.ToLowerInvariant(), Ct));
        count.ShouldBe(1);
    }

    [Theory]
    [InlineData("abcd")] // too short
    [InlineData("1abcdef")] // must start with a letter
    [InlineData("abc-def")]
    [InlineData("abc def")]
    [InlineData("абвгдеж")] // Latin only
    [InlineData("abcdefghijabcdefghijabcdefghijabc")] // 33 chars
    [InlineData("admin")]
    [InlineData("Support")]
    [InlineData("system")]
    [InlineData("SkyMessage")]
    [InlineData("skymessage_team")]
    [InlineData("telegram")]
    [InlineData("telegram_bot1")]
    [InlineData("WhatsApp")]
    [InlineData("deleted_account")]
    [InlineData("deletedUser")]
    public async Task Invalid_or_reserved_usernames_are_rejected(string username)
    {
        var response = await factory.NewClient().Post(ApiRoutes.Auth.Register, RegisterRequest(username));
        await response.ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.UsernameInvalid);
    }

    [Fact]
    public async Task Short_password_is_too_weak()
    {
        var response = await factory.NewClient().Post(ApiRoutes.Auth.Register, RegisterRequest(password: "1234567"));
        await response.ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.PasswordTooWeak);

        var ok = await factory.NewClient().Post(ApiRoutes.Auth.Register, RegisterRequest(password: "12345678"));
        await ok.ShouldBeStatusAsync(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Display_name_is_required(string displayName)
    {
        var response = await factory.NewClient().Post(ApiRoutes.Auth.Register, RegisterRequest(displayName: displayName));
        await response.ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Display_name_is_limited_to_64_characters_after_trimming()
    {
        var tooLong = await factory.NewClient().Post(ApiRoutes.Auth.Register, RegisterRequest(displayName: new string('x', Limits.DisplayNameMaxLength + 1)));
        await tooLong.ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var padded = await factory.NewClient().PostAs<AuthResponse>(ApiRoutes.Auth.Register,
            RegisterRequest(displayName: "  " + new string('y', Limits.DisplayNameMaxLength) + "  "));
        padded.User.DisplayName.ShouldBe(new string('y', Limits.DisplayNameMaxLength));
    }

    [Fact]
    public async Task Username_availability_check_reports_format_errors_taken_names_and_the_callers_own_name()
    {
        var user = await factory.NewUserAsync(NewUsername("Avail"));
        var anonymous = factory.NewClient();

        var free = await anonymous.GetAs<UsernameAvailabilityDto>(ApiRoutes.Auth.UsernameAvailablePath("@" + NewUsername("Free")));
        free.Available.ShouldBeTrue();
        free.ErrorCode.ShouldBeNull();
        free.Username.ShouldNotStartWith("@");

        var taken = await anonymous.GetAs<UsernameAvailabilityDto>(ApiRoutes.Auth.UsernameAvailablePath(user.Username.ToUpperInvariant()));
        taken.Available.ShouldBeFalse();
        taken.ErrorCode.ShouldBe(ErrorCodes.UsernameTaken);

        var invalid = await anonymous.GetAs<UsernameAvailabilityDto>(ApiRoutes.Auth.UsernameAvailablePath("ab"));
        invalid.Available.ShouldBeFalse();
        invalid.ErrorCode.ShouldBe(ErrorCodes.UsernameInvalid);

        var reserved = await anonymous.GetAs<UsernameAvailabilityDto>(ApiRoutes.Auth.UsernameAvailablePath("administrator"));
        reserved.Available.ShouldBeFalse();
        reserved.ErrorCode.ShouldBe(ErrorCodes.UsernameInvalid);

        var own = await user.Client.GetAs<UsernameAvailabilityDto>(ApiRoutes.Auth.UsernameAvailablePath(user.Username));
        own.Available.ShouldBeTrue();
    }

    [Fact]
    public async Task Invite_code_mode_requires_the_configured_code()
    {
        const string code = "family-2026";
        await using var invited = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Server:RegistrationMode", nameof(RegistrationMode.InviteCode));
            b.UseSetting("Server:RegistrationCode", code);
        });

        var info = await Client(invited).GetAs<ServerInfoDto>(ApiRoutes.ServerInfo);
        info.RegistrationMode.ShouldBe(RegistrationMode.InviteCode);

        await (await Client(invited).Post(ApiRoutes.Auth.Register, RegisterRequest()))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.RegistrationClosed);
        await (await Client(invited).Post(ApiRoutes.Auth.Register, RegisterRequest() with { InviteCode = "wrong-code" }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.RegistrationClosed);

        var registered = await Client(invited).PostAs<AuthResponse>(ApiRoutes.Auth.Register, RegisterRequest() with { InviteCode = " " + code + " " });
        registered.User.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Closed_mode_rejects_every_registration_but_still_allows_sign_in()
    {
        var existing = await factory.NewUserAsync();
        await using var closed = factory.WithWebHostBuilder(b => b.UseSetting("Server:RegistrationMode", nameof(RegistrationMode.Closed)));

        await (await Client(closed).Post(ApiRoutes.Auth.Register, RegisterRequest() with { InviteCode = "anything" }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.RegistrationClosed);

        var login = await Client(closed).Post(ApiRoutes.Auth.Login, new LoginRequest
        {
            Login = existing.Username,
            Password = existing.Password,
            Device = Device(),
        });
        await login.ShouldBeStatusAsync(HttpStatusCode.OK);
    }

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", NewIp());
        return client;
    }
}
