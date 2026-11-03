using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>
/// The caller's address book (one-directional, like a phone book). Every operation is idempotent and
/// publishes <c>ContactsChanged</c> to the caller's devices. Contacts that phone-book sync added stay marked as
/// synced (<see cref="ContactSource.PhoneSync"/>) until the user adds or renames them by hand, so "remove synced
/// contacts" never takes away people the user chose.
/// </summary>
public sealed class ContactService(
    IAppDbContext db,
    IUserSummaryService summaries,
    IRealtimeNotifier notifier,
    IPresenceService presence,
    IClock clock,
    ILogger<ContactService> logger)
{
    private static readonly StringComparer NameComparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);

    public async Task<IReadOnlyList<ContactDto>> ListAsync(Guid ownerId, CancellationToken ct)
    {
        var rows = await db.Contacts.AsNoTracking()
            .Where(c => c.OwnerId == ownerId && !c.ContactUser!.IsDeleted)
            .Select(c => new ContactRow(c.ContactUserId, c.CustomName, c.CreatedAt, c.Source))
            .ToListAsync(ct);
        var contacts = await BuildAsync(ownerId, rows, ct);
        return contacts.OrderBy(c => c.User.ContactName ?? c.User.DisplayName, NameComparer).ToList();
    }

    public async Task<ContactDto> AddAsync(Guid ownerId, AddContactRequest request, CancellationToken ct)
    {
        if (request.UserId == ownerId)
            throw new BadRequestException(ErrorCodes.CannotTargetSelf);
        var contacts = await UpsertAsync(ownerId, [request], overwriteNames: true, ContactSource.Manual, ct);
        return contacts.FirstOrDefault() ?? throw new NotFoundException(ErrorCodes.UserNotFound);
    }

    /// <summary>
    /// Phone-book import (confirmed by the user) or sync (<see cref="AddContactsBulkRequest.Synced"/>): unknown users
    /// and self are skipped; existing custom names are kept.
    /// </summary>
    public Task<IReadOnlyList<ContactDto>> AddBulkAsync(Guid ownerId, AddContactsBulkRequest request, CancellationToken ct) =>
        UpsertAsync(ownerId, request.Contacts ?? [], overwriteNames: false, request.Synced ? ContactSource.PhoneSync : ContactSource.Manual, ct);

    /// <summary>Renames (or adds) a contact; null or empty clears the custom name.</summary>
    public async Task<ContactDto> UpdateAsync(Guid ownerId, Guid userId, UpdateContactRequest request, CancellationToken ct)
    {
        if (userId == ownerId)
            throw new BadRequestException(ErrorCodes.CannotTargetSelf);
        var name = CleanName(request.CustomName);
        var existing = await db.Contacts.FirstOrDefaultAsync(c => c.OwnerId == ownerId && c.ContactUserId == userId, ct);
        if (existing is null)
        {
            var added = await UpsertAsync(ownerId, [new AddContactRequest { UserId = userId, CustomName = name }], overwriteNames: true,
                ContactSource.Manual, ct);
            return added.FirstOrDefault() ?? throw new NotFoundException(ErrorCodes.UserNotFound);
        }

        // Renaming a synced contact makes it the user's own.
        if (existing.CustomName != name || existing.Source != ContactSource.Manual)
        {
            existing.CustomName = name;
            existing.Source = ContactSource.Manual;
            await db.SaveChangesAsync(ct);
            await PublishAsync(ownerId, membershipChangedFor: []);
        }
        var built = await BuildAsync(ownerId, [new ContactRow(existing.ContactUserId, existing.CustomName, existing.CreatedAt, existing.Source)], ct);
        return built.FirstOrDefault() ?? throw new NotFoundException(ErrorCodes.UserNotFound);
    }

    public async Task RemoveAsync(Guid ownerId, Guid userId, CancellationToken ct)
    {
        if (userId == ownerId)
            throw new BadRequestException(ErrorCodes.CannotTargetSelf);
        var removed = await db.Contacts.Where(c => c.OwnerId == ownerId && c.ContactUserId == userId).ExecuteDeleteAsync(ct);
        if (removed > 0)
            await PublishAsync(ownerId, membershipChangedFor: [userId]);
    }

    /// <summary>Removes every contact phone-book sync added (and the user has not adopted since).</summary>
    public async Task<RemoveSyncedContactsResultDto> RemoveSyncedAsync(Guid ownerId, CancellationToken ct)
    {
        var synced = db.Contacts.Where(c => c.OwnerId == ownerId && c.Source == ContactSource.PhoneSync);
        var ids = await synced.AsNoTracking().Select(c => c.ContactUserId).ToListAsync(ct);
        if (ids.Count == 0) return new RemoveSyncedContactsResultDto();
        var removed = await synced.Where(c => ids.Contains(c.ContactUserId)).ExecuteDeleteAsync(ct);
        if (removed > 0)
            await PublishAsync(ownerId, membershipChangedFor: ids);
        return new RemoveSyncedContactsResultDto { Removed = removed };
    }

    /// <param name="source">
    /// How new contacts are marked. <see cref="ContactSource.Manual"/> also adopts existing synced contacts (the user
    /// chose them); a sync leaves existing contacts as they are.
    /// </param>
    private async Task<IReadOnlyList<ContactDto>> UpsertAsync(Guid ownerId, IReadOnlyList<AddContactRequest> requests, bool overwriteNames,
        ContactSource source, CancellationToken ct)
    {
        var wanted = new Dictionary<Guid, string?>();
        foreach (var request in requests)
        {
            if (request is null || request.UserId == Guid.Empty || request.UserId == ownerId) continue;
            wanted[request.UserId] = CleanName(request.CustomName);
        }
        if (wanted.Count == 0) return [];
        var ids = wanted.Keys.ToList();

        for (var attempt = 0; ; attempt++)
        {
            var existingUsers = (await db.Users.AsNoTracking()
                    .Where(u => ids.Contains(u.Id) && !u.IsDeleted)
                    .Select(u => u.Id)
                    .ToListAsync(ct))
                .ToHashSet();
            var existing = await db.Contacts
                .Where(c => c.OwnerId == ownerId && ids.Contains(c.ContactUserId))
                .ToDictionaryAsync(c => c.ContactUserId, ct);

            var now = clock.UtcNow;
            var added = new List<Guid>();
            var renamed = false;
            foreach (var (userId, name) in wanted)
            {
                if (!existingUsers.Contains(userId)) continue;
                if (existing.TryGetValue(userId, out var contact))
                {
                    if (name is not null && contact.CustomName != name && (overwriteNames || contact.CustomName is null))
                    {
                        contact.CustomName = name;
                        renamed = true;
                    }
                    if (source == ContactSource.Manual && contact.Source != ContactSource.Manual)
                    {
                        contact.Source = ContactSource.Manual;
                        renamed = true;
                    }
                }
                else
                {
                    var created = new Contact { OwnerId = ownerId, ContactUserId = userId, CustomName = name, CreatedAt = now, Source = source };
                    db.Contacts.Add(created);
                    existing[userId] = created;
                    added.Add(userId);
                }
            }

            if (added.Count > 0 || renamed)
            {
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException) when (attempt == 0)
                {
                    // A concurrent request added some of them first: start over against the fresh state.
                    db.ChangeTracker.Clear();
                    continue;
                }
                await PublishAsync(ownerId, membershipChangedFor: added);
            }

            var rows = ids
                .Where(id => existingUsers.Contains(id) && existing.ContainsKey(id))
                .Select(id => new ContactRow(id, existing[id].CustomName, existing[id].CreatedAt, existing[id].Source))
                .ToList();
            return await BuildAsync(ownerId, rows, ct);
        }
    }

    private async Task<List<ContactDto>> BuildAsync(Guid ownerId, IReadOnlyList<ContactRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.UserId).ToList();
        var users = await summaries.GetAsync(ownerId, ids, ct);
        var mutual = (await db.Contacts.AsNoTracking()
                .Where(c => c.ContactUserId == ownerId && ids.Contains(c.OwnerId))
                .Select(c => c.OwnerId)
                .ToListAsync(ct))
            .ToHashSet();
        return rows
            .Where(r => users.ContainsKey(r.UserId))
            .Select(r => new ContactDto
            {
                User = users[r.UserId],
                CustomName = r.CustomName,
                IsMutual = mutual.Contains(r.UserId),
                AddedAt = r.AddedAt,
                IsSynced = r.Source == ContactSource.PhoneSync,
            })
            .ToList();
    }

    /// <param name="membershipChangedFor">
    /// Users added to / removed from the address book: what they may see of the owner (Contacts-level
    /// privacy) changed, and so did their own "mutual" flag.
    /// </param>
    private async Task PublishAsync(Guid ownerId, IReadOnlyCollection<Guid> membershipChangedFor)
    {
        try
        {
            await notifier.User(ownerId).ContactsChanged(new ContactsChangedEvent());
            var targets = membershipChangedFor.Where(presence.IsConnected).ToList();
            if (targets.Count == 0) return;

            await notifier.Users(targets).UserChanged(new UserRefEvent { UserId = ownerId });
            var haveOwner = await db.Contacts.AsNoTracking()
                .Where(c => c.ContactUserId == ownerId && targets.Contains(c.OwnerId))
                .Select(c => c.OwnerId)
                .ToListAsync();
            if (haveOwner.Count > 0)
                await notifier.Users(haveOwner).ContactsChanged(new ContactsChangedEvent());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish contact changes of {UserId}", ownerId);
        }
    }

    private static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= Limits.ContactNameMaxLength ? trimmed : trimmed[..Limits.ContactNameMaxLength];
    }

    private sealed record ContactRow(Guid UserId, string? CustomName, DateTimeOffset AddedAt, ContactSource Source);
}
