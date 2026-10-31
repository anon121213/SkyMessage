using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

internal sealed partial class MessageService
{
    private const int MaxForwardAttempts = 2;

    private sealed record ExistingMessage(Guid Id, Guid ChatId);

    public async Task<MessageDto> SendAsync(Guid chatId, SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        if (request.ClientMessageId == Guid.Empty)
            throw MessagingErrors.Validation("clientMessageId", "ClientMessageId is required.");

        // Retries of a send that already succeeded return the original, whatever changed since.
        if (await FindByClientIdAsync(me, request.ClientMessageId, cancellationToken) is { } existing)
            return await ExistingAsync(existing, chatId, cancellationToken);

        var mediaIds = request.MediaIds ?? [];
        ValidateContent(request.Text, request.Entities, mediaIds);
        var hasText = !MessageContentRules.IsBlank(request.Text);
        if (!hasText && mediaIds.Count == 0) throw new BadRequestException(ErrorCodes.MessageEmpty);

        await MessagingChecks.EnsureCanSendAsync(db, privacy, member, mediaIds.Count > 0, cancellationToken);
        var media = await LoadOwnReadyMediaAsync(me, mediaIds, cancellationToken);
        var type = MessageTypeRules.Infer(media.Select(m => m.Kind).ToList())
                   ?? throw MessagingErrors.Validation("mediaIds", MessageTypeRules.CombinationHint);

        var text = hasText ? request.Text : null;
        var entities = hasText ? request.Entities : null;

        Guid? replyToId = null;
        Guid? replyToSenderId = null;
        if (request.ReplyToMessageId is { } replyId)
        {
            var target = await db.VisibleMessages(member).AsNoTracking()
                             .Where(m => m.Id == replyId)
                             .Select(m => new { m.Id, m.SenderId, m.DeletedAt })
                             .FirstOrDefaultAsync(cancellationToken)
                         ?? throw new NotFoundException(ErrorCodes.MessageNotFound);
            // Deleted for everyone while the reply was being written: send it as a plain message.
            if (target.DeletedAt is null)
            {
                replyToId = target.Id;
                replyToSenderId = target.SenderId;
            }
        }

        var mentioned = await mentions.ResolveAsync(member.Type, chatId, me, text, entities, replyToSenderId, cancellationToken);

        var now = clock.UtcNow;
        var message = new Message
        {
            ChatId = chatId,
            SenderId = me,
            Type = type,
            Text = text,
            Entities = MessageContentRules.ToEntities(entities),
            ReplyToMessageId = replyToId,
            ClientMessageId = request.ClientMessageId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        message.Attachments = media
            .Select((m, index) => new MessageAttachment { MessageId = message.Id, MediaId = m.Id, Order = index })
            .ToList();

        List<Guid> unhidden;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                message.Seq = await ChatSequence.NextAsync(db, chatId, now, cancellationToken);

                // A concurrent retry of the same send waited for the chat lock above; it sees the committed row now.
                if (await FindByClientIdAsync(me, request.ClientMessageId, cancellationToken) is { } raced)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    return await ExistingAsync(raced, chatId, cancellationToken);
                }

                db.Messages.Add(message);
                db.MessageMentions.AddRange(MentionResolver.Rows(message.Id, chatId, message.Seq, mentioned));
                await db.SaveChangesAsync(cancellationToken);
                unhidden = await CompleteAppendAsync(chatId, me, message.Id, message.Seq, cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Unique (SenderId, ClientMessageId): the same id was used concurrently (possibly in another chat).
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                var winner = await FindByClientIdAsync(me, request.ClientMessageId, CancellationToken.None);
                if (winner is null) throw;
                return await ExistingAsync(winner, chatId, cancellationToken);
            }
        }
        db.ChangeTracker.Clear();

        var dto = await loader.LoadOneAsync(message.Id, CancellationToken.None)
                  ?? throw new InvalidOperationException($"Message {message.Id} vanished after commit.");
        await PublishCreatedAsync(dto, unhidden, mentioned);
        return dto;
    }

    public async Task<IReadOnlyList<MessageDto>> ForwardAsync(Guid chatId, ForwardMessagesRequest request, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        ValidateForward(request);
        var target = await db.RequireMemberAsync(chatId, me, cancellationToken);
        var source = request.FromChatId == chatId ? target : await db.RequireMemberAsync(request.FromChatId, me, cancellationToken);
        var clientIdBySource = request.MessageIds.Zip(request.ClientMessageIds).ToDictionary(p => p.First, p => p.Second);
        var clientIds = request.ClientMessageIds.ToList();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ForwardOnceAsync(target, source, clientIdBySource, clientIds, cancellationToken);
            }
            catch (DbUpdateException) when (attempt < MaxForwardAttempts)
            {
                // A concurrent retry inserted some of the copies: the next pass skips them by ClientMessageId.
                db.ChangeTracker.Clear();
                logger.LogDebug("Forward into chat {ChatId} raced with a retry; re-running", chatId);
            }
        }
    }

    private async Task<IReadOnlyList<MessageDto>> ForwardOnceAsync(MemberContext target, MemberContext source,
        Dictionary<Guid, Guid> clientIdBySource, List<Guid> clientIds, CancellationToken ct)
    {
        var me = target.UserId;
        var chatId = target.ChatId;

        var existing = await ExistingByClientIdsAsync(me, clientIds, ct);
        if (existing.Any(e => e.ChatId != chatId))
            throw new ConflictException(ErrorCodes.Conflict, "A ClientMessageId was already used in another chat.");
        var done = existing.Select(e => e.ClientMessageId).ToHashSet();
        var pendingSourceIds = clientIdBySource.Where(p => !done.Contains(p.Value)).Select(p => p.Key).ToList();
        // Content protection: nothing more leaves the chat, whoever asks and wherever to (Saved Messages and the chat
        // itself included). A retry of a forward made before protection was turned on still gets its copies back.
        if (pendingSourceIds.Count > 0 && source.ProtectContent) throw new ForbiddenException(ErrorCodes.ForwardRestricted);

        var created = new List<Message>();
        List<Guid> unhidden = [];
        if (pendingSourceIds.Count > 0)
        {
            var sources = await db.VisibleMessages(source).AsNoTracking()
                .Where(m => pendingSourceIds.Contains(m.Id) && m.DeletedAt == null
                            && m.Type != MessageType.System && m.Type != MessageType.Call)
                .OrderBy(m => m.Seq)
                .ToListAsync(ct);

            if (sources.Count > 0)
            {
                var sourceIds = sources.Select(s => s.Id).ToList();
                var sourceAttachments = (await db.MessageAttachments.AsNoTracking()
                        .Where(a => sourceIds.Contains(a.MessageId))
                        .Select(a => new { a.MessageId, a.MediaId, a.Order })
                        .ToListAsync(ct))
                    .GroupBy(a => a.MessageId)
                    .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Order).ThenBy(a => a.MediaId).Select(a => a.MediaId).ToList());

                await MessagingChecks.EnsureCanSendAsync(db, privacy, target, sourceAttachments.Count > 0, ct);

                // Original authors' names at forward time (forwards of forwards keep their original info).
                var authorIds = sources.Where(s => s.ForwardOriginalDate is null && s.SenderId is not null)
                    .Select(s => s.SenderId!.Value).Distinct().ToList();
                var authorNames = authorIds.Count == 0
                    ? new Dictionary<Guid, string>()
                    : await db.Users.AsNoTracking()
                        .Where(u => authorIds.Contains(u.Id))
                        .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

                var now = clock.UtcNow;
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                foreach (var s in sources)
                {
                    var copy = new Message
                    {
                        ChatId = chatId,
                        SenderId = me,
                        Type = s.Type,
                        Text = s.Text,
                        Entities = (s.Entities ?? []).Select(MessageContentRules.Clone).ToList(),
                        ClientMessageId = clientIdBySource[s.Id],
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    if (s.ForwardOriginalDate is not null)
                    {
                        copy.ForwardFromUserId = s.ForwardFromUserId;
                        copy.ForwardFromChatId = s.ForwardFromChatId;
                        copy.ForwardFromMessageId = s.ForwardFromMessageId;
                        copy.ForwardSenderName = s.ForwardSenderName;
                        copy.ForwardOriginalDate = s.ForwardOriginalDate;
                    }
                    else
                    {
                        copy.ForwardFromUserId = s.SenderId;
                        copy.ForwardFromChatId = s.ChatId;
                        copy.ForwardFromMessageId = s.Id;
                        copy.ForwardSenderName = s.SenderId is { } author && authorNames.TryGetValue(author, out var name)
                            ? MessageContentRules.Truncate(name, Limits.DisplayNameMaxLength)
                            : "";
                        copy.ForwardOriginalDate = s.CreatedAt;
                    }
                    copy.Attachments = (sourceAttachments.GetValueOrDefault(s.Id) ?? [])
                        .Select((mediaId, index) => new MessageAttachment { MessageId = copy.Id, MediaId = mediaId, Order = index })
                        .ToList();

                    copy.Seq = await ChatSequence.NextAsync(db, chatId, now, ct);
                    db.Messages.Add(copy);
                    created.Add(copy);
                }
                await db.SaveChangesAsync(ct);
                var newest = created[^1];
                unhidden = await CompleteAppendAsync(chatId, me, newest.Id, newest.Seq, ct);
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
            }
        }

        var allIds = (await ExistingByClientIdsAsync(me, clientIds, CancellationToken.None))
            .Where(e => e.ChatId == chatId)
            .OrderBy(e => e.Seq)
            .Select(e => e.Id)
            .ToList();
        var result = await loader.LoadAsync(allIds, CancellationToken.None);

        if (created.Count > 0)
        {
            var createdIds = created.Select(c => c.Id).ToHashSet();
            var first = true;
            foreach (var dto in result.Where(m => createdIds.Contains(m.Id)))
            {
                await PublishCreatedAsync(dto, first ? unhidden : [], []);
                first = false;
            }
        }
        return result;
    }

    /// <summary>
    /// Chat aggregate updates that follow every append (inside the transaction, chat row already locked):
    /// LastMessageId, unhide members who had hidden the chat, mark the sender's own message read.
    /// Returns the unhidden members (they get ChatAdded).
    /// </summary>
    private async Task<List<Guid>> CompleteAppendAsync(Guid chatId, Guid senderId, Guid messageId, long seq, CancellationToken ct)
    {
        await db.Chats.Where(c => c.Id == chatId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastMessageId, messageId), ct);

        var unhidden = await db.ChatMembers.Where(m => m.ChatId == chatId && m.IsHidden).Select(m => m.UserId).ToListAsync(ct);
        if (unhidden.Count > 0)
        {
            await db.ChatMembers.Where(m => m.ChatId == chatId && m.IsHidden)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsHidden, false), ct);
        }

        await db.ChatMembers.Where(m => m.ChatId == chatId && m.UserId == senderId && m.LastReadSeq < seq)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, seq), ct);
        return unhidden;
    }

    /// <summary>MessageCreated to every member, ChatAdded to members the message unhid, push to the other members.</summary>
    private async Task PublishCreatedAsync(MessageDto dto, IReadOnlyCollection<Guid> unhidden, IReadOnlyCollection<Guid> mentioned)
    {
        var now = clock.UtcNow;
        var memberRows = await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == dto.ChatId)
            .Select(m => new { m.UserId, m.MutedUntil })
            .ToListAsync(CancellationToken.None);
        var memberIds = memberRows.Select(m => m.UserId).ToList();

        await events.ToUsersAsync(memberIds, "MessageCreated", c => c.MessageCreated(dto));
        await events.ToUsersAsync(unhidden, "ChatAdded", c => c.ChatAdded(new ChatRefEvent { ChatId = dto.ChatId }));

        // Muted chats stay silent unless the recipient is mentioned or replied to.
        var mentionedSet = mentioned.ToHashSet();
        var recipients = memberRows
            .Where(m => m.UserId != dto.SenderId && (!MuteRules.IsMuted(m.MutedUntil, now) || mentionedSet.Contains(m.UserId)))
            .Select(m => m.UserId)
            .ToList();
        await events.PushNewMessageAsync(dto, recipients);
    }

    private Task<ExistingMessage?> FindByClientIdAsync(Guid senderId, Guid clientMessageId, CancellationToken ct) =>
        db.Messages.AsNoTracking()
            .Where(m => m.SenderId == senderId && m.ClientMessageId == clientMessageId)
            .Select(m => new ExistingMessage(m.Id, m.ChatId))
            .FirstOrDefaultAsync(ct);

    private async Task<List<(Guid Id, Guid ChatId, Guid ClientMessageId, long Seq)>> ExistingByClientIdsAsync(
        Guid senderId, List<Guid> clientIds, CancellationToken ct)
    {
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.SenderId == senderId && m.ClientMessageId != null && clientIds.Contains(m.ClientMessageId.Value))
            .Select(m => new { m.Id, m.ChatId, ClientMessageId = m.ClientMessageId!.Value, m.Seq })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, r.ChatId, r.ClientMessageId, r.Seq)).ToList();
    }

    private async Task<MessageDto> ExistingAsync(ExistingMessage existing, Guid chatId, CancellationToken ct)
    {
        if (existing.ChatId != chatId)
            throw new ConflictException(ErrorCodes.Conflict, "This ClientMessageId was already used in another chat.");
        return await loader.LoadOneAsync(existing.Id, ct) ?? throw new NotFoundException(ErrorCodes.MessageNotFound);
    }

    private sealed record MediaRow(Guid Id, Guid OwnerId, MediaKind Kind, MediaStatus Status);

    /// <summary>Attachments must be the sender's own, processed media; returned in the requested order.</summary>
    private async Task<List<MediaRow>> LoadOwnReadyMediaAsync(Guid ownerId, IReadOnlyList<Guid> mediaIds, CancellationToken ct)
    {
        if (mediaIds.Count == 0) return [];
        var ids = mediaIds.ToList();
        var rows = await db.Media.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new MediaRow(m.Id, m.OwnerId, m.Kind, m.Status))
            .ToDictionaryAsync(m => m.Id, ct);

        var result = new List<MediaRow>(ids.Count);
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var row) || row.OwnerId != ownerId)
                throw new NotFoundException(ErrorCodes.MediaNotFound);
            if (row.Status == MediaStatus.Failed) throw new BadRequestException(ErrorCodes.MediaUnsupported);
            if (row.Status != MediaStatus.Ready) throw new BadRequestException(ErrorCodes.MediaNotReady);
            result.Add(row);
        }
        return result;
    }

    /// <summary>Static content rules (also enforced by the request validators; repeated here for non-HTTP callers).</summary>
    private static void ValidateContent(string? text, IReadOnlyList<MessageEntityDto>? entities, IReadOnlyList<Guid> mediaIds)
    {
        var errors = new Dictionary<string, string[]>();
        var textErrors = MessageContentRules.TextErrors(text).ToArray();
        if (textErrors.Length > 0) errors["text"] = textErrors;
        var entityErrors = MessageContentRules.EntityErrors(text, entities).ToArray();
        if (entityErrors.Length > 0) errors["entities"] = entityErrors;
        if (mediaIds.Count > Limits.MaxAttachmentsPerMessage)
            errors["mediaIds"] = [$"At most {Limits.MaxAttachmentsPerMessage} attachments are allowed."];
        else if (mediaIds.Distinct().Count() != mediaIds.Count || mediaIds.Contains(Guid.Empty))
            errors["mediaIds"] = ["Media ids must be distinct and not empty."];
        if (errors.Count > 0) throw new BadRequestException(ErrorCodes.Validation, "Validation failed", errors);
    }

    private static void ValidateForward(ForwardMessagesRequest request)
    {
        var messageIds = request.MessageIds ?? [];
        var clientIds = request.ClientMessageIds ?? [];
        if (request.FromChatId == Guid.Empty)
            throw MessagingErrors.Validation("fromChatId", "Source chat is required.");
        if (messageIds.Count is 0 or > Limits.MaxMessagesPerBatch)
            throw MessagingErrors.Validation("messageIds", $"Forward 1 to {Limits.MaxMessagesPerBatch} messages.");
        if (clientIds.Count != messageIds.Count)
            throw MessagingErrors.Validation("clientMessageIds", "ClientMessageIds must be parallel to MessageIds.");
        if (messageIds.Distinct().Count() != messageIds.Count || messageIds.Contains(Guid.Empty))
            throw MessagingErrors.Validation("messageIds", "Message ids must be distinct and not empty.");
        if (clientIds.Distinct().Count() != clientIds.Count || clientIds.Contains(Guid.Empty))
            throw MessagingErrors.Validation("clientMessageIds", "ClientMessageIds must be distinct and not empty.");
    }
}
