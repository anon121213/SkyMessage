using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>
/// The <c>/livekit</c> reverse proxy: only the signaling surface livekit-client uses (<c>/livekit</c> probe and
/// <c>/livekit/rtc/**</c>) is forwarded, without the prefix; LiveKit's own API stays private.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LiveKitProxyTests(ApiFactory api)
{
    [Theory]
    [InlineData("HEAD", "/livekit")]
    [InlineData("GET", "/livekit")]
    [InlineData("GET", "/livekit/rtc")]
    [InlineData("GET", "/livekit/rtc/v1")]
    [InlineData("GET", "/livekit/rtc/validate?access_token=abc")]
    [InlineData("GET", "/livekit/rtc/v1/validate?access_token=abc")]
    public async Task Signaling_paths_are_forwarded_anonymously_even_while_livekit_is_down(string method, string path)
    {
        var response = await api.NewClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path), CallKit.Ct);

        // LiveKit is unreachable in this host: the proxy itself answers 502 (not 401, not our 404).
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Theory]
    [InlineData("POST", "/livekit/twirp/livekit.RoomService/ListRooms")]
    [InlineData("POST", "/livekit/twirp/livekit.RoomService/DeleteRoom")]
    [InlineData("GET", "/livekit/twirp")]
    [InlineData("GET", "/livekit/settings")]
    [InlineData("GET", "/livekit/rtcx")]
    [InlineData("GET", "/livekit/metrics")]
    public async Task Everything_else_under_livekit_is_not_found(string method, string path)
    {
        var response = await api.NewClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path), CallKit.Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Requests_reach_livekit_without_the_prefix_and_with_its_own_cors_headers()
    {
        await using var liveKit = await FakeLiveKit.StartAsync();
        await using var host = api.WithFakeLiveKit(liveKit);
        var client = host.NewClient();

        var validate = new HttpRequestMessage(HttpMethod.Get, "/livekit/rtc/v1/validate?access_token=tok%2Ben&auto_subscribe=1&sdk=js");
        validate.Headers.Add("Origin", "https://0.0.0.1");
        var validated = await client.SendAsync(validate, CallKit.Ct);
        var probe = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/livekit"), CallKit.Ct);
        var twirp = await client.PostAsync("/livekit/twirp/livekit.RoomService/ListRooms", new StringContent("{}"), CallKit.Ct);

        validated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await validated.Content.ReadAsStringAsync(CallKit.Ct)).ShouldBe("fake-livekit");
        // Exactly LiveKit's own CORS answer: ours on top would duplicate the header and browsers would reject it.
        validated.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["*"]);
        validated.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
        probe.StatusCode.ShouldBe(HttpStatusCode.OK);
        await twirp.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var forwarded = liveKit.Proxied;
        forwarded.Select(r => (r.Method, r.Path)).ShouldBe([("GET", "/rtc/v1/validate"), ("HEAD", "/")]);
        forwarded[0].Query.ShouldBe("?access_token=tok%2Ben&auto_subscribe=1&sdk=js");
        liveKit.TwirpCalls().ShouldNotContain(c => c.Method == "ListRooms" && c.Body.GetRawText() == "{}" && !c.Authorized);

        // The API's own CORS still applies to its own endpoints (so the check above means something).
        var info = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.ServerInfo);
        info.Headers.Add("Origin", "https://0.0.0.1");
        (await client.SendAsync(info, CallKit.Ct)).Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["https://0.0.0.1"]);
    }

    [Fact]
    public async Task A_livekit_token_in_the_authorization_header_is_passed_through_not_judged_by_the_api()
    {
        await using var liveKit = await FakeLiveKit.StartAsync();
        await using var host = api.WithFakeLiveKit(liveKit);
        var alice = await host.NewUserAsync("Alice");
        var bob = await host.NewUserAsync("Bob");
        var join = await alice.StartCallAsync(await alice.PrivateChatWithAsync(bob));

        var request = new HttpRequestMessage(HttpMethod.Get, "/livekit/rtc/v1/validate");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + join.LiveKitToken);
        var response = await host.NewClient().SendAsync(request, CallKit.Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        liveKit.Proxied.Single(r => r.Path == "/rtc/v1/validate").Headers["Authorization"].ShouldBe("Bearer " + join.LiveKitToken);
        await alice.HangupAsync(join.Call.Id);
    }
}
