using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Stickers;

/// <summary>Sticker and GIF facts the chat, the panel and the settings share.</summary>
public static class StickerUi
{
    /// <summary>The pack editor: <c>/settings/stickerpack?id=</c>.</summary>
    public static string EditorPath(Guid packId) => $"{SettingsRoutes.PathOf(SettingsRoutes.StickerPack)}?id={packId}";

    public static string SettingsPath => SettingsRoutes.PathOf(SettingsRoutes.Stickers);

    /// <summary>A sticker message: one photo whose media carries sticker attributes (pending copies included).</summary>
    public static MediaDto? StickerOf(MessageDto message) =>
        message is { Type: MessageType.Photo, Attachments.Count: 1 } && message.Attachments[0].Sticker is not null ? message.Attachments[0] : null;

    public static bool IsSticker(MessageDto message) => StickerOf(message) is not null;

    /// <summary>The ready GIF (a muted MP4 loop) of a message, if it has one.</summary>
    public static MediaDto? GifOf(MessageDto message) =>
        message.Attachments.FirstOrDefault(a => a.IsAnimation && a.Status == MediaStatus.Ready);

    /// <summary>The sticker as a pack lists it, from the media of a sent one (favorites, the preview).</summary>
    public static StickerDto? AsSticker(MediaDto media) => media.Sticker is { } info
        ? new StickerDto { Id = info.StickerId, PackId = info.PackId, Emoji = string.IsNullOrEmpty(info.Emoji) ? [] : [info.Emoji], Media = media }
        : null;

    /// <summary>"😺 ❤️" — a sticker's emoji for labels and screen readers.</summary>
    public static string EmojiLine(StickerDto sticker) => string.Join(' ', sticker.Emoji);

    /// <summary>A file the sticker editor accepts (by type; the server checks the bytes).</summary>
    public static bool IsStickerImage(string? contentType) =>
        contentType is not null && contentType.ToLowerInvariant() is "image/png" or "image/webp" or "image/jpeg" or "image/jpg";

    /// <summary>HEIC / HEIF (phone cameras): the web view cannot show it, the device can make it a JPEG.</summary>
    public static bool IsHeif(string? contentType) =>
        contentType is not null && contentType.ToLowerInvariant() is "image/heic" or "image/heif" or "image/heic-sequence" or "image/heif-sequence";
}

/// <summary>
/// What the new-sticker dialog hands back: the picture (null when only the emoji were edited) and the emoji; or
/// <paramref name="StopAll"/>: the rest of the batch is skipped.
/// </summary>
public sealed record NewStickerResult(SkyMessage.Client.Core.Platform.PickedFile? File, IReadOnlyList<string> Emoji, bool StopAll = false);

