using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.Persistence;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>Group calls: start (service message, active call on the chat), join, decline, leave, last one out, membership changes.</summary>
[Collection(ApiCollection.Name)]
public sealed class GroupCallTests(ApiFactory api)
{
    private sealed record Group(CallUser Owner, CallUser Bob, CallUser Carol, Guid ChatId, CallHub OwnerHub, CallHub BobHub, CallHub CarolHub) : IAsyncDisposable
    {
        public CallHub[] Hubs => [OwnerHub, BobHub, CarolHub];

        public async ValueTask DisposeAsync()
        {
            foreach (var hub in Hubs) await hub.DisposeAsync();
        }
    }

    private async Task<Group> GroupAsync()
    {
        var owner = await api.NewUserAsync("Olga");
        var bob = await api.NewUserAsync("Bob");
        var carol = await api.NewUserAsync("Carol");
        var chatId = await owner.CreateGroupAsync("Family " + Guid.NewGuid().ToString("N")[..6], bob, carol);
        return new Group(owner, bob, carol, chatId,
            await CallHub.ConnectAsync(owner), await CallHub.ConnectAsync(bob), await CallHub.ConnectAsync(carol));
    }

    [Fact]
    public async Task Starting_rings_every_member_posts_a_service_message_and_marks_the_chat()
    {
        await using var g = await GroupAsync();

        var join = await g.Owner.StartCallAsync(g.ChatId, isVideo: true);

        var call = join.Call;
        call.Kind.ShouldBe(CallKind.Group);
        call.Status.ShouldBe(CallStatus.Active);
        call.IsVideo.ShouldBeTrue();
        call.AnsweredAt.ShouldNotBeNull();
        call.Participants[0].UserId.ShouldBe(g.Owner.Id);
        call.ShouldHaveParticipants(
            (g.Owner, CallParticipantStatus.Joined), (g.Bob, CallParticipantStatus.Invited), (g.Carol, CallParticipantStatus.Invited));
        join.LiveKitToken.ShouldNotBeNullOrWhiteSpace();

        (await g.BobHub.IncomingCallAsync(call.Id)).Kind.ShouldBe(CallKind.Group);
        await g.CarolHub.IncomingCallAsync(call.Id);
        await g.OwnerHub.CallStatusAsync(call.Id, CallStatus.Active);
        foreach (var hub in g.Hubs)
        {
            (await hub.ChatUpdatedAsync(g.ChatId, c => c.ActiveCallId == call.Id)).Type.ShouldBe(ChatType.Group);
            var started = await hub.SystemEventAsync(g.ChatId, SystemEventType.GroupCallStarted, call.Id);
            started.SystemEvent!.ActorId.ShouldBe(g.Owner.Id);
        }
        await g.OwnerHub.FlushAsync();
        g.OwnerHub.CallEvents(nameof(IRealtimeClient.IncomingCall), call.Id).ShouldBeEmpty();

        (await g.Carol.GetActiveCallAsync(g.ChatId))!.Id.ShouldBe(call.Id);
        (await g.Carol.GetChatAsync(g.ChatId)).ActiveCallId.ShouldBe(call.Id);
        var startedMessage = (await g.Carol.GetMessagesAsync(g.ChatId)).Messages
            .Single(m => m.SystemEvent?.Type == SystemEventType.GroupCallStarted && m.SystemEvent.CallId == call.Id);
        (await api.LoadCallAsync(call.Id)).MessageId.ShouldBe(startedMessage.Id);

        await g.Owner.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Members_join_decline_and_the_last_one_out_ends_the_call()
    {
        await using var g = await GroupAsync();
        var call = (await g.Owner.StartCallAsync(g.ChatId)).Call;

        // Pressing "call" while a call is running joins it.
        var bobJoin = await g.Bob.StartCallAsync(g.ChatId);
        bobJoin.Call.Id.ShouldBe(call.Id);
        bobJoin.Call.StatusOf(g.Bob).ShouldBe(CallParticipantStatus.Joined);
        await g.CarolHub.CallUpdatedAsync(call.Id, c => c.StatusOf(g.Bob) == CallParticipantStatus.Joined);

        await g.Carol.DeclineAsync(call.Id);
        var declined = await g.OwnerHub.CallUpdatedAsync(call.Id, c => c.StatusOf(g.Carol) == CallParticipantStatus.Declined);
        declined.Status.ShouldBe(CallStatus.Active);

        await g.Owner.HangupAsync(call.Id);
        var ownerLeft = await g.BobHub.CallUpdatedAsync(call.Id, c => c.StatusOf(g.Owner) == CallParticipantStatus.Left);
        ownerLeft.Status.ShouldBe(CallStatus.Active);
        ownerLeft.Participant(g.Owner).LeftAt.ShouldNotBeNull();

        var marks = g.Hubs.Select(h => h.Checkpoint()).ToArray();
        await g.Bob.HangupAsync(call.Id);

        for (var i = 0; i < g.Hubs.Length; i++)
        {
            var hub = g.Hubs[i];
            var ended = await hub.CallStatusAsync(call.Id, CallStatus.Ended);
            ended.ShouldHaveParticipants(
                (g.Owner, CallParticipantStatus.Left), (g.Bob, CallParticipantStatus.Left), (g.Carol, CallParticipantStatus.Declined));
            ended.EndedAt.ShouldNotBeNull();
            await hub.ChatUpdatedAsync(g.ChatId, c => c.ActiveCallId == null, after: marks[i]);
            await hub.SystemEventAsync(g.ChatId, SystemEventType.GroupCallEnded, call.Id);
        }
        (await g.Owner.GetActiveCallAsync(g.ChatId)).ShouldBeNull();
        (await g.Owner.GetChatAsync(g.ChatId)).ActiveCallId.ShouldBeNull();
        // Group calls get service messages, not call logs.
        (await g.Owner.CallLogsAsync(g.ChatId, call.Id)).ShouldBeEmpty();
        await (await g.Carol.TryJoinAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CallEnded);
    }

    [Fact]
    public async Task Members_starting_at_the_same_time_end_up_in_one_call()
    {
        await using var g = await GroupAsync();

        var results = await Task.WhenAll(g.Owner.StartCallAsync(g.ChatId), g.Bob.StartCallAsync(g.ChatId), g.Carol.StartCallAsync(g.ChatId));

        results.Select(r => r.Call.Id).Distinct().Count().ShouldBe(1);
        var call = await g.Owner.GetCallAsync(results[0].Call.Id);
        call.ShouldHaveParticipants(
            (g.Owner, CallParticipantStatus.Joined), (g.Bob, CallParticipantStatus.Joined), (g.Carol, CallParticipantStatus.Joined));
        var started = (await g.Owner.GetMessagesAsync(g.ChatId)).Messages.Count(m => m.SystemEvent?.Type == SystemEventType.GroupCallStarted);
        started.ShouldBe(1);

        foreach (var member in new[] { g.Owner, g.Bob, g.Carol }) await member.HangupAsync(call.Id);
        (await g.Owner.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ended);
    }

    [Fact]
    public async Task Pressing_call_just_as_the_last_member_leaves_starts_a_new_call()
    {
        await using var g = await GroupAsync();
        var call = (await g.Owner.StartCallAsync(g.ChatId)).Call;

        // Hold the call row, so both requests queue behind it: first the owner's hang-up, then Bob's start, which
        // already found the call running and now joins a call that ended a moment ago.
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using (var tx = await db.Database.BeginTransactionAsync(CallKit.Ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT id FROM calls WHERE id = {call.Id} FOR UPDATE", CallKit.Ct);
            var hangup = g.Owner.TryHangupAsync(call.Id);
            await WaitForCallRowWaitersAsync(1);
            var start = g.Bob.TryStartCallAsync(g.ChatId);
            await WaitForCallRowWaitersAsync(2);
            await tx.RollbackAsync(CallKit.Ct);

            await (await hangup).ShouldBeAsync(HttpStatusCode.NoContent);
            var started = await (await start).ReadAsync<CallJoinInfoDto>();
            started.Call.Id.ShouldNotBe(call.Id);
            started.Call.Status.ShouldBe(CallStatus.Active);
            started.Call.InitiatorId.ShouldBe(g.Bob.Id);
            (await g.Owner.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ended);
            (await g.Carol.GetActiveCallAsync(g.ChatId))!.Id.ShouldBe(started.Call.Id);
            await g.Bob.HangupAsync(started.Call.Id);
        }
    }

    /// <summary>Requests blocked on a call row lock (MutateAsync's SELECT … FOR UPDATE).</summary>
    private async Task WaitForCallRowWaitersAsync(int count)
    {
        var deadline = DateTime.UtcNow + CallKit.EventTimeout;
        while (await api.WithDbAsync(db => db.Database
                   .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%FROM calls WHERE id%FOR UPDATE%'")
                   .SingleAsync(CallKit.Ct)) < count)
        {
            if (DateTime.UtcNow > deadline) throw new ShouldAssertException($"{count} request(s) never queued behind the call row lock");
            await Task.Delay(20, CallKit.Ct);
        }
    }

    [Fact]
    public async Task Left_members_can_rejoin_and_invited_members_hanging_up_decline()
    {
        await using var g = await GroupAsync();
        var call = (await g.Owner.StartCallAsync(g.ChatId)).Call;
        await g.Bob.JoinAsync(call.Id);

        await g.Owner.HangupAsync(call.Id);
        var back = await g.Owner.JoinAsync(call.Id);
        back.Call.StatusOf(g.Owner).ShouldBe(CallParticipantStatus.Joined);
        back.Call.Participant(g.Owner).LeftAt.ShouldBeNull();

        // Hanging up an invitation one never answered declines it; the call goes on.
        await g.Carol.HangupAsync(call.Id);
        var after = await g.Owner.GetCallAsync(call.Id);
        after.Status.ShouldBe(CallStatus.Active);
        after.StatusOf(g.Carol).ShouldBe(CallParticipantStatus.Declined);

        // Declining after having joined changes nothing (that is what hangup is for).
        await g.Bob.DeclineAsync(call.Id);
        (await g.Owner.GetCallAsync(call.Id)).StatusOf(g.Bob).ShouldBe(CallParticipantStatus.Joined);

        await g.Owner.HangupAsync(call.Id);
        await g.Bob.HangupAsync(call.Id);
        (await g.Owner.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ended);
    }

    [Fact]
    public async Task Members_added_after_the_start_can_join()
    {
        await using var g = await GroupAsync();
        var call = (await g.Owner.StartCallAsync(g.ChatId)).Call;
        var dave = await api.NewUserAsync("Dave");
        var added = await g.Owner.Http.PostJsonAsync(ApiRoutes.Chats.MembersPath(g.ChatId), new AddMembersRequest { UserIds = [dave.Id] });
        (await added.ReadAsync<AddMembersResultDto>()).Added.ShouldBe([dave.Id]);

        var join = await dave.JoinAsync(call.Id);

        join.Call.StatusOf(dave).ShouldBe(CallParticipantStatus.Joined);
        await g.CarolHub.CallUpdatedAsync(call.Id, c => c.Participants.Any(p => p.UserId == dave.Id && p.Status == CallParticipantStatus.Joined));
        await g.Owner.HangupAsync(call.Id);
        await dave.HangupAsync(call.Id);
        (await dave.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ended);
    }

    [Fact]
    public async Task Unanswered_invitations_stop_ringing_after_the_ring_timeout_but_can_still_join()
    {
        await using var g = await GroupAsync();
        var call = (await g.Owner.StartCallAsync(g.ChatId)).Call;
        await g.Bob.JoinAsync(call.Id);

        await api.BackdateCallAsync(call.Id, TimeSpan.FromSeconds(Limits.CallRingTimeoutSeconds + 1));
        await api.RunMaintenanceAsync(m => m.ExpireRingingAsync(CallKit.Ct));

        var expired = await g.CarolHub.CallUpdatedAsync(call.Id, c => c.StatusOf(g.Carol) == CallParticipantStatus.Missed);
        expired.Status.ShouldBe(CallStatus.Active);
        expired.StatusOf(g.Owner).ShouldBe(CallParticipantStatus.Joined);
        expired.StatusOf(g.Bob).ShouldBe(CallParticipantStatus.Joined);

        (await g.Carol.JoinAsync(call.Id)).Call.StatusOf(g.Carol).ShouldBe(CallParticipantStatus.Joined);

        foreach (var member in new[] { g.Owner, g.Bob, g.Carol }) await member.HangupAsync(call.Id);
        (await g.Owner.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ended);
    }

    [Fact]
    public async Task Removed_member_is_marked_left_and_loses_sight_of_the_call()
    {
        await using var g = await GroupAsync();
        var call = (await g.Owner.StartCallAsync(g.ChatId)).Call;
        await g.Bob.JoinAsync(call.Id);

        await g.Owner.RemoveMemberAsync(g.ChatId, g.Bob);

        // The removed member hears about it once more (so the client leaves the room), then the call is gone for them.
        var bobLeft = await g.BobHub.CallUpdatedAsync(call.Id, c => c.StatusOf(g.Bob) == CallParticipantStatus.Left);
        bobLeft.Status.ShouldBe(CallStatus.Active);
        bobLeft.Participant(g.Bob).LeftAt.ShouldNotBeNull();
        await g.CarolHub.CallUpdatedAsync(call.Id, c => c.StatusOf(g.Bob) == CallParticipantStatus.Left);
        await (await g.Bob.Http.GetAsync(ApiRoutes.Calls.ByIdPath(call.Id), CallKit.Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
        await (await g.Bob.TryJoinAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
        (await g.Bob.GetHistoryAsync()).ShouldNotContain(h => h.Call.Id == call.Id);
        var now = await g.Owner.GetCallAsync(call.Id);
        now.Status.ShouldBe(CallStatus.Active);
        now.StatusOf(g.Owner).ShouldBe(CallParticipantStatus.Joined);

        await g.Owner.HangupAsync(call.Id);
        (await g.Owner.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ended);
    }

    [Fact]
    public async Task Member_leaving_the_group_leaves_its_call()
    {
        await using var g = await GroupAsync();
        var call = (await g.Owner.StartCallAsync(g.ChatId)).Call;
        await g.Carol.JoinAsync(call.Id);

        await g.Carol.LeaveChatAsync(g.ChatId);

        var left = await g.OwnerHub.CallUpdatedAsync(call.Id, c => c.StatusOf(g.Carol) == CallParticipantStatus.Left);
        left.Status.ShouldBe(CallStatus.Active);
        await g.Owner.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Removing_the_last_member_in_the_call_ends_it_and_only_members_get_the_chat_update()
    {
        await using var g = await GroupAsync();
        var call = (await g.Owner.StartCallAsync(g.ChatId)).Call;
        await g.Bob.JoinAsync(call.Id);
        await g.Owner.HangupAsync(call.Id);
        var ownerMark = g.OwnerHub.Checkpoint();
        var carolMark = g.CarolHub.Checkpoint();

        await g.Owner.RemoveMemberAsync(g.ChatId, g.Bob);

        var ended = await g.OwnerHub.CallStatusAsync(call.Id, CallStatus.Ended);
        ended.StatusOf(g.Bob).ShouldBe(CallParticipantStatus.Left);
        await g.OwnerHub.ChatUpdatedAsync(g.ChatId, c => c.ActiveCallId == null, after: ownerMark);
        await g.CarolHub.ChatUpdatedAsync(g.ChatId, c => c.ActiveCallId == null, after: carolMark);
        await g.CarolHub.SystemEventAsync(g.ChatId, SystemEventType.GroupCallEnded, call.Id);
        (await g.BobHub.CallStatusAsync(call.Id, CallStatus.Ended)).StatusOf(g.Bob).ShouldBe(CallParticipantStatus.Left);

        // Bob is no longer a member: the chat's state is none of his business any more (SPEC §7: ChatUpdated → members).
        await g.BobHub.FlushAsync();
        g.BobHub.Received<ChatInfoDto>(nameof(IRealtimeClient.ChatUpdated), c => c.Id == g.ChatId && c.ActiveCallId == null).ShouldBeEmpty();
    }
}
