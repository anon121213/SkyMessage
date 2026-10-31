using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Calls;

internal sealed record CallMutation(Call Call, CallOutcome Outcome);

/// <summary>
/// Shared call mechanics: row-locked transitions, post-commit side effects (realtime, push, call logs, group
/// service messages, LiveKit room cleanup) and the transitions LiveKit drives (webhooks, grace checks,
/// reconciliation, removed members). Scoped; side effects run after commit and never fail the caller.
/// </summary>
internal sealed class CallLifecycle(
    IAppDbContext db,
    IClock clock,
    IRealtimeNotifier notifier,
    IEnumerable<IPushNotificationService> pushServices,
    ISystemMessageService systemMessages,
    ISessionValidator sessions,
    ILiveKitRoomService liveKit,
    ICallGraceScheduler graceScheduler,
    ILogger<CallLifecycle> logger) : ICallGraceHandler
{
    /// <summary>Presence lookups on request paths must not hang on a stuck LiveKit.</summary>
    private static readonly TimeSpan PresenceLookupTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Locks the call row (serializing API calls, webhooks and background checks of one call), applies the
    /// transition, saves and commits, then publishes. Null when the call does not exist.
    /// </summary>
    public async Task<CallMutation?> MutateAsync(
        Guid callId,
        Func<Call, DateTimeOffset, CallOutcome> transition,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? alsoNotify = null)
    {
        Call call;
        CallOutcome outcome;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var locked = await db.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM calls WHERE id = {callId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            if (locked.Count == 0) return null;

            call = await db.Calls.Include(c => c.Participants).SingleAsync(c => c.Id == callId, cancellationToken);
            var known = call.Participants.Select(p => p.UserId).ToHashSet();
            outcome = transition(call, clock.UtcNow);
            if (outcome != CallOutcome.Unchanged)
            {
                foreach (var participant in call.Participants)
                {
                    if (!known.Contains(participant.UserId)) db.CallParticipants.Add(participant);
                }
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            // Also after a refused transition: scopes of background jobs reuse this context for other calls.
            db.ChangeTracker.Clear();
        }

        await PublishAsync(call, outcome, alsoNotify);
        return new CallMutation(call, outcome);
    }

    /// <summary>Events for a freshly created (committed) call.</summary>
    public async Task PublishStartedAsync(Call call)
    {
        logger.LogInformation("Call {CallId} ({Kind}) started in chat {ChatId}", call.Id, call.Kind, call.ChatId);
        var dto = CallMapping.ToDto(call);

        if (call.Kind == CallKind.Private)
        {
            var callees = call.Participants.Where(p => p.UserId != call.InitiatorId).Select(p => p.UserId).ToList();
            await SendAsync(callees, nameof(IRealtimeClient.IncomingCall), client => client.IncomingCall(dto));
            await SendAsync([call.InitiatorId], nameof(IRealtimeClient.CallUpdated), client => client.CallUpdated(dto));
            await PushAsync("incoming call", call.Id, push => push.NotifyIncomingCallAsync(dto, callees, CancellationToken.None));
            return;
        }

        var members = await MemberIdsAsync(call);
        var others = members.Where(id => id != call.InitiatorId).ToList();
        await SendAsync(others, nameof(IRealtimeClient.IncomingCall), client => client.IncomingCall(dto));
        await SendAsync([call.InitiatorId], nameof(IRealtimeClient.CallUpdated), client => client.CallUpdated(dto));
        await PublishActiveCallAsync(call.ChatId, call.Id, members);
        await PushAsync("incoming call", call.Id, push => push.NotifyIncomingCallAsync(dto, others, CancellationToken.None));

        var started = await PostSystemEventAsync(call.ChatId, new SystemEventDto
        {
            Type = SystemEventType.GroupCallStarted,
            ActorId = call.InitiatorId,
            CallId = call.Id,
        });
        if (started is not null) await LinkMessageAsync(call.Id, started.Id);
    }

    /// <summary>Events for a committed transition: CallUpdated; when it ended also push, call log / GroupCallEnded and room cleanup.</summary>
    public async Task PublishAsync(Call call, CallOutcome outcome, IReadOnlyCollection<Guid>? alsoNotify = null)
    {
        if (outcome == CallOutcome.Unchanged) return;

        var dto = CallMapping.ToDto(call);
        // Group: CallUpdated goes to all members; alsoNotify adds people who are not (or no longer) members.
        var members = call.Kind == CallKind.Group ? await MemberIdsAsync(call) : null;
        var recipients = (members ?? call.Participants.Select(p => p.UserId)).ToHashSet();
        if (alsoNotify is not null) recipients.UnionWith(alsoNotify);
        await SendAsync(recipients, nameof(IRealtimeClient.CallUpdated), client => client.CallUpdated(dto));
        if (outcome != CallOutcome.Ended) return;

        logger.LogInformation("Call {CallId} in chat {ChatId} finished: {Status}", call.Id, call.ChatId, call.Status);
        await PushAsync("call ended", call.Id, push => push.NotifyCallEndedAsync(dto, recipients, CancellationToken.None));

        if (call.Kind == CallKind.Private)
        {
            var log = await PostCallLogAsync(call);
            if (log is not null) await LinkMessageAsync(call.Id, log.Id);
        }
        else
        {
            // Chat state is for members only (a member removed just now is told about the call, not the chat).
            await PublishActiveCallAsync(call.ChatId, null, members!);
            await PostSystemEventAsync(call.ChatId, new SystemEventDto
            {
                Type = SystemEventType.GroupCallEnded,
                CallId = call.Id,
            });
        }

        CloseRoomInBackground(call.RoomName);
    }

    public async Task OnParticipantJoinedAsync(string roomName, string identity, CancellationToken cancellationToken)
    {
        if (!LiveKitNames.TryParseRoomName(roomName, out var callId)) return;
        if (!LiveKitNames.TryParseIdentity(identity, out var userId, out var sessionId)) return;

        var call = await db.Calls.AsNoTracking()
            .Where(c => c.Id == callId)
            .Select(c => new { c.ChatId, c.Status, c.RoomName })
            .FirstOrDefaultAsync(cancellationToken);
        if (call is null || call.RoomName != roomName || !CallStateMachine.IsUnfinished(call.Status))
        {
            // Someone with a still-valid token entered the room of a finished call: close it again.
            CloseRoomInBackground(roomName);
            return;
        }

        // Tokens outlive memberships and sessions by up to their TTL: re-check both on entry.
        var allowed = await db.ChatMembers.AsNoTracking().AnyAsync(m => m.ChatId == call.ChatId && m.UserId == userId, cancellationToken)
                      && await sessions.IsActiveAsync(sessionId, userId, cancellationToken);
        if (!allowed)
        {
            logger.LogInformation("Removing user {UserId} (session {SessionId}) from call {CallId}: not allowed in it", userId, sessionId, callId);
            KickInBackground(roomName, [identity]);
            return;
        }

        var result = await MutateAsync(callId, (c, now) => CallStateMachine.DeviceJoined(c, userId, now), cancellationToken);
        if (result is null || result.Call.IsFinished) CloseRoomInBackground(roomName);
        else if (CallStateMachine.Participant(result.Call, userId) is null) KickInBackground(roomName, [identity]);
    }

    public async Task OnParticipantLeftAsync(string roomName, string identity, string? participantSid, CancellationToken cancellationToken)
    {
        if (!LiveKitNames.TryParseRoomName(roomName, out var callId)) return;
        if (!LiveKitNames.TryParseIdentity(identity, out var userId, out _)) return;

        var unfinished = await db.Calls.AsNoTracking()
            .Where(c => c.Id == callId && c.RoomName == roomName)
            .Where(CallStateMachine.IsUnfinishedCall)
            .AnyAsync(cancellationToken);
        if (!unfinished) return;

        // Another device of the user, or this one reconnected under a new sid, may still be in the room.
        var present = await TryGetUsersInRoomAsync(roomName, p => participantSid is not null && p.Sid == participantSid, cancellationToken);
        var stillThere = present?.Contains(userId) == true;
        var result = await MutateAsync(callId, (c, now) => CallStateMachine.DeviceLeft(c, userId, stillThere, now), cancellationToken);
        if (result is null || stillThere || result.Call.IsFinished || result.Call.Kind != CallKind.Private) return;

        graceScheduler.Schedule(callId, userId, CallStateMachine.GracePeriod);
    }

    public async Task OnRoomFinishedAsync(string roomName, CancellationToken cancellationToken)
    {
        if (!LiveKitNames.TryParseRoomName(roomName, out var callId)) return;
        await MutateAsync(callId, (c, now) => c.RoomName == roomName
            ? CallStateMachine.End(c, CallStateMachine.VanishedStatus(c), now)
            : CallOutcome.Unchanged, cancellationToken);
    }

    public async Task<TimeSpan?> RunGraceCheckAsync(Guid callId, Guid userId, CancellationToken cancellationToken = default)
    {
        var roomName = await db.Calls.AsNoTracking()
            .Where(c => c.Id == callId)
            .Where(CallStateMachine.IsUnfinishedCall)
            .Select(c => c.RoomName)
            .FirstOrDefaultAsync(cancellationToken);
        if (roomName is null) return null;

        var usersInRoom = await TryGetUsersInRoomAsync(roomName, null, cancellationToken);
        var retry = false;
        await MutateAsync(callId, (call, now) =>
        {
            switch (CallStateMachine.EvaluateGrace(call, userId, usersInRoom, now))
            {
                case GraceDecision.End:
                    return CallStateMachine.End(call, CallStateMachine.VanishedStatus(call), now);
                case GraceDecision.Retry:
                    retry = true;
                    return CallOutcome.Unchanged;
                default:
                    // Back in the room although no participant_joined arrived (yet).
                    return usersInRoom?.Contains(userId) == true
                        ? CallStateMachine.DeviceJoined(call, userId, now)
                        : CallOutcome.Unchanged;
            }
        }, cancellationToken);
        return retry ? CallStateMachine.GracePeriod : null;
    }

    /// <summary>
    /// The chat is about to be deleted (its calls cascade away): end its unfinished calls now so participants get
    /// CallUpdated and the LiveKit room closes, instead of talking on in a room nobody tracks.
    /// </summary>
    public async Task OnChatDeletingAsync(Guid chatId, CancellationToken cancellationToken)
    {
        var callIds = await db.Calls.AsNoTracking()
            .Where(c => c.ChatId == chatId)
            .Where(CallStateMachine.IsUnfinishedCall)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        foreach (var callId in callIds)
        {
            await MutateAsync(callId,
                (c, now) => CallStateMachine.End(c, c.Status == CallStatus.Ringing ? CallStatus.Cancelled : CallStatus.Ended, now),
                cancellationToken);
        }
    }

    /// <summary>A member was removed from the chat: drop them from its unfinished call and disconnect all their devices.</summary>
    public async Task OnMemberRemovedAsync(Guid chatId, Guid userId, CancellationToken cancellationToken)
    {
        var call = await db.Calls.AsNoTracking()
            .Where(c => c.ChatId == chatId)
            .Where(CallStateMachine.IsUnfinishedCall)
            .Select(c => new { c.Id, c.RoomName })
            .FirstOrDefaultAsync(cancellationToken);
        if (call is null) return;

        var participants = await TryListParticipantsAsync(call.RoomName, cancellationToken);
        var othersInRoom = participants is null ? null : UsersIn(participants, p => LiveKitNames.BelongsTo(p.Identity, userId));
        await MutateAsync(call.Id, (c, now) => CallStateMachine.RemoveMember(c, userId, othersInRoom, now), cancellationToken, alsoNotify: [userId]);

        var identities = participants?.Where(p => LiveKitNames.BelongsTo(p.Identity, userId)).Select(p => p.Identity).ToList();
        if (identities is { Count: > 0 }) KickInBackground(call.RoomName, identities);
    }

    /// <summary>Users with a connected device in the room, optionally ignoring some devices; null when LiveKit could not be asked.</summary>
    public async Task<IReadOnlySet<Guid>?> TryGetUsersInRoomAsync(
        string roomName,
        Func<LiveKitParticipantInfo, bool>? ignore,
        CancellationToken cancellationToken)
    {
        var participants = await TryListParticipantsAsync(roomName, cancellationToken);
        return participants is null ? null : UsersIn(participants, ignore);
    }

    public void KickInBackground(string roomName, IReadOnlyCollection<string> identities)
    {
        var rooms = liveKit;
        var log = logger;
        _ = Task.Run(async () =>
        {
            foreach (var identity in identities)
            {
                try
                {
                    await rooms.RemoveParticipantAsync(roomName, identity, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log.LogInformation("Participant could not be removed from LiveKit room {Room}: {Reason}", roomName, ex.Message);
                }
            }
        });
    }

    public void CloseRoomInBackground(string roomName)
    {
        var rooms = liveKit;
        var log = logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await rooms.DeleteRoomAsync(roomName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogInformation("LiveKit room {Room} could not be closed: {Reason}", roomName, ex.Message);
            }
        });
    }

    private async Task<IReadOnlyList<LiveKitParticipantInfo>?> TryListParticipantsAsync(string roomName, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PresenceLookupTimeout);
        try
        {
            return await liveKit.ListParticipantsAsync(roomName, timeout.Token);
        }
        catch (LiveKitUnavailableException ex)
        {
            logger.LogDebug("LiveKit participants of {Room} unknown: {Reason}", roomName, ex.Message);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("LiveKit participants of {Room} unknown: timeout", roomName);
            return null;
        }
    }

    private static HashSet<Guid> UsersIn(IEnumerable<LiveKitParticipantInfo> participants, Func<LiveKitParticipantInfo, bool>? ignore)
    {
        var users = new HashSet<Guid>();
        foreach (var participant in participants)
        {
            if (!participant.IsConnected || (ignore?.Invoke(participant) ?? false)) continue;
            if (LiveKitNames.TryParseIdentity(participant.Identity, out var userId, out _)) users.Add(userId);
        }
        return users;
    }

    /// <summary>Group recipients = all members; falls back to the participants when members cannot be read.</summary>
    private async Task<IReadOnlyList<Guid>> MemberIdsAsync(Call call)
    {
        try
        {
            return await db.ChatMembers.AsNoTracking()
                .Where(m => m.ChatId == call.ChatId)
                .Select(m => m.UserId)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Members of chat {ChatId} could not be read for call {CallId} events", call.ChatId, call.Id);
            return call.Participants.Select(p => p.UserId).ToList();
        }
    }

    private async Task PublishActiveCallAsync(Guid chatId, Guid? activeCallId, IReadOnlyCollection<Guid> members)
    {
        ChatInfoDto info;
        try
        {
            var chat = await db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId);
            if (chat is null) return;
            info = new ChatInfoDto
            {
                Id = chat.Id,
                Type = chat.Type,
                Title = chat.Title,
                Description = chat.Description,
                AvatarMediaId = chat.AvatarMediaId,
                MemberCount = chat.MemberCount,
                Permissions = CallMapping.ToDto(chat.Permissions),
                ActiveCallId = activeCallId,
                ProtectContent = chat.ProtectContent,
                ProtectContentBy = chat.ProtectContentBy,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat {ChatId} could not be read for ChatUpdated", chatId);
            return;
        }
        await SendAsync(members, nameof(IRealtimeClient.ChatUpdated), client => client.ChatUpdated(info));
    }

    private async Task<MessageDto?> PostCallLogAsync(Call call)
    {
        try
        {
            return await systemMessages.PostCallLogAsync(call.ChatId, CallMapping.ToCallInfo(call), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Call log for call {CallId} could not be posted in chat {ChatId}", call.Id, call.ChatId);
            return null;
        }
    }

    private async Task<MessageDto?> PostSystemEventAsync(Guid chatId, SystemEventDto systemEvent)
    {
        try
        {
            return await systemMessages.PostSystemEventAsync(chatId, systemEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "System message {Type} for call {CallId} could not be posted in chat {ChatId}", systemEvent.Type, systemEvent.CallId, chatId);
            return null;
        }
    }

    private async Task LinkMessageAsync(Guid callId, Guid messageId)
    {
        try
        {
            await db.Calls.Where(c => c.Id == callId).ExecuteUpdateAsync(s => s.SetProperty(c => c.MessageId, messageId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Message {MessageId} could not be linked to call {CallId}", messageId, callId);
        }
    }

    private async Task SendAsync(IReadOnlyCollection<Guid> userIds, string eventName, Func<IRealtimeClient, Task> send)
    {
        if (userIds.Count == 0) return;
        try
        {
            await send(userIds.Count == 1 ? notifier.User(userIds.First()) : notifier.Users(userIds));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Realtime event {Event} could not be published to {Count} user(s)", eventName, userIds.Count);
        }
    }

    private async Task PushAsync(string what, Guid callId, Func<IPushNotificationService, Task> send)
    {
        foreach (var push in pushServices)
        {
            try
            {
                await send(push);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Push ({What}) failed for call {CallId}", what, callId);
            }
        }
    }
}
