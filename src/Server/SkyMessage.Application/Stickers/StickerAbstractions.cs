using SkyMessage.Contracts;

namespace SkyMessage.Application.Stickers;

/// <summary>
/// My stickers and GIFs (SPEC §6.6): installed packs and their order, favorites, saved GIFs (recent stickers follow my
/// sends). Every change runs under my <c>sticker_preferences</c> row lock, answers with the whole new state and sends it
/// to all my devices (StickersChanged); a change that changes nothing is silent.
/// </summary>
public interface IStickerService
{
    Task<StickerStateDto> GetStateAsync(CancellationToken cancellationToken = default);
    /// <summary>Installs a pack (it goes first); anyone signed in may install any existing pack.</summary>
    Task<StickerStateDto> InstallAsync(Guid packId, CancellationToken cancellationToken = default);
    Task<StickerStateDto> UninstallAsync(Guid packId, CancellationToken cancellationToken = default);
    Task<StickerStateDto> ReorderInstalledAsync(ReorderStickerPacksRequest request, CancellationToken cancellationToken = default);
    /// <summary>A new favorite goes first; past <see cref="Limits.MaxFavoriteStickers"/> the oldest one goes.</summary>
    Task<StickerStateDto> SetFavoriteAsync(Guid stickerId, bool favorite, CancellationToken cancellationToken = default);
    /// <summary>
    /// Saves the animation of a message I can see (403 forward_restricted from a chat with content protection); it goes
    /// first, past <see cref="Limits.MaxSavedGifs"/> the oldest one goes.
    /// </summary>
    Task<StickerStateDto> SaveGifAsync(SaveGifRequest request, CancellationToken cancellationToken = default);
    Task<StickerStateDto> RemoveGifAsync(Guid mediaId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sticker packs: anyone signed in sees any existing pack (by id or by its link's slug); only the creator changes or
/// deletes it (403 not_sticker_pack_creator). Every change bumps the pack's version and the sticker state revision of
/// everyone it concerns (the creator, who installed it, who keeps a removed sticker among favorites or recent ones).
/// </summary>
public interface IStickerPackService
{
    Task<StickerPackDto> GetAsync(Guid packId, CancellationToken cancellationToken = default);
    Task<StickerPackDto> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);
    /// <summary>A new pack of mine (installed for me, first); a repeat with the same id returns it.</summary>
    Task<StickerPackDto> CreateAsync(CreateStickerPackRequest request, CancellationToken cancellationToken = default);
    Task<StickerPackDto> RenameAsync(Guid packId, UpdateStickerPackRequest request, CancellationToken cancellationToken = default);
    /// <summary>A new sticker (last) made from an upload of mine; a repeat with the same id returns the pack.</summary>
    Task<StickerPackDto> AddStickerAsync(Guid packId, AddStickerRequest request, CancellationToken cancellationToken = default);
    Task<StickerPackDto> UpdateStickerAsync(Guid packId, Guid stickerId, UpdateStickerRequest request, CancellationToken cancellationToken = default);
    Task<StickerPackDto> DeleteStickerAsync(Guid packId, Guid stickerId, CancellationToken cancellationToken = default);
    Task<StickerPackDto> ReorderStickersAsync(Guid packId, ReorderStickersRequest request, CancellationToken cancellationToken = default);
    /// <summary>Deletes the pack: nobody can install it any more; stickers already sent keep showing.</summary>
    Task DeleteAsync(Guid packId, CancellationToken cancellationToken = default);
}

/// <summary>Recent stickers follow sends: Messaging calls this after a sticker message was committed.</summary>
public interface IStickerRecents
{
    /// <summary>Puts the sticker first among the user's recent ones (silent when it already is); never throws.</summary>
    Task TouchAsync(Guid userId, Guid stickerId);
}

/// <summary>
/// A sticker's image, normalized from an upload: WebP of at most <see cref="Limits.StickerMaxSide"/> px on the long
/// side with its transparency, plus a smaller WebP preview.
/// </summary>
public sealed record PreparedSticker(Guid SourceMediaId, byte[] Webp, int Width, int Height, byte[] Thumbnail, int ThumbnailWidth, int ThumbnailHeight);

/// <summary>Turns uploads into sticker media (Infrastructure: SkiaSharp and the chunk store).</summary>
public interface IStickerMediaFactory
{
    /// <summary>
    /// Reads and checks an upload of <paramref name="ownerId"/> (404 media_not_found when it is not theirs or not
    /// processed; 400 sticker_image_invalid when it is not a PNG, WebP or JPEG, is larger than
    /// <see cref="Limits.StickerSourceMaxBytes"/> or has a side under <see cref="Limits.StickerSourceMinSide"/> px) and
    /// makes the sticker's WebP. No database writes.
    /// </summary>
    Task<PreparedSticker> PrepareAsync(Guid ownerId, Guid sourceMediaId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a prepared sticker as a new Ready media of <paramref name="ownerId"/> carrying <paramref name="sticker"/>
    /// (inside the caller's open transaction).
    /// </summary>
    Task<Guid> StoreAsync(PreparedSticker prepared, Guid ownerId, StickerInfoDto sticker, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the upload a sticker was made from once nothing uses it (after the sticker's commit; never throws: media
    /// cleanup takes what is left).
    /// </summary>
    Task DropSourceAsync(Guid ownerId, Guid sourceMediaId);
}
