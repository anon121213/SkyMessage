using System.Collections.Concurrent;
using System.Linq.Expressions;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Calls;

internal enum CallOutcome
{
    /// <summary>Nothing changed: no save, no events.</summary>
    Unchanged,
    /// <summary>The call goes on with a changed state: save, then CallUpdated.</summary>
    Updated,
    /// <summary>The call finished with this change: save, then CallUpdated, call log / GroupCallEnded, room cleanup.</summary>
    Ended,
}

internal enum GraceDecision
{
    Keep,
    /// <summary>The user re-joined through the API moments ago and may still be connecting: check again later.</summary>
    Retry,
    End,
}

/// <summary>
/// The call state machine as pure functions over a loaded <see cref="Call"/> (participants included). Callers lock
/// the call row, apply one transition, save, commit and then publish according to the returned outcome.
/// <para>
/// Private: Ringing (caller Joined, callee Ringing) → accept → Active → hangup → Ended; callee declines → Declined
/// or Busy; caller hangs up while ringing → Cancelled; nobody answers / the caller vanished → Missed.
/// Group: Active from the start (initiator Joined, other members Invited); members join and leave; the last one
/// out ends it.
/// </para>
/// </summary>
internal static class CallStateMachine
{
    public static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(Limits.CallRingTimeoutSeconds);

    /// <summary>How long a private call survives a device dropping out of the room before it is ended.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(25);

    /// <summary>Reconciliation: absent from the room (or an empty room) for longer than this → left / ended.</summary>
    public static readonly TimeSpan AbsenceTimeout = TimeSpan.FromSeconds(60);

    public static readonly Expression<Func<Call, bool>> IsUnfinishedCall =
        c => c.Status == CallStatus.Ringing || c.Status == CallStatus.Active;

    public static bool IsUnfinished(CallStatus status) => status is CallStatus.Ringing or CallStatus.Active;

    /// <summary>
    /// "Busy" = ringing for or taking part in an unfinished call in another chat. Invited (group calls nobody
    /// answered yet), left, declined or missed participants are not busy. EF-translatable.
    /// </summary>
    public static Expression<Func<CallParticipant, bool>> IsBusyElsewhere(Guid userId, Guid chatId) =>
        p => p.UserId == userId
             && (p.Status == CallParticipantStatus.Ringing || p.Status == CallParticipantStatus.Joined)
             && (p.Call!.Status == CallStatus.Ringing || p.Call.Status == CallStatus.Active)
             && p.Call.ChatId != chatId;

    public static Call CreatePrivate(Guid chatId, Guid callerId, Guid calleeId, bool isVideo, DateTimeOffset now)
    {
        var call = NewCall(chatId, CallKind.Private, callerId, isVideo, CallStatus.Ringing, now);
        call.Participants.Add(NewJoined(call.Id, callerId, now));
        call.Participants.Add(new CallParticipant
        {
            CallId = call.Id,
            UserId = calleeId,
            Status = CallParticipantStatus.Ringing,
            InvitedAt = now,
        });
        return call;
    }

    public static Call CreateGroup(Guid chatId, Guid initiatorId, IEnumerable<Guid> memberIds, bool isVideo, DateTimeOffset now)
    {
        var call = NewCall(chatId, CallKind.Group, initiatorId, isVideo, CallStatus.Active, now);
        call.AnsweredAt = now;
        call.Participants.Add(NewJoined(call.Id, initiatorId, now));
        foreach (var memberId in memberIds.Distinct())
        {
            if (memberId == initiatorId) continue;
            call.Participants.Add(new CallParticipant
            {
                CallId = call.Id,
                UserId = memberId,
                Status = CallParticipantStatus.Invited,
                InvitedAt = now,
            });
        }
        return call;
    }

    /// <summary>The callee answers a ringing private call (also: answered on another device → re-join; group → join).</summary>
    public static CallOutcome Accept(Call call, Guid userId, DateTimeOffset now)
    {
        EnsureUnfinished(call);
        if (call.Kind == CallKind.Group || call.InitiatorId == userId)
            return Join(call, userId, now);

        var me = Participant(call, userId) ?? throw new NotFoundException(ErrorCodes.CallNotFound);
        if (call.Status == CallStatus.Ringing)
        {
            call.Status = CallStatus.Active;
            call.AnsweredAt = now;
        }
        MarkJoined(me, now);
        return CallOutcome.Updated;
    }

    /// <summary>
    /// Private: the callee refuses a ringing call (Declined, or Busy when declined automatically). Anything else
    /// (the caller, an answered call — e.g. accepted on another device) is a no-op. Group: only my invitation.
    /// </summary>
    public static CallOutcome Decline(Call call, Guid userId, bool busy, DateTimeOffset now)
    {
        if (call.IsFinished) return CallOutcome.Unchanged;
        var me = Participant(call, userId);
        if (me is null) return CallOutcome.Unchanged;

        if (call.Kind == CallKind.Group)
        {
            if (me.Status is not (CallParticipantStatus.Invited or CallParticipantStatus.Ringing)) return CallOutcome.Unchanged;
            me.Status = CallParticipantStatus.Declined;
            return CallOutcome.Updated;
        }

        if (call.Status != CallStatus.Ringing || call.InitiatorId == userId || me.Status != CallParticipantStatus.Ringing)
            return CallOutcome.Unchanged;
        return End(call, busy ? CallStatus.Busy : CallStatus.Declined, now);
    }

    /// <summary>Joins a group call, or re-joins an unfinished private call I take part in (the ringing callee answers).</summary>
    public static CallOutcome Join(Call call, Guid userId, DateTimeOffset now)
    {
        EnsureUnfinished(call);
        var me = Participant(call, userId);

        if (call.Kind == CallKind.Group)
        {
            if (me is null) call.Participants.Add(NewJoined(call.Id, userId, now));
            else MarkJoined(me, now);
            return CallOutcome.Updated;
        }

        if (me is null) throw new NotFoundException(ErrorCodes.CallNotFound);
        if (call.Status == CallStatus.Ringing && call.InitiatorId != userId)
            return Accept(call, userId, now);
        MarkJoined(me, now);
        return CallOutcome.Updated;
    }

    /// <summary>
    /// Private: caller while ringing → Cancelled, callee while ringing → Declined, answered → Ended.
    /// Group: leave (an unanswered invitation counts as declined); the last one out ends the call.
    /// </summary>
    /// <param name="usersStillInRoom">
    /// Users LiveKit still sees in the room apart from the device hanging up; null when LiveKit could not be asked.
    /// </param>
    public static CallOutcome Hangup(Call call, Guid userId, IReadOnlySet<Guid>? usersStillInRoom, DateTimeOffset now)
    {
        if (call.IsFinished) return CallOutcome.Unchanged;
        var me = Participant(call, userId);
        if (me is null) return CallOutcome.Unchanged;

        if (call.Kind == CallKind.Private)
        {
            var status = call.Status != CallStatus.Ringing ? CallStatus.Ended
                : call.InitiatorId == userId ? CallStatus.Cancelled
                : CallStatus.Declined;
            return End(call, status, now);
        }

        switch (me.Status)
        {
            case CallParticipantStatus.Invited or CallParticipantStatus.Ringing:
                me.Status = CallParticipantStatus.Declined;
                return CallOutcome.Updated;
            // Another device of mine is still in the room: I have not left.
            case CallParticipantStatus.Joined when usersStillInRoom?.Contains(userId) != true:
                me.Status = CallParticipantStatus.Left;
                me.LeftAt = now;
                return IsLastOneOut(call, usersStillInRoom) ? End(call, CallStatus.Ended, now) : CallOutcome.Updated;
            default:
                return CallOutcome.Unchanged;
        }
    }

    /// <summary>A member removed from the chat: marked Left in a group call (last one out ends it); a private call ends.</summary>
    public static CallOutcome RemoveMember(Call call, Guid userId, IReadOnlySet<Guid>? othersInRoom, DateTimeOffset now)
    {
        if (call.IsFinished) return CallOutcome.Unchanged;
        var me = Participant(call, userId);
        if (me is null) return CallOutcome.Unchanged;
        if (call.Kind == CallKind.Private) return Hangup(call, userId, null, now);
        if (me.Status is CallParticipantStatus.Left or CallParticipantStatus.Declined) return CallOutcome.Unchanged;

        var wasJoined = me.Status == CallParticipantStatus.Joined;
        me.Status = CallParticipantStatus.Left;
        me.LeftAt = now;
        return wasJoined && IsLastOneOut(call, othersInRoom) ? End(call, CallStatus.Ended, now) : CallOutcome.Updated;
    }

    public static CallOutcome ExpireRinging(Call call, DateTimeOffset now) =>
        call.Status == CallStatus.Ringing && now - call.CreatedAt >= RingTimeout
            ? End(call, CallStatus.Missed, now)
            : CallOutcome.Unchanged;

    /// <summary>Group invitations nobody answered within the ring timeout stop ringing (they can still join later).</summary>
    public static CallOutcome ExpireInvites(Call call, DateTimeOffset now)
    {
        if (call.Kind != CallKind.Group || call.Status != CallStatus.Active) return CallOutcome.Unchanged;
        var changed = false;
        foreach (var p in call.Participants)
        {
            if (p.Status != CallParticipantStatus.Invited || now - p.InvitedAt < RingTimeout) continue;
            p.Status = CallParticipantStatus.Missed;
            changed = true;
        }
        return changed ? CallOutcome.Updated : CallOutcome.Unchanged;
    }

    /// <summary>
    /// LiveKit reported a device of the user in the room (webhook / reconciliation). The caller has already checked
    /// that the user may be there (chat member, active session).
    /// </summary>
    public static CallOutcome DeviceJoined(Call call, Guid userId, DateTimeOffset now)
    {
        if (call.IsFinished) return CallOutcome.Unchanged;
        var me = Participant(call, userId);
        if (me is null)
        {
            if (call.Kind != CallKind.Group) return CallOutcome.Unchanged;
            call.Participants.Add(NewJoined(call.Id, userId, now));
            return CallOutcome.Updated;
        }
        if (call.Kind == CallKind.Private && call.Status == CallStatus.Ringing && call.InitiatorId != userId)
            return Accept(call, userId, now);
        if (me.Status == CallParticipantStatus.Joined) return CallOutcome.Unchanged;
        MarkJoined(me, now);
        return CallOutcome.Updated;
    }

    /// <summary>A device of the user left the room; the user is marked Left only when no other device of theirs is still there.</summary>
    public static CallOutcome DeviceLeft(Call call, Guid userId, bool userStillInRoom, DateTimeOffset now)
    {
        if (call.IsFinished || userStillInRoom) return CallOutcome.Unchanged;
        var me = Participant(call, userId);
        if (me is null || me.Status != CallParticipantStatus.Joined) return CallOutcome.Unchanged;
        me.Status = CallParticipantStatus.Left;
        me.LeftAt = now;
        return CallOutcome.Updated;
    }

    /// <summary>
    /// The grace check after a device left a private call: end it unless some device of the user is back in the
    /// room. <paramref name="usersInRoom"/> is null when LiveKit could not be asked; the webhook-driven state
    /// decides then (a participant_joined in the meantime marked the user Joined again).
    /// </summary>
    public static GraceDecision EvaluateGrace(Call call, Guid userId, IReadOnlySet<Guid>? usersInRoom, DateTimeOffset now)
    {
        if (call.IsFinished || call.Kind != CallKind.Private) return GraceDecision.Keep;
        var me = Participant(call, userId);
        // Before answering, the callee is not expected in the room.
        if (me is null || (call.Status == CallStatus.Ringing && call.InitiatorId != userId)) return GraceDecision.Keep;

        if (usersInRoom is null)
            return me.Status == CallParticipantStatus.Joined ? GraceDecision.Keep : GraceDecision.End;
        if (usersInRoom.Contains(userId)) return GraceDecision.Keep;
        return me.Status == CallParticipantStatus.Joined && me.JoinedAt > now - GracePeriod
            ? GraceDecision.Retry
            : GraceDecision.End;
    }

    /// <summary>A private call whose participant vanished: unanswered → Missed (for the callee), answered → Ended.</summary>
    public static CallStatus VanishedStatus(Call call) => call.Status == CallStatus.Ringing ? CallStatus.Missed : CallStatus.Ended;

    /// <summary>
    /// Periodic comparison of an Active call with the users LiveKit sees in its room. An empty (or missing) room for
    /// longer than <see cref="AbsenceTimeout"/> ends the call; a private call also ends when one side is absent that
    /// long; in groups absent members are marked Left. Users present in the room are marked Joined.
    /// </summary>
    public static CallOutcome Reconcile(Call call, IReadOnlySet<Guid> usersInRoom, CallPresenceTracker tracker, DateTimeOffset now)
    {
        if (call.Status != CallStatus.Active) return CallOutcome.Unchanged;
        var activeSince = call.AnsweredAt ?? call.CreatedAt;

        if (usersInRoom.Count > 0)
            tracker.Present(call.Id, Guid.Empty);
        else if (IsOverdue(tracker.FirstAbsent(call.Id, Guid.Empty, now), activeSince, now))
            return End(call, CallStatus.Ended, now);

        var changed = false;
        foreach (var p in call.Participants)
        {
            if (usersInRoom.Contains(p.UserId))
            {
                tracker.Present(call.Id, p.UserId);
                if (p.Status == CallParticipantStatus.Joined) continue;
                MarkJoined(p, now);
                changed = true;
                continue;
            }
            if (call.Kind == CallKind.Group && p.Status != CallParticipantStatus.Joined) continue;
            if (!IsOverdue(tracker.FirstAbsent(call.Id, p.UserId, now), p.JoinedAt ?? activeSince, now)) continue;

            if (call.Kind == CallKind.Private) return End(call, CallStatus.Ended, now);
            p.Status = CallParticipantStatus.Left;
            p.LeftAt = now;
            tracker.Present(call.Id, p.UserId);
            changed = true;
        }
        return changed ? CallOutcome.Updated : CallOutcome.Unchanged;
    }

    /// <summary>
    /// Finishes the call: joined participants leave; ringing/invited ones become Declined (Declined/Busy) or Missed.
    /// </summary>
    public static CallOutcome End(Call call, CallStatus status, DateTimeOffset now)
    {
        if (IsUnfinished(status)) throw new ArgumentOutOfRangeException(nameof(status), status, "Not a final call status.");
        if (call.IsFinished) return CallOutcome.Unchanged;

        call.Status = status;
        call.EndedAt = now;
        var refused = status is CallStatus.Declined or CallStatus.Busy;
        foreach (var p in call.Participants)
        {
            if (p.Status == CallParticipantStatus.Joined)
            {
                p.Status = CallParticipantStatus.Left;
                p.LeftAt = now;
            }
            else if (p.Status is CallParticipantStatus.Ringing or CallParticipantStatus.Invited)
            {
                p.Status = refused ? CallParticipantStatus.Declined : CallParticipantStatus.Missed;
            }
        }
        return CallOutcome.Ended;
    }

    public static int DurationSeconds(DateTimeOffset? answeredAt, DateTimeOffset? endedAt) =>
        answeredAt is { } start && endedAt is { } end && end > start
            ? (int)Math.Min(int.MaxValue, Math.Floor((end - start).TotalSeconds))
            : 0;

    public static CallParticipant? Participant(Call call, Guid userId) =>
        call.Participants.FirstOrDefault(p => p.UserId == userId);

    private static bool IsLastOneOut(Call call, IReadOnlySet<Guid>? usersInRoom) =>
        call.Participants.All(p => p.Status != CallParticipantStatus.Joined) && (usersInRoom is null || usersInRoom.Count == 0);

    private static bool IsOverdue(DateTimeOffset firstSeenAbsent, DateTimeOffset notBefore, DateTimeOffset now) =>
        now - (firstSeenAbsent > notBefore ? firstSeenAbsent : notBefore) > AbsenceTimeout;

    private static void EnsureUnfinished(Call call)
    {
        if (call.IsFinished) throw new ConflictException(ErrorCodes.CallEnded);
    }

    /// <summary>Joined "now" also on a re-join, so a device that is still connecting gets its grace time.</summary>
    private static void MarkJoined(CallParticipant participant, DateTimeOffset now)
    {
        participant.Status = CallParticipantStatus.Joined;
        participant.JoinedAt = now;
        participant.LeftAt = null;
    }

    private static CallParticipant NewJoined(Guid callId, Guid userId, DateTimeOffset now) => new()
    {
        CallId = callId,
        UserId = userId,
        Status = CallParticipantStatus.Joined,
        InvitedAt = now,
        JoinedAt = now,
    };

    private static Call NewCall(Guid chatId, CallKind kind, Guid initiatorId, bool isVideo, CallStatus status, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        return new Call
        {
            Id = id,
            ChatId = chatId,
            Kind = kind,
            IsVideo = isVideo,
            Status = status,
            InitiatorId = initiatorId,
            RoomName = LiveKitNames.RoomName(id),
            CreatedAt = now,
        };
    }
}

/// <summary>
/// In-memory "first seen absent" times for reconciliation, per (call, user) and per room (<see cref="Guid.Empty"/>).
/// Losing it (restart) only delays reconciliation by one absence timeout. Singleton.
/// </summary>
internal sealed class CallPresenceTracker
{
    private readonly ConcurrentDictionary<(Guid CallId, Guid UserId), DateTimeOffset> _absentSince = new();
    private int _liveKitUnreachable;

    public DateTimeOffset FirstAbsent(Guid callId, Guid userId, DateTimeOffset now) =>
        _absentSince.GetOrAdd((callId, userId), now);

    public void Present(Guid callId, Guid userId) => _absentSince.TryRemove((callId, userId), out _);

    /// <summary>Forgets everything about calls that are no longer active.</summary>
    public void Retain(IReadOnlyCollection<Guid> activeCallIds)
    {
        var keep = activeCallIds as IReadOnlySet<Guid> ?? activeCallIds.ToHashSet();
        foreach (var key in _absentSince.Keys)
        {
            if (!keep.Contains(key.CallId)) _absentSince.TryRemove(key, out _);
        }
    }

    /// <summary>Records LiveKit reachability; true when it changed (so transitions are logged, not every tick).</summary>
    public bool SetLiveKitReachable(bool reachable)
    {
        var value = reachable ? 0 : 1;
        return Interlocked.Exchange(ref _liveKitUnreachable, value) != value;
    }
}
