using Shouldly;
using SkyMessage.Application.Common.Mapping;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Media;

/// <summary>The two EF projections of a media (with and without the mini thumbnail) say the same about everything else.</summary>
public sealed class MediaMappingTests
{
    [Fact]
    public void The_projection_without_the_mini_thumbnail_maps_everything_else_alike()
    {
        var media = new Domain.Entities.Media
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            Kind = MediaKind.Video,
            Status = MediaStatus.Ready,
            FileName = "loop.mp4",
            ContentType = "video/mp4",
            SizeBytes = 12345,
            Width = 720,
            Height = 404,
            DurationMs = 1500,
            Waveform = [1, 2, 3],
            HasThumbnail = true,
            MiniThumbnail = [9, 9, 9],
            StickerPackId = Guid.NewGuid(),
            StickerId = Guid.NewGuid(),
            StickerEmoji = "😺",
            IsAnimation = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var full = MediaMapping.Projection.Compile()(media);
        var light = MediaMapping.ProjectionWithoutMini.Compile()(media);

        full.MiniThumbnail.ShouldBe(media.MiniThumbnail);
        light.MiniThumbnail.ShouldBeNull();
        light.ShouldBe(full with { MiniThumbnail = null });
    }
}
