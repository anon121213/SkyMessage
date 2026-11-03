using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>
/// Where a chat lands in a member's list when something arrives, by the member's <c>ChatListPreferences</c> (SPEC §6.3
/// "Folders and the archive"). A chat from a stranger — a private chat whose first message (or call) comes from someone
/// not in the member's contacts, a group such a person adds them to — starts in the archive for members who want that.
/// A new message brings an archived chat back for members who do not keep archived chats archived, unless it is muted
/// for them or it came from a stranger (still one) while they send those to the archive. Members without a preferences
/// row have the defaults: new chats stay in the list, archived chats stay archived.
/// </summary>
internal static class ChatPlacement
{
    /// <summary>Of <paramref name="userIds"/>, those for whom a new chat from <paramref name="fromUserId"/> starts in the archive.</summary>
    public static async Task<HashSet<Guid>> ArchivedOnArrivalAsync(IAppDbContext db, IReadOnlyCollection<Guid> userIds, Guid? fromUserId,
        CancellationToken ct)
    {
        if (fromUserId is not { } from) return [];
        var ids = userIds.Where(id => id != from).Distinct().ToList();
        if (ids.Count == 0) return [];
        var archived = await db.ChatListPreferences.AsNoTracking()
            .Where(p => ids.Contains(p.UserId) && p.ArchiveNewFromStrangers
                        && !db.Contacts.Any(c => c.OwnerId == p.UserId && c.ContactUserId == from))
            .Select(p => p.UserId)
            .ToListAsync(ct);
        return archived.ToHashSet();
    }

    /// <summary>Puts the chat into the archive of these members (inside the transaction that made it appear for them).</summary>
    public static async Task ArchiveAsync(IAppDbContext db, Guid chatId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return;
        var ids = userIds.ToList();
        await db.ChatMembers.Where(m => m.ChatId == chatId && ids.Contains(m.UserId) && !m.IsArchived)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsArchived, true), ct);
    }

    /// <summary>
    /// Inside the transaction of a new message from <paramref name="senderId"/> (chat row locked): takes the chat out of the
    /// archive of the members it comes back to and returns them (they get ChatSettingsChanged after the commit).
    /// </summary>
    public static async Task<List<Guid>> UnarchiveForMessageAsync(IAppDbContext db, Guid chatId, ChatType type, Guid senderId, DateTimeOffset now,
        CancellationToken ct)
    {
        var candidates = await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.UserId != senderId && m.IsArchived && !m.IsHidden
                        && (m.MutedUntil == null || m.MutedUntil <= now)
                        && db.ChatListPreferences.Any(p => p.UserId == m.UserId && !p.KeepArchived))
            .Select(m => new
            {
                m.UserId,
                m.InvitedById,
                StrangersToArchive = db.ChatListPreferences.Any(p => p.UserId == m.UserId && p.ArchiveNewFromStrangers),
            })
            .ToListAsync(ct);
        if (candidates.Count == 0) return [];

        // Who the chat came from: the other person of a private chat, whoever added the member to a group (none when they
        // joined by a link or created it).
        Guid? CameFrom(Guid? invitedBy) => type == ChatType.Private ? senderId : invitedBy;
        var checks = candidates.Where(c => c.StrangersToArchive && CameFrom(c.InvitedById) is not null).ToList();
        var known = new HashSet<(Guid Owner, Guid Contact)>();
        if (checks.Count > 0)
        {
            var owners = checks.Select(c => c.UserId).Distinct().ToList();
            var others = checks.Select(c => CameFrom(c.InvitedById)!.Value).Distinct().ToList();
            var rows = await db.Contacts.AsNoTracking()
                .Where(c => owners.Contains(c.OwnerId) && others.Contains(c.ContactUserId))
                .Select(c => new { c.OwnerId, c.ContactUserId })
                .ToListAsync(ct);
            known = rows.Select(r => (r.OwnerId, r.ContactUserId)).ToHashSet();
        }

        var back = candidates
            .Where(c => !c.StrangersToArchive || CameFrom(c.InvitedById) is not { } from || known.Contains((c.UserId, from)))
            .Select(c => c.UserId)
            .ToList();
        if (back.Count == 0) return [];
        await db.ChatMembers.Where(m => m.ChatId == chatId && back.Contains(m.UserId))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsArchived, false), ct);
        return back;
    }

    /// <summary>After the commit: each member whose chat moved hears about it with their own settings of the chat.</summary>
    public static async Task PublishSettingsAsync(IAppDbContext db, MessagingEvents events, Guid chatId, IReadOnlyCollection<Guid> userIds)
    {
        if (userIds.Count == 0) return;
        var ids = userIds.ToList();
        var rows = await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId && ids.Contains(m.UserId))
            .Select(m => new
            {
                m.UserId,
                Settings = new ChatUserSettingsDto
                {
                    IsPinned = m.IsPinned,
                    PinnedAt = m.PinnedAt,
                    PinOrder = m.IsPinned ? m.PinOrder : 0,
                    IsArchived = m.IsArchived,
                    MutedUntil = m.MutedUntil,
                    ContactBarDismissed = m.ContactBarDismissed,
                },
            })
            .ToListAsync(CancellationToken.None);
        foreach (var row in rows)
        {
            await events.ToUserAsync(row.UserId, "ChatSettingsChanged",
                c => c.ChatSettingsChanged(new ChatSettingsChangedEvent { ChatId = chatId, Settings = row.Settings }));
        }
    }
}
