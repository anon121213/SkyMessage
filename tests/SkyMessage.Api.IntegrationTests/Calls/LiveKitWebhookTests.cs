using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using SkyMessage.Application.Calls;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>
/// <c>POST api/livekit/webhook</c> on the default test host, where LiveKit itself is unreachable (room state
/// unknown): signature checks and the call transitions the events drive.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LiveKitWebhookTests(ApiFactory api)
{
    private async Task<(CallUser Alice, CallUser Bob, Guid ChatId, CallDto Call, CallHub AliceHub)> ActiveCallAsync()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        var hub = await CallHub.ConnectAsync(alice);
        var call = await alice.ActivePrivateCallAsync(bob, chatId);
        return (alice, bob, chatId, call, hub);
    }

    private static string Room(CallDto call) => LiveKitNames.RoomName(call.Id);

    [Fact]
    public async Task Requests_without_a_valid_livekit_signature_are_rejected()
    {
        var (alice, _, _, call, hub) = await ActiveCallAsync();
        await using var _ = hub;
        var body = LiveKitWebhook.RoomFinished(Room(call));
        var other = LiveKitWebhook.RoomFinished("call-" + Guid.NewGuid().ToString("N"));

        var refused = new Dictionary<string, string?>
        {
            ["no header"] = null,
            ["garbage"] = "not-a-jwt",
            ["wrong secret"] = LiveKitWebhook.Sign(body, apiSecret: "another-secret-another-secret-another-secret"),
            ["wrong api key"] = LiveKitWebhook.Sign(body, apiKey: "otherkey"),
            ["expired"] = LiveKitWebhook.Sign(body, notBefore: DateTime.UtcNow.AddMinutes(-20), lifetime: TimeSpan.FromMinutes(5)),
            ["not yet valid"] = LiveKitWebhook.Sign(body, notBefore: DateTime.UtcNow.AddMinutes(10)),
            ["no body hash"] = LiveKitWebhook.Sign(body, includeHash: false),
            ["signed for another body"] = LiveKitWebhook.Sign(other),
            ["our own access token"] = "Bearer " + alice.AccessToken,
        };
        foreach (var (name, authorization) in refused)
        {
            var response = await LiveKitWebhook.PostAsync(api, body, authorization);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, name);
            await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        }

        (await alice.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Active);
        await hub.FlushAsync();
        hub.CallEvents(nameof(IRealtimeClient.CallUpdated), call.Id).ShouldNotContain(c => c.Status == CallStatus.Ended);

        // The same event, properly signed (also with a "Bearer " prefix), is applied.
        (await LiveKitWebhook.PostAsync(api, body, "Bearer " + LiveKitWebhook.Sign(body))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await hub.CallStatusAsync(call.Id, CallStatus.Ended);
    }

    [Fact]
    public async Task Signed_garbage_is_a_bad_request_and_unknown_events_are_acknowledged()
    {
        var garbage = Encoding.UTF8.GetBytes("this is not json");
        await (await LiveKitWebhook.PostAsync(api, garbage, LiveKitWebhook.Sign(garbage)))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        foreach (var body in new[]
                 {
                     LiveKitWebhook.Event("room_started", "call-" + Guid.NewGuid().ToString("N"), null),
                     LiveKitWebhook.Event("track_published", "call-" + Guid.NewGuid().ToString("N"), null),
                     LiveKitWebhook.Participant("participant_joined", "not-our-room", "someone"),
                     LiveKitWebhook.Participant("participant_left", "call-" + Guid.NewGuid().ToString("N"), $"{Guid.NewGuid():N}.{Guid.NewGuid():N}"),
                     LiveKitWebhook.RoomFinished("call-" + Guid.NewGuid().ToString("N")),
                     Encoding.UTF8.GetBytes("{}"),
                 })
        {
            await LiveKitWebhook.SendAsync(api, body);
        }
    }

    [Fact]
    public async Task Oversized_bodies_are_refused_before_anything_else()
    {
        var body = new byte[300 * 1024];
        Array.Fill(body, (byte)' ');

        var response = await LiveKitWebhook.PostAsync(api, body, LiveKitWebhook.Sign(body));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Participant_left_marks_the_device_left_and_participant_joined_brings_it_back()
    {
        var (alice, bob, _, call, hub) = await ActiveCallAsync();
        await using var _ = hub;

        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_left", Room(call), alice.Identity));

        // Only marked: the call itself survives until the grace check (LiveKit could not be asked who is still there).
        var left = await hub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Left);
        left.Status.ShouldBe(CallStatus.Active);
        left.StatusOf(bob).ShouldBe(CallParticipantStatus.Joined);
        left.Participant(alice).LeftAt.ShouldNotBeNull();
        var mark = hub.Checkpoint();

        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_joined", Room(call), alice.Identity));

        var back = await hub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Joined, after: mark);
        back.Status.ShouldBe(CallStatus.Active);
        back.Participant(alice).LeftAt.ShouldBeNull();

        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Reconnecting_with_the_same_identity_is_not_leaving()
    {
        var (alice, _, _, call, hub) = await ActiveCallAsync();
        await using var _ = hub;

        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_left", Room(call), alice.Identity, disconnectReason: "DUPLICATE_IDENTITY"));
        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_left", Room(call), alice.Identity, disconnectReason: JsonValue.Create(2)));

        await hub.FlushAsync();
        hub.CallEvents(nameof(IRealtimeClient.CallUpdated), call.Id).ShouldNotContain(c => c.StatusOf(alice) == CallParticipantStatus.Left);
        (await alice.GetCallAsync(call.Id)).StatusOf(alice).ShouldBe(CallParticipantStatus.Joined);
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Redelivered_events_are_applied_once()
    {
        var (alice, _, _, call, hub) = await ActiveCallAsync();
        await using var _ = hub;
        var left = LiveKitWebhook.Participant("participant_left", Room(call), alice.Identity, eventId: "EV_left_" + Guid.NewGuid().ToString("N"));

        await LiveKitWebhook.SendAsync(api, left);
        await hub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Left);
        var mark = hub.Checkpoint();
        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_joined", Room(call), alice.Identity));
        await hub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Joined, after: mark);
        var updates = hub.CallEvents(nameof(IRealtimeClient.CallUpdated), call.Id).Count;

        // LiveKit retries a delivery it thinks failed: same event id, must not undo the newer state.
        await LiveKitWebhook.SendAsync(api, left);

        (await alice.GetCallAsync(call.Id)).StatusOf(alice).ShouldBe(CallParticipantStatus.Joined);
        await hub.FlushAsync();
        hub.CallEvents(nameof(IRealtimeClient.CallUpdated), call.Id).Count.ShouldBe(updates);
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Room_finished_ends_an_answered_call_with_a_call_log()
    {
        var (alice, _, chatId, call, hub) = await ActiveCallAsync();
        await using var _ = hub;

        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.RoomFinished(Room(call)));

        var ended = await hub.CallStatusAsync(call.Id, CallStatus.Ended);
        ended.Participants.ShouldAllBe(p => p.Status == CallParticipantStatus.Left);
        (await hub.CallLogAsync(chatId, call.Id)).Call!.Status.ShouldBe(CallStatus.Ended);

        // Finishing a finished call again changes nothing.
        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.RoomFinished(Room(call)));
        (await alice.CallLogsAsync(chatId, call.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Room_finished_while_ringing_is_a_missed_call()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        await using var bobHub = await CallHub.ConnectAsync(bob);
        var call = (await alice.StartCallAsync(chatId)).Call;

        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.RoomFinished(Room(call)));

        var missed = await bobHub.CallStatusAsync(call.Id, CallStatus.Missed);
        missed.StatusOf(bob).ShouldBe(CallParticipantStatus.Missed);
        (await bobHub.CallLogAsync(chatId, call.Id)).Call!.Status.ShouldBe(CallStatus.Missed);
    }

    [Fact]
    public async Task Room_finished_ends_a_group_call()
    {
        var owner = await api.NewUserAsync("Olga");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await owner.CreateGroupAsync("Team", bob);
        await using var bobHub = await CallHub.ConnectAsync(bob);
        var call = (await owner.StartCallAsync(chatId)).Call;
        await bob.JoinAsync(call.Id);
        var mark = bobHub.Checkpoint();

        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.RoomFinished(Room(call)));

        await bobHub.CallStatusAsync(call.Id, CallStatus.Ended);
        await bobHub.ChatUpdatedAsync(chatId, c => c.ActiveCallId == null, after: mark);
        await bobHub.SystemEventAsync(chatId, SystemEventType.GroupCallEnded, call.Id);
    }

    [Fact]
    public async Task Devices_that_may_not_be_in_the_call_do_not_change_it()
    {
        var (alice, bob, _, call, hub) = await ActiveCallAsync();
        await using var _ = hub;
        var mallory = await api.NewUserAsync("Mallory");
        var aliceLaptop = await alice.SignInAgainAsync();
        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_left", Room(call), alice.Identity));
        await hub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Left);
        await (await alice.Http.DeleteAsync(ApiRoutes.Sessions.ByIdPath(aliceLaptop.SessionId), CallKit.Ct)).ShouldBeAsync(HttpStatusCode.NoContent);

        // A stranger (not a member) and a signed-out device entering the room with leftover tokens.
        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_joined", Room(call), mallory.Identity));
        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_joined", Room(call), aliceLaptop.Identity));

        await hub.FlushAsync();
        var now = await bob.GetCallAsync(call.Id);
        now.ShouldHaveParticipants((alice, CallParticipantStatus.Left), (bob, CallParticipantStatus.Joined));
        hub.CallEvents(nameof(IRealtimeClient.CallUpdated), call.Id).Last().StatusOf(alice).ShouldBe(CallParticipantStatus.Left);

        // The member's live device is welcome back.
        var mark = hub.Checkpoint();
        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_joined", Room(call), alice.Identity));
        await hub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Joined, after: mark);
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task A_member_entering_a_group_call_room_is_added_as_joined()
    {
        var owner = await api.NewUserAsync("Olga");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await owner.CreateGroupAsync("Team", bob);
        await using var ownerHub = await CallHub.ConnectAsync(owner);
        var call = (await owner.StartCallAsync(chatId)).Call;
        var dave = await api.NewUserAsync("Dave");
        var added = await owner.Http.PostJsonAsync(ApiRoutes.Chats.MembersPath(chatId), new AddMembersRequest { UserIds = [dave.Id] });
        (await added.ReadAsync<AddMembersResultDto>()).Added.ShouldBe([dave.Id]);

        await LiveKitWebhook.SendAsync(api, LiveKitWebhook.Participant("participant_joined", Room(call), dave.Identity));

        var updated = await ownerHub.CallUpdatedAsync(call.Id, c => c.Participants.Any(p => p.UserId == dave.Id));
        updated.StatusOf(dave).ShouldBe(CallParticipantStatus.Joined);
        await owner.HangupAsync(call.Id);
        await dave.HangupAsync(call.Id);
    }
}
