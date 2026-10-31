using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Calls;

/// <summary>1:1 and group call signaling for the REST API. <c>sessionId</c> parameters name the caller's device (LiveKit identity).</summary>
public interface ICallService
{
    /// <summary>
    /// Private chat: re-join my unfinished call there (answering it when the peer is calling me), else ring the peer
    /// (privacy → 403, peer busy → 409 call_busy). Group: join the active call or start one.
    /// </summary>
    Task<CallJoinInfoDto> StartAsync(Guid userId, Guid sessionId, StartCallRequest request, CancellationToken cancellationToken = default);

    Task<CallJoinInfoDto> AcceptAsync(Guid userId, Guid sessionId, Guid callId, CancellationToken cancellationToken = default);

    Task DeclineAsync(Guid userId, Guid callId, bool busy, CancellationToken cancellationToken = default);

    Task<CallJoinInfoDto> JoinAsync(Guid userId, Guid sessionId, Guid callId, CancellationToken cancellationToken = default);

    Task HangupAsync(Guid userId, Guid sessionId, Guid callId, CancellationToken cancellationToken = default);

    Task<CallDto> GetAsync(Guid userId, Guid callId, CancellationToken cancellationToken = default);

    /// <summary>The unfinished call of a chat I am a member of; null when there is none.</summary>
    Task<CallDto?> GetActiveAsync(Guid userId, Guid chatId, CancellationToken cancellationToken = default);

    /// <summary>My calls, newest first, created before <paramref name="before"/>.</summary>
    Task<IReadOnlyList<CallHistoryItemDto>> GetHistoryAsync(Guid userId, DateTimeOffset? before, int limit, CancellationToken cancellationToken = default);
}

public static class CallHistoryLimits
{
    public const int Default = 50;
    public const int Max = 100;
}

internal sealed class CallService(
    IAppDbContext db,
    IClock clock,
    IPrivacyService privacy,
    IUserSummaryService userSummaries,
    ILiveKitTokenService tokens,
    IOptions<LiveKitOptions> options,
    CallLifecycle lifecycle) : ICallService
{
    /// <summary>Namespace mixed into advisory lock keys so they cannot collide with other features' locks.</summary>
    private const long UserLockNamespace = 0x534B_5943_414C_4C53; // "SKYCALLS"

    public async Task<CallJoinInfoDto> StartAsync(Guid userId, Guid sessionId, StartCallRequest request, CancellationToken cancellationToken = default)
    {
        var chat = await db.Chats.AsNoTracking()
            .Where(c => c.Id == request.ChatId && !c.IsDeleted && c.Members.Any(m => m.UserId == userId))
            .Select(c => new { c.Id, c.Type })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.ChatNotFound);
        EnsureEnabled();

        return chat.Type switch
        {
            ChatType.Private => await StartPrivateAsync(userId, sessionId, chat.Id, request.IsVideo, cancellationToken),
            ChatType.Group => await StartGroupAsync(userId, sessionId, chat.Id, request.IsVideo, cancellationToken),
            _ => throw new BadRequestException(ErrorCodes.CannotTargetSelf, "Calls to Saved Messages are not possible."),
        };
    }

    public async Task<CallJoinInfoDto> AcceptAsync(Guid userId, Guid sessionId, Guid callId, CancellationToken cancellationToken = default)
    {
        await RequireVisibleCallAsync(userId, callId, cancellationToken);
        EnsureEnabled();
        var result = await lifecycle.MutateAsync(callId, (call, now) => CallStateMachine.Accept(call, userId, now), cancellationToken)
                     ?? throw new NotFoundException(ErrorCodes.CallNotFound);
        return await JoinInfoAsync(result.Call, userId, sessionId, cancellationToken);
    }

    public async Task DeclineAsync(Guid userId, Guid callId, bool busy, CancellationToken cancellationToken = default)
    {
        await RequireVisibleCallAsync(userId, callId, cancellationToken);
        await lifecycle.MutateAsync(callId, (call, now) => CallStateMachine.Decline(call, userId, busy, now), cancellationToken);
    }

    public async Task<CallJoinInfoDto> JoinAsync(Guid userId, Guid sessionId, Guid callId, CancellationToken cancellationToken = default)
    {
        await RequireVisibleCallAsync(userId, callId, cancellationToken);
        EnsureEnabled();
        var result = await lifecycle.MutateAsync(callId, (call, now) => CallStateMachine.Join(call, userId, now), cancellationToken)
                     ?? throw new NotFoundException(ErrorCodes.CallNotFound);
        return await JoinInfoAsync(result.Call, userId, sessionId, cancellationToken);
    }

    public async Task HangupAsync(Guid userId, Guid sessionId, Guid callId, CancellationToken cancellationToken = default)
    {
        var call = await RequireVisibleCallAsync(userId, callId, cancellationToken);

        // Leaving a group call must not end it while people (or my other devices) are still in the room even if
        // their webhook-driven state lags behind; this device itself is on its way out.
        IReadOnlySet<Guid>? othersInRoom = null;
        if (call.Kind == CallKind.Group && CallStateMachine.IsUnfinished(call.Status))
        {
            var myIdentity = LiveKitNames.Identity(userId, sessionId);
            othersInRoom = await lifecycle.TryGetUsersInRoomAsync(call.RoomName, p => p.Identity == myIdentity, cancellationToken);
        }

        await lifecycle.MutateAsync(callId, (c, now) => CallStateMachine.Hangup(c, userId, othersInRoom, now), cancellationToken);
    }

    public async Task<CallDto> GetAsync(Guid userId, Guid callId, CancellationToken cancellationToken = default)
    {
        var call = await db.Calls.AsNoTracking()
            .Include(c => c.Participants)
            .Where(c => c.Id == callId && c.Chat!.Members.Any(m => m.UserId == userId))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.CallNotFound);
        return CallMapping.ToDto(call);
    }

    public async Task<CallDto?> GetActiveAsync(Guid userId, Guid chatId, CancellationToken cancellationToken = default)
    {
        var isMember = await db.ChatMembers.AsNoTracking()
            .AnyAsync(m => m.ChatId == chatId && m.UserId == userId, cancellationToken);
        if (!isMember) throw new NotFoundException(ErrorCodes.ChatNotFound);

        var call = await db.Calls.AsNoTracking()
            .Include(c => c.Participants)
            .Where(c => c.ChatId == chatId)
            .Where(CallStateMachine.IsUnfinishedCall)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return call is null ? null : CallMapping.ToDto(call);
    }

    public async Task<IReadOnlyList<CallHistoryItemDto>> GetHistoryAsync(Guid userId, DateTimeOffset? before, int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, CallHistoryLimits.Max);
        var query = db.Calls.AsNoTracking()
            .Where(c => c.Participants.Any(p => p.UserId == userId) && c.Chat!.Members.Any(m => m.UserId == userId));
        if (before is { } cursor)
        {
            // Npgsql only writes UTC DateTimeOffsets to timestamptz; clients may send local time (e.g. +07:00).
            var utcCursor = cursor.ToUniversalTime();
            query = query.Where(c => c.CreatedAt < utcCursor);
        }

        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(limit)
            .Select(c => new
            {
                c.Id,
                c.ChatId,
                c.Kind,
                c.IsVideo,
                c.Status,
                c.InitiatorId,
                c.RoomName,
                c.CreatedAt,
                c.AnsweredAt,
                c.EndedAt,
                ChatType = c.Chat!.Type,
                ChatTitle = c.Chat.Title,
                ChatAvatarMediaId = c.Chat.AvatarMediaId,
                // Big groups: only the people who actually took part (and me), not every invited member.
                Participants = c.Participants
                    .Where(p => c.Kind == CallKind.Private
                                || p.UserId == userId
                                || p.Status == CallParticipantStatus.Joined
                                || p.Status == CallParticipantStatus.Left)
                    .Select(p => new CallParticipant
                    {
                        CallId = p.CallId,
                        UserId = p.UserId,
                        Status = p.Status,
                        InvitedAt = p.InvitedAt,
                        JoinedAt = p.JoinedAt,
                        LeftAt = p.LeftAt,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        var peerIds = rows
            .Where(r => r.Kind == CallKind.Private)
            .Select(r => r.Participants.FirstOrDefault(p => p.UserId != userId)?.UserId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        IReadOnlyDictionary<Guid, UserSummaryDto> peers = peerIds.Count == 0
            ? new Dictionary<Guid, UserSummaryDto>()
            : await userSummaries.GetAsync(userId, peerIds, cancellationToken);

        return rows.Select(r =>
        {
            var call = new Call
            {
                Id = r.Id,
                ChatId = r.ChatId,
                Kind = r.Kind,
                IsVideo = r.IsVideo,
                Status = r.Status,
                InitiatorId = r.InitiatorId,
                RoomName = r.RoomName,
                CreatedAt = r.CreatedAt,
                AnsweredAt = r.AnsweredAt,
                EndedAt = r.EndedAt,
                Participants = r.Participants,
            };
            UserSummaryDto? peer = null;
            if (r.Kind == CallKind.Private && r.Participants.FirstOrDefault(p => p.UserId != userId) is { } other)
                peers.TryGetValue(other.UserId, out peer);

            return new CallHistoryItemDto
            {
                Call = CallMapping.ToDto(call),
                ChatType = r.ChatType,
                ChatTitle = r.ChatType == ChatType.Group ? r.ChatTitle ?? "" : peer?.ContactName ?? peer?.DisplayName ?? "",
                ChatAvatarMediaId = r.ChatType == ChatType.Group ? r.ChatAvatarMediaId : peer?.AvatarMediaId,
                Peer = peer,
                IsOutgoing = r.InitiatorId == userId,
                DurationSeconds = CallStateMachine.DurationSeconds(r.AnsweredAt, r.EndedAt),
            };
        }).ToList();
    }

    private async Task<CallJoinInfoDto> StartPrivateAsync(Guid userId, Guid sessionId, Guid chatId, bool isVideo, CancellationToken cancellationToken)
    {
        var peerId = await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.UserId != userId)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.ChatNotFound);

        if (await FindUnfinishedCallAsync(chatId, cancellationToken) is { } existing)
            return await RejoinPrivateAsync(userId, sessionId, existing, cancellationToken);

        await EnsureCanCallAsync(userId, peerId, cancellationToken);

        Call? call = null;
        Guid? raced;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // One unfinished call per chat: concurrent starts (both sides pressing "call") serialize on the chat row.
            await ChatSequence.LockChatAsync(db, chatId, cancellationToken);
            raced = await FindUnfinishedCallAsync(chatId, cancellationToken);
            if (raced is null)
            {
                // Busy detection must not race with another caller ringing the same person.
                await LockUsersAsync([userId, peerId], cancellationToken);
                var busy = await db.CallParticipants.AsNoTracking()
                    .AnyAsync(CallStateMachine.IsBusyElsewhere(peerId, chatId), cancellationToken);
                if (busy) throw new ConflictException(ErrorCodes.CallBusy);

                call = CallStateMachine.CreatePrivate(chatId, userId, peerId, isVideo, clock.UtcNow);
                db.Calls.Add(call);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
        }
        db.ChangeTracker.Clear();

        if (raced is { } racedId)
            return await RejoinPrivateAsync(userId, sessionId, racedId, cancellationToken);

        await lifecycle.PublishStartedAsync(call!);
        return await JoinInfoAsync(call!, userId, sessionId, cancellationToken);
    }

    /// <summary>My own unfinished call in this private chat: re-join it, or answer it when the peer is the one calling.</summary>
    private async Task<CallJoinInfoDto> RejoinPrivateAsync(Guid userId, Guid sessionId, Guid callId, CancellationToken cancellationToken)
    {
        var result = await lifecycle.MutateAsync(callId, (call, now) =>
                     {
                         if (CallStateMachine.Participant(call, userId) is null) throw new ConflictException(ErrorCodes.CallBusy);
                         return CallStateMachine.Join(call, userId, now);
                     }, cancellationToken)
                     ?? throw new NotFoundException(ErrorCodes.CallNotFound);
        return await JoinInfoAsync(result.Call, userId, sessionId, cancellationToken);
    }

    private async Task<CallJoinInfoDto> StartGroupAsync(Guid userId, Guid sessionId, Guid chatId, bool isVideo, CancellationToken cancellationToken)
    {
        try
        {
            return await StartOrJoinGroupAsync(userId, sessionId, chatId, isVideo, cancellationToken);
        }
        catch (ConflictException ex) when (ex.Code == ErrorCodes.CallEnded)
        {
            // The running call we were about to join ended in the meantime (its last member hung up): there is no
            // active call any more, so this start begins a new one.
            db.ChangeTracker.Clear();
            return await StartOrJoinGroupAsync(userId, sessionId, chatId, isVideo, cancellationToken);
        }
    }

    private async Task<CallJoinInfoDto> StartOrJoinGroupAsync(Guid userId, Guid sessionId, Guid chatId, bool isVideo, CancellationToken cancellationToken)
    {
        if (await FindUnfinishedCallAsync(chatId, cancellationToken) is { } existing)
            return await JoinAsync(userId, sessionId, existing, cancellationToken);

        Call? call = null;
        Guid? raced;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await ChatSequence.LockChatAsync(db, chatId, cancellationToken);
            raced = await FindUnfinishedCallAsync(chatId, cancellationToken);
            if (raced is null)
            {
                var members = await db.ChatMembers.AsNoTracking()
                    .Where(m => m.ChatId == chatId)
                    .Select(m => m.UserId)
                    .ToListAsync(cancellationToken);
                call = CallStateMachine.CreateGroup(chatId, userId, members, isVideo, clock.UtcNow);
                db.Calls.Add(call);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
        }
        db.ChangeTracker.Clear();

        if (raced is { } racedId)
            return await JoinAsync(userId, sessionId, racedId, cancellationToken);

        await lifecycle.PublishStartedAsync(call!);
        return await JoinInfoAsync(call!, userId, sessionId, cancellationToken);
    }

    private Task<Guid?> FindUnfinishedCallAsync(Guid chatId, CancellationToken cancellationToken) =>
        db.Calls.AsNoTracking()
            .Where(c => c.ChatId == chatId)
            .Where(CallStateMachine.IsUnfinishedCall)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task EnsureCanCallAsync(Guid userId, Guid peerId, CancellationToken cancellationToken)
    {
        if (await privacy.CanCallAsync(userId, peerId, cancellationToken)) return;
        var blocked = await privacy.IsBlockedEitherWayAsync(userId, peerId, cancellationToken);
        throw new ForbiddenException(blocked ? ErrorCodes.UserBlocked : ErrorCodes.PrivacyRestricted);
    }

    /// <summary>Transaction-scoped advisory locks per user, taken in a fixed order so two starts cannot deadlock.</summary>
    private async Task LockUsersAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        foreach (var key in userIds.Distinct().Select(UserLockKey).Order())
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    private static long UserLockKey(Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes) ^ BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]) ^ UserLockNamespace;
    }

    private async Task<CallRef> RequireVisibleCallAsync(Guid userId, Guid callId, CancellationToken cancellationToken) =>
        await db.Calls.AsNoTracking()
            .Where(c => c.Id == callId && c.Chat!.Members.Any(m => m.UserId == userId))
            .Select(c => new CallRef(c.Kind, c.Status, c.RoomName))
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException(ErrorCodes.CallNotFound);

    private async Task<CallJoinInfoDto> JoinInfoAsync(Call call, Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var me = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.AvatarMediaId })
            .FirstOrDefaultAsync(cancellationToken);
        var token = tokens.CreateJoinToken(new LiveKitJoinGrant(call.RoomName, userId, sessionId, me?.DisplayName ?? "", me?.AvatarMediaId));
        return new CallJoinInfoDto
        {
            Call = CallMapping.ToDto(call),
            LiveKitUrl = options.Value.PublicUrl,
            LiveKitToken = token,
        };
    }

    private void EnsureEnabled()
    {
        if (!tokens.IsConfigured)
            throw new AppException(ErrorCodes.CallsDisabled, "Calls are disabled on this server.", 503);
    }

    private sealed record CallRef(CallKind Kind, CallStatus Status, string RoomName);
}
