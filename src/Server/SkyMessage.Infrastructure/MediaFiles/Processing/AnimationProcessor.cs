using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>What a GIF's header and frame table say before anything is decoded.</summary>
/// <param name="Width">The canvas every frame is drawn on (ffmpeg decodes each frame at this size).</param>
internal sealed record GifInfo(int Width, int Height, int Frames)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// GIF animations become muted looping MP4s (what players and phones handle best, a fraction of the GIF's size): H.264
/// yuv420p with even sides, at most <see cref="Limits.AnimationMaxSide"/> px on the long side, no audio, <c>+faststart</c>,
/// white where the GIF is transparent (video has no alpha), with the first frame as the poster. ffmpeg reads only
/// local files through the GIF demuxer (protocol and format whitelists, like every other conversion).
/// A GIF is a few bytes that can describe a huge canvas and thousands of frames, and ffmpeg decodes every frame at the
/// canvas size: only GIFs within <see cref="IsWithinBudget"/> are converted (the others stay the GIF they are, behind
/// the photo pipeline's own pixel limit), the frame is scaled down before anything else is done with it, and the
/// output is cut at <see cref="MaxDurationSeconds"/> / <see cref="MaxOutputFrames"/> within <see cref="Timeout"/>.
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
    internal const int MaxDurationSeconds = 60;
    /// <summary>The demuxer plays at most 50 frames a second, so this is the duration cap in frames.</summary>
    internal const int MaxOutputFrames = MaxDurationSeconds * 50;
    /// <summary>Each side of the canvas (a banner may be long, not huge).</summary>
    internal const int MaxCanvasSide = 4096;
    /// <summary>The canvas (≈ 16 MB a decoded frame; ffmpeg keeps a few in flight).</summary>
    internal const long MaxCanvasPixels = 4L * 1024 * 1024;
    /// <summary>Frames × canvas pixels: the decoding work (≈ seconds of CPU on a small server).</summary>
    internal const long MaxDecodedPixels = 1_500_000_000;
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>The canvas and the frame count of a GIF; null for anything else (or a GIF Skia cannot read).</summary>
    public static GifInfo? Inspect(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is not { EncodedFormat: SKEncodedImageFormat.Gif }) return null;
        return new GifInfo(codec.Info.Width, codec.Info.Height, codec.FrameCount);
    }

    /// <summary>A GIF with more than one frame (one frame stays a photo).</summary>
    public static bool IsAnimatedGif(string path) => Inspect(path) is { Frames: > 1 };

    /// <summary>Small enough to decode every frame of (the frames past the duration cap are never decoded).</summary>
    public static bool IsWithinBudget(GifInfo gif) =>
        gif is { Width: > 0, Height: > 0 }
        && gif.Width <= MaxCanvasSide && gif.Height <= MaxCanvasSide
        && gif.Pixels <= MaxCanvasPixels
        && Math.Min(gif.Frames, MaxOutputFrames) * gif.Pixels <= MaxDecodedPixels;

    public async Task<VideoOutput> ConvertGifAsync(string inputPath, string workDirectory, CancellationToken cancellationToken)
    {
        var max = Limits.AnimationMaxSide.ToString(CultureInfo.InvariantCulture);
        // Scaled first, so nothing after the decoder holds a canvas-sized frame. The decoder paints transparent pixels
        // white (its trans_color, kept at its default 0x00FFFFFF: GIF transparency is all or nothing), so dropping the
        // alpha for yuv420p leaves them white.
        var filter = $"[0:v]scale=w='if(gte(iw,ih),max(2,trunc(min(iw,{max})/2)*2),-2)':h='if(gte(iw,ih),-2,max(2,trunc(min(ih,{max})/2)*2))'," +
                     "format=yuv420p[v]";
        var outputPath = Path.Combine(workDirectory, "animation.mp4");
        await ffmpeg.RunFfmpegAsync(
            [
                "-protocol_whitelist", "file", "-format_whitelist", InputFormats, "-f", "gif",
                "-trans_color", "0x00FFFFFF", "-i", inputPath,
                "-t", MaxDurationSeconds.ToString(CultureInfo.InvariantCulture),
                "-filter_complex", filter, "-map", "[v]", "-an", "-sn", "-dn",
                "-frames:v", MaxOutputFrames.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "veryfast",
                "-crf", Math.Clamp(options.Value.VideoCrf, 0, 51).ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p", "-fpsmax", "50",
                "-map_metadata", "-1", "-map_chapters", "-1",
                "-movflags", "+faststart", "-f", "mp4", outputPath,
            ],
            Timeout, cancellationToken);

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
