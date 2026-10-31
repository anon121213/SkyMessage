using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>
/// Computes the <see cref="MessageMention"/> index of a message: Mention entities with a user id, plain
/// "@username" text resolved to members, and the author of the replied-to message. Only current, non-deleted
/// members other than the sender are indexed (so Saved Messages never has any).
/// </summary>
internal sealed class MentionResolver(IAppDbContext db)
{
    public async Task<List<Guid>> ResolveAsync(
        ChatType chatType,
        Guid chatId,
        Guid senderId,
        string? text,
        IReadOnlyList<MessageEntityDto>? entities,
        Guid? replyToSenderId,
        CancellationToken ct)
    {
        if (chatType == ChatType.Saved) return [];

        var userIds = new HashSet<Guid>();
        if (!string.IsNullOrEmpty(text) && entities is not null)
        {
            foreach (var entity in entities)
                if (entity is { Type: MessageEntityType.Mention, UserId: { } mentioned }) userIds.Add(mentioned);
        }
        if (replyToSenderId is { } replyAuthor) userIds.Add(replyAuthor);
        userIds.Remove(senderId);
        userIds.Remove(Guid.Empty);

        var usernames = MentionParser.ExtractUsernames(text, entities).ToList();
        if (userIds.Count == 0 && usernames.Count == 0) return [];

        var ids = userIds.ToList();
        return await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId
                        && m.UserId != senderId
                        && !m.User!.IsDeleted
                        && (ids.Contains(m.UserId) || usernames.Contains(m.User.UsernameNormalized)))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public static IEnumerable<MessageMention> Rows(Guid messageId, Guid chatId, long seq, IEnumerable<Guid> userIds) =>
        userIds.Select(userId => new MessageMention { MessageId = messageId, UserId = userId, ChatId = chatId, Seq = seq });
}
