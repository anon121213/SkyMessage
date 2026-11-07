using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.MediaFiles;

public interface IMediaAccessService
{
    /// <summary>
    /// May <paramref name="viewerId"/> read media <paramref name="mediaId"/> (owned by <paramref name="ownerId"/>)?
    /// Yes for the owner; for the current avatar, an avatar-history photo (while there is a current one) or the cover
    /// photo of a user whose profile photo the viewer may see; for the current avatar of a chat the viewer is a member of
    /// and its history photos (while it has a current one; always for who may change its info); and for an attachment
    /// of a non-deleted message that is visible to the viewer in a chat they are a member of; and for anyone signed in, the
    /// image of a sticker of an existing pack (stickers are shared by nature). Positive answers are cached for 60 s.
    /// </summary>
    Task<bool> CanViewAsync(Guid viewerId, Guid mediaId, Guid ownerId, CancellationToken cancellationToken = default);
}

internal sealed class MediaAccessService(IAppDbContext db, IPrivacyService privacy, IMemoryCache cache) : IMediaAccessService
{
    private static readonly TimeSpan PositiveCacheDuration = TimeSpan.FromSeconds(60);

    public async Task<bool> CanViewAsync(Guid viewerId, Guid mediaId, Guid ownerId, CancellationToken cancellationToken = default)
    {
        if (viewerId == ownerId) return true;

        var key = new AccessCacheKey(viewerId, mediaId);
        if (cache.TryGetValue(key, out _)) return true;

        if (!await IsAllowedAsync(viewerId, mediaId, ownerId, cancellationToken)) return false;

        // Size is set so the entry is valid even if someone configures a size-limited cache.
        cache.Set(key, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = PositiveCacheDuration, Size = 1 });
        return true;
    }

    private async Task<bool> IsAllowedAsync(Guid viewerId, Guid mediaId, Guid ownerId, CancellationToken cancellationToken)
    {
        // Most reads are chat attachments: attachment → message → the viewer's membership of that chat.
        var inVisibleMessage = await (
                from attachment in db.MessageAttachments
                join message in db.Messages on attachment.MessageId equals message.Id
                join member in db.ChatMembers on message.ChatId equals member.ChatId
                join chat in db.Chats on message.ChatId equals chat.Id
                where attachment.MediaId == mediaId
                      && message.DeletedAt == null
                      && member.UserId == viewerId
                      && message.Seq > member.ClearedUpToSeq
                      && !chat.IsDeleted
                      && !db.HiddenMessages.Any(h => h.MessageId == message.Id && h.UserId == viewerId)
                select attachment.Id)
            .AnyAsync(cancellationToken);
        if (inVisibleMessage) return true;

        // A sticker of a pack that exists: anyone may look at the pack before installing it.
        if (await db.Stickers.AnyAsync(s => s.MediaId == mediaId, cancellationToken)) return true;

        // A group's earlier photos: while it has a current one, or for who may change its info (ChatPolicy.CanChangeInfo),
        // as their list is (ChatService.GetAvatarsAsync).
        var chatAvatar = await (
                from member in db.ChatMembers
                join chat in db.Chats on member.ChatId equals chat.Id
                where member.UserId == viewerId && !chat.IsDeleted
                      && (chat.AvatarMediaId == mediaId
                          || (db.AvatarPhotos.Any(a => a.ChatId == chat.Id && a.MediaId == mediaId)
                              && (chat.AvatarMediaId != null
                                  || (chat.Type == ChatType.Group
                                      && (member.Role == ChatRole.Admin || member.Role == ChatRole.Owner || chat.Permissions.ChangeInfo)))))
                select member.ChatId)
            .AnyAsync(cancellationToken);
        if (chatAvatar) return true;

        // Profile photos (current, earlier ones, the cover) are always the user's own uploads, so the owner is the only
        // candidate (primary-key lookup). Earlier photos are shown only while there is a current one (as their list is).
        var isOwnersPhoto = await db.Users.AnyAsync(u => u.Id == ownerId && !u.IsDeleted
                                                         && (u.AvatarMediaId == mediaId || u.CoverMediaId == mediaId
                                                             || (u.AvatarMediaId != null
                                                                 && db.AvatarPhotos.Any(a => a.UserId == ownerId && a.MediaId == mediaId))),
            cancellationToken);
        return isOwnersPhoto && await privacy.CanSeeProfilePhotoAsync(viewerId, ownerId, cancellationToken);
    }

    private readonly record struct AccessCacheKey(Guid ViewerId, Guid MediaId);
}
