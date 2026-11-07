using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Mapping;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Application.Messaging.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Stickers;

/// <summary>
/// Shared plumbing of the sticker services: the per-user revision rows and their locks, the DTO builders and publishing
/// StickersChanged. Lock order everywhere: sticker packs first (a pack's edit takes it FOR UPDATE, a user's change that
/// adds rows pointing into a pack takes it FOR SHARE), then <c>sticker_preferences</c> rows in user id order. So a pack
/// edit that bumps many users never deadlocks with those users' own changes.
/// </summary>
internal sealed class StickerStates(
    IAppDbContext db,
    IPresenceService presence,
    MessagingEvents events,
    ILogger<StickerStates> logger)
{
    /// <summary>Creates my row on the first change (race-free), then locks it for the rest of the transaction.</summary>
    public async Task<Domain.Entities.StickerPreferences> LockAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO sticker_preferences (user_id, revision, updated_at)
            VALUES ({userId}, 0, {now})
            ON CONFLICT (user_id) DO NOTHING
            """, ct);
        // SqlQuery over non-composable SQL: materialize with ToListAsync (see ChatSequence).
        await db.Database
            .SqlQuery<Guid>($"SELECT user_id AS \"Value\" FROM sticker_preferences WHERE user_id = {userId} FOR UPDATE")
            .ToListAsync(ct);
        return await db.StickerPreferences.FirstAsync(p => p.UserId == userId, ct);
    }

    /// <summary>Bumps the revision of every listed user (rows created when missing, locked in user id order).</summary>
    public async Task BumpAsync(IReadOnlyCollection<Guid> userIds, DateTimeOffset now, CancellationToken ct)
    {
        var ids = userIds.Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0) return;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO sticker_preferences (user_id, revision, updated_at)
            SELECT u, 0, {now} FROM unnest({ids}) AS u
            ON CONFLICT (user_id) DO NOTHING
            """, ct);
        await db.Database
            .SqlQuery<Guid>($"SELECT user_id AS \"Value\" FROM sticker_preferences WHERE user_id = ANY({ids}) ORDER BY user_id FOR UPDATE")
            .ToListAsync(ct);
        await db.StickerPreferences.Where(p => ids.Contains(p.UserId))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Revision, p => p.Revision + 1).SetProperty(p => p.UpdatedAt, now), ct);
    }

    /// <summary>Shares the packs' rows (in id order) before rows pointing into them are added: their edits wait meanwhile.</summary>
    public async Task SharePacksAsync(IReadOnlyCollection<Guid> packIds, CancellationToken ct)
    {
        var ids = packIds.Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0) return;
        await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM sticker_packs WHERE id = ANY({ids}) ORDER BY id FOR SHARE")
            .ToListAsync(ct);
    }

    /// <summary>Locks a pack for an edit; null when it does not exist.</summary>
    public async Task<Domain.Entities.StickerPack?> LockPackAsync(Guid packId, CancellationToken ct)
    {
        var found = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM sticker_packs WHERE id = {packId} FOR UPDATE")
            .ToListAsync(ct);
        return found.Count == 0 ? null : await db.StickerPacks.FirstAsync(p => p.Id == packId, ct);
    }

    /// <summary>
    /// The user's state. <paramref name="revision"/>: the revision when known (built under the lock); else it is read
    /// first, so a change committed between the reads shows up with an older revision, which the apps replace when that
    /// change's event arrives (the other way round they would keep a stale state).
    /// </summary>
    public async Task<StickerStateDto> BuildAsync(Guid userId, long? revision, CancellationToken ct)
    {
        var rev = revision ?? await db.StickerPreferences.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (long?)p.Revision)
            .FirstOrDefaultAsync(ct) ?? 0;

        var installedIds = await db.InstalledStickerPacks.AsNoTracking()
            .Where(i => i.UserId == userId)
            .OrderBy(i => i.Position).ThenByDescending(i => i.InstalledAt).ThenBy(i => i.PackId)
            .Select(i => i.PackId)
            .ToListAsync(ct);
        var mineIds = await db.StickerPacks.AsNoTracking()
            .Where(p => p.CreatorId == userId)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var refs = await LoadRefsAsync(installedIds.Concat(mineIds).Distinct().ToList(), ct);

        var favoriteIds = await db.FavoriteStickers.AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.AddedAt).ThenBy(f => f.StickerId)
            .Take(Limits.MaxFavoriteStickers)
            .Select(f => f.StickerId)
            .ToListAsync(ct);
        var recentIds = await db.RecentStickers.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.UsedAt).ThenBy(r => r.StickerId)
            .Take(Limits.MaxRecentStickers)
            .Select(r => r.StickerId)
            .ToListAsync(ct);
        var stickers = await LoadStickersAsync(favoriteIds.Concat(recentIds).Distinct().ToList(), ct);

        var gifRows = await db.SavedGifs.AsNoTracking()
            .Where(g => g.UserId == userId)
            .OrderByDescending(g => g.SavedAt).ThenBy(g => g.MediaId)
            .Take(Limits.MaxSavedGifs)
            .Select(g => new { g.MediaId, g.SavedAt })
            .ToListAsync(ct);
        var gifIds = gifRows.Select(g => g.MediaId).ToList();
        // The tiny blurred placeholder stays out: the panel shows the posters, and the state goes to every device often.
        var gifMedia = gifIds.Count == 0
            ? new Dictionary<Guid, MediaDto>()
            : await db.Media.AsNoTracking().Where(m => gifIds.Contains(m.Id)).Select(MediaMapping.ProjectionWithoutMini).ToDictionaryAsync(m => m.Id, ct);
        // Still mine to send: my own upload, or in a message I can see (as IMediaAccessService decides for one media).
        var visible = MediaAccessService.AttachmentsVisibleTo(db, userId);
        var available = gifIds.Count == 0
            ? []
            : (await db.Media.AsNoTracking()
                .Where(m => gifIds.Contains(m.Id) && (m.OwnerId == userId || visible.Contains(m.Id)))
                .Select(m => m.Id)
                .ToListAsync(ct)).ToHashSet();

        return new StickerStateDto
        {
            Revision = rev,
            Installed = installedIds.Select(refs.GetValueOrDefault).OfType<StickerPackRefDto>().ToList(),
            Mine = mineIds.Select(refs.GetValueOrDefault).OfType<StickerPackRefDto>().ToList(),
            Favorites = favoriteIds.Select(stickers.GetValueOrDefault).OfType<StickerDto>().ToList(),
            Recent = recentIds.Select(stickers.GetValueOrDefault).OfType<StickerDto>().ToList(),
            Gifs = gifRows
                .Where(g => gifMedia.ContainsKey(g.MediaId))
                .Select(g => new SavedGifDto { Media = gifMedia[g.MediaId], SavedAt = g.SavedAt, Unavailable = !available.Contains(g.MediaId) })
                .ToList(),
        };
    }

    /// <summary>A pack with its stickers in order; null when it does not exist.</summary>
    public async Task<StickerPackDto?> LoadPackAsync(Guid packId, CancellationToken ct)
    {
        var pack = await db.StickerPacks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == packId, ct);
        if (pack is null) return null;
        var ids = await db.Stickers.AsNoTracking()
            .Where(s => s.PackId == packId)
            .OrderBy(s => s.Position).ThenBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);
        var stickers = await LoadStickersAsync(ids, ct);
        return new StickerPackDto
        {
            Id = pack.Id,
            Slug = pack.Slug,
            Title = pack.Title,
            CreatorId = pack.CreatorId,
            Version = pack.Version,
            Stickers = ids.Select(stickers.GetValueOrDefault).OfType<StickerDto>().ToList(),
            CreatedAt = pack.CreatedAt,
            UpdatedAt = pack.UpdatedAt,
        };
    }

    public async Task<Dictionary<Guid, StickerDto>> LoadStickersAsync(IReadOnlyCollection<Guid> stickerIds, CancellationToken ct)
    {
        if (stickerIds.Count == 0) return [];
        var ids = stickerIds.ToList();
        var rows = await db.Stickers.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.PackId, s.Emoji, s.MediaId })
            .ToListAsync(ct);
        var mediaIds = rows.Select(r => r.MediaId).Distinct().ToList();
        var media = mediaIds.Count == 0
            ? new Dictionary<Guid, MediaDto>()
            : await db.Media.AsNoTracking().Where(m => mediaIds.Contains(m.Id)).Select(MediaMapping.Projection).ToDictionaryAsync(m => m.Id, ct);
        var result = new Dictionary<Guid, StickerDto>(rows.Count);
        foreach (var row in rows)
        {
            if (!media.TryGetValue(row.MediaId, out var dto)) continue;
            result[row.Id] = new StickerDto { Id = row.Id, PackId = row.PackId, Emoji = row.Emoji.ToList(), Media = dto };
        }
        return result;
    }

    private async Task<Dictionary<Guid, StickerPackRefDto>> LoadRefsAsync(List<Guid> packIds, CancellationToken ct)
    {
        if (packIds.Count == 0) return [];
        var rows = await db.StickerPacks.AsNoTracking()
            .Where(p => packIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.Slug,
                p.Title,
                p.CreatorId,
                p.Version,
                Count = db.Stickers.Count(s => s.PackId == p.Id),
                CoverId = db.Stickers.Where(s => s.PackId == p.Id)
                    .OrderBy(s => s.Position).ThenBy(s => s.CreatedAt).ThenBy(s => s.Id)
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var covers = await LoadStickersAsync(rows.Where(r => r.CoverId is not null).Select(r => r.CoverId!.Value).ToList(), ct);
        return rows.ToDictionary(r => r.Id, r => new StickerPackRefDto
        {
            Id = r.Id,
            Slug = r.Slug,
            Title = r.Title,
            CreatorId = r.CreatorId,
            Version = r.Version,
            StickerCount = r.Count,
            Cover = r.CoverId is { } cover ? covers.GetValueOrDefault(cover) : null,
        });
    }

    /// <summary>
    /// StickersChanged to each user's devices (after the commit): <paramref name="known"/> is sent as it is to its owner;
    /// the others get their state built now — users with no open connection are skipped (their apps load the state when
    /// they connect again).
    /// </summary>
    public async Task PublishAsync(IEnumerable<Guid> userIds, Guid? knownUserId = null, StickerStateDto? known = null)
    {
        foreach (var userId in userIds.Distinct())
        {
            StickerStateDto state;
            if (userId == knownUserId && known is not null)
            {
                state = known;
            }
            else
            {
                if (!presence.IsConnected(userId)) continue;
                try
                {
                    state = await BuildAsync(userId, null, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "The sticker state of user {UserId} could not be built for StickersChanged", userId);
                    continue;
                }
            }
            await events.ToUserAsync(userId, "StickersChanged", c => c.StickersChanged(state));
        }
    }
}
