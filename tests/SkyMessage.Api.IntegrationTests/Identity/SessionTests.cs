using System.Net;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using SkyMessage.Application.Common.Security;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class SessionTests(ApiFactory factory)
{
    [Fact]
    public async Task Sessions_list_shows_active_devices_with_the_current_one_first()
    {
        var first = await factory.NewUserAsync();
        var second = await first.SignInAgainAsync("Pixel 9", ClientPlatform.Android);
        var third = await first.SignInAgainAsync("revoked one");
        await (await third.Client.Post(ApiRoutes.Auth.Logout)).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        var sessions = await second.Client.GetAs<IReadOnlyList<SessionDto>>(ApiRoutes.Sessions.Root);

        sessions.Select(s => s.Id).ShouldBe([second.SessionId, first.SessionId]);
        sessions[0].IsCurrent.ShouldBeTrue();
        sessions[1].IsCurrent.ShouldBeFalse();
        sessions[0].DeviceName.ShouldBe("Pixel 9");
        sessions[0].Platform.ShouldBe(ClientPlatform.Android);
        sessions[0].AppVersion.ShouldBe("1.0.0-test");
        sessions[0].OsVersion.ShouldBe("TestOS 1");
        sessions[0].IpAddress.ShouldBe(second.Ip);
        sessions[1].IpAddress.ShouldBe(first.Ip);
        sessions[1].DeviceName.ShouldBe("tests");

        var fromFirst = await first.Client.GetAs<IReadOnlyList<SessionDto>>(ApiRoutes.Sessions.Root);
        fromFirst[0].Id.ShouldBe(first.SessionId);
        fromFirst[0].IsCurrent.ShouldBeTrue();
    }

    [Fact]
    public async Task Terminating_a_session_cuts_off_its_tokens_and_realtime_connection()
    {
        var phone = await factory.NewUserAsync();
        var tablet = await phone.SignInAgainAsync("tablet");
        await using var phoneHub = await RealtimeProbe.ConnectAsync(phone);
        await using var tabletHub = await RealtimeProbe.ConnectAsync(tablet);
        // Warm the validator cache for the tablet: revocation must evict it, not wait for the TTL.
        (await tablet.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(phone.Id);

        await (await phone.Client.Delete(ApiRoutes.Sessions.ByIdPath(tablet.SessionId))).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        await (await tablet.Client.Get(ApiRoutes.Users.Me)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        await (await factory.NewClient().Post(ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = tablet.Auth.RefreshToken }))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);
        (await tabletHub.WaitForAsync<SessionRevokedEvent>()).SessionId.ShouldBe(tablet.SessionId);
        await tabletHub.Closed.WaitAsync(EventTimeout, Ct);

        // The revoked device cannot reconnect either.
        await ShouldFailToRunAsync(() => RealtimeProbe.ConnectAsync(tablet));

        await phoneHub.FlushAsync();
        phoneHub.Received<SessionRevokedEvent>().ShouldBeEmpty();
        phoneHub.Hub.State.ShouldBe(HubConnectionState.Connected);
        (await phone.Client.GetAs<IReadOnlyList<SessionDto>>(ApiRoutes.Sessions.Root)).ShouldHaveSingleItem().Id.ShouldBe(phone.SessionId);

        // Idempotent for the owner.
        await (await phone.Client.Delete(ApiRoutes.Sessions.ByIdPath(tablet.SessionId))).ShouldBeStatusAsync(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task All_realtime_connections_of_a_revoked_session_are_closed()
    {
        var user = await factory.NewUserAsync();
        var other = await user.SignInAgainAsync();
        await using var tab1 = await RealtimeProbe.ConnectAsync(other);
        await using var tab2 = await RealtimeProbe.ConnectAsync(other);

        await (await user.Client.Delete(ApiRoutes.Sessions.ByIdPath(other.SessionId))).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        await tab1.WaitForAsync<SessionRevokedEvent>();
        await tab2.WaitForAsync<SessionRevokedEvent>();
        await Task.WhenAll(tab1.Closed, tab2.Closed).WaitAsync(EventTimeout, Ct);
    }

    [Fact]
    public async Task Someone_elses_or_an_unknown_session_is_not_found()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();

        await (await alice.Client.Delete(ApiRoutes.Sessions.ByIdPath(bob.SessionId))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await (await alice.Client.Delete(ApiRoutes.Sessions.ByIdPath(Guid.CreateVersion7()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);

        (await bob.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(bob.Id);
    }

    [Fact]
    public async Task Terminating_the_current_session_signs_it_out()
    {
        var user = await factory.NewUserAsync();

        await (await user.Client.Delete(ApiRoutes.Sessions.ByIdPath(user.SessionId))).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        await (await user.Client.Get(ApiRoutes.Users.Me)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
    }

    [Fact]
    public async Task Terminating_other_sessions_keeps_only_the_current_one()
    {
        var current = await factory.NewUserAsync();
        var others = new[] { await current.SignInAgainAsync("a"), await current.SignInAgainAsync("b") };
        var stranger = await factory.NewUserAsync();
        await using var currentHub = await RealtimeProbe.ConnectAsync(current);
        await using var otherHub = await RealtimeProbe.ConnectAsync(others[0]);

        await (await current.Client.Delete(ApiRoutes.Sessions.Others)).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        foreach (var other in others)
            await (await other.Client.Get(ApiRoutes.Users.Me)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        (await otherHub.WaitForAsync<SessionRevokedEvent>()).SessionId.ShouldBe(others[0].SessionId);
        await otherHub.Closed.WaitAsync(EventTimeout, Ct);
        (await current.Client.GetAs<IReadOnlyList<SessionDto>>(ApiRoutes.Sessions.Root)).ShouldHaveSingleItem().Id.ShouldBe(current.SessionId);
        await currentHub.FlushAsync();
        currentHub.Received<SessionRevokedEvent>().ShouldBeEmpty();
        (await stranger.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(stranger.Id);
    }

    [Fact]
    public async Task A_token_carrying_someone_elses_session_id_is_rejected()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var forged = Issue(alice.Id, alice.Username, bob.SessionId);

        await (await factory.NewClient(accessToken: forged).Get(ApiRoutes.Users.Me))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        await ShouldFailToRunAsync(() => RealtimeProbe.ConnectAsync(factory, forged));

        // Control: the same issuer with the right session works.
        var genuine = Issue(alice.Id, alice.Username, alice.SessionId);
        (await factory.NewClient(accessToken: genuine).GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(alice.Id);
    }

    [Fact]
    public async Task Tokens_with_unknown_sessions_bad_signatures_or_past_expiry_are_rejected()
    {
        var user = await factory.NewUserAsync();

        await (await factory.NewClient(accessToken: Issue(user.Id, user.Username, Guid.CreateVersion7())).Get(ApiRoutes.Users.Me))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);

        var expired = Issue(user.Id, user.Username, user.SessionId, DateTimeOffset.UtcNow.AddHours(-2));
        await (await factory.NewClient(accessToken: expired).Get(ApiRoutes.Users.Me))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);

        var foreignKey = SignWith("another-key-that-is-long-enough-0123456789", user.Id, user.SessionId);
        await (await factory.NewClient(accessToken: foreignKey).Get(ApiRoutes.Users.Me))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);

        await (await factory.NewClient(accessToken: "garbage").Get(ApiRoutes.Users.Me))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        await (await factory.NewClient().Get(ApiRoutes.Users.Me))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Change_password_checks_the_current_one_and_can_terminate_other_sessions()
    {
        var user = await factory.NewUserAsync();
        var other = await user.SignInAgainAsync();
        await using var otherHub = await RealtimeProbe.ConnectAsync(other);

        await (await user.Client.Post(ApiRoutes.Auth.ChangePassword, new ChangePasswordRequest { CurrentPassword = "wrong-password", NewPassword = "new-password-123" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.WrongPassword);
        await (await user.Client.Post(ApiRoutes.Auth.ChangePassword, new ChangePasswordRequest { CurrentPassword = user.Password, NewPassword = "short" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.PasswordTooWeak);

        await (await user.Client.Post(ApiRoutes.Auth.ChangePassword, new ChangePasswordRequest { CurrentPassword = user.Password, NewPassword = "new-password-123" }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);

        (await user.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
        await (await other.Client.Get(ApiRoutes.Users.Me)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        (await otherHub.WaitForAsync<SessionRevokedEvent>()).SessionId.ShouldBe(other.SessionId);
        await (await factory.TryLoginAsync(user.Username, user.Password)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await (await factory.TryLoginAsync(user.Username, "new-password-123")).ShouldBeStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Change_password_can_keep_other_sessions()
    {
        var user = await factory.NewUserAsync();
        var other = await user.SignInAgainAsync();

        await (await user.Client.Post(ApiRoutes.Auth.ChangePassword, new ChangePasswordRequest
            {
                CurrentPassword = user.Password,
                NewPassword = "another-password-1",
                TerminateOtherSessions = false,
            }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);

        (await other.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Repeated_wrong_current_passwords_are_locked_out()
    {
        var user = await factory.NewUserAsync();
        for (var i = 0; i < 10; i++)
            await (await user.Client.Post(ApiRoutes.Auth.ChangePassword, new ChangePasswordRequest { CurrentPassword = "wrong-" + i, NewPassword = "new-password-123" }))
                .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.WrongPassword);

        await (await user.Client.Post(ApiRoutes.Auth.ChangePassword, new ChangePasswordRequest { CurrentPassword = user.Password, NewPassword = "new-password-123" }))
            .ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        await (await user.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = user.Password }))
            .ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        // The account itself is intact.
        await (await factory.TryLoginAsync(user.Username, user.Password)).ShouldBeStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Push_registration_is_stored_moves_between_sessions_and_is_cleared_on_revoke()
    {
        var user = await factory.NewUserAsync();
        var reinstalled = await user.SignInAgainAsync();
        var token = "fcm-" + Guid.NewGuid().ToString("N");
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

        await (await user.Client.Put(ApiRoutes.Sessions.CurrentPush, new RegisterPushRequest { Provider = PushProvider.Fcm, Token = token, EncryptionKey = key }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);
        var stored = await factory.LoadSessionAsync(user.SessionId);
        stored.PushProvider.ShouldBe(PushProvider.Fcm);
        stored.PushToken.ShouldBe(token);
        stored.PushEncryptionKey.ShouldBe(Convert.FromBase64String(key));

        // The same device token registered by a newer session leaves the older one.
        await (await reinstalled.Client.Put(ApiRoutes.Sessions.CurrentPush, new RegisterPushRequest { Provider = PushProvider.Fcm, Token = token }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);
        (await factory.LoadSessionAsync(user.SessionId)).PushToken.ShouldBeNull();
        (await factory.LoadSessionAsync(reinstalled.SessionId)).PushToken.ShouldBe(token);

        await (await reinstalled.Client.Post(ApiRoutes.Auth.Logout)).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        var revoked = await factory.LoadSessionAsync(reinstalled.SessionId);
        revoked.PushProvider.ShouldBe(PushProvider.None);
        revoked.PushToken.ShouldBeNull();
    }

    [Fact]
    public async Task Push_registration_validates_the_request_and_can_be_cleared()
    {
        var user = await factory.NewUserAsync();

        await (await user.Client.Put(ApiRoutes.Sessions.CurrentPush, new RegisterPushRequest { Provider = PushProvider.Fcm, Token = "" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await user.Client.Put(ApiRoutes.Sessions.CurrentPush, new RegisterPushRequest
            {
                Provider = PushProvider.Apns,
                Token = "apns-token",
                EncryptionKey = Convert.ToBase64String(new byte[16]),
            }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        await (await user.Client.Put(ApiRoutes.Sessions.CurrentPush, new RegisterPushRequest { Provider = PushProvider.Apns, Token = "apns-token" }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);
        await (await user.Client.Put(ApiRoutes.Sessions.CurrentPush, new RegisterPushRequest { Provider = PushProvider.None }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);
        var session = await factory.LoadSessionAsync(user.SessionId);
        session.PushProvider.ShouldBe(PushProvider.None);
        session.PushToken.ShouldBeNull();
    }

    [Fact]
    public async Task Access_tokens_carry_user_session_name_and_a_unique_id()
    {
        var user = await factory.NewUserAsync();
        var again = await user.SignInAgainAsync();
        var handler = new JsonWebTokenHandler();

        var jwt = handler.ReadJsonWebToken(user.AccessToken);

        jwt.Alg.ShouldBe(SecurityAlgorithms.HmacSha256);
        jwt.Subject.ShouldBe(user.Id.ToString());
        jwt.GetClaim(SkyClaims.SessionId).Value.ShouldBe(user.SessionId.ToString());
        jwt.GetClaim(SkyClaims.Username).Value.ShouldBe(user.Username);
        jwt.Id.ShouldNotBeNullOrWhiteSpace();
        jwt.Issuer.ShouldBe("skymessage");
        jwt.Audiences.ShouldBe(["skymessage-clients"]);
        (jwt.ValidTo - jwt.IssuedAt).ShouldBe(TimeSpan.FromMinutes(15));
        (user.Auth.AccessTokenExpiresAt.UtcDateTime - jwt.ValidTo).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));
        handler.ReadJsonWebToken(again.AccessToken).Id.ShouldNotBe(jwt.Id);
    }

    [Fact]
    public async Task Sessions_whose_refresh_token_expired_are_neither_listed_nor_usable()
    {
        var user = await factory.NewUserAsync();
        var stale = await user.SignInAgainAsync("stale");
        await factory.WithDbAsync(db => db.Sessions.Where(s => s.Id == stale.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefreshTokenExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));

        (await user.Client.GetAs<IReadOnlyList<SessionDto>>(ApiRoutes.Sessions.Root)).ShouldHaveSingleItem().Id.ShouldBe(user.SessionId);
        await (await stale.Client.Get(ApiRoutes.Users.Me)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        await (await factory.NewClient().Get(ApiRoutes.ServerProofPath(stale.SessionId, "valid-nonce")))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task A_second_logout_with_the_same_token_is_unauthorized()
    {
        var user = await factory.NewUserAsync();

        await (await user.Client.Post(ApiRoutes.Auth.Logout)).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        await (await user.Client.Post(ApiRoutes.Auth.Logout)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
    }

    private string Issue(Guid userId, string username, Guid sessionId, DateTimeOffset? now = null) =>
        factory.Services.GetRequiredService<IAccessTokenIssuer>().Issue(userId, username, sessionId, now ?? DateTimeOffset.UtcNow).Token;

    private static string SignWith(string key, Guid userId, Guid sessionId) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "skymessage",
            Audience = "skymessage-clients",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                [SkyClaims.UserId] = userId.ToString(),
                [SkyClaims.SessionId] = sessionId.ToString(),
                [SkyClaims.Username] = "forger",
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256),
        });
}
