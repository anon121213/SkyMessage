using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class TokenRefreshTests(ApiFactory factory)
{
    [Fact]
    public async Task Refresh_rotates_the_token_and_keeps_the_session_and_media_key()
    {
        var user = await factory.NewUserAsync();
        var before = await factory.LoadSessionAsync(user.SessionId);

        var refreshed = await Refresh(user.Auth.RefreshToken, user.Ip);

        refreshed.SessionId.ShouldBe(user.SessionId);
        refreshed.RefreshToken.ShouldNotBe(user.Auth.RefreshToken);
        refreshed.AccessToken.ShouldNotBe(user.AccessToken);
        refreshed.MediaKey.ShouldBe(user.Auth.MediaKey);
        refreshed.User.Id.ShouldBe(user.Id);
        refreshed.User.SavedMessagesChatId.ShouldBe(user.Auth.User.SavedMessagesChatId);
        refreshed.RefreshTokenExpiresAt.ShouldBeGreaterThanOrEqualTo(user.Auth.RefreshTokenExpiresAt);

        var after = await factory.LoadSessionAsync(user.SessionId);
        after.RefreshTokenHash.ShouldBe(RefreshTokens.Hash(refreshed.RefreshToken));
        after.PreviousRefreshTokenHash.ShouldBe(before.RefreshTokenHash);
        after.RefreshTokenRotatedAt.ShouldNotBeNull();
        after.RevokedAt.ShouldBeNull();

        (await factory.NewClient(accessToken: refreshed.AccessToken).GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
        var next = await Refresh(refreshed.RefreshToken, user.Ip);
        next.SessionId.ShouldBe(user.SessionId);
    }

    [Fact]
    public async Task Refresh_from_another_address_records_it_on_the_session()
    {
        var user = await factory.NewUserAsync();
        var newIp = NewIp();

        await Refresh(user.Auth.RefreshToken, newIp);

        (await factory.LoadSessionAsync(user.SessionId)).IpAddress.ShouldBe(newIp);
    }

    [Fact]
    public async Task Replaying_the_previous_token_within_the_grace_window_is_a_lost_response_retry()
    {
        var user = await factory.NewUserAsync();
        var first = await Refresh(user.Auth.RefreshToken, user.Ip);
        // 90 s after the rotation the client retries with the token it still has.
        await SetRotatedAgoAsync(user.SessionId, TimeSpan.FromSeconds(90));

        var retry = await Refresh(user.Auth.RefreshToken, user.Ip);

        retry.SessionId.ShouldBe(user.SessionId);
        retry.RefreshToken.ShouldBe(first.RefreshToken); // idempotent: the successor the lost response carried
        retry.AccessToken.ShouldNotBe(first.AccessToken);
        retry.MediaKey.ShouldBe(user.Auth.MediaKey);
        (await factory.LoadSessionAsync(user.SessionId)).RevokedAt.ShouldBeNull();
        (await factory.NewClient(accessToken: retry.AccessToken).GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
        (await Refresh(retry.RefreshToken, user.Ip)).SessionId.ShouldBe(user.SessionId);
    }

    [Fact]
    public async Task A_lost_refresh_response_can_be_retried_any_number_of_times_however_late()
    {
        var user = await factory.NewUserAsync();
        // Every response is lost: the client never uses what came back and keeps presenting its old token.
        var first = await Refresh(user.Auth.RefreshToken, user.Ip);
        var second = await Refresh(user.Auth.RefreshToken, user.Ip);
        await SetRotatedAgoAsync(user.SessionId, TimeSpan.FromHours(20)); // the phone was offline overnight

        var late = await Refresh(user.Auth.RefreshToken, user.Ip);

        second.RefreshToken.ShouldBe(first.RefreshToken);
        late.RefreshToken.ShouldBe(first.RefreshToken);
        var session = await factory.LoadSessionAsync(user.SessionId);
        session.RevokedAt.ShouldBeNull();
        session.RefreshTokenHash.ShouldBe(RefreshTokens.Hash(first.RefreshToken));
        session.PreviousRefreshTokenHash.ShouldBe(RefreshTokens.Hash(user.Auth.RefreshToken));
        (await factory.NewClient(accessToken: late.AccessToken).GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
        (await Refresh(late.RefreshToken, user.Ip)).SessionId.ShouldBe(user.SessionId);
    }

    [Fact]
    public async Task Using_the_new_access_token_confirms_the_rotation()
    {
        var user = await factory.NewUserAsync();
        var refreshed = await Refresh(user.Auth.RefreshToken, user.Ip);
        (await factory.LoadSessionAsync(user.SessionId)).RefreshTokenConfirmedAt.ShouldBeNull();

        // An access token from before the rotation says nothing about the new refresh token.
        (await user.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
        (await factory.LoadSessionAsync(user.SessionId)).RefreshTokenConfirmedAt.ShouldBeNull();

        (await factory.NewClient(accessToken: refreshed.AccessToken).GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
        (await factory.LoadSessionAsync(user.SessionId)).RefreshTokenConfirmedAt.ShouldNotBeNull();

        await Refresh(refreshed.RefreshToken, user.Ip);
        (await factory.LoadSessionAsync(user.SessionId)).RefreshTokenConfirmedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Replaying_the_previous_token_after_a_confirmed_rotation_and_the_grace_window_revokes_the_session()
    {
        var user = await factory.NewUserAsync();
        await using var hub = await RealtimeProbe.ConnectAsync(user);
        var current = await Refresh(user.Auth.RefreshToken, user.Ip);
        // The client got the response: it uses the new access token.
        (await factory.NewClient(accessToken: current.AccessToken).GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
        await SetRotatedAgoAsync(user.SessionId, TimeSpan.FromMinutes(3));

        var replay = await TryRefresh(user.Auth.RefreshToken, user.Ip);
        await replay.ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);

        (await factory.LoadSessionAsync(user.SessionId)).RevokedAt.ShouldNotBeNull();
        await (await TryRefresh(current.RefreshToken, user.Ip)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);
        await (await factory.NewClient(accessToken: current.AccessToken).Get(ApiRoutes.Users.Me))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        (await hub.WaitForAsync<SessionRevokedEvent>()).SessionId.ShouldBe(user.SessionId);
        await hub.Closed.WaitAsync(EventTimeout, Ct);
    }

    [Fact]
    public async Task Concurrent_refreshes_with_one_token_both_succeed()
    {
        var user = await factory.NewUserAsync();

        var results = await Task.WhenAll(TryRefresh(user.Auth.RefreshToken, user.Ip), TryRefresh(user.Auth.RefreshToken, user.Ip));

        var responses = new List<AuthResponse>();
        foreach (var result in results)
            responses.Add(await result.ReadAsync<AuthResponse>());
        // Both get the same successor, so whichever response the client keeps stays valid.
        responses.Select(r => r.RefreshToken).Distinct().Count().ShouldBe(1);
        (await Refresh(responses[1].RefreshToken, user.Ip)).SessionId.ShouldBe(user.SessionId);
        (await factory.LoadSessionAsync(user.SessionId)).RevokedAt.ShouldBeNull();
        foreach (var response in responses)
            (await factory.NewClient(accessToken: response.AccessToken).GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Tokens_older_than_the_previous_one_are_rejected_without_revoking()
    {
        var user = await factory.NewUserAsync();
        var first = await Refresh(user.Auth.RefreshToken, user.Ip);
        var second = await Refresh(first.RefreshToken, user.Ip);

        await (await TryRefresh(user.Auth.RefreshToken, user.Ip)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);

        (await Refresh(second.RefreshToken, user.Ip)).SessionId.ShouldBe(user.SessionId);
    }

    [Theory]
    [InlineData("not-a-real-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Unknown_tokens_are_invalid(string token)
    {
        await (await TryRefresh(token, NewIp())).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);
    }

    [Fact]
    public async Task Empty_or_oversized_tokens_are_validation_errors()
    {
        await (await TryRefresh("", NewIp())).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await TryRefresh(new string('a', RefreshTokens.MaxLength + 1), NewIp())).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Expired_refresh_tokens_are_invalid()
    {
        var user = await factory.NewUserAsync();
        await factory.WithDbAsync(db => db.Sessions.Where(s => s.Id == user.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefreshTokenExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));

        await (await TryRefresh(user.Auth.RefreshToken, user.Ip)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);
    }

    [Fact]
    public async Task Logout_revokes_the_current_session_only()
    {
        var phone = await factory.NewUserAsync();
        var laptop = await phone.SignInAgainAsync("laptop");
        await using var phoneHub = await RealtimeProbe.ConnectAsync(phone);
        await using var laptopHub = await RealtimeProbe.ConnectAsync(laptop);

        await (await phone.Client.Post(ApiRoutes.Auth.Logout)).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        await (await phone.Client.Get(ApiRoutes.Users.Me)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        await (await TryRefresh(phone.Auth.RefreshToken, phone.Ip)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);
        (await phoneHub.WaitForAsync<SessionRevokedEvent>()).SessionId.ShouldBe(phone.SessionId);
        await phoneHub.Closed.WaitAsync(EventTimeout, Ct);

        (await laptop.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(phone.Id);
        await laptopHub.FlushAsync();
        laptopHub.Received<SessionRevokedEvent>().ShouldBeEmpty();
        laptopHub.Hub.State.ShouldBe(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected);
    }

    private Task<HttpResponseMessage> TryRefresh(string token, string ip) =>
        factory.NewClient(ip).Post(ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = token });

    private async Task<AuthResponse> Refresh(string token, string ip) => await (await TryRefresh(token, ip)).ReadAsync<AuthResponse>();

    private Task SetRotatedAgoAsync(Guid sessionId, TimeSpan ago) =>
        factory.WithDbAsync(db => db.Sessions.Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefreshTokenRotatedAt, DateTimeOffset.UtcNow - ago), Ct));
}
