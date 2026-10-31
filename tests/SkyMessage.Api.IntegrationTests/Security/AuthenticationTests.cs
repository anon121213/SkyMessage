using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>Forged, expired, foreign-session and revoked tokens (SPEC §3: the session must belong to <c>sub</c> on every request).</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthenticationTests(ApiFactory factory)
{
    [Fact]
    public async Task Forged_and_malformed_tokens_are_rejected_and_the_genuine_one_works()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var now = DateTime.UtcNow;

        // Control: the token the server issued, and the same claims re-signed with the server key, both work.
        (await alice.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(alice.Id);
        var resigned = TokenKit.Create(factory, TokenKit.Claims(alice.Id, alice.SessionId));
        (await factory.ClientWithToken(resigned).GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(alice.Id);

        var cases = new (string Name, string Token, string Code)[]
        {
            ("garbage", "not-a-jwt", ErrorCodes.Unauthorized),
            ("alg none", TokenKit.Unsigned(factory, alice.Id, alice.SessionId), ErrorCodes.Unauthorized),
            ("wrong key", TokenKit.Create(factory, TokenKit.Claims(alice.Id, alice.SessionId), signingKey: "attacker-key-0123456789abcdef0123456789abcdef"), ErrorCodes.Unauthorized),
            ("tampered payload", Tamper(alice.AccessToken, bob.Id), ErrorCodes.Unauthorized),
            ("expired", TokenKit.Create(factory, TokenKit.Claims(alice.Id, alice.SessionId), notBefore: now.AddMinutes(-30), expires: now.AddMinutes(-5)), ErrorCodes.Unauthorized),
            ("not yet valid", TokenKit.Create(factory, TokenKit.Claims(alice.Id, alice.SessionId), notBefore: now.AddMinutes(5), expires: now.AddMinutes(20)), ErrorCodes.Unauthorized),
            ("wrong issuer", TokenKit.Create(factory, TokenKit.Claims(alice.Id, alice.SessionId), issuer: "someone-else"), ErrorCodes.Unauthorized),
            ("wrong audience", TokenKit.Create(factory, TokenKit.Claims(alice.Id, alice.SessionId), audience: "another-app"), ErrorCodes.Unauthorized),
            // Validly signed, but the session/user binding is wrong: rejected by the per-request session check.
            ("foreign session (sub=alice, sid=bob's)", TokenKit.Create(factory, TokenKit.Claims(alice.Id, bob.SessionId)), ErrorCodes.SessionRevoked),
            ("foreign session (sub=bob, sid=alice's)", TokenKit.Create(factory, TokenKit.Claims(bob.Id, alice.SessionId)), ErrorCodes.SessionRevoked),
            ("unknown session", TokenKit.Create(factory, TokenKit.Claims(alice.Id, Guid.NewGuid())), ErrorCodes.SessionRevoked),
            ("no sid claim", TokenKit.Create(factory, new Dictionary<string, object> { ["sub"] = alice.Id.ToString() }), ErrorCodes.SessionRevoked),
            ("sub not a guid", TokenKit.Create(factory, new Dictionary<string, object> { ["sub"] = "admin", ["sid"] = alice.SessionId.ToString() }), ErrorCodes.SessionRevoked),
        };

        foreach (var (name, token, code) in cases)
        {
            var client = factory.ClientWithToken(token);
            using var response = await client.GetAsync(ApiRoutes.Users.Me, SecurityKit.Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, name);
            await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, code);

            // The same token must not open a realtime connection either.
            await using var hub = factory.CreateHubConnection(token);
            var error = await Should.ThrowAsync<HttpRequestException>(() => hub.StartAsync(SecurityKit.Ct), name);
            error.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, name);
        }

        // Nothing above touched alice's or bob's real sessions.
        (await alice.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(alice.Id);
        (await bob.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(bob.Id);
    }

    [Fact]
    public async Task Logout_kills_the_access_and_refresh_token_of_that_session_only()
    {
        var phone = await factory.RegisterUserAsync("alice");
        var laptop = await factory.SignInAgainAsync(phone, "laptop");

        await phone.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Auth.Logout);

        // No 30-second validator cache window: the revoked session is refused at once.
        using (var me = await phone.Client.GetAsync(ApiRoutes.Users.Me, SecurityKit.Ct))
            await me.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        await factory.NewClient().ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Refresh,
            new RefreshRequest { RefreshToken = phone.Auth.RefreshToken }, HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);

        (await laptop.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(phone.Id);
        var sessions = await laptop.Client.GetJsonAsync<List<SessionDto>>(ApiRoutes.Sessions.Root);
        sessions.Select(s => s.Id).ShouldBe([laptop.SessionId]);
    }

    [Fact]
    public async Task Password_change_with_sign_out_revokes_every_other_session()
    {
        var phone = await factory.RegisterUserAsync("alice");
        var laptop = await factory.SignInAgainAsync(phone, "laptop");
        await using var laptopHub = await HubProbe.ConnectAsync(factory, laptop);

        await phone.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Auth.ChangePassword, new ChangePasswordRequest
        {
            CurrentPassword = SecurityKit.Password,
            NewPassword = "a-brand-new-passphrase",
            TerminateOtherSessions = true,
        });

        (await laptopHub.WaitAsync<SessionRevokedEvent>(nameof(IRealtimeClient.SessionRevoked))).SessionId.ShouldBe(laptop.SessionId);
        await laptopHub.Closed.WaitAsync(SecurityKit.EventTimeout, SecurityKit.Ct);
        using (var me = await laptop.Client.GetAsync(ApiRoutes.Users.Me, SecurityKit.Ct))
            await me.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        (await phone.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(phone.Id);

        var anonymous = factory.NewClient();
        await anonymous.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
            new LoginRequest { Login = phone.Username, Password = SecurityKit.Password, Device = SecurityKit.Device("old password") },
            HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await anonymous.PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Login,
            new LoginRequest { Login = phone.Username, Password = "a-brand-new-passphrase", Device = SecurityKit.Device("new password") });
    }

    /// <summary>SPEC §6.2: "Hub endpoints close on token expiry anyway" — a connection must not outlive its access token.</summary>
    [Fact]
    public async Task Realtime_connection_closes_when_its_access_token_expires()
    {
        var alice = await factory.RegisterUserAsync("alice");
        // A genuine, server-signed token for alice's live session that expires in a few seconds.
        var shortLived = TokenKit.Create(factory, TokenKit.Claims(alice.Id, alice.SessionId), expires: DateTime.UtcNow.AddSeconds(4));
        var probe = await HubProbe.ConnectAsync(factory, alice with { Auth = alice.Auth with { AccessToken = shortLived } }, "short-lived");
        await using (probe)
        {
            var closed = await Task.WhenAny(probe.Closed, Task.Delay(TimeSpan.FromSeconds(25), SecurityKit.Ct));
            closed.ShouldBe(probe.Closed, "the hub connection was still open 20 s after its access token expired");
        }
    }

    [Fact]
    public async Task Access_token_in_the_query_string_is_only_honored_on_the_hub()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var anonymous = factory.NewClient();

        // Tokens in REST URLs would end up in proxy and access logs.
        using var response = await anonymous.GetAsync($"{ApiRoutes.Users.Me}?access_token={Uri.EscapeDataString(alice.AccessToken)}", SecurityKit.Ct);
        await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);

        // The hub accepts it there (browsers cannot set headers on WebSocket/SSE requests).
        await using var hub = await HubProbe.ConnectAsync(factory, alice);
        hub.Connection.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Deleted_account_has_no_working_credentials_left()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var tablet = await factory.SignInAgainAsync(alice, "tablet");
        var bob = await factory.RegisterUserAsync("bob");

        await alice.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = "wrong-password" },
            HttpStatusCode.BadRequest, ErrorCodes.WrongPassword);
        await alice.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = SecurityKit.Password });

        foreach (var session in new[] { alice, tablet })
        {
            using var me = await session.Client.GetAsync(ApiRoutes.Users.Me, SecurityKit.Ct);
            await me.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
            await factory.NewClient().ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Refresh,
                new RefreshRequest { RefreshToken = session.Auth.RefreshToken }, HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);
        }

        await factory.NewClient().ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
            new LoginRequest { Login = alice.Username, Password = SecurityKit.Password, Device = SecurityKit.Device("again") },
            HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await bob.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Users.ByUsernamePath(alice.Username), null,
            HttpStatusCode.NotFound, ErrorCodes.UserNotFound);

        // The tombstone keeps nothing personal.
        var tombstone = await bob.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id));
        tombstone.User.IsDeleted.ShouldBeTrue();
        tombstone.User.Username.ShouldNotBe(alice.Username);
        tombstone.User.AvatarMediaId.ShouldBeNull();
        tombstone.Bio.ShouldBeNull();
        tombstone.PhoneNumber.ShouldBeNull();
        tombstone.CanMessage.ShouldBeFalse();
    }

    [Fact]
    public async Task Refresh_tokens_are_bound_to_their_own_session()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var anonymous = factory.NewClient();

        await anonymous.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = "made-up-refresh-token" },
            HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);
        // An access token is not a refresh token.
        await anonymous.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = alice.AccessToken },
            HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var rotated = await anonymous.PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = alice.Auth.RefreshToken });
        rotated.SessionId.ShouldBe(alice.SessionId);
        rotated.User.Id.ShouldBe(alice.Id);
        rotated.RefreshToken.ShouldNotBe(alice.Auth.RefreshToken);
        rotated.MediaKey.ShouldBe(alice.Auth.MediaKey);
        (await factory.ClientWithToken(rotated.AccessToken).GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(alice.Id);
    }

    /// <summary>Swaps the <c>sub</c> claim in the payload but keeps the original signature.</summary>
    private static string Tamper(string jwt, Guid newSub)
    {
        var parts = jwt.Split('.');
        var payload = TokenKit.Payload(jwt);
        var claims = payload.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        claims["sub"] = newSub.ToString();
        var json = System.Text.Json.JsonSerializer.Serialize(claims);
        parts[1] = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(json));
        return string.Join('.', parts);
    }
}
