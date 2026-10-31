using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

// Every action on an existing message locks its row first (MessageLocks), so an edit, reaction or pin can
// never write into a message that a concurrent "delete for everyone" has just turned into a tombstone.
internal sealed partial class MessageService
{
    public async Task<MessageDto> EditAsync(Guid chatId, Guid messageId, EditMessageRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        ValidateContent(request.Text, request.Entities, []);
        await MessagingChecks.EnsureCanSendAsync(db, privacy, member, hasMedia: false, cancellationToken);

        var hasText = !MessageContentRules.IsBlank(request.Text);
        var newText = hasText ? request.Text : null;
        var newEntities = hasText ? request.Entities : null;
        var now = clock.UtcNow;

        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await MessageLocks.LockAsync(db, [messageId], cancellationToken);
            var message = await db.VisibleMessages(member).FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
            if (message is null || message.DeletedAt is not null) throw new NotFoundException(ErrorCodes.MessageNotFound);
            if (message.SenderId != me || !MessageTypeRules.IsUserContent(message.Type) || message.ForwardOriginalDate is not null)
                throw new ForbiddenException(ErrorCodes.MessageNotEditable);
            if (now - message.CreatedAt > TimeSpan.FromHours(Limits.EditWindowHours))
                throw new ForbiddenException(ErrorCodes.MessageEditExpired);
            if (!hasText && !await db.MessageAttachments.AnyAsync(a => a.MessageId == messageId, cancellationToken))
                throw new BadRequestException(ErrorCodes.MessageEmpty);

            var entities = MessageContentRules.ToEntities(newEntities);
            if (newText == message.Text && MessageContentRules.SameEntities(entities, message.Entities ?? []))
            {
                // Nothing changed: no "edited" mark, no event.
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                return await loader.LoadOneAsync(messageId, CancellationToken.None) ?? throw new NotFoundException(ErrorCodes.MessageNotFound);
            }

            message.Text = newText;
            message.Entities = entities;
            message.EditedAt = now;
            message.UpdatedAt = now;

            Guid? replyToSenderId = null;
            if (message.ReplyToMessageId is { } replyId)
            {
                replyToSenderId = await db.Messages.AsNoTracking()
                    .Where(m => m.Id == replyId && m.DeletedAt == null)
                    .Select(m => m.SenderId)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            var mentioned = await mentions.ResolveAsync(member.Type, chatId, me, newText, newEntities, replyToSenderId, cancellationToken);

            await db.MessageMentions.Where(x => x.MessageId == messageId).ExecuteDeleteAsync(cancellationToken);
            db.MessageMentions.AddRange(MentionResolver.Rows(messageId, chatId, message.Seq, mentioned));
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        db.ChangeTracker.Clear();

        return await PublishUpdatedAsync(chatId, messageId);
    }

    public async Task DeleteAsync(Guid chatId, DeleteMessagesRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        var ids = (request.MessageIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > Limits.MaxMessagesPerBatch)
            throw MessagingErrors.Validation("messageIds", $"Delete 1 to {Limits.MaxMessagesPerBatch} messages.");

        if (request.ForEveryone)
            await DeleteForEveryoneAsync(member, ids, cancellationToken);
        else
            await DeleteForMeAsync(member, ids, cancellationToken);
    }

    private async Task DeleteForEveryoneAsync(MemberContext member, List<Guid> ids, CancellationToken ct)
    {
        var chatId = member.ChatId;
        var me = member.UserId;
        var now = clock.UtcNow;
        List<Guid> deleted;
        bool pinsChanged;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await ChatSequence.LockChatAsync(db, chatId, ct);
            await MessageLocks.LockAsync(db, ids, ct);

            var role = await db.ChatMembers.AsNoTracking()
                           .Where(m => m.ChatId == chatId && m.UserId == me)
                           .Select(m => (ChatRole?)m.Role)
                           .FirstOrDefaultAsync(ct)
                       ?? throw new NotFoundException(ErrorCodes.ChatNotFound);
            var messages = await db.VisibleMessages(member)
                .Where(m => ids.Contains(m.Id) && m.DeletedAt == null)
                .ToListAsync(ct);
            // Unknown and already deleted ids are ignored (idempotent).
            if (messages.Count == 0) return;
            if (messages.Any(m => !ChatPolicy.CanDeleteForEveryone(member.Type, role, me, m.SenderId)))
                throw new ForbiddenException(ErrorCodes.Forbidden);

            deleted = messages.Select(m => m.Id).ToList();
            foreach (var m in messages)
            {
                m.Text = null;
                m.Entities = [];
                m.SystemEvent = null;
                m.CallLog = null;
                m.ReplyToMessageId = null;
                m.ForwardFromUserId = null;
                m.ForwardFromChatId = null;
                m.ForwardFromMessageId = null;
                m.ForwardSenderName = null;
                m.ForwardOriginalDate = null;
                m.DeletedAt = now;
                m.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);

            await db.MessageAttachments.Where(a => deleted.Contains(a.MessageId)).ExecuteDeleteAsync(ct);
            await db.MessageReactions.Where(r => deleted.Contains(r.MessageId)).ExecuteDeleteAsync(ct);
            await db.MessageMentions.Where(x => deleted.Contains(x.MessageId)).ExecuteDeleteAsync(ct);
            pinsChanged = await db.PinnedMessages.Where(p => p.ChatId == chatId && deleted.Contains(p.MessageId)).ExecuteDeleteAsync(ct) > 0;

            // LastMessageId must keep pointing at the newest message that still exists.
            var lastMessageId = await db.Chats.Where(c => c.Id == chatId).Select(c => c.LastMessageId).FirstOrDefaultAsync(ct);
            if (lastMessageId is { } lastId && deleted.Contains(lastId))
            {
                var newest = await db.Messages
                    .Where(m => m.ChatId == chatId && m.DeletedAt == null)
                    .OrderByDescending(m => m.Seq)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefaultAsync(ct);
                await db.Chats.Where(c => c.Id == chatId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastMessageId, newest), ct);
            }
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        var memberIds = await db.MemberIdsAsync(chatId, CancellationToken.None);
        await events.ToUsersAsync(memberIds, "MessagesDeleted",
            c => c.MessagesDeleted(new MessagesDeletedEvent { ChatId = chatId, MessageIds = deleted, ForEveryone = true }));
        if (pinsChanged)
            await events.ToUsersAsync(memberIds, "PinnedMessagesChanged", c => c.PinnedMessagesChanged(new ChatRefEvent { ChatId = chatId }));
    }

    private async Task DeleteForMeAsync(MemberContext member, List<Guid> ids, CancellationToken ct)
    {
        var chatId = member.ChatId;
        var me = member.UserId;
        var visible = await db.VisibleMessages(member).AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => m.Id)
            .ToListAsync(ct);
        if (visible.Count == 0) return;

        var now = clock.UtcNow;
        var hide = visible.ToArray();
        // ON CONFLICT: the same message may be hidden concurrently from another device.
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO hidden_messages (message_id, user_id, created_at) SELECT x, {me}, {now} FROM unnest({hide}) AS x ON CONFLICT DO NOTHING",
            ct);

        await events.ToUserAsync(me, "MessagesDeleted",
            c => c.MessagesDeleted(new MessagesDeletedEvent { ChatId = chatId, MessageIds = visible, ForEveryone = false }));
    }

    public async Task<MessageDto> SetReactionAsync(Guid chatId, Guid messageId, SetReactionRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var emoji = request.Emoji?.Trim() ?? "";
        if (!ReactionRules.IsValid(emoji))
            throw MessagingErrors.Validation("emoji", $"A reaction is one emoji of at most {Limits.ReactionMaxLength} characters.");
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        await EnsureNotBlockedAsync(member, cancellationToken);

        var now = clock.UtcNow;
        var changed = false;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await LockReactableAsync(member, messageId, cancellationToken);
            var reaction = await db.MessageReactions.FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == me, cancellationToken);
            if (reaction is null)
            {
                db.MessageReactions.Add(new MessageReaction { MessageId = messageId, UserId = me, Emoji = emoji, CreatedAt = now });
                changed = true;
            }
            else if (reaction.Emoji != emoji)
            {
                // One reaction per user: a new emoji replaces the old one.
                reaction.Emoji = emoji;
                reaction.CreatedAt = now;
                changed = true;
            }

            if (changed)
            {
                await db.SaveChangesAsync(cancellationToken);
                await TouchAsync(messageId, now, cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
        }
        db.ChangeTracker.Clear();

        return changed
            ? await PublishUpdatedAsync(chatId, messageId)
            : await loader.LoadOneAsync(messageId, CancellationToken.None) ?? throw new NotFoundException(ErrorCodes.MessageNotFound);
    }

    public async Task<MessageDto> RemoveReactionAsync(Guid chatId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);

        var now = clock.UtcNow;
        var changed = false;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await LockReactableAsync(member, messageId, cancellationToken);
            var removed = await db.MessageReactions.Where(r => r.MessageId == messageId && r.UserId == me).ExecuteDeleteAsync(cancellationToken);
            if (removed > 0)
            {
                await TouchAsync(messageId, now, cancellationToken);
                await tx.CommitAsync(cancellationToken);
                changed = true;
            }
        }

        return changed
            ? await PublishUpdatedAsync(chatId, messageId)
            : await loader.LoadOneAsync(messageId, CancellationToken.None) ?? throw new NotFoundException(ErrorCodes.MessageNotFound);
    }

    public async Task PinAsync(Guid chatId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        if (!ChatPolicy.CanPinMessages(member.Type, member.Role, member.Permissions))
            throw new ForbiddenException(ErrorCodes.NotChatAdmin);
        await EnsureNotBlockedAsync(member, cancellationToken);

        var now = clock.UtcNow;
        var pinned = false;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await LockPinnableAsync(member, messageId, cancellationToken);
            if (!await db.PinnedMessages.AnyAsync(p => p.ChatId == chatId && p.MessageId == messageId, cancellationToken))
            {
                db.PinnedMessages.Add(new PinnedMessage { ChatId = chatId, MessageId = messageId, PinnedById = me, PinnedAt = now });
                await db.SaveChangesAsync(cancellationToken);
                await TouchAsync(messageId, now, cancellationToken);
                await tx.CommitAsync(cancellationToken);
                pinned = true;
            }
        }
        db.ChangeTracker.Clear();
        if (!pinned) return;

        await PublishUpdatedAsync(chatId, messageId);
        await PublishPinsChangedAsync(chatId);
        await events.PostSystemEventAsync(chatId, new SystemEventDto
        {
            Type = SystemEventType.MessagePinned,
            ActorId = me,
            MessageId = messageId,
        });
    }

    public async Task UnpinAsync(Guid chatId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        if (!ChatPolicy.CanPinMessages(member.Type, member.Role, member.Permissions))
            throw new ForbiddenException(ErrorCodes.NotChatAdmin);

        var now = clock.UtcNow;
        var unpinned = false;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await MessageLocks.LockAsync(db, [messageId], cancellationToken);
            if (!await db.VisibleMessages(member).AnyAsync(m => m.Id == messageId, cancellationToken))
                throw new NotFoundException(ErrorCodes.MessageNotFound);
            var removed = await db.PinnedMessages.Where(p => p.ChatId == chatId && p.MessageId == messageId).ExecuteDeleteAsync(cancellationToken);
            if (removed > 0)
            {
                await TouchAsync(messageId, now, cancellationToken);
                await tx.CommitAsync(cancellationToken);
                unpinned = true;
            }
        }
        if (!unpinned) return;

        await PublishUpdatedAsync(chatId, messageId);
        await PublishPinsChangedAsync(chatId);
    }

    /// <summary>
    /// Reactions and pins in private chats are a channel too (a pin even posts a service message, which brings a
    /// chat the blocker hid back into their list): blocked either way → 403. Taking them away stays allowed.
    /// </summary>
    private async Task EnsureNotBlockedAsync(MemberContext member, CancellationToken ct)
    {
        if (member.Type != ChatType.Private) return;
        if (await db.PeerIdAsync(member.ChatId, member.UserId, ct) is { } peerId
            && await privacy.IsBlockedEitherWayAsync(member.UserId, peerId, ct))
            throw new ForbiddenException(ErrorCodes.UserBlocked);
    }

    /// <summary>Locks the message and checks it is visible, not deleted and not a service message.</summary>
    private async Task LockReactableAsync(MemberContext member, Guid messageId, CancellationToken ct)
    {
        await MessageLocks.LockAsync(db, [messageId], ct);
        var message = await db.VisibleMessages(member).AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.Type, m.DeletedAt })
            .FirstOrDefaultAsync(ct);
        if (message is null || message.DeletedAt is not null) throw new NotFoundException(ErrorCodes.MessageNotFound);
        if (message.Type == MessageType.System) throw new BadRequestException(ErrorCodes.MessageNotEditable);
    }

    private Task LockPinnableAsync(MemberContext member, Guid messageId, CancellationToken ct) => LockReactableAsync(member, messageId, ct);

    /// <summary>Every change of a message bumps UpdatedAt (drives /changes and client merge order).</summary>
    private Task TouchAsync(Guid messageId, DateTimeOffset now, CancellationToken ct) =>
        db.Messages.Where(m => m.Id == messageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.UpdatedAt, now), ct);

    private async Task PublishPinsChangedAsync(Guid chatId)
    {
        var memberIds = await db.MemberIdsAsync(chatId, CancellationToken.None);
        await events.ToUsersAsync(memberIds, "PinnedMessagesChanged", c => c.PinnedMessagesChanged(new ChatRefEvent { ChatId = chatId }));
    }
}
