namespace SkyMessage.Domain.Entities;

/// <summary>
/// A sticker pack made by a user. Anyone signed in may look at it and install it; only <see cref="CreatorId"/> edits it.
/// Deleting it deletes its stickers and every installation, favorite and recent use of them; the sticker media stay as
/// long as messages show them (the media keep their sticker attributes, Media.StickerPackId etc.).
/// </summary>
public sealed class StickerPack
{
    /// <summary>Chosen by the app that creates the pack (a repeated create is the same pack).</summary>
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>The pack link's random part (unique): <c>/addstickers/{slug}</c>.</summary>
    public string Slug { get; set; } = "";
    /// <summary>Grows with every change of the pack (Contracts.StickerPackDto.Version).</summary>
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A sticker of a pack: a WebP media made from an upload of the creator's (owned by the creator).</summary>
public sealed class Sticker
{
    /// <summary>Chosen by the app that adds it (a repeated add is the same sticker).</summary>
    public Guid Id { get; set; }
    public Guid PackId { get; set; }
    public Guid MediaId { get; set; }
    /// <summary>At least one emoji, the first is the main one (text[]).</summary>
    public List<string> Emoji { get; set; } = [];
    /// <summary>Place in the pack: lower comes first (gaps are fine).</summary>
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A pack in someone's sticker panel.</summary>
public sealed class InstalledStickerPack
{
    public Guid UserId { get; set; }
    public Guid PackId { get; set; }
    /// <summary>Place among the user's packs: lower comes first (a new one goes before all others).</summary>
    public int Position { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
}

public sealed class FavoriteSticker
{
    public Guid UserId { get; set; }
    public Guid StickerId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>A sticker the user sent (the newest <c>Limits.MaxRecentStickers</c> are kept).</summary>
public sealed class RecentSticker
{
    public Guid UserId { get; set; }
    public Guid StickerId { get; set; }
    public DateTimeOffset UsedAt { get; set; }
}

/// <summary>An animation (a GIF made into an MP4 loop) the user saved to send again.</summary>
public sealed class SavedGif
{
    public Guid UserId { get; set; }
    public Guid MediaId { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>
/// The revision of a user's stickers and GIFs (Contracts.StickerStateDto.Revision). The row (created on the first change)
/// is also the lock that puts changes from several devices in order.
/// </summary>
public sealed class StickerPreferences
{
    public Guid UserId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
