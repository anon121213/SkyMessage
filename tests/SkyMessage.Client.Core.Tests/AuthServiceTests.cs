using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class AuthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Instance
    {
        public AuthService Auth { get; }
        public AuthSessionStore Sessions { get; }
        public AccountLifecycle Lifecycle { get; }

        public Instance(InMemorySecureStore secureStore, IAuthChannel channel, IInterProcessLock interProcessLock, TimeProvider time)
        {
            Sessions = new AuthSessionStore(secureStore, Log.For<AuthSessionStore>());
            (_, Lifecycle) = Fakes.Account();
            var platform = Substitute.For<IPlatformInfo>();
            platform.DeviceName.Returns("Test device");
            platform.Platform.Returns(ClientPlatform.Android);
            Auth = new AuthService(Sessions, channel, interProcessLock, platform, Lifecycle, time, Log.For<AuthService>());
        }
    }

    private static StoredSession Session(string accessToken, string refreshToken, TimeSpan validFor, Guid? userId = null, Guid? sessionId = null) => new()
    {
        UserId = userId ?? Guid.CreateVersion7(),
        SessionId = sessionId ?? Guid.CreateVersion7(),
        AccessToken = accessToken,
        AccessTokenExpiresAt = Now + validFor,
        LocalAccessExpiresAt = Now + validFor,
        RefreshToken = refreshToken,
        RefreshTokenExpiresAt = Now.AddDays(30),
        MediaKey = MediaSignature.EncodeKey(MediaSignature.NewKey()),
        Me = new MeDto { DisplayName = "Me" },
    };

    private static AuthResponse Rotated(StoredSession session, string access, string refresh) => new()
    {
        SessionId = session.SessionId,
        AccessToken = access,
        AccessTokenExpiresAt = Now.AddMinutes(15),
        RefreshToken = refresh,
        RefreshTokenExpiresAt = Now.AddDays(30),
        MediaKey = session.MediaKey,
        User = new MeDto { Id = session.UserId, DisplayName = "Me" },
    };

    [Fact]
    public async Task Concurrent_callers_share_one_refresh()
    {
        var store = new InMemorySecureStore();
        var channel = Substitute.For<IAuthChannel>();
        var clock = new ManualClock(Now);
        var stale = Session("access-1", "refresh-1", TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource<AuthResponse>();
        channel.RefreshAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(gate.Task);
        var instance = new Instance(store, channel, new SharedLock(), clock);
        await instance.Sessions.SaveAsync(stale);
        await instance.Auth.InitializeAsync();

        var calls = Enumerable.Range(0, 5).Select(_ => instance.Auth.GetAccessTokenAsync()).ToList();
        await Task.Delay(50);
        gate.SetResult(Rotated(stale, "access-2", "refresh-2"));
        var tokens = await Task.WhenAll(calls);

        tokens.ShouldAllBe(t => t == "access-2");
        await channel.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        store.Values[AuthSessionStore.Key].ShouldContain("refresh-2");
    }

    [Fact]
    public async Task A_second_instance_adopts_tokens_rotated_by_the_first_inside_the_shared_lock()
    {
        var store = new InMemorySecureStore();
        var sharedLock = new SharedLock();
        var clock = new ManualClock(Now);
        var stale = Session("access-1", "refresh-1", TimeSpan.FromSeconds(20));
        var channelA = Substitute.For<IAuthChannel>();
        var channelB = Substitute.For<IAuthChannel>();
        var gate = new TaskCompletionSource<AuthResponse>();
        channelA.RefreshAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(gate.Task);
        var a = new Instance(store, channelA, sharedLock, clock);
        var b = new Instance(store, channelB, sharedLock, clock);
        await a.Sessions.SaveAsync(stale);
        await a.Auth.InitializeAsync();
        await b.Auth.InitializeAsync();

        var fromA = a.Auth.GetAccessTokenAsync();
        await Task.Delay(50);
        var fromB = b.Auth.GetAccessTokenAsync();
        await Task.Delay(50);
        gate.SetResult(Rotated(stale, "access-2", "refresh-2"));

        (await fromA).ShouldBe("access-2");
        (await fromB).ShouldBe("access-2");
        await channelB.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        sharedLock.Acquisitions.ShouldBe(2);
        b.Sessions.Current!.RefreshToken.ShouldBe("refresh-2", "the rotated refresh token is never lost");
    }

    [Fact]
    public async Task A_profile_update_in_one_tab_never_writes_back_a_refresh_token_another_tab_rotated()
    {
        var store = new InMemorySecureStore();
        var sharedLock = new SharedLock();
        var clock = new ManualClock(Now);
        var session = Session("access-1", "refresh-1", TimeSpan.FromMinutes(10));
        var channelA = Substitute.For<IAuthChannel>();
        channelA.RefreshAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(Rotated(session, "access-2", "refresh-2"));
        var a = new Instance(store, channelA, sharedLock, clock);
        var b = new Instance(store, Substitute.For<IAuthChannel>(), sharedLock, clock);
        await a.Sessions.SaveAsync(session);
        await a.Auth.InitializeAsync();
        await b.Auth.InitializeAsync();

        // Tab A refreshes early (after a 401); tab B's access token is far from expiry, so B still holds refresh-1.
        (await a.Auth.HandleUnauthorizedAsync("access-1", ErrorCodes.Unauthorized, CancellationToken.None)).ShouldBe("access-2");
        // The profile changed on another device: tab B stores the new one.
        b.Auth.UpdateMe(new MeDto { Id = session.UserId, DisplayName = "New name" });

        await Wait.UntilAsync(() => store.Values[AuthSessionStore.Key].Contains("New name"), "the profile is stored");
        store.Values[AuthSessionStore.Key].ShouldContain("refresh-2", Case.Sensitive,
            "a rotated-out refresh token in the store would be presented later, and the server treats that as theft");
        b.Sessions.Current!.RefreshToken.ShouldBe("refresh-2", "tab B adopts the newer tokens");
    }

    [Fact]
    public async Task A_rejected_refresh_signs_out_locally()
    {
        var store = new InMemorySecureStore();
        var channel = Substitute.For<IAuthChannel>();
        var clock = new ManualClock(Now);
        var stale = Session("access-1", "refresh-1", TimeSpan.FromSeconds(10));
        channel.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ApiException(401, ErrorCodes.RefreshTokenInvalid));
        var instance = new Instance(store, channel, new SharedLock(), clock);
        await instance.Sessions.SaveAsync(stale);
        await instance.Auth.InitializeAsync();
        await instance.Lifecycle.ActivateAsync(stale.UserId);

        var token = await instance.Auth.GetAccessTokenAsync();

        token.ShouldBeNull();
        await Wait.UntilAsync(() => instance.Auth.State == AuthState.SignedOut);
        store.Values.ContainsKey(AuthSessionStore.Key).ShouldBeFalse();
        await channel.DidNotReceive().LogoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        instance.Lifecycle.ActiveUserId.ShouldBeNull();
    }

    [Fact]
    public async Task Network_errors_keep_the_session_while_the_token_is_still_valid()
    {
        var store = new InMemorySecureStore();
        var channel = Substitute.For<IAuthChannel>();
        var clock = new ManualClock(Now);
        var stale = Session("access-1", "refresh-1", TimeSpan.FromSeconds(40));
        channel.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ApiException(0, ClientErrorCodes.Network));
        var instance = new Instance(store, channel, new SharedLock(), clock);
        await instance.Sessions.SaveAsync(stale);
        await instance.Auth.InitializeAsync();

        (await instance.Auth.GetAccessTokenAsync()).ShouldBe("access-1");
        instance.Auth.State.ShouldBe(AuthState.SignedIn);

        clock.Advance(TimeSpan.FromMinutes(1));
        var expired = await Should.ThrowAsync<ApiException>(() => instance.Auth.GetAccessTokenAsync());
        expired.IsNetworkError.ShouldBeTrue("an expired token offline surfaces as a transient network error");
        instance.Auth.State.ShouldBe(AuthState.SignedIn);
    }

    [Fact]
    public async Task Unauthorized_with_a_stale_token_returns_the_already_refreshed_one()
    {
        var store = new InMemorySecureStore();
        var channel = Substitute.For<IAuthChannel>();
        var instance = new Instance(store, channel, new SharedLock(), new ManualClock(Now));
        await instance.Sessions.SaveAsync(Session("access-new", "refresh-1", TimeSpan.FromMinutes(10)));
        await instance.Auth.InitializeAsync();

        var token = await instance.Auth.HandleUnauthorizedAsync("access-old", ErrorCodes.Unauthorized, CancellationToken.None);

        token.ShouldBe("access-new");
        await channel.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Token_lifetime_comes_from_the_jwt_so_a_wrong_device_clock_does_not_matter()
    {
        static string Segment(string json) => System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(json));
        var jwt = $"{Segment("{\"alg\":\"HS256\"}")}.{Segment("{\"iat\":1000,\"nbf\":1000,\"exp\":1900}")}.sig";
        var deviceNow = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var lifetime = StoredSession.AccessTokenLifetime(jwt, DateTimeOffset.UnixEpoch.AddSeconds(1900), deviceNow);

        lifetime.ShouldBe(TimeSpan.FromSeconds(900));
    }
}
