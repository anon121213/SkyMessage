using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>
/// GIF animations become muted looping MP4s (what players and phones handle best, a fraction of the GIF's size): H.264
/// yuv420p with even sides, at most <see cref="Limits.AnimationMaxSide"/> px on the long side, no audio, <c>+faststart</c>,
/// over white where the GIF is transparent (video has no alpha), with the first frame as the poster. ffmpeg reads only
/// local files through the GIF demuxer (protocol and format whitelists, like every other conversion).
/// </summary>
internal sealed class AnimationProcessor(
    FfmpegRunner ffmpeg,
    ImageProcessor images,
    IOptions<MediaProcessingOptions> options,
    ILogger<AnimationProcessor> logger)
{
    private const string InputFormats = "gif";
    private const string OutputFormats = "mov";
    /// <summary>Longer GIFs are cut here (they are loops, not films).</summary>
    private const int MaxDurationSeconds = 120;

    /// <summary>A GIF with more than one frame (one frame stays a photo).</summary>
    public static bool IsAnimatedGif(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec is { EncodedFormat: SKEncodedImageFormat.Gif, FrameCount: > 1 };
    }

    public async Task<VideoOutput> ConvertGifAsync(string inputPath, string workDirectory, CancellationToken cancellationToken)
    {
        var max = Limits.AnimationMaxSide.ToString(CultureInfo.InvariantCulture);
        // Transparent pixels land on white, then the frame is fitted (even sides) and made yuv420p for every player.
        var filter = "[0:v]format=rgba,split[bg][fg];[bg]lutrgb=r=255:g=255:b=255:a=255[white];[white][fg]overlay=format=auto," +
                     $"scale=w='if(gte(iw,ih),max(2,trunc(min(iw,{max})/2)*2),-2)':h='if(gte(iw,ih),-2,max(2,trunc(min(ih,{max})/2)*2))'," +
                     "format=yuv420p[v]";
        var outputPath = Path.Combine(workDirectory, "animation.mp4");
        await ffmpeg.RunFfmpegAsync(
            [
                "-protocol_whitelist", "file", "-format_whitelist", InputFormats, "-f", "gif", "-i", inputPath,
                "-t", MaxDurationSeconds.ToString(CultureInfo.InvariantCulture),
                "-filter_complex", filter, "-map", "[v]", "-an", "-sn", "-dn",
                "-c:v", "libx264", "-preset", "veryfast",
                "-crf", Math.Clamp(options.Value.VideoCrf, 0, 51).ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p", "-fpsmax", "50",
                "-map_metadata", "-1", "-map_chapters", "-1",
                "-movflags", "+faststart", "-f", "mp4", outputPath,
            ],
            MediaTimeouts.Transcode(MaxDurationSeconds), cancellationToken);

        var result = await ffmpeg.ProbeAsync(outputPath, OutputFormats, cancellationToken);
        var video = result.Video ?? throw new MediaProcessingException("The converted animation has no video stream.");
        if (result.Audio is not null) throw new MediaProcessingException("The converted animation has sound.");
        var poster = await PosterAsync(outputPath, workDirectory, cancellationToken);
        return new VideoOutput(outputPath, video.DisplayWidth, video.DisplayHeight, result.DurationMs, poster, Remuxed: false);
    }

    /// <summary>The first frame: a GIF starts where its author meant it to (no black lead-in like camera videos).</summary>
    private async Task<ThumbnailSet?> PosterAsync(string videoPath, string workDirectory, CancellationToken cancellationToken)
    {
        var posterPath = Path.Combine(workDirectory, "animation-poster.png");
        try
        {
            await ffmpeg.RunFfmpegAsync(
                [
                    "-protocol_whitelist", "file", "-format_whitelist", OutputFormats, "-i", videoPath,
                    "-frames:v", "1", "-an", "-update", "1", "-f", "image2", "-c:v", "png", posterPath,
                ],
                MediaTimeouts.StillImage, cancellationToken);
            return File.Exists(posterPath) && new FileInfo(posterPath).Length > 0 ? images.CreateThumbnails(posterPath) : null;
        }
        catch (MediaProcessingException ex)
        {
            logger.LogInformation("Animation poster failed: {Error}", ex.Message);
            return null;
        }
    }
}
