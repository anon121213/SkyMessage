using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Viewer-dependent user cards in four queries per batch (users, my contact names, who has me as a
/// contact, who blocked me) plus in-memory presence.
/// </summary>
internal sealed class UserSummaryService(IAppDbContext db, IPresenceService presence, IClock clock) : IUserSummaryService
{
    private static readonly IReadOnlyDictionary<Guid, UserSummaryDto> Empty = new Dictionary<Guid, UserSummaryDto>();

    public async Task<IReadOnlyDictionary<Guid, UserSummaryDto>> GetAsync(Guid viewerId, IEnumerable<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return Empty;

        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new UserRow(
                u.Id, u.Username, u.DisplayName, u.AvatarMediaId, u.IsDeleted, u.LastSeenAt,
                u.Privacy.LastSeen, u.Privacy.ProfilePhoto))
            .ToListAsync(cancellationToken);
        if (users.Count == 0) return Empty;

        var found = users.Select(u => u.Id).ToList();
        var myContacts = await db.Contacts.AsNoTracking()
            .Where(c => c.OwnerId == viewerId && found.Contains(c.ContactUserId))
            .Select(c => new { c.ContactUserId, c.CustomName })
            .ToDictionaryAsync(c => c.ContactUserId, c => c.CustomName, cancellationToken);
        var haveMeAsContact = (await db.Contacts.AsNoTracking()
                .Where(c => c.ContactUserId == viewerId && found.Contains(c.OwnerId))
                .Select(c => c.OwnerId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var blockedMe = (await db.Blocks.AsNoTracking()
                .Where(b => b.BlockedId == viewerId && found.Contains(b.BlockerId))
                .Select(b => b.BlockerId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var now = clock.UtcNow;
        var result = new Dictionary<Guid, UserSummaryDto>(users.Count);
        foreach (var user in users)
            result[user.Id] = Build(viewerId, user, myContacts, haveMeAsContact.Contains(user.Id), blockedMe.Contains(user.Id), now);
        return result;
    }

    public async Task<UserSummaryDto?> GetOneAsync(Guid viewerId, Guid userId, CancellationToken cancellationToken = default)
    {
        var map = await GetAsync(viewerId, [userId], cancellationToken);
        return map.GetValueOrDefault(userId);
    }

    public async Task<PresenceDto> GetPresenceAsync(Guid viewerId, Guid userId, CancellationToken cancellationToken = default)
    {
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.IsDeleted,
                u.LastSeenAt,
                Level = u.Privacy.LastSeen,
                BlockedViewer = db.Blocks.Any(b => b.BlockerId == userId && b.BlockedId == viewerId),
                HasViewerInContacts = db.Contacts.Any(c => c.OwnerId == userId && c.ContactUserId == viewerId),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return PresenceCalculator.LongAgo;

        var facts = new PresenceFacts(row.Level, viewerId == userId, row.IsDeleted, row.BlockedViewer,
            row.HasViewerInContacts, presence.IsOnline(userId), row.LastSeenAt);
        return PresenceCalculator.For(facts, clock.UtcNow);
    }

    private UserSummaryDto Build(Guid viewerId, UserRow user, Dictionary<Guid, string?> myContacts, bool ownerHasViewer, bool ownerBlockedViewer, DateTimeOffset now)
    {
        var isSelf = user.Id == viewerId;
        string? contactName = null;
        var isContact = !isSelf && myContacts.TryGetValue(user.Id, out contactName);
        ownerBlockedViewer &= !isSelf;

        var facts = new PresenceFacts(user.LastSeenLevel, isSelf, user.IsDeleted, ownerBlockedViewer, ownerHasViewer,
            !user.IsDeleted && presence.IsOnline(user.Id), user.LastSeenAt);
        var photoVisible = isSelf || (!ownerBlockedViewer && PrivacyRules.Allows(user.PhotoLevel, ownerHasViewer));

        return new UserSummaryDto
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            ContactName = isContact ? contactName : null,
            AvatarMediaId = !user.IsDeleted && photoVisible ? user.AvatarMediaId : null,
            Presence = PresenceCalculator.For(facts, now),
            IsContact = isContact,
            IsDeleted = user.IsDeleted,
        };
    }

    private sealed record UserRow(
        Guid Id,
        string Username,
        string DisplayName,
        Guid? AvatarMediaId,
        bool IsDeleted,
        DateTimeOffset? LastSeenAt,
        PrivacyLevel LastSeenLevel,
        PrivacyLevel PhotoLevel);
}
