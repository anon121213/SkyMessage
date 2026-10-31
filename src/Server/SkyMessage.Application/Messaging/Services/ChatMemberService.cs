using System.Buffers.Text;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>
/// Group membership. Every membership change runs in a transaction that first takes the chat row lock
/// (<see cref="ChatSequence.LockChatAsync"/>), so concurrent adds/removes/joins/role changes are serialized and
/// <see cref="Chat.MemberCount"/> stays exact.
/// </summary>
internal sealed class ChatMemberService(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IPrivacyService privacy,
    ChatSummaryBuilder summaries,
    MessagingEvents events) : IChatMemberService
{
    private const int InviteTokenBytes = 16;
    private const int InviteTokenMaxLength = 64;

    public async Task<AddMembersResultDto> AddMembersAsync(Guid chatId, AddMembersRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = (await db.RequireMemberAsync(chatId, me, cancellationToken)).RequireGroup();
        if (!ChatPolicy.CanAddMembers(member.Type, member.Role, member.Permissions))
            throw new ForbiddenException(ErrorCodes.NotChatAdmin);

        var requested = (request.UserIds ?? []).Where(id => id != Guid.Empty && id != me).Distinct().ToList();
        if (requested.Count == 0) return new AddMembersResultDto();

        // Existing members are neither added nor failures (idempotent retries).
        var alreadyMembers = await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId && requested.Contains(m.UserId))
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        var candidates = requested.Except(alreadyMembers).ToList();

        // Privacy checks may take one query per user: done before the chat lock is taken.
        var (allowed, failed) = await MessagingChecks.CheckAddableAsync(db, privacy, me, candidates, cancellationToken);
        var added = new List<Guid>();
        if (allowed.Count > 0)
        {
            var now = clock.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await ChatSequence.LockChatAsync(db, chatId, cancellationToken);

            var actorRole = await CurrentRoleAsync(chatId, me, cancellationToken) ?? throw new NotFoundException(ErrorCodes.ChatNotFound);
            var chat = await db.Chats.AsNoTracking()
                .Where(c => c.Id == chatId)
                .Select(c => new { c.MemberCount, c.LastMessageSeq })
                .FirstAsync(cancellationToken);
            var present = (await db.ChatMembers.AsNoTracking()
                    .Where(m => m.ChatId == chatId && allowed.Contains(m.UserId))
                    .Select(m => m.UserId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
            var banned = (await db.ChatBans.AsNoTracking()
                    .Where(b => b.ChatId == chatId && allowed.Contains(b.UserId))
                    .Select(b => b.UserId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            var room = Math.Max(0, Limits.MaxGroupMembers - chat.MemberCount);
            foreach (var userId in allowed)
            {
                if (present.Contains(userId)) continue;
                if (banned.Contains(userId) && !ChatPolicy.CanAddBannedUser(actorRole))
                {
                    failed.Add(new AddMemberFailureDto { UserId = userId, Code = ErrorCodes.NotChatAdmin });
                    continue;
                }
                if (added.Count >= room)
                {
                    failed.Add(new AddMemberFailureDto { UserId = userId, Code = ErrorCodes.ChatMemberLimit });
                    continue;
                }
                db.ChatMembers.Add(new ChatMember
                {
                    ChatId = chatId,
                    UserId = userId,
                    Role = ChatRole.Member,
                    JoinedAt = now,
                    InvitedById = me,
                    // History stays readable, but it is not "unread" for someone who just arrived.
                    LastReadSeq = chat.LastMessageSeq,
                });
                added.Add(userId);
            }

            if (added.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                // An admin adding a banned user explicitly lifts the ban.
                await db.ChatBans.Where(b => b.ChatId == chatId && added.Contains(b.UserId)).ExecuteDeleteAsync(cancellationToken);
                var count = added.Count;
                await db.Chats.Where(c => c.Id == chatId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.MemberCount, c => c.MemberCount + count).SetProperty(c => c.UpdatedAt, now), cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            db.ChangeTracker.Clear();
        }

        if (added.Count > 0)
        {
            await events.ToUsersAsync(added, "ChatAdded", c => c.ChatAdded(new ChatRefEvent { ChatId = chatId }));
            await PublishMembershipChangedAsync(chatId);
            await events.PostSystemEventAsync(chatId, new SystemEventDto
            {
                Type = SystemEventType.MembersAdded,
                ActorId = me,
                TargetUserIds = added,
            });
        }

        return new AddMembersResultDto { Added = added, Failed = failed };
    }

    public async Task RemoveMemberAsync(Guid chatId, Guid userId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        if (userId == me)
        {
            await LeaveAsync(chatId, cancellationToken);
            return;
        }

        var member = (await db.RequireMemberAsync(chatId, me, cancellationToken)).RequireGroup();
        if (!member.IsAdmin) throw new ForbiddenException(ErrorCodes.NotChatAdmin);

        var now = clock.UtcNow;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await ChatSequence.LockChatAsync(db, chatId, cancellationToken);
            var actorRole = await CurrentRoleAsync(chatId, me, cancellationToken) ?? throw new NotFoundException(ErrorCodes.ChatNotFound);
            var target = await db.ChatMembers.FirstOrDefaultAsync(m => m.ChatId == chatId && m.UserId == userId, cancellationToken)
                         ?? throw new NotFoundException(ErrorCodes.NotChatMember);
            if (!ChatPolicy.CanRemoveMember(actorRole, target.Role))
                throw new ForbiddenException(target.Role == ChatRole.Owner ? ErrorCodes.Forbidden : ErrorCodes.NotChatAdmin);

            db.ChatMembers.Remove(target);
            // Removed users cannot come back through invite links or ordinary members (admins lift the ban by adding).
            if (!await db.ChatBans.AnyAsync(b => b.ChatId == chatId && b.UserId == userId, cancellationToken))
                db.ChatBans.Add(new ChatBan { ChatId = chatId, UserId = userId, BannedById = me, CreatedAt = now });
            await db.SaveChangesAsync(cancellationToken);
            await db.Chats.Where(c => c.Id == chatId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.MemberCount, c => c.MemberCount - 1).SetProperty(c => c.UpdatedAt, now), cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        db.ChangeTracker.Clear();

        await events.ToUserAsync(userId, "ChatRemoved", c => c.ChatRemoved(new ChatRefEvent { ChatId = chatId }));
        await PublishMembershipChangedAsync(chatId);
        await events.MemberRemovedAsync(chatId, [userId]);
        await events.PostSystemEventAsync(chatId, new SystemEventDto
        {
            Type = SystemEventType.MemberRemoved,
            ActorId = me,
            TargetUserIds = [userId],
        });
    }

    public async Task SetRoleAsync(Guid chatId, Guid userId, SetMemberRoleRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = (await db.RequireMemberAsync(chatId, me, cancellationToken)).RequireGroup();
        if (!Enum.IsDefined(request.Role)) throw MessagingErrors.Validation("role", "Unknown role.");
        if (!ChatPolicy.CanSetRoles(member.Role)) throw new ForbiddenException(ErrorCodes.Forbidden);
        if (userId == me)
        {
            if (request.Role == ChatRole.Owner) return;
            // The owner steps down by transferring ownership to someone else.
            throw new BadRequestException(ErrorCodes.CannotTargetSelf);
        }

        var changed = false;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await ChatSequence.LockChatAsync(db, chatId, cancellationToken);
            var mine = await db.ChatMembers.FirstOrDefaultAsync(m => m.ChatId == chatId && m.UserId == me, cancellationToken)
                       ?? throw new NotFoundException(ErrorCodes.ChatNotFound);
            if (!ChatPolicy.CanSetRoles(mine.Role)) throw new ForbiddenException(ErrorCodes.Forbidden);
            var target = await db.ChatMembers.FirstOrDefaultAsync(m => m.ChatId == chatId && m.UserId == userId, cancellationToken)
                         ?? throw new NotFoundException(ErrorCodes.NotChatMember);

            if (target.Role != request.Role)
            {
                if (request.Role == ChatRole.Owner)
                {
                    target.Role = ChatRole.Owner;
                    mine.Role = ChatRole.Admin;
                }
                else
                {
                    target.Role = request.Role;
                }
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                changed = true;
            }
        }
        db.ChangeTracker.Clear();

        if (changed) await PublishMembersChangedAsync(chatId);
    }

    public async Task LeaveAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        (await db.RequireMemberAsync(chatId, me, cancellationToken)).RequireGroup();

        var now = clock.UtcNow;
        bool groupDeleted;
        List<Guid> remaining;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await ChatSequence.LockChatAsync(db, chatId, cancellationToken);
            var mine = await db.ChatMembers.AsNoTracking()
                           .Where(m => m.ChatId == chatId && m.UserId == me)
                           .Select(m => (ChatRole?)m.Role)
                           .FirstOrDefaultAsync(cancellationToken)
                       ?? throw new NotFoundException(ErrorCodes.ChatNotFound);
            var others = await db.ChatMembers.AsNoTracking()
                .Where(m => m.ChatId == chatId && m.UserId != me)
                .Select(m => new OwnershipRules.Candidate(m.UserId, m.Role, m.JoinedAt))
                .ToListAsync(cancellationToken);
            remaining = others.Select(o => o.UserId).ToList();

            if (others.Count == 0)
            {
                // The last member out deletes the group (cascades remove its messages, bans, pins, calls).
                await db.Chats.Where(c => c.Id == chatId).ExecuteDeleteAsync(cancellationToken);
                groupDeleted = true;
            }
            else
            {
                await db.ChatMembers.Where(m => m.ChatId == chatId && m.UserId == me).ExecuteDeleteAsync(cancellationToken);
                if (mine == ChatRole.Owner && others.All(o => o.Role != ChatRole.Owner)
                    && OwnershipRules.PickSuccessor(others) is { } successor)
                {
                    await db.ChatMembers.Where(m => m.ChatId == chatId && m.UserId == successor)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ChatRole.Owner), cancellationToken);
                }
                await db.Chats.Where(c => c.Id == chatId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.MemberCount, c => c.MemberCount - 1).SetProperty(c => c.UpdatedAt, now), cancellationToken);
                groupDeleted = false;
            }
            await tx.CommitAsync(cancellationToken);
        }

        await events.ToUserAsync(me, "ChatRemoved", c => c.ChatRemoved(new ChatRefEvent { ChatId = chatId }));
        await events.MemberRemovedAsync(chatId, [me]);
        if (groupDeleted) return;

        await PublishMembershipChangedAsync(chatId);
        await events.PostSystemEventAsync(chatId, new SystemEventDto { Type = SystemEventType.MemberLeft, ActorId = me });
    }

    public async Task<InviteLinkDto> CreateInviteLinkAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = (await db.RequireMemberAsync(chatId, me, cancellationToken)).RequireGroup();
        if (!ChatPolicy.CanAddMembers(member.Type, member.Role, member.Permissions))
            throw new ForbiddenException(ErrorCodes.NotChatAdmin);
        if (member.InviteToken is { } current) return new InviteLinkDto { Token = current };

        for (var attempt = 0; ; attempt++)
        {
            var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(InviteTokenBytes));
            var now = clock.UtcNow;
            try
            {
                var updated = await db.Chats
                    .Where(c => c.Id == chatId && c.InviteToken == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.InviteToken, token).SetProperty(c => c.UpdatedAt, now), cancellationToken);
                if (updated == 1) return new InviteLinkDto { Token = token };
            }
            catch (Exception ex) when (ex is DbException or DbUpdateException && attempt < 3)
            {
                // Unique index on the token: a (practically impossible) collision, try another token.
                continue;
            }

            // Another request created a link at the same time: everyone gets that one.
            var existing = await db.Chats.AsNoTracking().Where(c => c.Id == chatId).Select(c => c.InviteToken).FirstOrDefaultAsync(cancellationToken);
            if (existing is not null) return new InviteLinkDto { Token = existing };
            if (attempt >= 3) throw new ConflictException();
        }
    }

    public async Task RevokeInviteLinkAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = (await db.RequireMemberAsync(chatId, me, cancellationToken)).RequireGroup();
        if (!ChatPolicy.CanAddMembers(member.Type, member.Role, member.Permissions))
            throw new ForbiddenException(ErrorCodes.NotChatAdmin);

        var now = clock.UtcNow;
        await db.Chats
            .Where(c => c.Id == chatId && c.InviteToken != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.InviteToken, (string?)null).SetProperty(c => c.UpdatedAt, now), cancellationToken);
    }

    public async Task<ChatInvitePreviewDto> GetInvitePreviewAsync(string token, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var chatId = await FindInviteChatAsync(token, me, cancellationToken);
        return await db.Chats.AsNoTracking()
            .Where(c => c.Id == chatId)
            .Select(c => new ChatInvitePreviewDto
            {
                Token = token,
                ChatId = c.Id,
                Title = c.Title ?? "",
                Description = c.Description,
                AvatarMediaId = c.AvatarMediaId,
                MemberCount = c.MemberCount,
                IsMember = db.ChatMembers.Any(m => m.ChatId == c.Id && m.UserId == me),
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.InviteInvalid);
    }

    public async Task<ChatSummaryDto> JoinByInviteAsync(string token, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var chatId = await FindInviteChatAsync(token, me, cancellationToken);

        var joined = false;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await ChatSequence.LockChatAsync(db, chatId, cancellationToken);
            var chat = await db.Chats.AsNoTracking()
                .Where(c => c.Id == chatId && c.InviteToken == token && c.Type == ChatType.Group && !c.IsDeleted)
                .Select(c => new { c.MemberCount, c.LastMessageSeq })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException(ErrorCodes.InviteInvalid);
            if (await db.ChatBans.AnyAsync(b => b.ChatId == chatId && b.UserId == me, cancellationToken))
                throw new NotFoundException(ErrorCodes.InviteInvalid);

            if (!await db.ChatMembers.AnyAsync(m => m.ChatId == chatId && m.UserId == me, cancellationToken))
            {
                if (chat.MemberCount >= Limits.MaxGroupMembers) throw new ConflictException(ErrorCodes.ChatMemberLimit);
                var now = clock.UtcNow;
                db.ChatMembers.Add(new ChatMember
                {
                    ChatId = chatId,
                    UserId = me,
                    Role = ChatRole.Member,
                    JoinedAt = now,
                    LastReadSeq = chat.LastMessageSeq,
                });
                await db.SaveChangesAsync(cancellationToken);
                await db.Chats.Where(c => c.Id == chatId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.MemberCount, c => c.MemberCount + 1).SetProperty(c => c.UpdatedAt, now), cancellationToken);
                await tx.CommitAsync(cancellationToken);
                joined = true;
            }
        }
        db.ChangeTracker.Clear();

        if (joined)
        {
            await events.ToUserAsync(me, "ChatAdded", c => c.ChatAdded(new ChatRefEvent { ChatId = chatId }));
            await PublishMembershipChangedAsync(chatId);
            await events.PostSystemEventAsync(chatId, new SystemEventDto { Type = SystemEventType.MemberJoinedByLink, ActorId = me });
        }
        return await summaries.BuildOneAsync(me, chatId, CancellationToken.None);
    }

    /// <summary>Chat id behind a valid invite token; malformed, unknown and revoked tokens and banned callers are all 404 invite_invalid.</summary>
    private async Task<Guid> FindInviteChatAsync(string? token, Guid userId, CancellationToken ct)
    {
        if (!IsWellFormedToken(token)) throw new NotFoundException(ErrorCodes.InviteInvalid);
        var chatId = await db.Chats.AsNoTracking()
            .Where(c => c.InviteToken == token && c.Type == ChatType.Group && !c.IsDeleted)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        if (chatId is null) throw new NotFoundException(ErrorCodes.InviteInvalid);
        if (await db.ChatBans.AnyAsync(b => b.ChatId == chatId && b.UserId == userId, ct))
            throw new NotFoundException(ErrorCodes.InviteInvalid);
        return chatId.Value;
    }

    internal static bool IsWellFormedToken(string? token) =>
        !string.IsNullOrEmpty(token)
        && token.Length <= InviteTokenMaxLength
        && token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private Task<ChatRole?> CurrentRoleAsync(Guid chatId, Guid userId, CancellationToken ct) =>
        db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.UserId == userId)
            .Select(m => (ChatRole?)m.Role)
            .FirstOrDefaultAsync(ct);

    /// <summary>ChatMembersChanged + ChatUpdated (member count) to everyone currently in the chat.</summary>
    private async Task PublishMembershipChangedAsync(Guid chatId)
    {
        var memberIds = await db.MemberIdsAsync(chatId, CancellationToken.None);
        await events.ToUsersAsync(memberIds, "ChatMembersChanged", c => c.ChatMembersChanged(new ChatRefEvent { ChatId = chatId }));
        if (await db.LoadChatInfoAsync(chatId, CancellationToken.None) is { } info)
            await events.ToUsersAsync(memberIds, "ChatUpdated", c => c.ChatUpdated(info));
    }

    private async Task PublishMembersChangedAsync(Guid chatId)
    {
        var memberIds = await db.MemberIdsAsync(chatId, CancellationToken.None);
        await events.ToUsersAsync(memberIds, "ChatMembersChanged", c => c.ChatMembersChanged(new ChatRefEvent { ChatId = chatId }));
    }
}
