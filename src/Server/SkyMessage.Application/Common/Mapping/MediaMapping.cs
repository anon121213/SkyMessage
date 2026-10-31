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
    };

    private static readonly Func<Media, MediaDto> Compiled = Projection.Compile();

    public static MediaDto ToDto(this Media media) => Compiled(media);
}
