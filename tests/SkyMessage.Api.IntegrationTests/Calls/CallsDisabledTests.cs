using System.Net;
using System.Net.Http.Json;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>Servers without (usable) LiveKit configuration: calls answer 503 calls_disabled, everything else keeps working.</summary>
[Collection(ApiCollection.Name)]
public sealed class CallsDisabledTests(ApiFactory api)
{
    [Fact]
    public async Task Disabled_calls_refuse_to_ring_but_running_calls_can_still_be_ended()
    {
        await using var disabled = api.WithSettings(new Dictionary<string, string?> { ["LiveKit:Enabled"] = "false" });
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        // A call that was started before the switch (or on another instance).
        var running = (await alice.StartCallAsync(chatId)).Call;
        var offAlice = alice.On(disabled);
        var offBob = bob.On(disabled);
        var stranger = (await api.NewUserAsync("Mallory")).On(disabled);

        await (await offAlice.TryStartCallAsync(chatId)).ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, ErrorCodes.CallsDisabled);
        await (await offBob.TryAcceptAsync(running.Id)).ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, ErrorCodes.CallsDisabled);
        await (await offBob.TryJoinAsync(running.Id)).ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, ErrorCodes.CallsDisabled);
        // Existence is not leaked to non-members even when disabled.
        await (await stranger.TryStartCallAsync(chatId)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);

        (await offBob.GetCallAsync(running.Id)).Status.ShouldBe(CallStatus.Ringing);
        (await offBob.GetActiveCallAsync(chatId))!.Id.ShouldBe(running.Id);
        await offBob.DeclineAsync(running.Id);
        (await offAlice.GetCallAsync(running.Id)).Status.ShouldBe(CallStatus.Declined);
        (await offAlice.GetHistoryAsync()).Single().Call.Id.ShouldBe(running.Id);

        var info = await (await disabled.NewClient().GetAsync(ApiRoutes.ServerInfo, CallKit.Ct)).ReadAsync<ServerInfoDto>();
        info.CallsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Disabled_calls_answer_the_livekit_path_with_503()
    {
        await using var disabled = api.WithSettings(new Dictionary<string, string?> { ["LiveKit:Enabled"] = "false" });
        var client = disabled.NewClient();

        foreach (var path in new[] { "/livekit", "/livekit/rtc", "/livekit/rtc/v1?access_token=x", "/livekit/rtc/v1/validate", "/livekit/twirp/livekit.RoomService/ListRooms" })
            await (await client.GetAsync(path, CallKit.Ct)).ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, ErrorCodes.CallsDisabled);
    }

    [Fact]
    public async Task A_secret_too_short_to_sign_tokens_disables_calls()
    {
        await using var weak = api.WithSettings(new Dictionary<string, string?> { ["LiveKit:ApiSecret"] = "short-secret" });
        var alice = (await api.NewUserAsync("Alice")).On(weak);
        var bob = (await api.NewUserAsync("Bob")).On(weak);
        var chatId = await alice.PrivateChatWithAsync(bob);

        await (await alice.TryStartCallAsync(chatId)).ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, ErrorCodes.CallsDisabled);
        await (await weak.NewClient(alice.AccessToken).PostJsonAsync(ApiRoutes.Calls.Root, new StartCallRequest { ChatId = chatId, IsVideo = true }))
            .ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, ErrorCodes.CallsDisabled);
    }

    [Fact]
    public async Task Server_info_reports_calls_disabled_when_the_secret_is_unusable()
    {
        await using var weak = api.WithSettings(new Dictionary<string, string?> { ["LiveKit:ApiSecret"] = "short-secret" });

        // Clients decide whether to show call buttons from server info: it must agree with POST api/calls (503).
        var info = await weak.NewClient().GetFromJsonAsync<ServerInfoDto>(ApiRoutes.ServerInfo, SkyJson.Options, CallKit.Ct);
        info!.CallsEnabled.ShouldBeFalse();
    }
}
