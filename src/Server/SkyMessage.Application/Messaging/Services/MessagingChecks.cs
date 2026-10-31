using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Services;

internal static class MessagingChecks
{
    /// <summary>
    /// May the member post (or edit) in this chat? Groups: SendMessages (+ SendMedia with attachments) unless
    /// admin. Private: the peer exists, is not deleted and <see cref="IPrivacyService.CanMessageAsync"/> allows
    /// it (403 user_blocked / privacy_restricted). Saved: always.
    /// </summary>
    public static async Task EnsureCanSendAsync(IAppDbContext db, IPrivacyService privacy, MemberContext member, bool hasMedia, CancellationToken ct)
    {
        switch (member.Type)
        {
            case ChatType.Group:
                if (!ChatPolicy.CanSendMessages(member.Type, member.Role, member.Permissions))
                    throw new ForbiddenException(ErrorCodes.SendForbidden);
                if (hasMedia && !ChatPolicy.CanSendMedia(member.Type, member.Role, member.Permissions))
                    throw new ForbiddenException(ErrorCodes.SendForbidden);
                return;

            case ChatType.Private:
                var peerId = await db.PeerIdAsync(member.ChatId, member.UserId, ct)
                             ?? throw new ForbiddenException(ErrorCodes.SendForbidden);
                var peerDeleted = await db.Users.AsNoTracking()
                    .Where(u => u.Id == peerId)
                    .Select(u => (bool?)u.IsDeleted)
                    .FirstOrDefaultAsync(ct);
                if (peerDeleted is not false)
                    throw new ForbiddenException(ErrorCodes.SendForbidden);
                if (!await privacy.CanMessageAsync(member.UserId, peerId, ct))
                    throw await PrivacyDeniedAsync(privacy, member.UserId, peerId, ct);
                return;

            default:
                return;
        }
    }

    /// <summary>403 with user_blocked when either side blocked the other, privacy_restricted otherwise.</summary>
    public static async Task<ForbiddenException> PrivacyDeniedAsync(IPrivacyService privacy, Guid actorId, Guid targetId, CancellationToken ct) =>
        new(await privacy.IsBlockedEitherWayAsync(actorId, targetId, ct) ? ErrorCodes.UserBlocked : ErrorCodes.PrivacyRestricted);

    /// <summary>
    /// Splits group member candidates into users the actor may add (existing, not deleted,
    /// <see cref="IPrivacyService.CanAddToGroupAsync"/>) and failures with a reason code. Order is preserved.
    /// </summary>
    public static async Task<(List<Guid> Allowed, List<AddMemberFailureDto> Failed)> CheckAddableAsync(
        IAppDbContext db, IPrivacyService privacy, Guid actorId, IReadOnlyCollection<Guid> candidates, CancellationToken ct)
    {
        var allowed = new List<Guid>();
        var failed = new List<AddMemberFailureDto>();
        if (candidates.Count == 0) return (allowed, failed);

        var ids = candidates.ToList();
        var existing = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && !u.IsDeleted)
            .Select(u => u.Id)
            .ToListAsync(ct);
        var existingSet = existing.ToHashSet();

        foreach (var userId in ids)
        {
            if (!existingSet.Contains(userId))
            {
                failed.Add(new AddMemberFailureDto { UserId = userId, Code = ErrorCodes.UserNotFound });
                continue;
            }
            if (!await privacy.CanAddToGroupAsync(actorId, userId, ct))
            {
                var code = await privacy.IsBlockedEitherWayAsync(actorId, userId, ct) ? ErrorCodes.UserBlocked : ErrorCodes.PrivacyRestricted;
                failed.Add(new AddMemberFailureDto { UserId = userId, Code = code });
                continue;
            }
            allowed.Add(userId);
        }
        return (allowed, failed);
    }

    /// <summary>A chat photo must be the caller's own processed photo.</summary>
    public static async Task RequireOwnReadyPhotoAsync(IAppDbContext db, Guid mediaId, Guid ownerId, CancellationToken ct)
    {
        var media = await db.Media.AsNoTracking()
            .Where(m => m.Id == mediaId)
            .Select(m => new { m.OwnerId, m.Kind, m.Status })
            .FirstOrDefaultAsync(ct);
        if (media is null || media.OwnerId != ownerId) throw new NotFoundException(ErrorCodes.MediaNotFound);
        if (media.Kind != MediaKind.Photo) throw new BadRequestException(ErrorCodes.MediaUnsupported);
        if (media.Status != MediaStatus.Ready)
            throw new BadRequestException(media.Status == MediaStatus.Failed ? ErrorCodes.MediaUnsupported : ErrorCodes.MediaNotReady);
    }
}

/// <summary>Builds <see cref="MessagesPageDto"/>s (messages, referenced people, my reactions, sync cursor).</summary>
internal sealed class MessagePageBuilder(IAppDbContext db, MessageDtoLoader loader, IUserSummaryService userSummaries)
{
    /// <param name="ids">Message ids in the order the page lists them.</param>
    /// <param name="readAt">When the page was read (the cursor lags behind it).</param>
    public async Task<MessagesPageDto> BuildAsync(Guid viewerId, IReadOnlyList<Guid> ids, bool hasMoreBefore, bool hasMoreAfter,
        DateTimeOffset readAt, CancellationToken ct)
    {
        var messages = await loader.LoadAsync(ids, ct);

        var userIds = MessageDtoLoader.ReferencedUserIds(messages);
        IReadOnlyList<UserSummaryDto> users = userIds.Count == 0
            ? []
            : (await userSummaries.GetAsync(viewerId, userIds, ct)).Values.ToList();

        var liveIds = messages.Where(m => !m.IsDeleted).Select(m => m.Id).ToList();
        var myReactions = liveIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.MessageReactions.AsNoTracking()
                .Where(r => r.UserId == viewerId && liveIds.Contains(r.MessageId))
                .ToDictionaryAsync(r => r.MessageId, r => r.Emoji, ct);

        return new MessagesPageDto
        {
            Messages = messages,
            Users = users,
            HasMoreBefore = hasMoreBefore,
            HasMoreAfter = hasMoreAfter,
            SyncCursor = SyncCursor.ForNow(readAt).Encode(),
            MyReactions = myReactions,
        };
    }
}
