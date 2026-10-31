using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>History reads and sync (this file), sending/forwarding and message actions (partial files).</summary>
internal sealed partial class MessageService(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IPrivacyService privacy,
    IMessagingDbFunctions dbFunctions,
    MessageDtoLoader loader,
    MessagePageBuilder pages,
    MentionResolver mentions,
    MessagingEvents events,
    ILogger<MessageService> logger) : IMessageService
{
    private const int MaxUnreadMentions = 1000;

    public async Task<MessagesPageDto> GetPageAsync(Guid chatId, long? beforeSeq, long? afterSeq, long? aroundSeq, int? limit,
        CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        var take = PageWindow.ClampLimit(limit, Limits.MessagesPageDefault, Limits.MessagesPageMax);
        var readAt = clock.UtcNow;
        var visible = db.VisibleMessages(member).AsNoTracking().Where(m => m.DeletedAt == null);

        List<Guid> ids;
        bool hasMoreBefore;
        bool hasMoreAfter;
        if (aroundSeq is { } around)
        {
            var older = await visible.Where(m => m.Seq < around).OrderByDescending(m => m.Seq).Select(m => m.Id)
                .Take(take + 1).ToListAsync(cancellationToken);
            var newer = await visible.Where(m => m.Seq >= around).OrderBy(m => m.Seq).Select(m => m.Id)
                .Take(take + 1).ToListAsync(cancellationToken);
            var (beforeTake, afterTake) = PageWindow.Balance(older.Count, newer.Count, take);
            ids = older.Take(beforeTake).Reverse().Concat(newer.Take(afterTake)).ToList();
            hasMoreBefore = older.Count > beforeTake;
            hasMoreAfter = newer.Count > afterTake;
        }
        else if (afterSeq is { } after)
        {
            var newer = await visible.Where(m => m.Seq > after).OrderBy(m => m.Seq).Select(m => m.Id)
                .Take(take + 1).ToListAsync(cancellationToken);
            ids = newer.Take(take).ToList();
            hasMoreAfter = newer.Count > take;
            hasMoreBefore = await visible.AnyAsync(m => m.Seq <= after, cancellationToken);
        }
        else if (beforeSeq is { } before)
        {
            var older = await visible.Where(m => m.Seq < before).OrderByDescending(m => m.Seq).Select(m => m.Id)
                .Take(take + 1).ToListAsync(cancellationToken);
            ids = older.Take(take).Reverse().ToList();
            hasMoreBefore = older.Count > take;
            hasMoreAfter = await visible.AnyAsync(m => m.Seq >= before, cancellationToken);
        }
        else
        {
            var latest = await visible.OrderByDescending(m => m.Seq).Select(m => m.Id)
                .Take(take + 1).ToListAsync(cancellationToken);
            ids = latest.Take(take).Reverse().ToList();
            hasMoreBefore = latest.Count > take;
            hasMoreAfter = false;
        }

        return await pages.BuildAsync(me, ids, hasMoreBefore, hasMoreAfter, readAt, cancellationToken);
    }

    public async Task<MessageChangesDto> GetChangesAsync(Guid chatId, string? cursor, CancellationToken cancellationToken = default)
    {
        if (!SyncCursor.TryDecode(cursor, out var since))
            throw MessagingErrors.Validation("cursor", "Invalid sync cursor.");

        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        var readAt = clock.UtcNow;

        // Tombstones included; rows hidden "for me" are reported through HiddenMessageIds instead.
        var rows = await db.VisibleMessages(member).AsNoTracking()
            .Where(dbFunctions.MessageChangedAfter(since.UpdatedAt, since.Id))
            .OrderBy(m => m.UpdatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new { m.Id, m.UpdatedAt })
            .Take(Limits.MessageChangesMax + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > Limits.MessageChangesMax;
        var page = rows.Take(Limits.MessageChangesMax).ToList();
        var messages = await loader.LoadAsync(page.Select(r => r.Id).ToList(), cancellationToken);

        var sinceAt = since.UpdatedAt;
        var hidden = await db.HiddenMessages.AsNoTracking()
            .Where(h => h.UserId == me && h.CreatedAt >= sinceAt && db.Messages.Any(m => m.Id == h.MessageId && m.ChatId == chatId))
            .Select(h => h.MessageId)
            .ToListAsync(cancellationToken);

        var last = page.Count > 0 ? page[^1] : null;
        var next = ChangesCursorRules.Next(since, readAt, hasMore, last?.UpdatedAt, last?.Id);
        return new MessageChangesDto
        {
            Messages = messages,
            HiddenMessageIds = hidden,
            ClearedUpToSeq = member.ClearedUpToSeq,
            NextCursor = next.Encode(),
            HasMore = hasMore,
        };
    }

    public async Task<IReadOnlyList<long>> GetUnreadMentionsAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        var watermark = Math.Max(member.LastReadSeq, member.ClearedUpToSeq);
        return await db.MessageMentions.AsNoTracking()
            .Where(x => x.UserId == me && x.ChatId == chatId && x.Seq > watermark
                        && !db.HiddenMessages.Any(h => h.MessageId == x.MessageId && h.UserId == me))
            .OrderBy(x => x.Seq)
            .Select(x => x.Seq)
            .Take(MaxUnreadMentions)
            .ToListAsync(cancellationToken);
    }

    public async Task<MessageDto> GetMessageAsync(Guid chatId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var member = await db.RequireMemberAsync(chatId, currentUser.UserId, cancellationToken);
        if (!await db.VisibleMessages(member).AnyAsync(m => m.Id == messageId, cancellationToken))
            throw new NotFoundException(ErrorCodes.MessageNotFound);
        return await loader.LoadOneAsync(messageId, cancellationToken) ?? throw new NotFoundException(ErrorCodes.MessageNotFound);
    }

    /// <summary>Loads the DTO after a commit and broadcasts it to all members.</summary>
    private async Task<MessageDto> PublishUpdatedAsync(Guid chatId, Guid messageId)
    {
        var dto = await loader.LoadOneAsync(messageId, CancellationToken.None)
                  ?? throw new NotFoundException(ErrorCodes.MessageNotFound);
        var memberIds = await db.MemberIdsAsync(chatId, CancellationToken.None);
        await events.ToUsersAsync(memberIds, "MessageUpdated", c => c.MessageUpdated(dto));
        return dto;
    }
}
