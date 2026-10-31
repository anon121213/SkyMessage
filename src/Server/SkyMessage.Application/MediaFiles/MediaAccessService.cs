using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SkyMessage.Application.Common.Interfaces;

namespace SkyMessage.Application.MediaFiles;

public interface IMediaAccessService
{
    /// <summary>
    /// May <paramref name="viewerId"/> read media <paramref name="mediaId"/> (owned by <paramref name="ownerId"/>)?
    /// Yes for the owner; for the current avatar of a user whose profile photo the viewer may see; for the current
    /// avatar of a chat the viewer is a member of; and for an attachment of a non-deleted message that is visible
    /// to the viewer in a chat they are a member of. Positive answers are cached for 60 s.
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

        var chatAvatar = await (
                from member in db.ChatMembers
                join chat in db.Chats on member.ChatId equals chat.Id
                where member.UserId == viewerId && chat.AvatarMediaId == mediaId && !chat.IsDeleted
                select member.ChatId)
            .AnyAsync(cancellationToken);
        if (chatAvatar) return true;

        // Profile photos are always the user's own uploads, so the owner is the only candidate (primary-key lookup).
        var isOwnersAvatar = await db.Users.AnyAsync(u => u.Id == ownerId && u.AvatarMediaId == mediaId && !u.IsDeleted, cancellationToken);
        return isOwnersAvatar && await privacy.CanSeeProfilePhotoAsync(viewerId, ownerId, cancellationToken);
    }

    private readonly record struct AccessCacheKey(Guid ViewerId, Guid MediaId);
}
