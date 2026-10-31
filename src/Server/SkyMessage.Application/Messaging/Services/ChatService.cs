using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

internal sealed class ChatService(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IPrivacyService privacy,
    IUserSummaryService userSummaries,
    ChatSummaryBuilder summaries,
    MessageDtoLoader loader,
    IChatMemberService members,
    ChatDeletion deletion,
    MessagingEvents events,
    ILogger<ChatService> logger) : IChatService
{
    public async Task<IReadOnlyList<ChatSummaryDto>> GetChatsAsync(CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var chats = await summaries.BuildAsync(me, chatIds: null, visibleOnly: true, cancellationToken);
        return chats
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.PinnedAt)
            .ThenByDescending(c => c.LastActivityAt)
            .ThenByDescending(c => c.Id)
            .ToList();
    }

    public Task<ChatSummaryDto> GetChatAsync(Guid chatId, CancellationToken cancellationToken = default) =>
        summaries.BuildOneAsync(currentUser.UserId, chatId, cancellationToken);

    public async Task<ChatDetailsDto> GetDetailsAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        var summary = await summaries.BuildOneAsync(me, chatId, cancellationToken);

        var chat = await db.Chats.AsNoTracking()
            .Where(c => c.Id == chatId)
            .Select(c => new { c.Description, c.CreatedById, c.CreatedAt, c.InviteToken })
            .FirstAsync(cancellationToken);

        var rows = await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId)
            .Select(m => new { m.UserId, m.Role, m.JoinedAt, m.LastReadSeq })
            .ToListAsync(cancellationToken);
        var users = await userSummaries.GetAsync(me, rows.Select(r => r.UserId), cancellationToken);
        var memberDtos = rows
            .OrderByDescending(r => r.Role)
            .ThenBy(r => r.JoinedAt)
            .ThenBy(r => r.UserId)
            .Select(r => new ChatMemberDto
            {
                User = users.GetValueOrDefault(r.UserId) ?? new UserSummaryDto { Id = r.UserId, IsDeleted = true },
                Role = r.Role,
                JoinedAt = r.JoinedAt,
                LastReadSeq = r.LastReadSeq,
            })
            .ToList();

        return new ChatDetailsDto
        {
            Summary = summary,
            Description = chat.Description,
            Members = memberDtos,
            InviteToken = member.IsGroup && member.IsAdmin ? chat.InviteToken : null,
            PinnedMessages = await LoadPinnedAsync(member, cancellationToken),
            CreatedById = chat.CreatedById,
            CreatedAt = chat.CreatedAt,
        };
    }

    public async Task<IReadOnlyList<MessageDto>> GetPinnedAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var member = await db.RequireMemberAsync(chatId, currentUser.UserId, cancellationToken);
        return await LoadPinnedAsync(member, cancellationToken);
    }

    private async Task<List<MessageDto>> LoadPinnedAsync(MemberContext member, CancellationToken ct)
    {
        var chatId = member.ChatId;
        var visible = db.VisibleMessages(member).Where(m => m.DeletedAt == null);
        var ids = await db.PinnedMessages.AsNoTracking()
            .Where(p => p.ChatId == chatId && visible.Any(m => m.Id == p.MessageId))
            .OrderByDescending(p => p.PinnedAt)
            .ThenByDescending(p => p.MessageId)
            .Select(p => p.MessageId)
            .ToListAsync(ct);
        return await loader.LoadAsync(ids, ct);
    }

    public async Task<ChatSummaryDto> OpenPrivateChatAsync(CreatePrivateChatRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var peerId = request.UserId;
        if (peerId == Guid.Empty) throw MessagingErrors.Validation("userId", "User id is required.");
        if (peerId == me) return await OpenSavedAsync(me, cancellationToken);

        var key = Chat.PrivateKeyFor(me, peerId);
        if (await FindChatIdByKeyAsync(key, cancellationToken) is { } existingId)
            return await RevealAsync(existingId, me, cancellationToken);

        var peer = await db.Users.AsNoTracking()
            .Where(u => u.Id == peerId)
            .Select(u => new { u.IsDeleted })
            .FirstOrDefaultAsync(cancellationToken);
        if (peer is null || peer.IsDeleted) throw new NotFoundException(ErrorCodes.UserNotFound);
        if (!await privacy.CanMessageAsync(me, peerId, cancellationToken))
            throw await MessagingChecks.PrivacyDeniedAsync(privacy, me, peerId, cancellationToken);

        var now = clock.UtcNow;
        var chat = new Chat
        {
            Type = ChatType.Private,
            PrivateKey = key,
            CreatedById = me,
            CreatedAt = now,
            UpdatedAt = now,
            LastActivityAt = now,
            MemberCount = 2,
        };
        chat.Members.Add(new ChatMember { ChatId = chat.Id, UserId = me, Role = ChatRole.Member, JoinedAt = now });
        // The peer sees the chat only once the first message arrives (send unhides it).
        chat.Members.Add(new ChatMember { ChatId = chat.Id, UserId = peerId, Role = ChatRole.Member, JoinedAt = now, IsHidden = true });
        db.Chats.Add(chat);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Created concurrently (unique private key): use the winner's row.
            db.ChangeTracker.Clear();
            var racedId = await FindChatIdByKeyAsync(key, CancellationToken.None);
            if (racedId is null) throw;
            return await RevealAsync(racedId.Value, me, cancellationToken);
        }

        await events.ToUserAsync(me, "ChatAdded", c => c.ChatAdded(new ChatRefEvent { ChatId = chat.Id }));
        return await summaries.BuildOneAsync(me, chat.Id, CancellationToken.None);
    }

    private async Task<ChatSummaryDto> OpenSavedAsync(Guid me, CancellationToken ct)
    {
        var key = Chat.SavedKeyFor(me);
        var chatId = await FindChatIdByKeyAsync(key, ct);
        if (chatId is null)
        {
            // Registration creates it; recreate it if it is ever missing.
            var now = clock.UtcNow;
            var chat = new Chat
            {
                Type = ChatType.Saved,
                PrivateKey = key,
                CreatedById = me,
                CreatedAt = now,
                UpdatedAt = now,
                LastActivityAt = now,
                MemberCount = 1,
            };
            chat.Members.Add(new ChatMember { ChatId = chat.Id, UserId = me, Role = ChatRole.Owner, JoinedAt = now });
            db.Chats.Add(chat);
            try
            {
                await db.SaveChangesAsync(ct);
                chatId = chat.Id;
                await events.ToUserAsync(me, "ChatAdded", c => c.ChatAdded(new ChatRefEvent { ChatId = chat.Id }));
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                chatId = await FindChatIdByKeyAsync(key, CancellationToken.None);
                if (chatId is null) throw;
            }
        }
        return await RevealAsync(chatId.Value, me, ct);
    }

    private Task<Guid?> FindChatIdByKeyAsync(string key, CancellationToken ct) =>
        db.Chats.AsNoTracking().Where(c => c.PrivateKey == key && !c.IsDeleted).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);

    /// <summary>Unhides a private/saved chat for the caller (re-creating a missing membership row) and returns its summary.</summary>
    private async Task<ChatSummaryDto> RevealAsync(Guid chatId, Guid me, CancellationToken ct)
    {
        var unhidden = await db.ChatMembers
            .Where(m => m.ChatId == chatId && m.UserId == me && m.IsHidden)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsHidden, false), ct);

        if (unhidden == 0 && !await db.ChatMembers.AnyAsync(m => m.ChatId == chatId && m.UserId == me, ct))
        {
            var now = clock.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await ChatSequence.LockChatAsync(db, chatId, ct);
            if (!await db.ChatMembers.AnyAsync(m => m.ChatId == chatId && m.UserId == me, ct))
            {
                var lastSeq = await db.Chats.Where(c => c.Id == chatId).Select(c => c.LastMessageSeq).FirstAsync(ct);
                db.ChatMembers.Add(new ChatMember
                {
                    ChatId = chatId,
                    UserId = me,
                    Role = ChatRole.Member,
                    JoinedAt = now,
                    LastReadSeq = lastSeq,
                });
                await db.SaveChangesAsync(ct);
                await db.Chats.Where(c => c.Id == chatId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.MemberCount, c => c.MemberCount + 1), ct);
            }
            await tx.CommitAsync(ct);
            unhidden = 1;
            logger.LogWarning("Re-created missing membership of user {UserId} in chat {ChatId}", me, chatId);
        }

        if (unhidden > 0)
            await events.ToUserAsync(me, "ChatAdded", c => c.ChatAdded(new ChatRefEvent { ChatId = chatId }));
        return await summaries.BuildOneAsync(me, chatId, CancellationToken.None);
    }

    public async Task<CreateGroupResultDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var title = ValidateTitle(request.Title);
        var description = NormalizeDescription(request.Description);
        if (request.AvatarMediaId is { } avatarId)
            await MessagingChecks.RequireOwnReadyPhotoAsync(db, avatarId, me, cancellationToken);

        var candidates = (request.MemberIds ?? []).Where(id => id != Guid.Empty && id != me).Distinct().ToList();
        var (allowed, failed) = await MessagingChecks.CheckAddableAsync(db, privacy, me, candidates, cancellationToken);
        var room = Limits.MaxGroupMembers - 1;
        if (allowed.Count > room)
        {
            failed.AddRange(allowed.Skip(room).Select(id => new AddMemberFailureDto { UserId = id, Code = ErrorCodes.ChatMemberLimit }));
            allowed = allowed.Take(room).ToList();
        }

        var now = clock.UtcNow;
        var chat = new Chat
        {
            Type = ChatType.Group,
            Title = title,
            Description = description,
            AvatarMediaId = request.AvatarMediaId,
            CreatedById = me,
            CreatedAt = now,
            UpdatedAt = now,
            LastActivityAt = now,
            MemberCount = 1 + allowed.Count,
            Permissions = new GroupPermissions(),
        };
        chat.Members.Add(new ChatMember { ChatId = chat.Id, UserId = me, Role = ChatRole.Owner, JoinedAt = now });
        foreach (var userId in allowed)
            chat.Members.Add(new ChatMember { ChatId = chat.Id, UserId = userId, Role = ChatRole.Member, JoinedAt = now, InvitedById = me });
        db.Chats.Add(chat);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        List<Guid> memberIds = [me, .. allowed];
        await events.ToUsersAsync(memberIds, "ChatAdded", c => c.ChatAdded(new ChatRefEvent { ChatId = chat.Id }));
        await events.PostSystemEventAsync(chat.Id, new SystemEventDto
        {
            Type = SystemEventType.ChatCreated,
            ActorId = me,
            Text = title,
            TargetUserIds = allowed,
        });

        return new CreateGroupResultDto
        {
            Chat = await summaries.BuildOneAsync(me, chat.Id, CancellationToken.None),
            Failed = failed,
        };
    }

    public async Task<ChatInfoDto> UpdateChatAsync(Guid chatId, UpdateChatRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = (await db.RequireMemberAsync(chatId, me, cancellationToken)).RequireGroup();
        if ((request.Title is not null || request.Description is not null)
            && !ChatPolicy.CanChangeInfo(member.Type, member.Role, member.Permissions))
            throw new ForbiddenException(ErrorCodes.NotChatAdmin);
        if (request.Permissions is not null && !ChatPolicy.CanChangePermissions(member.Type, member.Role))
            throw new ForbiddenException(ErrorCodes.NotChatAdmin);

        var newTitle = request.Title is null ? null : ValidateTitle(request.Title);
        var newDescription = request.Description is null ? null : NormalizeDescription(request.Description);

        var chat = await db.Chats.FirstAsync(c => c.Id == chatId, cancellationToken);
        var titleChanged = newTitle is not null && newTitle != chat.Title;
        var descriptionChanged = request.Description is not null && newDescription != chat.Description;
        var permissionsChanged = false;

        if (titleChanged) chat.Title = newTitle;
        if (descriptionChanged) chat.Description = newDescription;
        if (request.Permissions is { } p)
        {
            var current = chat.Permissions;
            permissionsChanged = current.SendMessages != p.SendMessages || current.SendMedia != p.SendMedia
                                 || current.AddMembers != p.AddMembers || current.PinMessages != p.PinMessages
                                 || current.ChangeInfo != p.ChangeInfo;
            if (permissionsChanged)
            {
                current.SendMessages = p.SendMessages;
                current.SendMedia = p.SendMedia;
                current.AddMembers = p.AddMembers;
                current.PinMessages = p.PinMessages;
                current.ChangeInfo = p.ChangeInfo;
            }
        }

        var changed = titleChanged || descriptionChanged || permissionsChanged;
        if (changed)
        {
            chat.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        db.ChangeTracker.Clear();

        var info = await db.LoadChatInfoAsync(chatId, CancellationToken.None) ?? throw new NotFoundException(ErrorCodes.ChatNotFound);
        if (!changed) return info;

        await PublishChatUpdatedAsync(info);
        if (titleChanged)
            await events.PostSystemEventAsync(chatId, new SystemEventDto { Type = SystemEventType.TitleChanged, ActorId = me, Text = newTitle });
        if (descriptionChanged)
            await events.PostSystemEventAsync(chatId, new SystemEventDto { Type = SystemEventType.DescriptionChanged, ActorId = me, Text = newDescription });
        return info;
    }

    public async Task<ChatInfoDto> SetAvatarAsync(Guid chatId, SetChatAvatarRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = (await db.RequireMemberAsync(chatId, me, cancellationToken)).RequireGroup();
        if (!ChatPolicy.CanChangeInfo(member.Type, member.Role, member.Permissions))
            throw new ForbiddenException(ErrorCodes.NotChatAdmin);
        await MessagingChecks.RequireOwnReadyPhotoAsync(db, request.MediaId, me, cancellationToken);

        var mediaId = request.MediaId;
        var now = clock.UtcNow;
        var changed = await db.Chats
            .Where(c => c.Id == chatId && (c.AvatarMediaId == null || c.AvatarMediaId != mediaId))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AvatarMediaId, mediaId).SetProperty(c => c.UpdatedAt, now), cancellationToken);

        var info = await db.LoadChatInfoAsync(chatId, CancellationToken.None) ?? throw new NotFoundException(ErrorCodes.ChatNotFound);
        if (changed == 0) return info;

        await PublishChatUpdatedAsync(info);
        await events.PostSystemEventAsync(chatId, new SystemEventDto { Type = SystemEventType.PhotoChanged, ActorId = me });
        return info;
    }

    public async Task<ChatInfoDto> RemoveAvatarAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = (await db.RequireMemberAsync(chatId, me, cancellationToken)).RequireGroup();
        if (!ChatPolicy.CanChangeInfo(member.Type, member.Role, member.Permissions))
            throw new ForbiddenException(ErrorCodes.NotChatAdmin);

        var now = clock.UtcNow;
        var changed = await db.Chats
            .Where(c => c.Id == chatId && c.AvatarMediaId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AvatarMediaId, (Guid?)null).SetProperty(c => c.UpdatedAt, now), cancellationToken);

        var info = await db.LoadChatInfoAsync(chatId, CancellationToken.None) ?? throw new NotFoundException(ErrorCodes.ChatNotFound);
        if (changed == 0) return info;

        await PublishChatUpdatedAsync(info);
        await events.PostSystemEventAsync(chatId, new SystemEventDto { Type = SystemEventType.PhotoRemoved, ActorId = me });
        return info;
    }

    public async Task<ChatUserSettingsDto> UpdateSettingsAsync(Guid chatId, UpdateChatSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        await db.RequireMemberAsync(chatId, me, cancellationToken);
        if (request.MuteForSeconds is { } seconds && !MuteRules.IsValidDuration(seconds))
            throw MessagingErrors.Validation("muteForSeconds", "Use 0 to unmute, -1 to mute forever or a positive number of seconds.");

        var row = await db.ChatMembers.FirstAsync(m => m.ChatId == chatId && m.UserId == me, cancellationToken);
        var now = clock.UtcNow;
        if (request.IsPinned is { } pinned)
        {
            if (pinned && !row.IsPinned)
            {
                row.IsPinned = true;
                row.PinnedAt = now;
            }
            else if (!pinned && row.IsPinned)
            {
                row.IsPinned = false;
                row.PinnedAt = null;
            }
        }
        if (request.IsArchived is { } archived) row.IsArchived = archived;
        if (request.MuteForSeconds is { } mute) row.MutedUntil = MuteRules.MutedUntil(now, mute);

        var changed = db.ChangeTracker.HasChanges();
        if (changed) await db.SaveChangesAsync(cancellationToken);

        var settings = new ChatUserSettingsDto
        {
            IsPinned = row.IsPinned,
            PinnedAt = row.PinnedAt,
            IsArchived = row.IsArchived,
            MutedUntil = row.MutedUntil,
        };
        db.ChangeTracker.Clear();

        if (changed)
            await events.ToUserAsync(me, "ChatSettingsChanged", c => c.ChatSettingsChanged(new ChatSettingsChangedEvent { ChatId = chatId, Settings = settings }));
        return settings;
    }

    public async Task MarkReadAsync(Guid chatId, MarkReadRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        var upTo = Math.Clamp(request.UpToSeq, 0, member.LastMessageSeq);
        if (upTo <= member.LastReadSeq) return;

        // Only ever moves forward, even when requests from several devices race.
        var updated = await db.ChatMembers
            .Where(m => m.ChatId == chatId && m.UserId == me && m.LastReadSeq < upTo)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, upTo), cancellationToken);
        if (updated == 0) return;

        var memberIds = await db.MemberIdsAsync(chatId, CancellationToken.None);
        await events.ToUsersAsync(memberIds, "MessagesRead",
            c => c.MessagesRead(new MessagesReadEvent { ChatId = chatId, UserId = me, UpToSeq = upTo }));
    }

    public async Task ClearHistoryAsync(Guid chatId, bool forEveryone, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        if (forEveryone && member.IsGroup)
            throw MessagingErrors.Validation("forEveryone", "History can be cleared for everyone only in private chats.");

        var bothSides = forEveryone && member.Type == ChatType.Private;
        var targets = bothSides ? await db.MemberIdsAsync(chatId, cancellationToken) : [me];
        var upTo = member.LastMessageSeq;

        await db.ChatMembers
            .Where(m => m.ChatId == chatId && targets.Contains(m.UserId) && m.ClearedUpToSeq < upTo)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ClearedUpToSeq, upTo), cancellationToken);

        await events.ToUsersAsync(targets, "HistoryCleared",
            c => c.HistoryCleared(new HistoryClearedEvent { ChatId = chatId, UpToSeq = upTo, ForEveryone = bothSides }));
    }

    public async Task DeleteChatAsync(Guid chatId, bool forEveryone, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        switch (member.Type)
        {
            case ChatType.Group when !forEveryone:
                await members.LeaveAsync(chatId, cancellationToken);
                return;
            case ChatType.Group:
                if (!ChatPolicy.CanDeleteChatForEveryone(member.Type, member.Role))
                    throw new ForbiddenException(ErrorCodes.Forbidden);
                await deletion.DeleteAsync(chatId, cancellationToken);
                return;
            case ChatType.Private when forEveryone:
                await deletion.DeleteAsync(chatId, cancellationToken);
                return;
            default:
                await HideForMeAsync(member, cancellationToken);
                return;
        }
    }

    /// <summary>
    /// "Delete chat for me" (private and Saved): history up to now disappears for the caller and the chat
    /// leaves their list; the next message brings it back with only new messages.
    /// </summary>
    private async Task HideForMeAsync(MemberContext member, CancellationToken ct)
    {
        var chatId = member.ChatId;
        var me = member.UserId;
        var upTo = member.LastMessageSeq;
        await db.ChatMembers
            .Where(m => m.ChatId == chatId && m.UserId == me)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsHidden, true)
                .SetProperty(m => m.ClearedUpToSeq, m => Math.Max(m.ClearedUpToSeq, upTo))
                .SetProperty(m => m.IsPinned, false)
                .SetProperty(m => m.PinnedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.IsArchived, false), ct);

        await events.ToUserAsync(me, "ChatRemoved", c => c.ChatRemoved(new ChatRefEvent { ChatId = chatId }));
    }

    private async Task PublishChatUpdatedAsync(ChatInfoDto info)
    {
        var memberIds = await db.MemberIdsAsync(info.Id, CancellationToken.None);
        await events.ToUsersAsync(memberIds, "ChatUpdated", c => c.ChatUpdated(info));
    }

    private static string ValidateTitle(string? title)
    {
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > Limits.GroupTitleMaxLength || MessageContentRules.HasForbiddenChars(trimmed))
            throw MessagingErrors.Validation("title", $"Title must be 1 to {Limits.GroupTitleMaxLength} characters.");
        return trimmed;
    }

    /// <summary>Blank clears the description.</summary>
    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var trimmed = description.Trim();
        if (trimmed.Length > Limits.GroupDescriptionMaxLength || MessageContentRules.HasForbiddenChars(trimmed))
            throw MessagingErrors.Validation("description", $"Description must be at most {Limits.GroupDescriptionMaxLength} characters.");
        return trimmed;
    }
}

/// <summary>Deletes a whole chat for every member (database cascades remove messages, members, pins, bans, calls).</summary>
internal sealed class ChatDeletion(IAppDbContext db, MessagingEvents events)
{
    public async Task DeleteAsync(Guid chatId, CancellationToken ct)
    {
        // Before the cascade: observers still see related rows (Calls ends a running call for its participants).
        await events.ChatDeletingAsync(chatId);

        List<Guid> memberIds;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await ChatSequence.LockChatAsync(db, chatId, ct);
            memberIds = await db.ChatMembers.Where(m => m.ChatId == chatId).Select(m => m.UserId).ToListAsync(ct);
            await db.Chats.Where(c => c.Id == chatId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        await events.ToUsersAsync(memberIds, "ChatRemoved", c => c.ChatRemoved(new ChatRefEvent { ChatId = chatId }));
        await events.MemberRemovedAsync(chatId, memberIds);
    }
}
