using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Messaging.Services;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Stickers;

/// <summary>
/// My stickers and GIFs (<see cref="IStickerService"/>, <see cref="IStickerRecents"/>). Every change runs in a transaction
/// that first shares the packs it points into, then locks my <see cref="StickerPreferences"/> row, so changes from several
/// devices apply one after another; a real change bumps the revision and sends the whole new state to all my devices.
/// </summary>
internal sealed class StickerService(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    StickerStates states,
    StickerRecentsPublisher recentsPublisher,
    ILogger<StickerService> logger) : IStickerService, IStickerRecents
{
    public Task<StickerStateDto> GetStateAsync(CancellationToken cancellationToken = default) =>
        states.BuildAsync(currentUser.UserId, null, cancellationToken);

    public Task<StickerStateDto> InstallAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        return MutateAsync(me, [packId], async (now, ct) =>
        {
            if (!await db.StickerPacks.AnyAsync(p => p.Id == packId, ct)) throw new NotFoundException(ErrorCodes.StickerPackNotFound);
            var installed = await db.InstalledStickerPacks.Where(i => i.UserId == me).Select(i => new { i.PackId, i.Position }).ToListAsync(ct);
            if (installed.Any(i => i.PackId == packId)) return false;
            if (installed.Count >= Limits.MaxInstalledStickerPacks) throw new BadRequestException(ErrorCodes.StickerPackLimit);
            db.InstalledStickerPacks.Add(new InstalledStickerPack
            {
                UserId = me,
                PackId = packId,
                Position = installed.Count == 0 ? 0 : installed.Min(i => i.Position) - 1,
                InstalledAt = now,
            });
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public Task<StickerStateDto> UninstallAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        return MutateAsync(me, [], async (_, ct) =>
            await db.InstalledStickerPacks.Where(i => i.UserId == me && i.PackId == packId).ExecuteDeleteAsync(ct) > 0,
            cancellationToken);
    }

    public Task<StickerStateDto> ReorderInstalledAsync(ReorderStickerPacksRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requested = request.PackIds ?? [];
        if (requested.Count is 0 or > Limits.MaxInstalledStickerPacks)
            throw MessagingErrors.Validation("packIds", $"List 1 to {Limits.MaxInstalledStickerPacks} packs.");
        if (requested.Contains(Guid.Empty) || requested.Distinct().Count() != requested.Count)
            throw MessagingErrors.Validation("packIds", "Pack ids must be distinct and not empty.");
        var me = currentUser.UserId;
        return MutateAsync(me, [], async (_, ct) =>
        {
            var rows = await db.InstalledStickerPacks.Where(i => i.UserId == me)
                .OrderBy(i => i.Position).ThenByDescending(i => i.InstalledAt).ThenBy(i => i.PackId)
                .ToListAsync(ct);
            var current = rows.Select(r => r.PackId).ToList();
            var arranged = StickerRules.Arrange(current, requested);
            var changed = false;
            var byId = rows.ToDictionary(r => r.PackId);
            for (var i = 0; i < arranged.Count; i++)
            {
                var row = byId[arranged[i]];
                if (row.Position == i) continue;
                row.Position = i;
                changed = true;
            }
            // Positions are renumbered even when the order stays, but only an order that changed counts as a change.
            var moved = !current.SequenceEqual(arranged);
            if (changed) await db.SaveChangesAsync(ct);
            return moved;
        }, cancellationToken);
    }

    public async Task<StickerStateDto> SetFavoriteAsync(Guid stickerId, bool favorite, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        if (!favorite)
        {
            return await MutateAsync(me, [], async (_, ct) =>
                await db.FavoriteStickers.Where(f => f.UserId == me && f.StickerId == stickerId).ExecuteDeleteAsync(ct) > 0,
                cancellationToken);
        }

        var packId = await db.Stickers.AsNoTracking().Where(s => s.Id == stickerId).Select(s => (Guid?)s.PackId).FirstOrDefaultAsync(cancellationToken)
                     ?? throw new NotFoundException(ErrorCodes.StickerNotFound);
        return await MutateAsync(me, [packId], async (now, ct) =>
        {
            // Under the pack's share lock: deleted meanwhile means gone.
            if (!await db.Stickers.AnyAsync(s => s.Id == stickerId, ct)) throw new NotFoundException(ErrorCodes.StickerNotFound);
            var favorites = await db.FavoriteStickers.Where(f => f.UserId == me).OrderByDescending(f => f.AddedAt).ThenBy(f => f.StickerId).ToListAsync(ct);
            if (favorites.Any(f => f.StickerId == stickerId)) return false;
            db.FavoriteStickers.Add(new FavoriteSticker { UserId = me, StickerId = stickerId, AddedAt = now });
            // The oldest go once there are more than the limit (the new one is first).
            foreach (var old in favorites.Skip(Limits.MaxFavoriteStickers - 1)) db.FavoriteStickers.Remove(old);
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task<StickerStateDto> SaveGifAsync(SaveGifRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(request.ChatId, me, cancellationToken);
        var message = await db.VisibleMessages(member).AsNoTracking()
            .Where(m => m.Id == request.MessageId && m.DeletedAt == null)
            .Select(m => new { m.Id })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.MessageNotFound);
        // Saving is taking a copy out of the chat, like forwarding.
        if (member.ProtectContent) throw new ForbiddenException(ErrorCodes.ForwardRestricted);
        var mediaId = await (
                from a in db.MessageAttachments
                join m in db.Media on a.MediaId equals m.Id
                where a.MessageId == message.Id && m.IsAnimation && m.Kind == MediaKind.Video && m.Status == MediaStatus.Ready
                orderby a.Order
                select (Guid?)m.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw MessagingErrors.Validation("messageId", "The message has no GIF.");

        return await MutateAsync(me, [], async (now, ct) =>
        {
            var gifs = await db.SavedGifs.Where(g => g.UserId == me).OrderByDescending(g => g.SavedAt).ThenBy(g => g.MediaId).ToListAsync(ct);
            if (gifs.Count > 0 && gifs[0].MediaId == mediaId) return false;
            var existing = gifs.FirstOrDefault(g => g.MediaId == mediaId);
            if (existing is not null)
            {
                // Saved again: it moves to the front.
                existing.SavedAt = now;
            }
            else
            {
                db.SavedGifs.Add(new SavedGif { UserId = me, MediaId = mediaId, SavedAt = now });
                foreach (var old in gifs.Skip(Limits.MaxSavedGifs - 1)) db.SavedGifs.Remove(old);
            }
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public Task<StickerStateDto> RemoveGifAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        return MutateAsync(me, [], async (_, ct) =>
            await db.SavedGifs.Where(g => g.UserId == me && g.MediaId == mediaId).ExecuteDeleteAsync(ct) > 0,
            cancellationToken);
    }

    public async Task TouchAsync(Guid userId, Guid stickerId)
    {
        try
        {
            var packId = await db.Stickers.AsNoTracking().Where(s => s.Id == stickerId).Select(s => (Guid?)s.PackId).FirstOrDefaultAsync();
            if (packId is null) return;
            await MutateAsync(userId, [packId.Value], async (now, ct) =>
            {
                if (!await db.Stickers.AnyAsync(s => s.Id == stickerId, ct)) return false;
                var recent = await db.RecentStickers.Where(r => r.UserId == userId).OrderByDescending(r => r.UsedAt).ThenBy(r => r.StickerId).ToListAsync(ct);
                var existing = recent.FirstOrDefault(r => r.StickerId == stickerId);
                // Already the newest: the list looks the same, so nothing is announced.
                var first = recent.Count > 0 && recent[0].StickerId == stickerId;
                if (existing is not null)
                {
                    existing.UsedAt = now;
                }
                else
                {
                    db.RecentStickers.Add(new RecentSticker { UserId = userId, StickerId = stickerId, UsedAt = now });
                    foreach (var old in recent.Skip(Limits.MaxRecentStickers - 1)) db.RecentStickers.Remove(old);
                }
                await db.SaveChangesAsync(ct);
                return !first;
            }, CancellationToken.None, publishSoon: true);
        }
        catch (Exception ex)
        {
            // The message went out; a missed recent sticker is cosmetic.
            logger.LogWarning(ex, "Recent stickers of user {UserId} could not be updated", userId);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Runs one change of <paramref name="userId"/>'s state: the packs it points into are shared first, then the user's row
    /// is locked. <paramref name="change"/> returns whether the state changed (only then the revision moves and the state
    /// goes to the user's devices — at once, or with <paramref name="publishSoon"/> once for a burst of such changes).
    /// </summary>
    private async Task<StickerStateDto> MutateAsync(Guid userId, IReadOnlyCollection<Guid> packs,
        Func<DateTimeOffset, CancellationToken, Task<bool>> change, CancellationToken ct, bool publishSoon = false)
    {
        var now = clock.UtcNow;
        StickerStateDto state;
        bool changed;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await states.SharePacksAsync(packs, ct);
            var prefs = await states.LockAsync(userId, now, ct);
            changed = await change(now, ct);
            if (changed)
            {
                prefs.Revision++;
                prefs.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }
            // Built while the lock is held: exactly the state this change left.
            state = await states.BuildAsync(userId, prefs.Revision, ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        if (changed && publishSoon) recentsPublisher.Schedule(userId);
        else if (changed) await states.PublishAsync([userId], userId, state);
        return state;
    }
}
