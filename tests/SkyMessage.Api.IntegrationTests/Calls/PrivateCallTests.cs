using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>1:1 call lifecycle over REST + realtime: ring, answer, refuse, cancel, hang up, ring timeout, call logs.</summary>
[Collection(ApiCollection.Name)]
public sealed class PrivateCallTests(ApiFactory api)
{
    private async Task<(CallUser Alice, CallUser Bob, Guid ChatId, CallHub AliceHub, CallHub BobHub)> PairAsync()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        return (alice, bob, chatId, await CallHub.ConnectAsync(alice), await CallHub.ConnectAsync(bob));
    }

    [Fact]
    public async Task Start_rings_the_callee_and_returns_join_info_to_the_caller()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;

        var join = await alice.StartCallAsync(chatId, isVideo: true);

        var call = join.Call;
        call.ChatId.ShouldBe(chatId);
        call.Kind.ShouldBe(CallKind.Private);
        call.Status.ShouldBe(CallStatus.Ringing);
        call.IsVideo.ShouldBeTrue();
        call.InitiatorId.ShouldBe(alice.Id);
        call.AnsweredAt.ShouldBeNull();
        call.EndedAt.ShouldBeNull();
        call.Participants.Select(p => p.UserId).ShouldBe([alice.Id, bob.Id]); // initiator first
        call.Participant(alice).Status.ShouldBe(CallParticipantStatus.Joined);
        call.Participant(alice).JoinedAt.ShouldNotBeNull();
        call.Participant(bob).Status.ShouldBe(CallParticipantStatus.Ringing);
        call.Participant(bob).JoinedAt.ShouldBeNull();
        join.LiveKitUrl.ShouldBe(HubRoutes.LiveKitProxy);
        join.LiveKitToken.ShouldNotBeNullOrWhiteSpace();

        var incoming = await bobHub.IncomingCallAsync(call.Id);
        incoming.ShouldBe(call with { Participants = incoming.Participants });
        incoming.Participants.ShouldBe(call.Participants);
        (await aliceHub.CallStatusAsync(call.Id, CallStatus.Ringing)).InitiatorId.ShouldBe(alice.Id);

        // IncomingCall only to the callee; the caller's own devices get CallUpdated.
        await aliceHub.FlushAsync();
        await bobHub.FlushAsync();
        aliceHub.CallEvents(nameof(IRealtimeClient.IncomingCall), call.Id).ShouldBeEmpty();
        bobHub.CallEvents(nameof(IRealtimeClient.CallUpdated), call.Id).ShouldBeEmpty();

        (await bob.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ringing);
        (await bob.GetActiveCallAsync(chatId))!.Id.ShouldBe(call.Id);
        (await alice.GetChatAsync(chatId)).ActiveCallId.ShouldBe(call.Id);
        (await alice.CallLogsAsync(chatId, call.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Accept_makes_the_call_active_for_both_sides()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var started = await alice.StartCallAsync(chatId);

        var accepted = await bob.AcceptAsync(started.Call.Id);

        accepted.Call.Id.ShouldBe(started.Call.Id);
        accepted.Call.Status.ShouldBe(CallStatus.Active);
        accepted.Call.AnsweredAt.ShouldNotBeNull();
        accepted.Call.ShouldHaveParticipants((alice, CallParticipantStatus.Joined), (bob, CallParticipantStatus.Joined));
        accepted.Call.Participant(bob).JoinedAt.ShouldNotBeNull();
        accepted.LiveKitToken.ShouldNotBeNullOrWhiteSpace();
        accepted.LiveKitToken.ShouldNotBe(started.LiveKitToken);

        (await aliceHub.CallStatusAsync(started.Call.Id, CallStatus.Active)).AnsweredAt.ShouldBe(accepted.Call.AnsweredAt);
        (await bobHub.CallStatusAsync(started.Call.Id, CallStatus.Active)).AnsweredAt.ShouldBe(accepted.Call.AnsweredAt);
        var fetched = await alice.GetCallAsync(started.Call.Id);
        fetched.Status.ShouldBe(CallStatus.Active);
        fetched.AnsweredAt.ShouldBe(accepted.Call.AnsweredAt);
        fetched.Participants.ShouldBe(accepted.Call.Participants);

        // Accepting again (another device of the callee) keeps the first answer time.
        var again = await bob.AcceptAsync(started.Call.Id);
        again.Call.Status.ShouldBe(CallStatus.Active);
        again.Call.AnsweredAt.ShouldBe(accepted.Call.AnsweredAt);

        await alice.HangupAsync(started.Call.Id);
    }

    [Fact]
    public async Task Hanging_up_an_answered_call_ends_it_and_posts_one_call_log()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = await alice.ActivePrivateCallAsync(bob, chatId);

        await bob.HangupAsync(call.Id);

        var ended = await aliceHub.CallStatusAsync(call.Id, CallStatus.Ended);
        ended.EndedAt.ShouldNotBeNull();
        ended.AnsweredAt.ShouldBe(call.AnsweredAt);
        ended.ShouldHaveParticipants((alice, CallParticipantStatus.Left), (bob, CallParticipantStatus.Left));
        ended.Participants.ShouldAllBe(p => p.LeftAt != null);
        await bobHub.CallStatusAsync(call.Id, CallStatus.Ended);

        var log = await aliceHub.CallLogAsync(chatId, call.Id);
        log.Type.ShouldBe(MessageType.Call);
        log.SenderId.ShouldBe(alice.Id); // the initiator
        log.Call!.Status.ShouldBe(CallStatus.Ended);
        log.Call.IsVideo.ShouldBe(call.IsVideo);
        log.Call.InitiatorId.ShouldBe(alice.Id);
        log.Call.DurationSeconds.ShouldBe((int)Math.Floor((ended.EndedAt!.Value - ended.AnsweredAt!.Value).TotalSeconds));
        (await bobHub.CallLogAsync(chatId, call.Id)).Id.ShouldBe(log.Id);

        (await bob.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ended);
        (await alice.GetActiveCallAsync(chatId)).ShouldBeNull();
        (await alice.GetChatAsync(chatId)).ActiveCallId.ShouldBeNull();
        (await api.LoadCallAsync(call.Id)).MessageId.ShouldBe(log.Id);

        // Hanging up again (the other side, a retry) changes nothing and logs nothing.
        await alice.HangupAsync(call.Id);
        await bob.HangupAsync(call.Id);
        await aliceHub.FlushAsync();
        aliceHub.CallEvents(nameof(IRealtimeClient.CallUpdated), call.Id).Count(c => c.Status == CallStatus.Ended).ShouldBe(1);
        (await alice.CallLogsAsync(chatId, call.Id)).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(null, CallStatus.Declined)]
    [InlineData(false, CallStatus.Declined)]
    [InlineData(true, CallStatus.Busy)]
    public async Task Callee_declining_ends_the_call_with_a_call_log(bool? busy, CallStatus expected)
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = (await alice.StartCallAsync(chatId)).Call;

        await bob.DeclineAsync(call.Id, busy);

        var declined = await aliceHub.CallStatusAsync(call.Id, expected);
        declined.ShouldHaveParticipants((alice, CallParticipantStatus.Left), (bob, CallParticipantStatus.Declined));
        declined.AnsweredAt.ShouldBeNull();
        declined.EndedAt.ShouldNotBeNull();
        await bobHub.CallStatusAsync(call.Id, expected);

        var log = await aliceHub.CallLogAsync(chatId, call.Id);
        log.Call!.Status.ShouldBe(expected);
        log.Call.DurationSeconds.ShouldBe(0);
        (await alice.GetActiveCallAsync(chatId)).ShouldBeNull();

        // A finished call can no longer be answered or joined.
        await (await bob.TryAcceptAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CallEnded);
        await (await bob.TryJoinAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CallEnded);
        // Declining twice is a no-op.
        await bob.DeclineAsync(call.Id, busy);
        (await bob.GetCallAsync(call.Id)).Status.ShouldBe(expected);
    }

    [Fact]
    public async Task The_caller_cannot_decline_their_own_call()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = (await alice.StartCallAsync(chatId)).Call;

        await alice.DeclineAsync(call.Id, busy: true);

        (await alice.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ringing);
        await bobHub.FlushAsync();
        bobHub.CallEvents(nameof(IRealtimeClient.CallUpdated), call.Id).ShouldBeEmpty();
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Caller_hanging_up_while_ringing_cancels_and_the_callee_missed_it()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = (await alice.StartCallAsync(chatId)).Call;
        await bobHub.IncomingCallAsync(call.Id);

        await alice.HangupAsync(call.Id);

        var cancelled = await bobHub.CallStatusAsync(call.Id, CallStatus.Cancelled);
        cancelled.ShouldHaveParticipants((alice, CallParticipantStatus.Left), (bob, CallParticipantStatus.Missed));
        await aliceHub.CallStatusAsync(call.Id, CallStatus.Cancelled);
        (await bobHub.CallLogAsync(chatId, call.Id)).Call!.Status.ShouldBe(CallStatus.Cancelled);
    }

    [Fact]
    public async Task Callee_hanging_up_while_ringing_declines()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = (await alice.StartCallAsync(chatId)).Call;

        await bob.HangupAsync(call.Id);

        var declined = await aliceHub.CallStatusAsync(call.Id, CallStatus.Declined);
        declined.ShouldHaveParticipants((alice, CallParticipantStatus.Left), (bob, CallParticipantStatus.Declined));
        (await aliceHub.CallLogAsync(chatId, call.Id)).Call!.Status.ShouldBe(CallStatus.Declined);
    }

    [Fact]
    public async Task Unanswered_call_becomes_missed_after_the_ring_timeout()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var carol = await api.NewUserAsync("Carol");
        var dave = await api.NewUserAsync("Dave");
        var otherChat = await carol.PrivateChatWithAsync(dave);
        var call = (await alice.StartCallAsync(chatId)).Call;
        var fresh = (await carol.StartCallAsync(otherChat)).Call;

        // Not yet: a call that rang for less than the timeout keeps ringing.
        await api.BackdateCallAsync(call.Id, TimeSpan.FromSeconds(Limits.CallRingTimeoutSeconds - 15));
        await api.RunMaintenanceAsync(m => m.ExpireRingingAsync(CallKit.Ct));
        (await alice.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ringing);

        await api.BackdateCallAsync(call.Id, TimeSpan.FromSeconds(16));
        await api.RunMaintenanceAsync(m => m.ExpireRingingAsync(CallKit.Ct));

        var missed = await bobHub.CallStatusAsync(call.Id, CallStatus.Missed);
        missed.ShouldHaveParticipants((alice, CallParticipantStatus.Left), (bob, CallParticipantStatus.Missed));
        missed.EndedAt.ShouldNotBeNull();
        await aliceHub.CallStatusAsync(call.Id, CallStatus.Missed);
        var log = await bobHub.CallLogAsync(chatId, call.Id);
        log.Call!.Status.ShouldBe(CallStatus.Missed);
        log.SenderId.ShouldBe(alice.Id);
        (await carol.GetCallAsync(fresh.Id)).Status.ShouldBe(CallStatus.Ringing);

        await carol.HangupAsync(fresh.Id);
    }

    [Fact]
    public async Task Pressing_call_twice_returns_the_same_ringing_call()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;

        var first = await alice.StartCallAsync(chatId);
        var second = await alice.StartCallAsync(chatId);

        second.Call.Id.ShouldBe(first.Call.Id);
        second.Call.Status.ShouldBe(CallStatus.Ringing);
        second.LiveKitToken.ShouldNotBeNullOrWhiteSpace();
        await bobHub.IncomingCallAsync(first.Call.Id);
        await bobHub.FlushAsync();
        bobHub.CallEvents(nameof(IRealtimeClient.IncomingCall), first.Call.Id).Count.ShouldBe(1);
        (await api.WithDbAsync(db => db.Calls.CountAsync(c => c.ChatId == chatId, CallKit.Ct))).ShouldBe(1);

        await alice.HangupAsync(first.Call.Id);
    }

    [Fact]
    public async Task Concurrent_starts_by_the_caller_create_one_call()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => alice.StartCallAsync(chatId)));

        results.Select(r => r.Call.Id).Distinct().Count().ShouldBe(1);
        results.ShouldAllBe(r => r.Call.Status == CallStatus.Ringing);
        (await api.WithDbAsync(db => db.Calls.CountAsync(c => c.ChatId == chatId, CallKit.Ct))).ShouldBe(1);
        await bobHub.FlushAsync();
        bobHub.Received<CallDto>(nameof(IRealtimeClient.IncomingCall)).Count.ShouldBe(1);

        await alice.HangupAsync(results[0].Call.Id);
    }

    [Fact]
    public async Task Both_sides_calling_at_once_end_up_in_one_answered_call()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;

        var results = await Task.WhenAll(alice.StartCallAsync(chatId), bob.StartCallAsync(chatId));

        results[0].Call.Id.ShouldBe(results[1].Call.Id);
        var call = await alice.GetCallAsync(results[0].Call.Id);
        call.Status.ShouldBe(CallStatus.Active);
        call.ShouldHaveParticipants((alice, CallParticipantStatus.Joined), (bob, CallParticipantStatus.Joined));
        (await api.WithDbAsync(db => db.Calls.CountAsync(c => c.ChatId == chatId, CallKit.Ct))).ShouldBe(1);

        await bob.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Callee_pressing_call_in_the_ringing_chat_answers_it()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = (await alice.StartCallAsync(chatId)).Call;

        var join = await bob.StartCallAsync(chatId);

        join.Call.Id.ShouldBe(call.Id);
        join.Call.Status.ShouldBe(CallStatus.Active);
        join.Call.InitiatorId.ShouldBe(alice.Id);
        await aliceHub.CallStatusAsync(call.Id, CallStatus.Active);
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Join_rejoins_an_unfinished_private_call_from_any_device()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = await alice.ActivePrivateCallAsync(bob, chatId);
        var joinedAt = call.Participant(alice).JoinedAt!.Value;

        var rejoined = await alice.JoinAsync(call.Id);

        rejoined.Call.Id.ShouldBe(call.Id);
        rejoined.Call.Status.ShouldBe(CallStatus.Active);
        rejoined.Call.Participant(alice).Status.ShouldBe(CallParticipantStatus.Joined);
        rejoined.Call.Participant(alice).JoinedAt!.Value.ShouldBeGreaterThanOrEqualTo(joinedAt);
        Jwt.Payload(rejoined.LiveKitToken).GetProperty("sub").GetString().ShouldBe(alice.Identity);

        // After an app restart on another device: same call, that device's own identity.
        var aliceTablet = await alice.SignInAgainAsync();
        var fromTablet = await aliceTablet.JoinAsync(call.Id);
        fromTablet.Call.Id.ShouldBe(call.Id);
        Jwt.Payload(fromTablet.LiveKitToken).GetProperty("sub").GetString().ShouldBe(aliceTablet.Identity);
        aliceTablet.Identity.ShouldNotBe(alice.Identity);

        // Strangers cannot join.
        var carol = await api.NewUserAsync("Carol");
        await (await carol.TryJoinAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.CallNotFound);

        await alice.HangupAsync(call.Id);
        await (await alice.TryJoinAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CallEnded);
    }

    [Fact]
    public async Task Ringing_callee_joining_answers_the_call()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = (await alice.StartCallAsync(chatId)).Call;

        var joined = await bob.JoinAsync(call.Id);

        joined.Call.Status.ShouldBe(CallStatus.Active);
        joined.Call.AnsweredAt.ShouldNotBeNull();
        await aliceHub.CallStatusAsync(call.Id, CallStatus.Active);
        await bob.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Calls_of_other_chats_are_invisible()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = (await alice.StartCallAsync(chatId)).Call;
        var mallory = await api.NewUserAsync("Mallory");

        await (await mallory.TryStartCallAsync(chatId)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await mallory.Http.GetAsync(ApiRoutes.Calls.ByIdPath(call.Id), CallKit.Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
        await (await mallory.Http.GetAsync(ApiRoutes.Calls.ActivePath(chatId), CallKit.Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await mallory.TryAcceptAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
        await (await mallory.TryDeclineAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
        await (await mallory.TryJoinAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
        await (await mallory.TryHangupAsync(call.Id)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
        (await mallory.GetHistoryAsync()).ShouldBeEmpty();
        (await alice.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Ringing);

        await (await alice.Http.GetAsync(ApiRoutes.Calls.ByIdPath(Guid.NewGuid()), CallKit.Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Deleting_the_chat_for_everyone_during_a_call_ends_the_call_for_its_participants()
    {
        var (alice, bob, chatId, aliceHub, bobHub) = await PairAsync();
        await using var _ = aliceHub;
        await using var __ = bobHub;
        var call = await alice.ActivePrivateCallAsync(bob, chatId);

        await (await alice.Http.DeleteAsync(ApiRoutes.Chats.DeletePath(chatId, forEveryone: true), CallKit.Ct)).ShouldBeAsync(HttpStatusCode.NoContent);

        // SPEC §6.5: every change of a call reaches its participants; the call cannot outlive its chat silently.
        await bobHub.WaitAsync<ChatRefEvent>(nameof(IRealtimeClient.ChatRemoved), e => e.ChatId == chatId);
        var ended = await bobHub.CallUpdatedAsync(call.Id, c => c.Status != CallStatus.Active);
        ended.Participants.ShouldAllBe(p => p.Status == CallParticipantStatus.Left);
    }

    [Fact]
    public async Task Saved_messages_cannot_be_called_and_bad_requests_are_rejected()
    {
        var alice = await api.NewUserAsync("Alice");

        await (await alice.TryStartCallAsync(alice.Auth.User.SavedMessagesChatId))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.CannotTargetSelf);
        await (await alice.TryStartCallAsync(Guid.Empty)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TryStartCallAsync(Guid.NewGuid())).ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        (await api.NewClient().PostJsonAsync(ApiRoutes.Calls.Root, new StartCallRequest { ChatId = Guid.NewGuid() }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
