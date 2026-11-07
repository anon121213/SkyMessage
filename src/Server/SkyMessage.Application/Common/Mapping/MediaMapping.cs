using System.Linq.Expressions;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Common.Mapping;

public static class MediaMapping
{
    /// <summary>EF-translatable projection (never touches chunk or thumbnail bytes).</summary>
    public static readonly Expression<Func<Media, MediaDto>> Projection = m => new MediaDto
    {
        Id = m.Id,
        Kind = m.Kind,
        Status = m.Status,
        FileName = m.FileName,
        ContentType = m.ContentType,
        SizeBytes = m.SizeBytes,
        Width = m.Width,
        Height = m.Height,
        DurationMs = m.DurationMs,
        Waveform = m.Waveform,
        MiniThumbnail = m.MiniThumbnail,
        HasThumbnail = m.HasThumbnail,
        CreatedAt = m.CreatedAt,
        Sticker = m.StickerPackId != null && m.StickerId != null
            ? new StickerInfoDto { PackId = m.StickerPackId.Value, StickerId = m.StickerId.Value, Emoji = m.StickerEmoji ?? "" }
            : null,
        IsAnimation = m.IsAnimation,
    };

    /// <summary>
    /// <see cref="Projection"/> without the mini thumbnail, which is not even read (long lists sent often, whose apps
    /// show the real thumbnails: saved GIFs). Keep the two in step (MediaMappingTests compares them).
    /// </summary>
    public static readonly Expression<Func<Media, MediaDto>> ProjectionWithoutMini = m => new MediaDto
    {
        Id = m.Id,
        Kind = m.Kind,
        Status = m.Status,
        FileName = m.FileName,
        ContentType = m.ContentType,
        SizeBytes = m.SizeBytes,
        Width = m.Width,
        Height = m.Height,
        DurationMs = m.DurationMs,
        Waveform = m.Waveform,
        HasThumbnail = m.HasThumbnail,
        CreatedAt = m.CreatedAt,
        Sticker = m.StickerPackId != null && m.StickerId != null
            ? new StickerInfoDto { PackId = m.StickerPackId.Value, StickerId = m.StickerId.Value, Emoji = m.StickerEmoji ?? "" }
            : null,
        IsAnimation = m.IsAnimation,
    };

    private static readonly Func<Media, MediaDto> Compiled = Projection.Compile();

    public static MediaDto ToDto(this Media media) => Compiled(media);
}
