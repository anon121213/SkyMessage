using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Users who may display someone: members of chats they share (private chats and groups) and users who
/// saved them as a contact. Recipients of UserChanged and PresenceChanged.
/// </summary>
public static class RelatedUsers
{
    /// <returns>Related user ids, never including <paramref name="userId"/> itself.</returns>
    public static async Task<HashSet<Guid>> GetAsync(IAppDbContext db, Guid userId, CancellationToken ct)
    {
        var chatIds = db.ChatMembers
            .Where(m => m.UserId == userId && m.Chat!.Type != ChatType.Saved && !m.Chat.IsDeleted)
            .Select(m => m.ChatId);
        var coMembers = db.ChatMembers
            .Where(m => chatIds.Contains(m.ChatId) && m.UserId != userId)
            .Select(m => m.UserId);
        var contactOwners = db.Contacts
            .Where(c => c.ContactUserId == userId)
            .Select(c => c.OwnerId);

        var ids = await coMembers.Union(contactOwners).ToListAsync(ct);
        return ids.ToHashSet();
    }
}
