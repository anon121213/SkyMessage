namespace SkyMessage.Contracts;

/// <summary>Validation limits shared by the server and all clients.</summary>
public static class Limits
{
    public const int UsernameMinLength = 5;
    public const int UsernameMaxLength = 32;
    /// <summary>Latin letter first, then letters, digits or underscore.</summary>
    public const string UsernamePattern = "^[a-zA-Z][a-zA-Z0-9_]{4,31}$";

    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;

    public const int DisplayNameMaxLength = 64;
    public const int BioMaxLength = 200;
    public const int ContactNameMaxLength = 64;

    /// <summary>Photos kept in a profile's or a group's avatar history (the oldest go first).</summary>
    public const int AvatarHistoryMax = 30;
    /// <summary>Links on a profile.</summary>
    public const int ProfileLinksMax = 3;
    public const int ProfileLinkUrlMaxLength = 512;
    public const int ProfileLinkTitleMaxLength = 32;
    /// <summary>Earliest birth year accepted (a birthday without a year is fine too).</summary>
    public const int BirthYearMin = 1900;

    /// <summary>Chat ids in one <see cref="ReorderPinnedChatsRequest"/>.</summary>
    public const int MaxPinnedChatsReorder = 1000;

    /// <summary>Folders of one user (the built-in "All chats" not counted).</summary>
    public const int MaxChatFolders = 20;
    public const int ChatFolderNameMaxLength = 24;
    /// <summary>UTF-16 units of <see cref="ChatFolderDto.Icon"/> (an emoji or an icon key).</summary>
    public const int ChatFolderIconMaxLength = 32;
    /// <summary>Chats a folder includes by id, excludes by id, or pins (each list).</summary>
    public const int MaxChatFolderChats = 200;

    public const int StickerPackTitleMaxLength = 64;
    public const int MaxStickersPerPack = 120;
    /// <summary>A sticker's source upload (PNG, WebP or JPEG).</summary>
    public const long StickerSourceMaxBytes = 5L * 1024 * 1024;
    /// <summary>Each side of a sticker's source image at least this many pixels.</summary>
    public const int StickerSourceMinSide = 64;
    /// <summary>
    /// Each side of a sticker's source image at most this many pixels (it is decoded while the request waits; a larger
    /// picture is cropped on the device first, which makes it 512 px).
    /// </summary>
    public const int StickerSourceMaxSide = 4096;
    /// <summary>Stickers are stored as WebP of at most this many pixels on the long side.</summary>
    public const int StickerMaxSide = 512;
    /// <summary>Emoji of one sticker.</summary>
    public const int StickerEmojiMax = 8;
    public const int MaxFavoriteStickers = 50;
    public const int MaxRecentStickers = 20;
    public const int MaxSavedGifs = 200;
    /// <summary>Packs one user may have installed.</summary>
    public const int MaxInstalledStickerPacks = 200;
    /// <summary>Packs one user may make.</summary>
    public const int MaxOwnStickerPacks = 100;
    /// <summary>GIFs become MP4 loops of at most this many pixels on the long side.</summary>
    public const int AnimationMaxSide = 720;

    public const int GroupTitleMaxLength = 128;
    public const int GroupDescriptionMaxLength = 500;
    public const int MaxGroupMembers = 1000;

    public const int MessageTextMaxLength = 4096;
    public const int MaxAttachmentsPerMessage = 10;
    public const int MaxEntitiesPerMessage = 100;
    public const int MaxMessagesPerBatch = 100;
    public const int MessagesPageDefault = 50;
    public const int MessagesPageMax = 100;
    public const int MessageChangesMax = 500;
    public const int EditWindowHours = 48;
    public const int ReactionMaxLength = 16;
    public const int MaxReactionUserIds = 100;
    public const int ReplyPreviewTextLength = 120;
    public const int LinkUrlMaxLength = 2048;

    public const int DefaultChunkSize = 1024 * 1024;
    public const long DefaultMaxUploadBytes = 2L * 1024 * 1024 * 1024;
    public const int FileNameMaxLength = 255;
    public const int VideoNoteMaxDurationSeconds = 60;
    public const int VoiceMaxDurationSeconds = 3600;
    public const int WaveformSamples = 100;

    public const int TypingTimeoutSeconds = 6;
    public const int CallRingTimeoutSeconds = 45;

    public const int FindByPhonesMax = 1000;
    /// <summary>
    /// <c>find-by-phones</c> requests one user may make per <see cref="PhoneLookupWindowMinutes"/> (a fixed window shared
    /// by the import and contact sync).
    /// </summary>
    public const int PhoneLookupsPerWindow = 10;
    public const int PhoneLookupWindowMinutes = 10;
    public const int MaxBatchUsers = 200;
    public const int SearchQueryMinLength = 2;
    /// <summary>Global (non-contact) user search only matches username prefixes at least this long.</summary>
    public const int GlobalUsernameSearchMinLength = 3;
    public const int SearchResultsMax = 50;
    public const int DeviceNameMaxLength = 128;
}
