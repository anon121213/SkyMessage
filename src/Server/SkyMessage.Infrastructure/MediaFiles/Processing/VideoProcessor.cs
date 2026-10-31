using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <param name="DurationMs">Null when the container reports none (the client's value is kept then).</param>
/// <param name="Remuxed">True when the original streams were copied (only the container was rewritten).</param>
internal sealed record VideoOutput(string Path, int Width, int Height, int? DurationMs, ThumbnailSet? Thumbnails, bool Remuxed);

/// <summary>
/// Videos become web-friendly MP4 (H.264 + AAC, <c>+faststart</c> so playback starts before the download ends):
/// H.264/AAC MP4s within <c>VideoMaxDimension</c> are only remuxed, everything else is transcoded. Video notes are
/// always transcoded to a centered square. Metadata (e.g. GPS location tags) is dropped in both cases.
/// </summary>
internal sealed class VideoProcessor(
    FfmpegRunner ffmpeg,
    ImageProcessor images,
    IOptions<MediaProcessingOptions> options,
    ILogger<VideoProcessor> logger)
{
    /// <summary>Demuxers accepted for uploaded videos (MP4/MOV/3GP, Matroska/WebM, AVI, FLV, MPEG-TS/PS, Ogg, ASF/WMV).</summary>
    internal const string InputFormats = "mov,matroska,avi,flv,mpegts,mpeg,ogg,asf";

    /// <summary>Our own MP4 output.</summary>
    private const string OutputFormats = "mov";

    private const int PosterMaxWidth = 960;

    public async Task<VideoOutput> ProcessVideoAsync(string inputPath, string workDirectory, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var probe = await ffmpeg.ProbeAsync(inputPath, InputFormats, cancellationToken);
        var video = probe.Video ?? throw new MediaProcessingException("The file has no video stream.");
        var audio = probe.Audio;
        var outputPath = Path.Combine(workDirectory, "video.mp4");

        var remux = probe.IsMp4Family
                    && video.CodecName == "h264"
                    && video.PixelFormat is "yuv420p" or "yuvj420p"
                    && (audio is null || audio.CodecName == "aac")
                    && Math.Max(video.Width, video.Height) <= settings.VideoMaxDimension;
        if (remux)
        {
            try
            {
                await ffmpeg.RunFfmpegAsync(
                    [
                        .. InputArguments(inputPath),
                        "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy",
                        .. OutputArguments(outputPath),
                    ],
                    MediaTimeouts.Remux, cancellationToken);
            }
            catch (MediaProcessingException ex)
            {
                logger.LogInformation("Remux failed, transcoding instead: {Error}", ex.Message);
                remux = false;
            }
        }

        if (!remux)
        {
            var maxDimension = Even(settings.VideoMaxDimension);
            var scale = string.Create(CultureInfo.InvariantCulture,
                $"scale=w='if(gte(iw,ih),trunc(min(iw,{maxDimension})/2)*2,-2)':h='if(gte(iw,ih),-2,trunc(min(ih,{maxDimension})/2)*2)'");
            await ffmpeg.RunFfmpegAsync(
                [
                    .. InputArguments(inputPath),
                    "-map", "0:v:0", "-map", "0:a:0?",
                    "-vf", scale, "-fpsmax", "60",
                    .. H264Arguments(settings.VideoCrf),
                    "-c:a", "aac", "-b:a", "128k", .. (audio?.Channels > 2 ? ["-ac", "2"] : Array.Empty<string>()),
                    .. OutputArguments(outputPath),
                ],
                MediaTimeouts.Transcode(probe.DurationSeconds), cancellationToken);
        }

        return await DescribeOutputAsync(outputPath, workDirectory, remux, cancellationToken);
    }

    public async Task<VideoOutput> ProcessVideoNoteAsync(string inputPath, string workDirectory, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var probe = await ffmpeg.ProbeAsync(inputPath, InputFormats, cancellationToken);
        if (probe.Video is null) throw new MediaProcessingException("The file has no video stream.");
        var outputPath = Path.Combine(workDirectory, "video_note.mp4");
        var size = Even(settings.VideoNoteSize).ToString(CultureInfo.InvariantCulture);

        await ffmpeg.RunFfmpegAsync(
            [
                .. InputArguments(inputPath),
                "-t", Limits.VideoNoteMaxDurationSeconds.ToString(CultureInfo.InvariantCulture),
                "-map", "0:v:0", "-map", "0:a:0?",
                // crop defaults to the center; autorotation has already been applied to iw/ih.
                "-vf", $"crop='min(iw,ih)':'min(iw,ih)',scale={size}:{size},setsar=1", "-fpsmax", "30",
                .. H264Arguments(settings.VideoCrf),
                "-c:a", "aac", "-b:a", "64k", "-ac", "1",
                .. OutputArguments(outputPath),
            ],
            MediaTimeouts.Transcode(Math.Min(probe.DurationSeconds ?? 0, Limits.VideoNoteMaxDurationSeconds)), cancellationToken);

        return await DescribeOutputAsync(outputPath, workDirectory, remuxed: false, cancellationToken);
    }

    private async Task<VideoOutput> DescribeOutputAsync(string outputPath, string workDirectory, bool remuxed, CancellationToken cancellationToken)
    {
        var result = await ffmpeg.ProbeAsync(outputPath, OutputFormats, cancellationToken);
        var video = result.Video ?? throw new MediaProcessingException("The processed video has no video stream.");
        var thumbnails = await CreatePosterAsync(outputPath, result.DurationSeconds, workDirectory, cancellationToken);
        return new VideoOutput(outputPath, video.DisplayWidth, video.DisplayHeight, result.DurationMs, thumbnails, remuxed);
    }

    /// <summary>A frame ~1 s in (the very first frame is often black), falling back to the first one.</summary>
    private async Task<ThumbnailSet?> CreatePosterAsync(string videoPath, double? durationSeconds, string workDirectory, CancellationToken cancellationToken)
    {
        var posterPath = Path.Combine(workDirectory, "poster.png");
        foreach (var seek in durationSeconds is > 2 ? new[] { "1", "0" } : ["0"])
        {
            try
            {
                File.Delete(posterPath);
                await ffmpeg.RunFfmpegAsync(
                    [
                        "-ss", seek,
                        "-protocol_whitelist", "file", "-format_whitelist", OutputFormats, "-i", videoPath,
                        "-frames:v", "1", "-an", "-vf", $"scale=w='min({PosterMaxWidth},iw)':h=-2",
                        "-update", "1", "-f", "image2", "-c:v", "png", posterPath,
                    ],
                    MediaTimeouts.StillImage, cancellationToken);
                if (File.Exists(posterPath) && new FileInfo(posterPath).Length > 0)
                    return images.CreateThumbnails(posterPath);
            }
            catch (MediaProcessingException ex)
            {
                logger.LogInformation("Poster frame at {Seek}s failed: {Error}", seek, ex.Message);
            }
        }
        return null;
    }

    private static string[] InputArguments(string inputPath) =>
        ["-protocol_whitelist", "file", "-format_whitelist", InputFormats, "-i", inputPath];

    private static string[] H264Arguments(int crf) =>
    [
        "-c:v", "libx264", "-preset", "veryfast", "-crf", Math.Clamp(crf, 0, 51).ToString(CultureInfo.InvariantCulture),
        "-pix_fmt", "yuv420p",
    ];

    private static string[] OutputArguments(string outputPath) =>
    [
        "-map_metadata", "-1", "-map_chapters", "-1",
        "-movflags", "+faststart", "-max_muxing_queue_size", "1024",
        "-f", "mp4", outputPath,
    ];

    private static int Even(int value) => Math.Max(2, value & ~1);
}
