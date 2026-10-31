using AVFoundation;
using CoreGraphics;
using CoreMedia;
using Foundation;
using ImageIO;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using UIKit;

namespace SkyMessage.App.Apple;

/// <summary>
/// Platform codecs only. Photos: ImageIO thumbnail decode at the target size with the EXIF transform applied,
/// re-encoded as JPEG (PNG when transparent) with no metadata. Videos: AVAssetExportSession with the preset
/// matching MaxDimension (the bitrate follows the preset — best effort). Probe: ImageIO properties / AVAsset.
/// Anything that fails falls back to the original file (the server converts HEIC and transcodes video).
/// </summary>
public sealed class AppleMediaCompressor(ILogger<AppleMediaCompressor> logger) : IMediaCompressor
{
    private const int PreviewMaxSide = 320;

    public bool CanCompressImages => true;

    public bool CanCompressVideos => true;

    public async Task<CompressedMedia> ProbeAsync(PickedFile file, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            using var source = await LocalFile.OpenAsync(file, cancellationToken).ConfigureAwait(false);
            try
            {
                if (file.IsImage)
                {
                    var (width, height) = ImageSize(source.Url);
                    return Original(file, width, height, null, ImagePreview(source.Url));
                }
                if (file.IsVideo)
                {
                    var info = VideoInfo(source.Url, withFrame: true);
                    return Original(file, info.Width, info.Height, info.DurationMs, info.Preview);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Probe failed for a {Type} file", file.ContentType);
            }
            return Original(file, null, null, null, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompressedMedia> CompressImageAsync(PickedFile file, ImageCompressionOptions options, CancellationToken cancellationToken = default)
    {
        var type = file.ContentType.ToLowerInvariant();
        if (type == "image/gif") return await ProbeAsync(file, cancellationToken).ConfigureAwait(false);

        return await Task.Run(async () =>
        {
            using var source = await LocalFile.OpenAsync(file, cancellationToken).ConfigureAwait(false);
            int? width = null, height = null;
            try
            {
                (width, height) = ImageSize(source.Url);
                var longSide = Math.Max(width ?? 0, height ?? 0);
                var fits = longSide > 0 && longSide <= options.MaxDimension;
                if (fits && type is "image/png" or "image/webp") return Original(file, width, height, null, ImagePreview(source.Url));
                if (fits && !options.StripMetadata && type == "image/jpeg") return Original(file, width, height, null, ImagePreview(source.Url));

                using var imageSource = CGImageSource.FromUrl(source.Url);
                using var image = imageSource?.CreateThumbnail(0, new CGImageThumbnailOptions
                {
                    CreateThumbnailFromImageAlways = true,
                    CreateThumbnailWithTransform = true,
                    MaxPixelSize = options.MaxDimension,
                    ShouldCache = false,
                });
                if (image is null) return Original(file, width, height, null, null);

                var png = HasAlpha(image) && type is "image/png" or "image/webp";
                var output = AppPaths.NewTempFile(png ? ".png" : ".jpg");
                using (var destination = CGImageDestination.Create(NSUrl.FromFilename(output), png ? "public.png" : "public.jpeg", 1))
                {
                    if (destination is null) return Original(file, width, height, null, null);
                    destination.AddImage(image, new CGImageDestinationOptions { LossyCompressionQuality = Math.Clamp(options.Quality, 1, 100) / 100f });
                    if (!destination.Close()) throw new IOException("Image encoder failed");
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new CompressedMedia
                {
                    FileName = FileNames.WithExtension(file.FileName, png ? ".png" : ".jpg"),
                    ContentType = png ? "image/png" : "image/jpeg",
                    Size = new FileInfo(output).Length,
                    Width = (int)image.Width,
                    Height = (int)image.Height,
                    PreviewJpeg = PreviewFrom(image),
                    TempFilePath = output,
                    OpenReadAsync = _ => Task.FromResult<Stream>(File.OpenRead(output)),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Image compression failed; sending the original");
                return Original(file, width, height, null, null);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompressedMedia> CompressVideoAsync(PickedFile file, VideoCompressionOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var source = await LocalFile.OpenAsync(file, cancellationToken).ConfigureAwait(false);
        var info = await Task.Run(() => VideoInfo(source.Url, withFrame: true), cancellationToken).ConfigureAwait(false);
        try
        {
            if (info.Width is not { } w || info.Height is not { } h) return Original(file, null, null, info.DurationMs, info.Preview);
            var longSide = Math.Max(w, h);
            var needsScale = longSide > options.MaxDimension;
            var needsBitrate = info.BitrateKbps is { } kbps && kbps > options.VideoBitrateKbps * 1.25;
            if (!needsScale && !needsBitrate && file.ContentType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase))
                return Original(file, w, h, info.DurationMs, info.Preview);

            var preset = Math.Min(longSide, options.MaxDimension) switch
            {
                <= 640 => "AVAssetExportPreset640x480",
                <= 960 => "AVAssetExportPreset960x540",
                <= 1280 => "AVAssetExportPreset1280x720",
                <= 1920 => "AVAssetExportPreset1920x1080",
                _ => "AVAssetExportPresetHighestQuality",
            };
            var output = AppPaths.NewTempFile(".mp4");
            using var asset = AVAsset.FromUrl(source.Url);
            using var export = new AVAssetExportSession(asset, preset)
            {
                OutputUrl = NSUrl.FromFilename(output),
                OutputFileType = "public.mpeg-4",
                ShouldOptimizeForNetworkUse = true,
            };

            using var registration = cancellationToken.Register(() => export.CancelExport());
            var exporting = export.ExportTaskAsync();
            while (!exporting.IsCompleted)
            {
                progress?.Report(Math.Clamp(export.Progress, 0, 0.99));
                await Task.WhenAny(exporting, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (export.Status != AVAssetExportSessionStatus.Completed || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                logger.LogWarning("Video export failed: {Error}", export.Error?.LocalizedDescription);
                TryDelete(output);
                return Original(file, w, h, info.DurationMs, info.Preview);
            }

            var size = new FileInfo(output).Length;
            if (!needsScale && file.Size > 0 && size >= file.Size)
            {
                TryDelete(output);
                return Original(file, w, h, info.DurationMs, info.Preview);
            }
            var result = VideoInfo(NSUrl.FromFilename(output), withFrame: false);
            progress?.Report(1);
            return new CompressedMedia
            {
                FileName = FileNames.WithExtension(file.FileName, ".mp4"),
                ContentType = "video/mp4",
                Size = size,
                Width = result.Width,
                Height = result.Height,
                DurationMs = result.DurationMs ?? info.DurationMs,
                PreviewJpeg = info.Preview,
                TempFilePath = output,
                OpenReadAsync = _ => Task.FromResult<Stream>(File.OpenRead(output)),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Video compression failed; sending the original");
            return Original(file, info.Width, info.Height, info.DurationMs, info.Preview);
        }
    }

    private static CompressedMedia Original(PickedFile file, int? width, int? height, int? durationMs, byte[]? preview) => new()
    {
        FileName = file.FileName,
        ContentType = file.ContentType,
        Size = file.Size >= 0 ? file.Size : file.LocalPath is { } p && File.Exists(p) ? new FileInfo(p).Length : -1,
        Width = width,
        Height = height,
        DurationMs = durationMs,
        PreviewJpeg = preview,
        OpenReadAsync = file.OpenReadAsync,
    };

    private static (int? Width, int? Height) ImageSize(NSUrl url)
    {
        using var source = CGImageSource.FromUrl(url);
        using var properties = source?.CopyProperties(new CGImageOptions(), 0);
        if (properties is null) return (null, null);
        var width = (properties["PixelWidth"] as NSNumber)?.Int32Value;
        var height = (properties["PixelHeight"] as NSNumber)?.Int32Value;
        var orientation = (properties["Orientation"] as NSNumber)?.Int32Value ?? 1;
        if (width is null || height is null) return (null, null);
        return orientation is >= 5 and <= 8 ? (height, width) : (width, height);
    }

    private static byte[]? ImagePreview(NSUrl url)
    {
        try
        {
            using var source = CGImageSource.FromUrl(url);
            using var image = source?.CreateThumbnail(0, new CGImageThumbnailOptions
            {
                CreateThumbnailFromImageAlways = true,
                CreateThumbnailWithTransform = true,
                MaxPixelSize = PreviewMaxSide,
            });
            return image is null ? null : PreviewFrom(image);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? PreviewFrom(CGImage image)
    {
        try
        {
            var scale = Math.Min(1.0, PreviewMaxSide / (double)Math.Max(image.Width, image.Height));
            var width = Math.Max(1, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));
            using var renderer = new UIGraphicsImageRenderer(new CGSize(width, height), new UIGraphicsImageRendererFormat { Scale = 1, Opaque = true });
            using var flattened = renderer.CreateImage(context =>
            {
                UIColor.White.SetFill();
                context.FillRect(new CGRect(0, 0, width, height));
                using var ui = new UIImage(image);
                ui.Draw(new CGRect(0, 0, width, height));
            });
            using var data = flattened.AsJPEG(0.7f);
            return data?.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasAlpha(CGImage image) => image.AlphaInfo is not (CGImageAlphaInfo.None or CGImageAlphaInfo.NoneSkipFirst or CGImageAlphaInfo.NoneSkipLast);

    private static VideoProbe VideoInfo(NSUrl url, bool withFrame)
    {
        try
        {
            using var asset = AVAsset.FromUrl(url);
#pragma warning disable CA1422 // Synchronous track access is fine off the main thread.
            var track = asset.TracksWithMediaType(AVMediaTypes.Video.GetConstant()!).FirstOrDefault();
#pragma warning restore CA1422
            int? width = null, height = null, bitrate = null;
            if (track is not null)
            {
                var rect = track.PreferredTransform.TransformRect(new CGRect(CGPoint.Empty, track.NaturalSize));
                width = (int)Math.Round(Math.Abs(rect.Width));
                height = (int)Math.Round(Math.Abs(rect.Height));
                bitrate = track.EstimatedDataRate > 0 ? (int)(track.EstimatedDataRate / 1000) : null;
            }
            var seconds = asset.Duration.Seconds;
            int? duration = double.IsFinite(seconds) && seconds > 0 ? (int)Math.Round(seconds * 1000) : null;

            byte[]? preview = null;
            if (withFrame && track is not null)
            {
                try
                {
                    using var generator = new AVAssetImageGenerator(asset)
                    {
                        AppliesPreferredTrackTransform = true,
                        MaximumSize = new CGSize(PreviewMaxSide, PreviewMaxSide),
                    };
#pragma warning disable CA1422
                    using var frame = generator.CopyCGImageAtTime(CMTime.Zero, out _, out var error);
#pragma warning restore CA1422
                    if (frame is not null && error is null) preview = PreviewFrom(frame);
                }
                catch (Exception)
                {
                }
            }
            return new VideoProbe(width, height, duration, bitrate, preview);
        }
        catch (Exception)
        {
            return new VideoProbe(null, null, null, null, null);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private sealed record VideoProbe(int? Width, int? Height, int? DurationMs, int? BitrateKbps, byte[]? Preview);

    /// <summary>A file URL for ImageIO/AVFoundation; stream-only inputs are copied to a temp file (deleted on dispose).</summary>
    private sealed class LocalFile : IDisposable
    {
        private readonly string? _temp;

        private LocalFile(string path, string? temp)
        {
            Url = NSUrl.FromFilename(path);
            _temp = temp;
        }

        public NSUrl Url { get; }

        public static async Task<LocalFile> OpenAsync(PickedFile file, CancellationToken cancellationToken)
        {
            if (file.LocalPath is { } local && File.Exists(local)) return new LocalFile(local, null);
            var extension = Path.GetExtension(file.FileName);
            var temp = AppPaths.NewTempFile(string.IsNullOrEmpty(extension) ? FileNames.ExtensionFor(file.ContentType) is { Length: > 0 } e ? e : ".bin" : extension);
            await using (var input = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(temp))
                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            return new LocalFile(temp, temp);
        }

        public void Dispose()
        {
            Url.Dispose();
            if (_temp is not null) TryDelete(_temp);
        }
    }
}
