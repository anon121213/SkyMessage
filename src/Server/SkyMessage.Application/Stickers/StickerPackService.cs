using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Messaging.Services;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Stickers;

/// <summary>
/// Sticker packs (<see cref="IStickerPackService"/>). An edit locks the pack row first (edits of one pack apply one after
/// another), bumps its version, then bumps the state revision of everyone it concerns — in user id order, after the
/// pack, the same order every sticker change takes (<see cref="StickerStates"/>) — and sends them their new states after
/// the commit.
/// </summary>
internal sealed class StickerPackService(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    StickerStates states,
    IStickerMediaFactory stickerMedia) : IStickerPackService
{
    private sealed record PackEdit(bool Changed, IReadOnlyCollection<Guid> Concerned)
    {
        public static PackEdit None { get; } = new(false, []);
        public static PackEdit Done { get; } = new(true, []);
    }

    public async Task<StickerPackDto> GetAsync(Guid packId, CancellationToken cancellationToken = default) =>
        await states.LoadPackAsync(packId, cancellationToken) ?? throw new NotFoundException(ErrorCodes.StickerPackNotFound);

    public async Task<StickerPackDto> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (!StickerRules.IsValidSlug(slug)) throw new NotFoundException(ErrorCodes.StickerPackNotFound);
        var normalized = slug.ToLowerInvariant();
        var id = await db.StickerPacks.AsNoTracking().Where(p => p.Slug == normalized).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken)
                 ?? throw new NotFoundException(ErrorCodes.StickerPackNotFound);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<StickerPackDto> CreateAsync(CreateStickerPackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id == Guid.Empty) throw MessagingErrors.Validation("id", "Pack id is required.");
        var title = ValidTitle(request.Title);
        var me = currentUser.UserId;
        var now = clock.UtcNow;

        StickerStateDto? state = null;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var prefs = await states.LockAsync(me, now, cancellationToken);
            var existing = await db.StickerPacks.AsNoTracking().Where(p => p.Id == request.Id).Select(p => (Guid?)p.CreatorId).FirstOrDefaultAsync(cancellationToken);
            if (existing is { } creator)
            {
                // A repeat of my own create is harmless; anyone else's id is not mine to take.
                if (creator != me) throw new ConflictException(ErrorCodes.Conflict, "This pack id is taken.");
            }
            else
            {
                if (await db.StickerPacks.CountAsync(p => p.CreatorId == me, cancellationToken) >= Limits.MaxOwnStickerPacks)
                    throw new BadRequestException(ErrorCodes.StickerPackLimit);
                var slug = StickerRules.NewSlug();
                while (await db.StickerPacks.AnyAsync(p => p.Slug == slug, cancellationToken)) slug = StickerRules.NewSlug();
                db.StickerPacks.Add(new StickerPack
                {
                    Id = request.Id,
                    CreatorId = me,
                    Title = title,
                    Slug = slug,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                // My own pack is in my panel straight away (first).
                var top = await db.InstalledStickerPacks.Where(i => i.UserId == me).Select(i => (int?)i.Position).MinAsync(cancellationToken);
                db.InstalledStickerPacks.Add(new InstalledStickerPack { UserId = me, PackId = request.Id, Position = (top ?? 1) - 1, InstalledAt = now });
                prefs.Revision++;
                prefs.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                state = await states.BuildAsync(me, prefs.Revision, cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
        }
        db.ChangeTracker.Clear();

        if (state is not null) await states.PublishAsync([me], me, state);
        return await GetAsync(request.Id, CancellationToken.None);
    }

    public Task<StickerPackDto> RenameAsync(Guid packId, UpdateStickerPackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = ValidTitle(request.Title);
        return EditAsync(packId, (pack, _, _, _) =>
        {
            if (pack.Title == title) return Task.FromResult(PackEdit.None);
            pack.Title = title;
            return Task.FromResult(PackEdit.Done);
        }, cancellationToken);
    }

    public async Task<StickerPackDto> AddStickerAsync(Guid packId, AddStickerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id == Guid.Empty) throw MessagingErrors.Validation("id", "Sticker id is required.");
        if (request.MediaId == Guid.Empty) throw MessagingErrors.Validation("mediaId", "The sticker's image is required.");
        var emoji = ValidEmoji(request.Emoji);
        var me = currentUser.UserId;

        // A repeat (the answer was lost) changes nothing; the image is not made twice.
        var existing = await db.Stickers.AsNoTracking().Where(s => s.Id == request.Id).Select(s => (Guid?)s.PackId).FirstOrDefaultAsync(cancellationToken);
        if (existing is { } existingPack)
        {
            if (existingPack != packId) throw new ConflictException(ErrorCodes.Conflict, "This sticker id is taken.");
            return await GetAsync(packId, cancellationToken);
        }
        // Who may edit the pack is checked before the (costly) image work and again under the lock.
        var creator = await db.StickerPacks.AsNoTracking().Where(p => p.Id == packId).Select(p => (Guid?)p.CreatorId).FirstOrDefaultAsync(cancellationToken)
                      ?? throw new NotFoundException(ErrorCodes.StickerPackNotFound);
        if (creator != me) throw new ForbiddenException(ErrorCodes.NotStickerPackCreator);

        var prepared = await stickerMedia.PrepareAsync(me, request.MediaId, cancellationToken);
        var result = await EditAsync(packId, async (pack, stickers, now, ct) =>
        {
            if (stickers.Any(s => s.Id == request.Id)) return PackEdit.None;
            if (stickers.Count >= Limits.MaxStickersPerPack) throw new BadRequestException(ErrorCodes.StickerPackFull);
            var mediaId = await stickerMedia.StoreAsync(prepared, me,
                new StickerInfoDto { PackId = pack.Id, StickerId = request.Id, Emoji = StickerRules.MainEmoji(emoji) }, ct);
            db.Stickers.Add(new Sticker
            {
                Id = request.Id,
                PackId = pack.Id,
                MediaId = mediaId,
                Emoji = emoji,
                Position = stickers.Count == 0 ? 0 : stickers.Max(s => s.Position) + 1,
                CreatedAt = now,
            });
            return PackEdit.Done;
        }, cancellationToken);
        // The sticker has its own WebP now: the upload it was made from is no longer needed.
        await stickerMedia.DropSourceAsync(me, request.MediaId);
        return result;
    }

    public Task<StickerPackDto> UpdateStickerAsync(Guid packId, Guid stickerId, UpdateStickerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var emoji = ValidEmoji(request.Emoji);
        return EditAsync(packId, async (_, stickers, _, ct) =>
        {
            var sticker = stickers.FirstOrDefault(s => s.Id == stickerId) ?? throw new NotFoundException(ErrorCodes.StickerNotFound);
            if (sticker.Emoji.SequenceEqual(emoji)) return PackEdit.None;
            sticker.Emoji = emoji;
            // Sent copies show the new main emoji in their previews too.
            var main = StickerRules.MainEmoji(emoji);
            await db.Media.Where(m => m.Id == sticker.MediaId).ExecuteUpdateAsync(s => s.SetProperty(m => m.StickerEmoji, main), ct);
            return PackEdit.Done;
        }, cancellationToken);
    }

    public Task<StickerPackDto> DeleteStickerAsync(Guid packId, Guid stickerId, CancellationToken cancellationToken = default) =>
        EditAsync(packId, async (_, stickers, _, ct) =>
        {
            var sticker = stickers.FirstOrDefault(s => s.Id == stickerId);
            if (sticker is null) return PackEdit.None;
            // Their favorites and recent stickers lose it (the rows go with the sticker).
            var concerned = await StickerHoldersAsync([stickerId], ct);
            db.Stickers.Remove(sticker);
            return new PackEdit(true, concerned);
        }, cancellationToken);

    public Task<StickerPackDto> ReorderStickersAsync(Guid packId, ReorderStickersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requested = request.StickerIds ?? [];
        if (requested.Count is 0 or > Limits.MaxStickersPerPack)
            throw MessagingErrors.Validation("stickerIds", $"List 1 to {Limits.MaxStickersPerPack} stickers.");
        if (requested.Contains(Guid.Empty) || requested.Distinct().Count() != requested.Count)
            throw MessagingErrors.Validation("stickerIds", "Sticker ids must be distinct and not empty.");
        return EditAsync(packId, (_, stickers, _, _) =>
        {
            var current = stickers.Select(s => s.Id).ToList();
            var arranged = StickerRules.Arrange(current, requested);
            if (current.SequenceEqual(arranged)) return Task.FromResult(PackEdit.None);
            var byId = stickers.ToDictionary(s => s.Id);
            for (var i = 0; i < arranged.Count; i++) byId[arranged[i]].Position = i;
            return Task.FromResult(PackEdit.Done);
        }, cancellationToken);
    }

    public async Task DeleteAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        var now = clock.UtcNow;
        List<Guid> concerned;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var pack = await states.LockPackAsync(packId, cancellationToken) ?? throw new NotFoundException(ErrorCodes.StickerPackNotFound);
            if (pack.CreatorId != me) throw new ForbiddenException(ErrorCodes.NotStickerPackCreator);
            var stickerIds = await db.Stickers.Where(s => s.PackId == packId).Select(s => s.Id).ToListAsync(cancellationToken);
            concerned = (await InstallersAsync(packId, cancellationToken))
                .Concat(await StickerHoldersAsync(stickerIds, cancellationToken))
                .Append(me)
                .Distinct()
                .ToList();
            await states.BumpAsync(concerned, now, cancellationToken);
            // Its stickers, installations, favorites and recent uses go with it (ON DELETE CASCADE); the media stay for
            // the messages that show them (media cleanup takes the rest).
            db.StickerPacks.Remove(pack);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        db.ChangeTracker.Clear();
        await states.PublishAsync(concerned);
    }

    /// <summary>
    /// One edit of a pack of mine under its row lock: <paramref name="edit"/> changes the tracked pack and stickers and
    /// names the users it concerns besides the creator and the installers. A real edit bumps the pack's version and the
    /// state revision of everyone concerned, who get their new states after the commit.
    /// </summary>
    private async Task<StickerPackDto> EditAsync(Guid packId,
        Func<StickerPack, List<Sticker>, DateTimeOffset, CancellationToken, Task<PackEdit>> edit, CancellationToken ct)
    {
        var me = currentUser.UserId;
        var now = clock.UtcNow;
        List<Guid> concerned = [];
        bool changed;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var pack = await states.LockPackAsync(packId, ct) ?? throw new NotFoundException(ErrorCodes.StickerPackNotFound);
            if (pack.CreatorId != me) throw new ForbiddenException(ErrorCodes.NotStickerPackCreator);
            var stickers = await db.Stickers.Where(s => s.PackId == packId)
                .OrderBy(s => s.Position).ThenBy(s => s.CreatedAt).ThenBy(s => s.Id)
                .ToListAsync(ct);
            var result = await edit(pack, stickers, now, ct);
            changed = result.Changed;
            if (changed)
            {
                pack.Version++;
                pack.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                concerned = (await InstallersAsync(packId, ct)).Concat(result.Concerned).Append(me).Distinct().ToList();
                await states.BumpAsync(concerned, now, ct);
            }
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        if (changed) await states.PublishAsync(concerned);
        return await GetAsync(packId, CancellationToken.None);
    }

    private Task<List<Guid>> InstallersAsync(Guid packId, CancellationToken ct) =>
        db.InstalledStickerPacks.AsNoTracking().Where(i => i.PackId == packId).Select(i => i.UserId).ToListAsync(ct);

    /// <summary>Who keeps one of these stickers among their favorites or recent stickers.</summary>
    private async Task<List<Guid>> StickerHoldersAsync(IReadOnlyCollection<Guid> stickerIds, CancellationToken ct)
    {
        if (stickerIds.Count == 0) return [];
        var ids = stickerIds.ToList();
        var favorites = await db.FavoriteStickers.AsNoTracking().Where(f => ids.Contains(f.StickerId)).Select(f => f.UserId).Distinct().ToListAsync(ct);
        var recent = await db.RecentStickers.AsNoTracking().Where(r => ids.Contains(r.StickerId)).Select(r => r.UserId).Distinct().ToListAsync(ct);
        return favorites.Concat(recent).Distinct().ToList();
    }

    private static string ValidTitle(string? raw)
    {
        var title = StickerRules.NormalizeTitle(raw);
        if (!StickerRules.IsValidTitle(title))
            throw MessagingErrors.Validation("title", $"A title of 1 to {Limits.StickerPackTitleMaxLength} visible characters is required.");
        return title;
    }

    private static List<string> ValidEmoji(IReadOnlyList<string>? raw) =>
        StickerRules.NormalizeEmoji(raw)
        ?? throw MessagingErrors.Validation("emoji", $"1 to {Limits.StickerEmojiMax} emoji, each a single emoji, are required.");
}
