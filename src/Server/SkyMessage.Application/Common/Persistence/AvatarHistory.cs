using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Mapping;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Common.Persistence;

/// <summary>
/// Avatar histories of profiles (Identity) and groups (Messaging): every photo that became the avatar is kept
/// (at most <see cref="Limits.AvatarHistoryMax"/>, the oldest go first); setting one again moves it to the front.
/// The current avatar itself stays on the user / chat row.
/// </summary>
public static class AvatarHistory
{
    public static IQueryable<AvatarPhoto> OfUser(this IAppDbContext db, Guid userId) => db.AvatarPhotos.Where(a => a.UserId == userId);

    public static IQueryable<AvatarPhoto> OfChat(this IAppDbContext db, Guid chatId) => db.AvatarPhotos.Where(a => a.ChatId == chatId);

    /// <summary>Puts the photo at the front of a user's history (adds it when new) and trims the oldest. Saves.</summary>
    public static Task RecordForUserAsync(IAppDbContext db, Guid userId, Guid mediaId, DateTimeOffset now, CancellationToken ct) =>
        RecordAsync(db, db.OfUser(userId), new AvatarPhoto { UserId = userId, MediaId = mediaId, SetById = userId, SetAt = now }, ct);

    /// <summary>Puts the photo at the front of a group's history (adds it when new) and trims the oldest. Saves.</summary>
    public static Task RecordForChatAsync(IAppDbContext db, Guid chatId, Guid mediaId, Guid actorId, DateTimeOffset now, CancellationToken ct) =>
        RecordAsync(db, db.OfChat(chatId), new AvatarPhoto { ChatId = chatId, MediaId = mediaId, SetById = actorId, SetAt = now }, ct);

    private static async Task RecordAsync(IAppDbContext db, IQueryable<AvatarPhoto> history, AvatarPhoto entry, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var mediaId = entry.MediaId;
            var moved = await history.Where(a => a.MediaId == mediaId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.SetAt, entry.SetAt).SetProperty(a => a.SetById, entry.SetById), ct);
            if (moved > 0) break;

            db.AvatarPhotos.Add(entry);
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // The same photo was set at the same moment elsewhere: its entry exists now, move that one.
                db.ChangeTracker.Clear();
            }
        }
        await TrimAsync(history, ct);
    }

    /// <summary>Drops the entries beyond <see cref="Limits.AvatarHistoryMax"/> (oldest first).</summary>
    public static async Task TrimAsync(IQueryable<AvatarPhoto> history, CancellationToken ct)
    {
        var stale = await history.AsNoTracking()
            .OrderByDescending(a => a.SetAt)
            .ThenByDescending(a => a.Id)
            .Skip(Limits.AvatarHistoryMax)
            .Select(a => a.Id)
            .ToListAsync(ct);
        if (stale.Count > 0)
            await history.Where(a => stale.Contains(a.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Entries other than the current avatar (<paramref name="currentMediaId"/>; all of them without one).</summary>
    public static Task<int> CountEarlierAsync(IQueryable<AvatarPhoto> history, Guid? currentMediaId, CancellationToken ct) =>
        currentMediaId is { } current ? history.CountAsync(a => a.MediaId != current, ct) : history.CountAsync(ct);

    /// <summary>The newest entry other than <paramref name="exceptMediaId"/>: the avatar after that one is removed.</summary>
    public static Task<Guid?> NewestAsync(IQueryable<AvatarPhoto> history, Guid exceptMediaId, CancellationToken ct) =>
        history.AsNoTracking()
            .Where(a => a.MediaId != exceptMediaId)
            .OrderByDescending(a => a.SetAt)
            .ThenByDescending(a => a.Id)
            .Select(a => (Guid?)a.MediaId)
            .FirstOrDefaultAsync(ct);

    /// <summary>The history as shown: the current avatar first, then newest first (processed photos only).</summary>
    public static async Task<IReadOnlyList<AvatarPhotoDto>> ListAsync(IAppDbContext db, IQueryable<AvatarPhoto> history, Guid? currentMediaId,
        CancellationToken ct)
    {
        var rows = await history.AsNoTracking()
            .OrderByDescending(a => a.SetAt)
            .ThenByDescending(a => a.Id)
            .Select(a => new { a.MediaId, a.SetAt })
            .Take(Limits.AvatarHistoryMax)
            .ToListAsync(ct);
        var ids = rows.Select(r => r.MediaId).ToList();
        if (currentMediaId is { } current && !ids.Contains(current)) ids.Add(current);
        if (ids.Count == 0) return [];

        var media = await db.Media.AsNoTracking()
            .Where(m => ids.Contains(m.Id) && m.Status == MediaStatus.Ready && m.Kind == MediaKind.Photo)
            .Select(MediaMapping.Projection)
            .ToDictionaryAsync(m => m.Id, ct);

        var result = new List<AvatarPhotoDto>(ids.Count);
        if (currentMediaId is { } currentId && media.TryGetValue(currentId, out var currentMedia))
        {
            var setAt = rows.FirstOrDefault(r => r.MediaId == currentId)?.SetAt ?? currentMedia.CreatedAt;
            result.Add(new AvatarPhotoDto { Media = currentMedia, SetAt = setAt, IsCurrent = true });
        }
        foreach (var row in rows)
        {
            if (row.MediaId == currentMediaId || !media.TryGetValue(row.MediaId, out var dto)) continue;
            result.Add(new AvatarPhotoDto { Media = dto, SetAt = row.SetAt });
        }
        return result;
    }
}
