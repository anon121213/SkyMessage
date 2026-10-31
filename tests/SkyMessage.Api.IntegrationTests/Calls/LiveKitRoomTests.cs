using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Application.Calls;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>
/// Calls against a LiveKit that answers (a fake RoomService on loopback): the 25 s grace after a device dropped
/// out of a private call (on a manual clock), room cleanup and kicking removed members.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LiveKitRoomTests(ApiFactory api) : IAsyncLifetime
{
    private FakeLiveKit _liveKit = null!;
    private WebApplicationFactory<Program> _host = null!;
    private readonly ManualTimeProvider _graceClock = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly GraceChecks _graceChecks = new();

    public async ValueTask InitializeAsync()
    {
        _liveKit = await FakeLiveKit.StartAsync();
        _host = api.WithFakeLiveKit(_liveKit, services => services.UseManualGraceClock(_graceClock, _graceChecks));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _liveKit.DisposeAsync();
    }

    private static string Room(CallDto call) => LiveKitNames.RoomName(call.Id);

    /// <summary>Alice called Bob, Bob answered, both devices are in the LiveKit room.</summary>
    private async Task<(CallUser Alice, CallUser Bob, Guid ChatId, CallDto Call, CallHub BobHub)> ConnectedCallAsync()
    {
        var alice = await _host.NewUserAsync("Alice");
        var bob = await _host.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        var bobHub = await CallHub.ConnectAsync(bob);
        var call = await alice.ActivePrivateCallAsync(bob, chatId);
        _liveKit.AddParticipant(Room(call), alice.Identity);
        _liveKit.AddParticipant(Room(call), bob.Identity);
        return (alice, bob, chatId, call, bobHub);
    }

    private async Task DeviceLeavesAsync(CallDto call, CallUser device)
    {
        var sid = _liveKit.SidOf(Room(call), device.Identity);
        _liveKit.RemoveParticipantFromRoom(Room(call), device.Identity);
        await LiveKitWebhook.SendAsync(_host, LiveKitWebhook.Participant("participant_left", Room(call), device.Identity, participantSid: sid));
    }

    private IReadOnlyList<TimeSpan> GraceTimers => _graceClock.PendingDelays;

    [Fact]
    public async Task A_device_gone_for_the_whole_grace_period_ends_the_call()
    {
        var (alice, bob, chatId, call, bobHub) = await ConnectedCallAsync();
        await using var _ = bobHub;

        await DeviceLeavesAsync(call, alice);

        var left = await bobHub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Left);
        left.Status.ShouldBe(CallStatus.Active);
        GraceTimers.ShouldBe([TimeSpan.FromSeconds(25)]);

        _graceClock.Advance(TimeSpan.FromSeconds(24));
        _graceChecks.Completed.ShouldBeEmpty();
        (await bob.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Active);

        _graceClock.Advance(TimeSpan.FromSeconds(1));
        (await _graceChecks.WaitAsync(call.Id, alice.Id)).Retry.ShouldBeNull();

        var ended = await bobHub.CallStatusAsync(call.Id, CallStatus.Ended);
        ended.Participants.ShouldAllBe(p => p.Status == CallParticipantStatus.Left);
        (await bobHub.CallLogAsync(chatId, call.Id)).Call!.Status.ShouldBe(CallStatus.Ended);
        // It asked LiveKit who is still there, then closed the room.
        _liveKit.TwirpCalls("ListParticipants", Room(call)).ShouldNotBeEmpty();
        var deleted = await _liveKit.WaitForTwirpAsync("DeleteRoom", c => c.Room == Room(call));
        deleted.Authorized.ShouldBeTrue();
        deleted.Grant!.Value.GetProperty("roomCreate").GetBoolean().ShouldBeTrue();
        GraceTimers.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_device_back_in_the_room_within_the_grace_period_keeps_the_call()
    {
        var (alice, bob, _, call, bobHub) = await ConnectedCallAsync();
        await using var _ = bobHub;
        await DeviceLeavesAsync(call, alice);
        await bobHub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Left);
        var mark = bobHub.Checkpoint();

        // The app reconnected (new participant sid) but its participant_joined webhook got lost.
        _liveKit.AddParticipant(Room(call), alice.Identity);
        _graceClock.Advance(TimeSpan.FromSeconds(25));
        (await _graceChecks.WaitAsync(call.Id, alice.Id)).Retry.ShouldBeNull();

        var back = await bobHub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Joined, after: mark);
        back.Status.ShouldBe(CallStatus.Active);
        (await bob.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Active);
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Without_an_answer_from_livekit_the_grace_check_trusts_the_webhooks()
    {
        var (alice, bob, _, call, bobHub) = await ConnectedCallAsync();
        await using var _ = bobHub;
        await DeviceLeavesAsync(call, alice);
        await bobHub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Left);
        var mark = bobHub.Checkpoint();
        await LiveKitWebhook.SendAsync(_host, LiveKitWebhook.Participant("participant_joined", Room(call), alice.Identity));
        await bobHub.CallUpdatedAsync(call.Id, c => c.StatusOf(alice) == CallParticipantStatus.Joined, after: mark);

        _liveKit.Available = false;
        _graceClock.Advance(TimeSpan.FromSeconds(25));
        (await _graceChecks.WaitAsync(call.Id, alice.Id)).Retry.ShouldBeNull();

        var now = await bob.GetCallAsync(call.Id);
        now.Status.ShouldBe(CallStatus.Active);
        now.StatusOf(alice).ShouldBe(CallParticipantStatus.Joined);
        _liveKit.Available = true;
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task A_device_that_rejoined_moments_ago_gets_another_grace_period()
    {
        var (alice, bob, _, call, bobHub) = await ConnectedCallAsync();
        await using var _ = bobHub;
        await DeviceLeavesAsync(call, alice);
        // The app restarted and asked to re-join; it is still connecting to LiveKit.
        await alice.JoinAsync(call.Id);

        _graceClock.Advance(TimeSpan.FromSeconds(25));
        (await _graceChecks.WaitAsync(call.Id, alice.Id)).Retry.ShouldBe(TimeSpan.FromSeconds(25));
        (await bob.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Active);
        // The scheduler re-arms right after the check returned.
        await CallKit.EventuallyAsync(() => GraceTimers.SequenceEqual([TimeSpan.FromSeconds(25)]), "the grace check was rescheduled in 25 s");

        // It never made it into the room.
        await _host.WithDbAsync(db => db.CallParticipants.Where(p => p.CallId == call.Id && p.UserId == alice.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.JoinedAt, p => p.JoinedAt - TimeSpan.FromSeconds(30)), CallKit.Ct));
        _graceClock.Advance(TimeSpan.FromSeconds(25));
        (await _graceChecks.WaitAsync(call.Id, alice.Id, nth: 2)).Retry.ShouldBeNull();

        await bobHub.CallStatusAsync(call.Id, CallStatus.Ended);
    }

    [Fact]
    public async Task Leaving_on_one_device_while_another_stays_is_not_leaving()
    {
        var (alice, bob, _, call, bobHub) = await ConnectedCallAsync();
        await using var _ = bobHub;
        var alicePhone = await alice.SignInAgainAsync();
        await alicePhone.JoinAsync(call.Id);
        _liveKit.AddParticipant(Room(call), alicePhone.Identity);

        await DeviceLeavesAsync(call, alice);

        await bobHub.FlushAsync();
        bobHub.CallEvents(nameof(IRealtimeClient.CallUpdated), call.Id).ShouldNotContain(c => c.StatusOf(alice) == CallParticipantStatus.Left);
        GraceTimers.ShouldBeEmpty();
        (await bob.GetCallAsync(call.Id)).StatusOf(alice).ShouldBe(CallParticipantStatus.Joined);
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task A_caller_vanishing_while_it_rings_makes_the_call_missed()
    {
        var alice = await _host.NewUserAsync("Alice");
        var bob = await _host.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        var call = (await alice.StartCallAsync(chatId)).Call;
        _liveKit.AddParticipant(Room(call), alice.Identity);

        // The caller vanishes while it is still ringing: after the grace period the call is missed.
        await DeviceLeavesAsync(call, alice);
        _graceClock.Advance(TimeSpan.FromSeconds(25));
        await _graceChecks.WaitAsync(call.Id, alice.Id);

        var after = await bob.GetCallAsync(call.Id);
        after.Status.ShouldBe(CallStatus.Missed);
        after.StatusOf(bob).ShouldBe(CallParticipantStatus.Missed);
    }

    [Fact]
    public async Task Removed_members_are_kicked_from_the_room_on_every_device()
    {
        var owner = await _host.NewUserAsync("Olga");
        var bob = await _host.NewUserAsync("Bob");
        var carol = await _host.NewUserAsync("Carol");
        var groupId = await owner.CreateGroupAsync("Team", bob, carol);
        await using var carolHub = await CallHub.ConnectAsync(carol);
        var call = (await owner.StartCallAsync(groupId)).Call;
        var bobTablet = await bob.SignInAgainAsync();
        await bob.JoinAsync(call.Id);
        await bobTablet.JoinAsync(call.Id);
        _liveKit.AddParticipant(Room(call), owner.Identity);
        _liveKit.AddParticipant(Room(call), bob.Identity);
        _liveKit.AddParticipant(Room(call), bobTablet.Identity);

        await owner.RemoveMemberAsync(groupId, bob);

        var left = await carolHub.CallUpdatedAsync(call.Id, c => c.StatusOf(bob) == CallParticipantStatus.Left);
        left.Status.ShouldBe(CallStatus.Active);
        var kicked = new[]
        {
            await _liveKit.WaitForTwirpAsync("RemoveParticipant", c => c.Identity == bob.Identity),
            await _liveKit.WaitForTwirpAsync("RemoveParticipant", c => c.Identity == bobTablet.Identity),
        };
        foreach (var kick in kicked)
        {
            kick.Room.ShouldBe(Room(call));
            kick.Authorized.ShouldBeTrue();
            kick.Grant!.Value.GetProperty("roomAdmin").GetBoolean().ShouldBeTrue();
            kick.Grant.Value.GetProperty("room").GetString().ShouldBe(Room(call));
        }
        _liveKit.TwirpCalls("RemoveParticipant").ShouldNotContain(c => c.Identity == owner.Identity);
        (await owner.GetCallAsync(call.Id)).StatusOf(owner).ShouldBe(CallParticipantStatus.Joined);
        await owner.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Strangers_and_late_comers_with_leftover_tokens_are_shown_the_door()
    {
        var (alice, _, chatId, call, bobHub) = await ConnectedCallAsync();
        await using var _ = bobHub;
        var mallory = await _host.NewUserAsync("Mallory");

        await LiveKitWebhook.SendAsync(_host, LiveKitWebhook.Participant("participant_joined", Room(call), mallory.Identity));
        (await _liveKit.WaitForTwirpAsync("RemoveParticipant", c => c.Identity == mallory.Identity)).Room.ShouldBe(Room(call));

        await alice.HangupAsync(call.Id);
        await _liveKit.WaitForTwirpAsync("DeleteRoom", c => c.Room == Room(call));
        _liveKit.HasRoom(Room(call)).ShouldBeFalse();
        _liveKit.AddParticipant(Room(call), alice.Identity);

        // A device entering the room of a finished call closes the room again.
        await LiveKitWebhook.SendAsync(_host, LiveKitWebhook.Participant("participant_joined", Room(call), alice.Identity));
        await _liveKit.WaitForTwirpAsync("DeleteRoom", c => c.Room == Room(call), occurrence: 2);
        _liveKit.HasRoom(Room(call)).ShouldBeFalse();
        (await alice.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ended);
        (await alice.CallLogsAsync(chatId, call.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Group_hangup_on_one_device_while_another_is_in_the_room_keeps_me_in()
    {
        var owner = await _host.NewUserAsync("Olga");
        var bob = await _host.NewUserAsync("Bob");
        var groupId = await owner.CreateGroupAsync("Team", bob);
        await using var bobHub = await CallHub.ConnectAsync(bob);
        var call = (await owner.StartCallAsync(groupId)).Call;
        var ownerLaptop = await owner.SignInAgainAsync();
        await ownerLaptop.JoinAsync(call.Id);
        _liveKit.AddParticipant(Room(call), owner.Identity);
        _liveKit.AddParticipant(Room(call), ownerLaptop.Identity);

        await owner.HangupAsync(call.Id);

        var now = await bob.GetCallAsync(call.Id);
        now.Status.ShouldBe(CallStatus.Active);
        now.StatusOf(owner).ShouldBe(CallParticipantStatus.Joined);

        // Last device out: with nobody left in the room the call ends.
        _liveKit.RemoveParticipantFromRoom(Room(call), owner.Identity);
        await ownerLaptop.HangupAsync(call.Id);
        await bobHub.CallStatusAsync(call.Id, CallStatus.Ended);
    }

    [Fact]
    public async Task A_group_call_does_not_end_while_livekit_still_sees_someone_in_the_room()
    {
        var owner = await _host.NewUserAsync("Olga");
        var bob = await _host.NewUserAsync("Bob");
        var groupId = await owner.CreateGroupAsync("Team", bob);
        var call = (await owner.StartCallAsync(groupId)).Call;
        // Bob is connected, but his join has not been recorded yet (webhook lag).
        _liveKit.AddParticipant(Room(call), owner.Identity);
        _liveKit.AddParticipant(Room(call), bob.Identity);

        await owner.HangupAsync(call.Id);

        var now = await bob.GetCallAsync(call.Id);
        now.Status.ShouldBe(CallStatus.Active);
        now.StatusOf(owner).ShouldBe(CallParticipantStatus.Left);
        await bob.JoinAsync(call.Id);
        _liveKit.RemoveParticipantFromRoom(Room(call), owner.Identity);
        _liveKit.RemoveParticipantFromRoom(Room(call), bob.Identity);
        await bob.HangupAsync(call.Id);
        (await bob.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ended);
    }
}
