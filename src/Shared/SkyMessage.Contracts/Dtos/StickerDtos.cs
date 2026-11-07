namespace SkyMessage.Contracts;

/// <summary>
/// What a media that is a sticker says about itself (<see cref="MediaDto.Sticker"/>): the pack and sticker it was made
/// for and the sticker's main emoji. It stays on sent stickers after the pack (or the sticker) is deleted, so they keep
/// showing as stickers; the pack may be gone then (404 sticker_pack_not_found).
/// </summary>
public sealed record StickerInfoDto
{
    public Guid PackId { get; init; }
    public Guid StickerId { get; init; }
    /// <summary>The main emoji (the first of the sticker's emoji): previews ("Стикер 😀") and suggestions.</summary>
    public string Emoji { get; init; } = "";
}

/// <summary>A sticker of a pack: a WebP of at most <see cref="Limits.StickerMaxSide"/> px with transparency.</summary>
public sealed record StickerDto
{
    public Guid Id { get; init; }
    public Guid PackId { get; init; }
    /// <summary>At least one emoji, the first is the main one (<see cref="StickerInfoDto.Emoji"/>).</summary>
    public IReadOnlyList<string> Emoji { get; init; } = [];
    /// <summary>Kind Photo, <c>image/webp</c>, a WebP thumbnail of at most 256 px, <see cref="MediaDto.Sticker"/> set.</summary>
    public MediaDto Media { get; init; } = new();
}

/// <summary>
/// A sticker pack. Anyone signed in may look at it and install it; only its creator edits it. Viewer-independent (the
/// creator's apps compare <see cref="CreatorId"/> with their own id).
/// </summary>
public sealed record StickerPackDto
{
    public Guid Id { get; init; }
    /// <summary>The pack's link: <c>{public base url}/addstickers/{slug}</c> (<see cref="DeepLinks.StickerPackPath"/>).</summary>
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public Guid CreatorId { get; init; }
    /// <summary>Grows with every change of the pack (title, stickers, their emoji and order).</summary>
    public long Version { get; init; }
    /// <summary>In the creator's order.</summary>
    public IReadOnlyList<StickerDto> Stickers { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A pack as my state lists it: enough to show it and to know when its stickers changed (<see cref="Version"/>).</summary>
public sealed record StickerPackRefDto
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public Guid CreatorId { get; init; }
    public long Version { get; init; }
    public int StickerCount { get; init; }
    /// <summary>Its first sticker (the pack's face in strips and lists); null while it has none.</summary>
    public StickerDto? Cover { get; init; }
}

/// <summary>A GIF I saved ("Мои GIF"): an animation (<see cref="MediaDto.IsAnimation"/>) I can send again without uploading it.</summary>
public sealed record SavedGifDto
{
    public MediaDto Media { get; init; } = new();
    public DateTimeOffset SavedAt { get; init; }
}

/// <summary>
/// My stickers and GIFs, the same on all my devices: every change bumps <see cref="Revision"/> and sends the whole state
/// to all of them (<see cref="IRealtimeClient.StickersChanged"/>); a copy with a lower revision than one already seen is
/// older. A pack's own changes (its creator's edits) bump the revision of everyone who installed it or keeps one of its
/// stickers among favorites or recent ones.
/// </summary>
public sealed record StickerStateDto
{
    public long Revision { get; init; }
    /// <summary>The packs I installed, in my order (a pack I make is installed for me).</summary>
    public IReadOnlyList<StickerPackRefDto> Installed { get; init; } = [];
    /// <summary>The packs I made, newest first (installed or not).</summary>
    public IReadOnlyList<StickerPackRefDto> Mine { get; init; } = [];
    /// <summary>At most <see cref="Limits.MaxFavoriteStickers"/>, the newest first.</summary>
    public IReadOnlyList<StickerDto> Favorites { get; init; } = [];
    /// <summary>The stickers I sent last (at most <see cref="Limits.MaxRecentStickers"/>), the newest first.</summary>
    public IReadOnlyList<StickerDto> Recent { get; init; } = [];
    /// <summary>At most <see cref="Limits.MaxSavedGifs"/>, the newest first.</summary>
    public IReadOnlyList<SavedGifDto> Gifs { get; init; } = [];
}

/// <summary>A new pack of mine. The app picks its id (Guid v7), so a repeat of the same create is harmless.</summary>
public sealed record CreateStickerPackRequest
{
    public Guid Id { get; init; }
    /// <summary>1..<see cref="Limits.StickerPackTitleMaxLength"/> after <see cref="StickerRules.NormalizeTitle"/>.</summary>
    public string Title { get; init; } = "";
}

public sealed record UpdateStickerPackRequest
{
    public string Title { get; init; } = "";
}

/// <summary>
/// A new sticker made from an upload of mine (<see cref="MediaKind.File"/>: PNG, WebP or JPEG of at most
/// <see cref="Limits.StickerSourceMaxBytes"/>, each side at least <see cref="Limits.StickerSourceMinSide"/> px). The
/// server turns it into a WebP of at most <see cref="Limits.StickerMaxSide"/> px (transparency kept) and drops the upload.
/// The app picks the sticker's id, so a repeat is harmless.
/// </summary>
public sealed record AddStickerRequest
{
    public Guid Id { get; init; }
    public Guid MediaId { get; init; }
    /// <summary>1..<see cref="Limits.StickerEmojiMax"/> single emoji, the first is the main one.</summary>
    public IReadOnlyList<string> Emoji { get; init; } = [];
}

public sealed record UpdateStickerRequest
{
    public IReadOnlyList<string> Emoji { get; init; } = [];
}

/// <summary>The pack's stickers in the order wanted (the listed ones take the places they held, like pinned chats).</summary>
public sealed record ReorderStickersRequest
{
    public IReadOnlyList<Guid> StickerIds { get; init; } = [];
}

/// <summary>My installed packs in the order wanted (the listed ones take the places they held).</summary>
public sealed record ReorderStickerPacksRequest
{
    public IReadOnlyList<Guid> PackIds { get; init; } = [];
}

/// <summary>Saves the animation of a message ("Сохранить в GIF"); not from a chat with content protection.</summary>
public sealed record SaveGifRequest
{
    public Guid ChatId { get; init; }
    public Guid MessageId { get; init; }
}
