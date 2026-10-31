using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Mapping;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>
/// The single batched <see cref="MessageDto"/> builder: a fixed number of queries per batch (messages, reply
/// targets, attachments, media, reactions, pins) whatever the batch size. DTOs are viewer-independent.
/// </summary>
internal sealed class MessageDtoLoader(IAppDbContext db)
{
    private sealed record ReplyRow(Guid Id, long Seq, Guid? SenderId, MessageType Type, string? Text, bool IsDeleted);

    private sealed record AttachmentRow(Guid MessageId, Guid MediaId, int Order);

    private sealed record ReactionGroupRow(Guid MessageId, string Emoji, int Count, DateTimeOffset FirstAt);

    private sealed record ReactorRow(Guid MessageId, string Emoji, Guid UserId, DateTimeOffset CreatedAt);

    /// <summary>DTOs in the order of <paramref name="ids"/> (unknown ids skipped, duplicates collapsed).</summary>
    public async Task<List<MessageDto>> LoadAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var distinct = ids.Distinct().ToList();
        var messages = await db.Messages.AsNoTracking().Where(m => distinct.Contains(m.Id)).ToListAsync(ct);
        var built = await BuildAsync(messages, ct);

        var result = new List<MessageDto>(distinct.Count);
        foreach (var id in distinct)
            if (built.TryGetValue(id, out var dto)) result.Add(dto);
        return result;
    }

    public async Task<MessageDto?> LoadOneAsync(Guid id, CancellationToken ct) =>
        (await LoadAsync([id], ct)).FirstOrDefault();

    /// <summary>People a client needs to render these messages (senders, reply/forward authors, system event and mention targets).</summary>
    public static HashSet<Guid> ReferencedUserIds(IEnumerable<MessageDto> messages)
    {
        var ids = new HashSet<Guid>();
        foreach (var m in messages)
        {
            if (m.SenderId is { } sender) ids.Add(sender);
            if (m.ReplyTo?.SenderId is { } replySender) ids.Add(replySender);
            if (m.Forward?.FromUserId is { } forwardFrom) ids.Add(forwardFrom);
            if (m.SystemEvent is { } e)
            {
                if (e.ActorId is { } actor) ids.Add(actor);
                foreach (var target in e.TargetUserIds) ids.Add(target);
            }
            if (m.Call is { } call) ids.Add(call.InitiatorId);
            if (m.Entities is { } entities)
                foreach (var entity in entities)
                    if (entity.UserId is { } mentioned) ids.Add(mentioned);
        }
        ids.Remove(Guid.Empty);
        return ids;
    }

    private async Task<Dictionary<Guid, MessageDto>> BuildAsync(List<Message> messages, CancellationToken ct)
    {
        var live = messages.Where(m => m.DeletedAt is null).ToList();
        var liveIds = live.Select(m => m.Id).ToList();

        var replyIds = live.Where(m => m.ReplyToMessageId is not null).Select(m => m.ReplyToMessageId!.Value).Distinct().ToList();
        var replies = replyIds.Count == 0
            ? new Dictionary<Guid, ReplyRow>()
            : await db.Messages.AsNoTracking()
                .Where(m => replyIds.Contains(m.Id))
                .Select(m => new ReplyRow(m.Id, m.Seq, m.SenderId, m.Type, m.Text, m.DeletedAt != null))
                .ToDictionaryAsync(r => r.Id, ct);

        var attachmentOwners = liveIds.Concat(replies.Values.Where(r => !r.IsDeleted).Select(r => r.Id)).Distinct().ToList();
        var attachments = attachmentOwners.Count == 0
            ? []
            : await db.MessageAttachments.AsNoTracking()
                .Where(a => attachmentOwners.Contains(a.MessageId))
                .Select(a => new AttachmentRow(a.MessageId, a.MediaId, a.Order))
                .ToListAsync(ct);

        var mediaIds = attachments.Select(a => a.MediaId).Distinct().ToList();
        var media = mediaIds.Count == 0
            ? new Dictionary<Guid, MediaDto>()
            : await db.Media.AsNoTracking()
                .Where(m => mediaIds.Contains(m.Id))
                .Select(MediaMapping.Projection)
                .ToDictionaryAsync(m => m.Id, ct);

        var attachmentsByMessage = attachments
            .GroupBy(a => a.MessageId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<MediaDto>)g.OrderBy(a => a.Order).ThenBy(a => a.MediaId)
                    .Select(a => media.GetValueOrDefault(a.MediaId))
                    .OfType<MediaDto>()
                    .ToList());

        var reactions = await LoadReactionsAsync(liveIds, ct);

        var pinned = liveIds.Count == 0
            ? []
            : (await db.PinnedMessages.AsNoTracking()
                .Where(p => liveIds.Contains(p.MessageId))
                .Select(p => p.MessageId)
                .ToListAsync(ct)).ToHashSet();

        var result = new Dictionary<Guid, MessageDto>(messages.Count);
        foreach (var m in messages)
        {
            if (m.DeletedAt is not null)
            {
                result[m.Id] = Tombstone(m);
                continue;
            }

            ReplyPreviewDto? reply = null;
            if (m.ReplyToMessageId is { } replyId && replies.TryGetValue(replyId, out var r))
            {
                reply = new ReplyPreviewDto
                {
                    MessageId = r.Id,
                    Seq = r.Seq,
                    SenderId = r.SenderId,
                    Type = r.Type,
                    Text = r.IsDeleted || r.Text is null ? null : MessageContentRules.Truncate(r.Text, Limits.ReplyPreviewTextLength),
                    Media = r.IsDeleted ? null : attachmentsByMessage.GetValueOrDefault(r.Id)?.FirstOrDefault(),
                    IsDeleted = r.IsDeleted,
                };
            }

            result[m.Id] = Map(m,
                attachmentsByMessage.GetValueOrDefault(m.Id) ?? [],
                reply,
                reactions.GetValueOrDefault(m.Id) ?? [],
                pinned.Contains(m.Id));
        }
        return result;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<ReactionDto>>> LoadReactionsAsync(List<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0) return new();

        var groups = await db.MessageReactions.AsNoTracking()
            .Where(r => messageIds.Contains(r.MessageId))
            .GroupBy(r => new { r.MessageId, r.Emoji })
            .Select(g => new ReactionGroupRow(g.Key.MessageId, g.Key.Emoji, g.Count(), g.Min(r => r.CreatedAt)))
            .ToListAsync(ct);
        if (groups.Count == 0) return new();

        // Small groups (the usual case) are loaded whole; otherwise only the most recent reactors per emoji.
        List<ReactorRow> reactors;
        if (groups.All(g => g.Count <= Limits.MaxReactionUserIds))
        {
            reactors = await db.MessageReactions.AsNoTracking()
                .Where(r => messageIds.Contains(r.MessageId))
                .Select(r => new ReactorRow(r.MessageId, r.Emoji, r.UserId, r.CreatedAt))
                .ToListAsync(ct);
        }
        else
        {
            // Top N per (message, emoji). EF turns the correlated Take into ROW_NUMBER() over a derived table;
            // repeating the id filter inside keeps that window to this batch instead of the whole table.
            reactors = await db.MessageReactions.AsNoTracking()
                .Where(r => messageIds.Contains(r.MessageId))
                .Select(r => new { r.MessageId, r.Emoji })
                .Distinct()
                .SelectMany(k => db.MessageReactions
                    .Where(r => messageIds.Contains(r.MessageId) && r.MessageId == k.MessageId && r.Emoji == k.Emoji)
                    .OrderByDescending(r => r.CreatedAt)
                    .ThenByDescending(r => r.UserId)
                    .Take(Limits.MaxReactionUserIds))
                .Select(r => new ReactorRow(r.MessageId, r.Emoji, r.UserId, r.CreatedAt))
                .ToListAsync(ct);
        }

        var usersByGroup = reactors
            .GroupBy(r => (r.MessageId, r.Emoji))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Guid>)g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.UserId)
                    .Take(Limits.MaxReactionUserIds)
                    .Select(r => r.UserId)
                    .ToList());

        return groups
            .GroupBy(g => g.MessageId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ReactionDto>)g
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.FirstAt)
                    .ThenBy(x => x.Emoji, StringComparer.Ordinal)
                    .Select(x => new ReactionDto
                    {
                        Emoji = x.Emoji,
                        Count = x.Count,
                        UserIds = usersByGroup.GetValueOrDefault((x.MessageId, x.Emoji)) ?? [],
                    })
                    .ToList());
    }

    private static MessageDto Map(Message m, IReadOnlyList<MediaDto> attachments, ReplyPreviewDto? reply,
        IReadOnlyList<ReactionDto> reactions, bool isPinned) => new()
    {
        Id = m.Id,
        ChatId = m.ChatId,
        Seq = m.Seq,
        SenderId = m.SenderId,
        Type = m.Type,
        Text = m.Text,
        Entities = m.Entities is { Count: > 0 } entities ? entities.Select(MessageContentRules.ToDto).ToList() : null,
        Attachments = attachments,
        ReplyTo = reply,
        Forward = MapForward(m),
        Reactions = reactions,
        SystemEvent = m.SystemEvent is { } e
            ? new SystemEventDto
            {
                Type = e.Type,
                ActorId = e.ActorId,
                TargetUserIds = e.TargetUserIds?.ToList() ?? [],
                Text = e.Text,
                MessageId = e.MessageId,
                CallId = e.CallId,
            }
            : null,
        Call = m.CallLog is { } c
            ? new CallInfoDto
            {
                CallId = c.CallId,
                IsVideo = c.IsVideo,
                Status = c.Status,
                DurationSeconds = c.DurationSeconds,
                InitiatorId = c.InitiatorId,
            }
            : null,
        IsPinned = isPinned,
        IsDeleted = false,
        CreatedAt = m.CreatedAt,
        EditedAt = m.EditedAt,
        UpdatedAt = m.UpdatedAt,
        ClientMessageId = m.ClientMessageId,
    };

    private static ForwardInfoDto? MapForward(Message m)
    {
        if (m.ForwardOriginalDate is null && m.ForwardFromUserId is null && m.ForwardFromChatId is null && m.ForwardSenderName is null)
            return null;
        return new ForwardInfoDto
        {
            FromUserId = m.ForwardFromUserId,
            FromChatId = m.ForwardFromChatId,
            SenderName = m.ForwardSenderName ?? "",
            OriginalDate = m.ForwardOriginalDate ?? m.CreatedAt,
        };
    }

    /// <summary>Deleted for everyone: identity and timestamps only.</summary>
    private static MessageDto Tombstone(Message m) => new()
    {
        Id = m.Id,
        ChatId = m.ChatId,
        Seq = m.Seq,
        SenderId = m.SenderId,
        Type = m.Type,
        IsDeleted = true,
        CreatedAt = m.CreatedAt,
        EditedAt = m.EditedAt,
        UpdatedAt = m.UpdatedAt,
        ClientMessageId = m.ClientMessageId,
    };
}
