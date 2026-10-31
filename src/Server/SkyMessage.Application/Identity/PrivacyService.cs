using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Privacy + block rules. Blocks come first: when the owner blocked the viewer every level is Nobody.
/// Each check is a single round trip (subqueries), so other features can call them freely.
/// </summary>
internal sealed class PrivacyService(IAppDbContext db) : IPrivacyService
{
    public Task<bool> IsBlockedEitherWayAsync(Guid userA, Guid userB, CancellationToken cancellationToken = default) =>
        db.Blocks.AsNoTracking().AnyAsync(
            b => (b.BlockerId == userA && b.BlockedId == userB) || (b.BlockerId == userB && b.BlockedId == userA),
            cancellationToken);

    public async Task<bool> CanMessageAsync(Guid fromUserId, Guid toUserId, CancellationToken cancellationToken = default)
    {
        if (fromUserId == toUserId) return true;

        var privateKey = Chat.PrivateKeyFor(fromUserId, toUserId);
        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == toUserId)
            .Select(u => new
            {
                u.IsDeleted,
                Level = u.Privacy.Messages,
                Blocked = db.Blocks.Any(b => (b.BlockerId == fromUserId && b.BlockedId == toUserId) || (b.BlockerId == toUserId && b.BlockedId == fromUserId)),
                HasSenderInContacts = db.Contacts.Any(c => c.OwnerId == toUserId && c.ContactUserId == fromUserId),
                // The recipient already wrote in this private chat: replying is always allowed.
                RecipientWroteFirst = db.Messages.Any(m => m.SenderId == toUserId && m.Chat!.PrivateKey == privateKey),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (target is null || target.IsDeleted || target.Blocked) return false;
        return PrivacyRules.AllowsAtLeastContacts(target.Level, target.HasSenderInContacts) || target.RecipientWroteFirst;
    }

    public async Task<bool> CanCallAsync(Guid fromUserId, Guid toUserId, CancellationToken cancellationToken = default)
    {
        if (fromUserId == toUserId) return false;

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == toUserId)
            .Select(u => new
            {
                u.IsDeleted,
                Level = u.Privacy.Calls,
                Blocked = db.Blocks.Any(b => (b.BlockerId == fromUserId && b.BlockedId == toUserId) || (b.BlockerId == toUserId && b.BlockedId == fromUserId)),
                HasCallerInContacts = db.Contacts.Any(c => c.OwnerId == toUserId && c.ContactUserId == fromUserId),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (target is null || target.IsDeleted || target.Blocked) return false;
        return PrivacyRules.Allows(target.Level, target.HasCallerInContacts);
    }

    public async Task<bool> CanAddToGroupAsync(Guid actorId, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        if (actorId == targetUserId) return true;

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == targetUserId)
            .Select(u => new
            {
                u.IsDeleted,
                Level = u.Privacy.GroupInvites,
                Blocked = db.Blocks.Any(b => (b.BlockerId == actorId && b.BlockedId == targetUserId) || (b.BlockerId == targetUserId && b.BlockedId == actorId)),
                HasActorInContacts = db.Contacts.Any(c => c.OwnerId == targetUserId && c.ContactUserId == actorId),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (target is null || target.IsDeleted || target.Blocked) return false;
        return PrivacyRules.AllowsAtLeastContacts(target.Level, target.HasActorInContacts);
    }

    public async Task<bool> CanSeeProfilePhotoAsync(Guid viewerId, Guid ownerId, CancellationToken cancellationToken = default)
    {
        if (viewerId == ownerId) return true;

        var owner = await db.Users.AsNoTracking()
            .Where(u => u.Id == ownerId)
            .Select(u => new
            {
                u.IsDeleted,
                Level = u.Privacy.ProfilePhoto,
                BlockedViewer = db.Blocks.Any(b => b.BlockerId == ownerId && b.BlockedId == viewerId),
                HasViewerInContacts = db.Contacts.Any(c => c.OwnerId == ownerId && c.ContactUserId == viewerId),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (owner is null || owner.IsDeleted || owner.BlockedViewer) return false;
        return PrivacyRules.Allows(owner.Level, owner.HasViewerInContacts);
    }

    public Task<bool> IsInContactsOfAsync(Guid ownerId, Guid userId, CancellationToken cancellationToken = default) =>
        db.Contacts.AsNoTracking().AnyAsync(c => c.OwnerId == ownerId && c.ContactUserId == userId, cancellationToken);
}
