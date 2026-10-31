using Shouldly;
using SkyMessage.Application.Calls;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;

namespace SkyMessage.UnitTests.Calls;

public sealed class CallStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid ChatId = Guid.CreateVersion7();
    private static readonly Guid Caller = Guid.CreateVersion7();
    private static readonly Guid Callee = Guid.CreateVersion7();
    private static readonly Guid Alice = Guid.CreateVersion7();
    private static readonly Guid Bob = Guid.CreateVersion7();

    private static Call Ringing() => CallStateMachine.CreatePrivate(ChatId, Caller, Callee, isVideo: true, T0);

    private static Call Answered(DateTimeOffset? at = null)
    {
        var call = Ringing();
        CallStateMachine.Accept(call, Callee, at ?? T0.AddSeconds(5)).ShouldBe(CallOutcome.Updated);
        return call;
    }

    private static Call Group() => CallStateMachine.CreateGroup(ChatId, Caller, [Caller, Alice, Bob], isVideo: false, T0);

    private static CallParticipantStatus StatusOf(Call call, Guid userId) => CallStateMachine.Participant(call, userId)!.Status;

    private static HashSet<Guid> Users(params Guid[] ids) => [.. ids];

    // ── start ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Private_call_rings_with_the_caller_already_joined()
    {
        var call = Ringing();

        call.Kind.ShouldBe(CallKind.Private);
        call.Status.ShouldBe(CallStatus.Ringing);
        call.InitiatorId.ShouldBe(Caller);
        call.IsVideo.ShouldBeTrue();
        call.RoomName.ShouldBe(LiveKitNames.RoomName(call.Id));
        StatusOf(call, Caller).ShouldBe(CallParticipantStatus.Joined);
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Ringing);
        call.Participants.ShouldAllBe(p => p.CallId == call.Id);
    }

    [Fact]
    public void Group_call_is_active_at_once_with_everyone_else_invited()
    {
        var call = Group();

        call.Status.ShouldBe(CallStatus.Active);
        call.AnsweredAt.ShouldBe(T0);
        call.Participants.Count.ShouldBe(3);
        StatusOf(call, Caller).ShouldBe(CallParticipantStatus.Joined);
        StatusOf(call, Alice).ShouldBe(CallParticipantStatus.Invited);
        StatusOf(call, Bob).ShouldBe(CallParticipantStatus.Invited);
    }

    // ── busy ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CallStatus.Ringing, CallParticipantStatus.Ringing, true)]
    [InlineData(CallStatus.Ringing, CallParticipantStatus.Joined, true)]
    [InlineData(CallStatus.Active, CallParticipantStatus.Joined, true)]
    [InlineData(CallStatus.Active, CallParticipantStatus.Invited, false)]
    [InlineData(CallStatus.Active, CallParticipantStatus.Left, false)]
    [InlineData(CallStatus.Active, CallParticipantStatus.Declined, false)]
    [InlineData(CallStatus.Active, CallParticipantStatus.Missed, false)]
    [InlineData(CallStatus.Ended, CallParticipantStatus.Joined, false)]
    [InlineData(CallStatus.Missed, CallParticipantStatus.Ringing, false)]
    public void Busy_means_ringing_or_joined_in_an_unfinished_call(CallStatus callStatus, CallParticipantStatus participantStatus, bool busy)
    {
        var otherChat = Guid.CreateVersion7();
        var participant = new CallParticipant
        {
            UserId = Callee,
            Status = participantStatus,
            Call = new Call { ChatId = otherChat, Status = callStatus },
        };

        CallStateMachine.IsBusyElsewhere(Callee, ChatId).Compile()(participant).ShouldBe(busy);
    }

    [Fact]
    public void A_call_in_the_same_chat_or_of_someone_else_is_not_busy_elsewhere()
    {
        var sameChat = new CallParticipant
        {
            UserId = Callee,
            Status = CallParticipantStatus.Joined,
            Call = new Call { ChatId = ChatId, Status = CallStatus.Active },
        };
        var someoneElse = new CallParticipant
        {
            UserId = Alice,
            Status = CallParticipantStatus.Joined,
            Call = new Call { ChatId = Guid.CreateVersion7(), Status = CallStatus.Active },
        };

        var isBusy = CallStateMachine.IsBusyElsewhere(Callee, ChatId).Compile();
        isBusy(sameChat).ShouldBeFalse();
        isBusy(someoneElse).ShouldBeFalse();
    }

    // ── accept / decline ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Accept_by_the_callee_makes_the_call_active()
    {
        var call = Ringing();

        var outcome = CallStateMachine.Accept(call, Callee, T0.AddSeconds(3));

        outcome.ShouldBe(CallOutcome.Updated);
        call.Status.ShouldBe(CallStatus.Active);
        call.AnsweredAt.ShouldBe(T0.AddSeconds(3));
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Joined);
        CallStateMachine.Participant(call, Callee)!.JoinedAt.ShouldBe(T0.AddSeconds(3));
    }

    [Fact]
    public void Accept_by_the_caller_only_rejoins_and_keeps_ringing()
    {
        var call = Ringing();

        CallStateMachine.Accept(call, Caller, T0.AddSeconds(3));

        call.Status.ShouldBe(CallStatus.Ringing);
        call.AnsweredAt.ShouldBeNull();
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Ringing);
    }

    [Fact]
    public void Accept_on_another_device_keeps_the_first_answer_time()
    {
        var call = Answered(T0.AddSeconds(5));

        CallStateMachine.Accept(call, Callee, T0.AddSeconds(9));

        call.Status.ShouldBe(CallStatus.Active);
        call.AnsweredAt.ShouldBe(T0.AddSeconds(5));
    }

    [Fact]
    public void Accepting_a_finished_call_conflicts_with_call_ended()
    {
        var call = Ringing();
        CallStateMachine.Hangup(call, Caller, null, T0.AddSeconds(1));

        var error = Should.Throw<ConflictException>(() => CallStateMachine.Accept(call, Callee, T0.AddSeconds(2)));

        error.Code.ShouldBe(ErrorCodes.CallEnded);
    }

    [Theory]
    [InlineData(false, CallStatus.Declined)]
    [InlineData(true, CallStatus.Busy)]
    public void Callee_declining_ends_the_call(bool busy, CallStatus expected)
    {
        var call = Ringing();

        var outcome = CallStateMachine.Decline(call, Callee, busy, T0.AddSeconds(2));

        outcome.ShouldBe(CallOutcome.Ended);
        call.Status.ShouldBe(expected);
        call.EndedAt.ShouldBe(T0.AddSeconds(2));
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Declined);
        StatusOf(call, Caller).ShouldBe(CallParticipantStatus.Left);
        CallStateMachine.DurationSeconds(call.AnsweredAt, call.EndedAt).ShouldBe(0);
    }

    [Fact]
    public void Declining_an_answered_call_from_another_device_changes_nothing()
    {
        var call = Answered();

        CallStateMachine.Decline(call, Callee, busy: true, T0.AddSeconds(8)).ShouldBe(CallOutcome.Unchanged);

        call.Status.ShouldBe(CallStatus.Active);
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Joined);
    }

    [Fact]
    public void The_caller_cannot_decline_and_repeated_declines_are_no_ops()
    {
        var call = Ringing();

        CallStateMachine.Decline(call, Caller, busy: false, T0.AddSeconds(1)).ShouldBe(CallOutcome.Unchanged);
        call.Status.ShouldBe(CallStatus.Ringing);

        CallStateMachine.Decline(call, Callee, busy: false, T0.AddSeconds(2)).ShouldBe(CallOutcome.Ended);
        CallStateMachine.Decline(call, Callee, busy: true, T0.AddSeconds(3)).ShouldBe(CallOutcome.Unchanged);
        call.Status.ShouldBe(CallStatus.Declined);
    }

    // ── hangup ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Caller_hanging_up_while_ringing_cancels_and_the_callee_missed_it()
    {
        var call = Ringing();

        var outcome = CallStateMachine.Hangup(call, Caller, null, T0.AddSeconds(4));

        outcome.ShouldBe(CallOutcome.Ended);
        call.Status.ShouldBe(CallStatus.Cancelled);
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Missed);
        StatusOf(call, Caller).ShouldBe(CallParticipantStatus.Left);
    }

    [Fact]
    public void Callee_hanging_up_while_ringing_declines()
    {
        var call = Ringing();

        CallStateMachine.Hangup(call, Callee, null, T0.AddSeconds(4)).ShouldBe(CallOutcome.Ended);

        call.Status.ShouldBe(CallStatus.Declined);
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Declined);
    }

    [Fact]
    public void Hanging_up_an_answered_call_ends_it_for_both_with_its_duration()
    {
        var call = Answered(T0.AddSeconds(5));

        CallStateMachine.Hangup(call, Callee, null, T0.AddSeconds(5 + 125.7)).ShouldBe(CallOutcome.Ended);

        call.Status.ShouldBe(CallStatus.Ended);
        call.Participants.ShouldAllBe(p => p.Status == CallParticipantStatus.Left && p.LeftAt == T0.AddSeconds(5 + 125.7));
        CallStateMachine.DurationSeconds(call.AnsweredAt, call.EndedAt).ShouldBe(125);
        CallMapping.ToCallInfo(call).DurationSeconds.ShouldBe(125);
    }

    [Fact]
    public void Hanging_up_a_finished_call_is_idempotent()
    {
        var call = Answered();
        CallStateMachine.Hangup(call, Caller, null, T0.AddSeconds(30));

        CallStateMachine.Hangup(call, Callee, null, T0.AddSeconds(31)).ShouldBe(CallOutcome.Unchanged);

        call.EndedAt.ShouldBe(T0.AddSeconds(30));
    }

    // ── ring timeout ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unanswered_call_becomes_missed_after_the_ring_timeout()
    {
        var call = Ringing();

        CallStateMachine.ExpireRinging(call, T0 + CallStateMachine.RingTimeout - TimeSpan.FromSeconds(1)).ShouldBe(CallOutcome.Unchanged);
        CallStateMachine.ExpireRinging(call, T0 + CallStateMachine.RingTimeout).ShouldBe(CallOutcome.Ended);

        call.Status.ShouldBe(CallStatus.Missed);
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Missed);
        StatusOf(call, Caller).ShouldBe(CallParticipantStatus.Left);
        CallStateMachine.RingTimeout.ShouldBe(TimeSpan.FromSeconds(Limits.CallRingTimeoutSeconds));
    }

    [Fact]
    public void Ring_timeout_does_not_touch_answered_calls()
    {
        var call = Answered();

        CallStateMachine.ExpireRinging(call, T0.AddMinutes(10)).ShouldBe(CallOutcome.Unchanged);

        call.Status.ShouldBe(CallStatus.Active);
    }

    [Fact]
    public void Group_invitations_stop_ringing_after_the_ring_timeout()
    {
        var call = Group();
        CallStateMachine.Decline(call, Bob, busy: false, T0.AddSeconds(2));

        CallStateMachine.ExpireInvites(call, T0.AddSeconds(10)).ShouldBe(CallOutcome.Unchanged);
        CallStateMachine.ExpireInvites(call, T0 + CallStateMachine.RingTimeout).ShouldBe(CallOutcome.Updated);

        StatusOf(call, Alice).ShouldBe(CallParticipantStatus.Missed);
        StatusOf(call, Bob).ShouldBe(CallParticipantStatus.Declined);
        call.Status.ShouldBe(CallStatus.Active);
    }

    // ── join ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Join_by_the_ringing_callee_answers_the_call()
    {
        var call = Ringing();

        CallStateMachine.Join(call, Callee, T0.AddSeconds(2));

        call.Status.ShouldBe(CallStatus.Active);
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Joined);
    }

    [Fact]
    public void Rejoining_a_private_call_after_the_device_dropped_marks_joined_again()
    {
        var call = Answered();
        CallStateMachine.DeviceLeft(call, Callee, userStillInRoom: false, T0.AddSeconds(20)).ShouldBe(CallOutcome.Updated);
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Left);

        CallStateMachine.Join(call, Callee, T0.AddSeconds(30)).ShouldBe(CallOutcome.Updated);

        var me = CallStateMachine.Participant(call, Callee)!;
        me.Status.ShouldBe(CallParticipantStatus.Joined);
        me.LeftAt.ShouldBeNull();
        me.JoinedAt.ShouldBe(T0.AddSeconds(30));
    }

    [Fact]
    public void Strangers_cannot_join_a_private_call()
    {
        var error = Should.Throw<NotFoundException>(() => CallStateMachine.Join(Answered(), Alice, T0.AddSeconds(30)));

        error.Code.ShouldBe(ErrorCodes.CallNotFound);
    }

    [Fact]
    public void A_member_who_was_not_invited_can_join_a_group_call()
    {
        var call = Group();
        var newcomer = Guid.CreateVersion7();

        CallStateMachine.Join(call, newcomer, T0.AddSeconds(30)).ShouldBe(CallOutcome.Updated);

        StatusOf(call, newcomer).ShouldBe(CallParticipantStatus.Joined);
        CallStateMachine.Participant(call, newcomer)!.CallId.ShouldBe(call.Id);
    }

    // ── group leave ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Group_decline_only_changes_my_invitation()
    {
        var call = Group();

        CallStateMachine.Decline(call, Alice, busy: true, T0.AddSeconds(2)).ShouldBe(CallOutcome.Updated);

        call.Status.ShouldBe(CallStatus.Active);
        StatusOf(call, Alice).ShouldBe(CallParticipantStatus.Declined);
        StatusOf(call, Bob).ShouldBe(CallParticipantStatus.Invited);
    }

    [Fact]
    public void Leaving_a_group_call_with_others_still_joined_keeps_it_going()
    {
        var call = Group();
        CallStateMachine.Join(call, Alice, T0.AddSeconds(3));

        CallStateMachine.Hangup(call, Caller, Users(Alice), T0.AddSeconds(60)).ShouldBe(CallOutcome.Updated);

        call.Status.ShouldBe(CallStatus.Active);
        StatusOf(call, Caller).ShouldBe(CallParticipantStatus.Left);
    }

    [Fact]
    public void The_last_one_out_ends_the_group_call()
    {
        var call = Group();
        CallStateMachine.Join(call, Alice, T0.AddSeconds(3));
        CallStateMachine.Hangup(call, Alice, Users(Caller), T0.AddSeconds(30));

        CallStateMachine.Hangup(call, Caller, Users(), T0.AddSeconds(60)).ShouldBe(CallOutcome.Ended);

        call.Status.ShouldBe(CallStatus.Ended);
        StatusOf(call, Bob).ShouldBe(CallParticipantStatus.Missed);
        CallStateMachine.DurationSeconds(call.AnsweredAt, call.EndedAt).ShouldBe(60);
    }

    [Fact]
    public void A_group_call_does_not_end_while_livekit_still_sees_people_in_the_room()
    {
        var call = Group();

        // Alice's join webhook has not arrived yet, but she is in the room.
        CallStateMachine.Hangup(call, Caller, Users(Alice), T0.AddSeconds(60)).ShouldBe(CallOutcome.Updated);

        call.Status.ShouldBe(CallStatus.Active);
    }

    [Fact]
    public void Hanging_up_on_one_device_while_another_device_stays_in_the_group_call_changes_nothing()
    {
        var call = Group();

        CallStateMachine.Hangup(call, Caller, Users(Caller), T0.AddSeconds(60)).ShouldBe(CallOutcome.Unchanged);

        StatusOf(call, Caller).ShouldBe(CallParticipantStatus.Joined);
    }

    [Fact]
    public void Removed_member_leaves_the_group_call_and_the_last_one_ends_it()
    {
        var call = Group();
        CallStateMachine.Join(call, Alice, T0.AddSeconds(3));

        CallStateMachine.RemoveMember(call, Alice, Users(Caller), T0.AddSeconds(10)).ShouldBe(CallOutcome.Updated);
        StatusOf(call, Alice).ShouldBe(CallParticipantStatus.Left);
        call.Status.ShouldBe(CallStatus.Active);

        CallStateMachine.RemoveMember(call, Caller, Users(), T0.AddSeconds(20)).ShouldBe(CallOutcome.Ended);
        call.Status.ShouldBe(CallStatus.Ended);
    }

    // ── devices and the grace rule ────────────────────────────────────────────────────────

    [Fact]
    public void A_device_leaving_marks_the_user_left_only_when_no_other_device_remains()
    {
        var call = Answered();

        CallStateMachine.DeviceLeft(call, Callee, userStillInRoom: true, T0.AddSeconds(20)).ShouldBe(CallOutcome.Unchanged);
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Joined);

        CallStateMachine.DeviceLeft(call, Callee, userStillInRoom: false, T0.AddSeconds(21)).ShouldBe(CallOutcome.Updated);
        StatusOf(call, Callee).ShouldBe(CallParticipantStatus.Left);
        call.Status.ShouldBe(CallStatus.Active);
    }

    [Fact]
    public void Grace_check_ends_the_private_call_when_no_device_of_the_user_came_back()
    {
        var call = Answered();
        CallStateMachine.DeviceLeft(call, Callee, userStillInRoom: false, T0.AddSeconds(20));
        var now = T0.AddSeconds(20) + CallStateMachine.GracePeriod;

        CallStateMachine.EvaluateGrace(call, Callee, Users(Caller), now).ShouldBe(GraceDecision.End);
        CallStateMachine.VanishedStatus(call).ShouldBe(CallStatus.Ended);
    }

    [Fact]
    public void Grace_check_keeps_the_call_when_the_user_is_back_in_the_room()
    {
        var call = Answered();
        CallStateMachine.DeviceLeft(call, Callee, userStillInRoom: false, T0.AddSeconds(20));

        CallStateMachine.EvaluateGrace(call, Callee, Users(Caller, Callee), T0.AddSeconds(45)).ShouldBe(GraceDecision.Keep);
    }

    [Fact]
    public void Grace_check_retries_when_the_user_rejoined_moments_ago_and_is_still_connecting()
    {
        var call = Answered();
        CallStateMachine.DeviceLeft(call, Callee, userStillInRoom: false, T0.AddSeconds(20));
        CallStateMachine.Join(call, Callee, T0.AddSeconds(40));

        CallStateMachine.EvaluateGrace(call, Callee, Users(Caller), T0.AddSeconds(45)).ShouldBe(GraceDecision.Retry);
        CallStateMachine.EvaluateGrace(call, Callee, Users(Caller), T0.AddSeconds(40) + CallStateMachine.GracePeriod + TimeSpan.FromSeconds(1))
            .ShouldBe(GraceDecision.End);
    }

    [Fact]
    public void Grace_check_without_livekit_trusts_the_webhook_driven_state()
    {
        var call = Answered();
        CallStateMachine.DeviceLeft(call, Callee, userStillInRoom: false, T0.AddSeconds(20));

        CallStateMachine.EvaluateGrace(call, Callee, usersInRoom: null, T0.AddSeconds(45)).ShouldBe(GraceDecision.End);

        // A participant_joined arrived during the grace period.
        CallStateMachine.DeviceJoined(call, Callee, T0.AddSeconds(30)).ShouldBe(CallOutcome.Updated);
        CallStateMachine.EvaluateGrace(call, Callee, usersInRoom: null, T0.AddSeconds(45)).ShouldBe(GraceDecision.Keep);
    }

    [Fact]
    public void Grace_check_ignores_the_ringing_callee_but_ends_a_ringing_call_whose_caller_vanished()
    {
        var call = Ringing();

        CallStateMachine.EvaluateGrace(call, Callee, Users(Caller), T0.AddSeconds(30)).ShouldBe(GraceDecision.Keep);

        CallStateMachine.DeviceLeft(call, Caller, userStillInRoom: false, T0.AddSeconds(5));
        CallStateMachine.EvaluateGrace(call, Caller, Users(), T0.AddSeconds(30)).ShouldBe(GraceDecision.End);
        CallStateMachine.VanishedStatus(call).ShouldBe(CallStatus.Missed);
    }

    [Fact]
    public void Grace_check_never_ends_group_or_finished_calls()
    {
        var group = Group();
        CallStateMachine.EvaluateGrace(group, Caller, Users(), T0.AddMinutes(5)).ShouldBe(GraceDecision.Keep);

        var finished = Answered();
        CallStateMachine.Hangup(finished, Caller, null, T0.AddSeconds(30));
        CallStateMachine.EvaluateGrace(finished, Callee, Users(), T0.AddMinutes(5)).ShouldBe(GraceDecision.Keep);
    }

    [Fact]
    public void Device_joining_a_group_call_adds_members_who_had_no_row()
    {
        var call = Group();
        var latecomer = Guid.CreateVersion7();

        CallStateMachine.DeviceJoined(call, latecomer, T0.AddSeconds(10)).ShouldBe(CallOutcome.Updated);
        CallStateMachine.DeviceJoined(call, latecomer, T0.AddSeconds(11)).ShouldBe(CallOutcome.Unchanged);

        StatusOf(call, latecomer).ShouldBe(CallParticipantStatus.Joined);
    }

    [Fact]
    public void Device_events_for_finished_calls_or_strangers_change_nothing()
    {
        var call = Answered();
        CallStateMachine.DeviceJoined(call, Alice, T0.AddSeconds(10)).ShouldBe(CallOutcome.Unchanged);
        CallStateMachine.Participant(call, Alice).ShouldBeNull();

        CallStateMachine.Hangup(call, Caller, null, T0.AddSeconds(20));
        CallStateMachine.DeviceJoined(call, Callee, T0.AddSeconds(21)).ShouldBe(CallOutcome.Unchanged);
        CallStateMachine.DeviceLeft(call, Callee, userStillInRoom: false, T0.AddSeconds(22)).ShouldBe(CallOutcome.Unchanged);
    }

    // ── reconciliation ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reconcile_ends_a_call_whose_room_stays_empty_longer_than_the_absence_timeout()
    {
        var call = Group();
        var tracker = new CallPresenceTracker();
        var start = T0.AddMinutes(5);

        CallStateMachine.Reconcile(call, Users(), tracker, start).ShouldBe(CallOutcome.Unchanged);
        CallStateMachine.Reconcile(call, Users(), tracker, start.AddSeconds(30)).ShouldBe(CallOutcome.Unchanged);
        CallStateMachine.Reconcile(call, Users(), tracker, start.AddSeconds(61)).ShouldBe(CallOutcome.Ended);

        call.Status.ShouldBe(CallStatus.Ended);
    }

    [Fact]
    public void Reconcile_gives_a_freshly_started_call_time_to_connect()
    {
        var call = Group();
        var tracker = new CallPresenceTracker();

        // First observed empty long ago (e.g. state from before), but the call itself only just started.
        tracker.FirstAbsent(call.Id, Guid.Empty, T0.AddMinutes(-10));

        CallStateMachine.Reconcile(call, Users(), tracker, T0.AddSeconds(30)).ShouldBe(CallOutcome.Unchanged);
        call.Status.ShouldBe(CallStatus.Active);
    }

    [Fact]
    public void Reconcile_resets_emptiness_once_someone_is_in_the_room()
    {
        var call = Group();
        var tracker = new CallPresenceTracker();
        var start = T0.AddMinutes(5);

        CallStateMachine.Reconcile(call, Users(), tracker, start);
        CallStateMachine.Reconcile(call, Users(Caller), tracker, start.AddSeconds(30));

        CallStateMachine.Reconcile(call, Users(), tracker, start.AddSeconds(61)).ShouldBe(CallOutcome.Unchanged);
    }

    [Fact]
    public void Reconcile_ends_a_private_call_when_one_side_stays_away()
    {
        var call = Answered(T0.AddSeconds(5));
        var tracker = new CallPresenceTracker();
        var start = T0.AddMinutes(5);

        CallStateMachine.Reconcile(call, Users(Caller), tracker, start).ShouldBe(CallOutcome.Unchanged);
        CallStateMachine.Reconcile(call, Users(Caller), tracker, start.AddSeconds(61)).ShouldBe(CallOutcome.Ended);

        call.Status.ShouldBe(CallStatus.Ended);
    }

    [Fact]
    public void Reconcile_marks_absent_group_members_left_and_present_ones_joined()
    {
        var call = Group();
        CallStateMachine.Join(call, Alice, T0.AddSeconds(2));
        var tracker = new CallPresenceTracker();
        var start = T0.AddMinutes(5);

        // Bob is in the room although his join webhook was lost; Alice dropped without a leave webhook.
        CallStateMachine.Reconcile(call, Users(Caller, Bob), tracker, start).ShouldBe(CallOutcome.Updated);
        StatusOf(call, Bob).ShouldBe(CallParticipantStatus.Joined);
        StatusOf(call, Alice).ShouldBe(CallParticipantStatus.Joined);

        CallStateMachine.Reconcile(call, Users(Caller, Bob), tracker, start.AddSeconds(61)).ShouldBe(CallOutcome.Updated);
        StatusOf(call, Alice).ShouldBe(CallParticipantStatus.Left);
        call.Status.ShouldBe(CallStatus.Active);
    }

    [Fact]
    public void Reconcile_ignores_calls_that_are_not_active()
    {
        var call = Ringing();

        CallStateMachine.Reconcile(call, Users(), new CallPresenceTracker(), T0.AddMinutes(10)).ShouldBe(CallOutcome.Unchanged);

        call.Status.ShouldBe(CallStatus.Ringing);
    }

    // ── mapping ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dto_lists_the_initiator_first()
    {
        var call = Group();
        call.Participants.Reverse();

        var dto = CallMapping.ToDto(call);

        dto.Participants[0].UserId.ShouldBe(Caller);
        dto.Participants.Count.ShouldBe(3);
        dto.Status.ShouldBe(CallStatus.Active);
        dto.Kind.ShouldBe(CallKind.Group);
    }

    [Theory]
    [InlineData(null, 10.0, 0)]
    [InlineData(10.0, null, 0)]
    [InlineData(10.0, 5.0, 0)]
    [InlineData(0.0, 59.9, 59)]
    public void Duration_is_whole_seconds_between_answer_and_end(double? answered, double? ended, int expected)
    {
        DateTimeOffset? At(double? seconds) => seconds is { } s ? T0.AddSeconds(s) : null;

        CallStateMachine.DurationSeconds(At(answered), At(ended)).ShouldBe(expected);
    }
}
