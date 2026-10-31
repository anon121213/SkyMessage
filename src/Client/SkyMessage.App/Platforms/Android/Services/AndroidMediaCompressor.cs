using Android.Graphics;
using Android.Media;
using Android.OS;
using AndroidX.Media3.Common;
using AndroidX.Media3.Common.Audio;
using AndroidX.Media3.Effect;
using AndroidX.Media3.Transformer;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using AndroidUri = Android.Net.Uri;
using ExifInterface = AndroidX.ExifInterface.Media.ExifInterface;
using OperationCanceledException = System.OperationCanceledException;
using Paint = Android.Graphics.Paint;
using Path = System.IO.Path;
using Rect = Android.Graphics.Rect;
using Stream = System.IO.Stream;

namespace SkyMessage.App.Droid;

/// <summary>
/// Client-side compression with platform codecs only. Photos: ImageDecoder (API 28+, applies EXIF
/// orientation, decodes HEIF) or BitmapFactory + EXIF rotation (API 26–27), re-encoded without metadata.
/// Videos: Media3 Transformer on the main looper (HDR tone-mapped to SDR, short-side scaling, requested
/// bitrate, progress polled ~4 Hz, cancellable); any export failure falls back to the original file.
/// </summary>
public sealed class AndroidMediaCompressor(ILogger<AndroidMediaCompressor> logger) : IMediaCompressor
{
    private const int PreviewMaxSide = 320;

    public bool CanCompressImages => true;

    public bool CanCompressVideos => true;

    public async Task<CompressedMedia> ProbeAsync(PickedFile file, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            using var source = await MediaSource.OpenAsync(file, cancellationToken).ConfigureAwait(false);
            try
            {
                if (file.IsImage)
                {
                    var (width, height) = ImageBounds(source);
                    return Original(file, source, width, height, null, ImagePreview(source));
                }
                if (file.IsVideo)
                {
                    var info = VideoInfo(source, withFrame: true);
                    return Original(file, source, info.Width, info.Height, info.DurationMs, info.Preview);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Probe failed for a {Type} file", file.ContentType);
            }
            return Original(file, source, null, null, null, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompressedMedia> CompressImageAsync(PickedFile file, ImageCompressionOptions options, CancellationToken cancellationToken = default)
    {
        var type = file.ContentType.ToLowerInvariant();
        if (type == "image/gif") return await ProbeAsync(file, cancellationToken).ConfigureAwait(false);

        return await Task.Run(async () =>
        {
            using var source = await MediaSource.OpenAsync(file, cancellationToken).ConfigureAwait(false);
            int? width = null, height = null;
            try
            {
                (var w, var h) = ImageBounds(source);
                (width, height) = (w, h);
                var longSide = Math.Max(w ?? 0, h ?? 0);
                var fits = longSide > 0 && longSide <= options.MaxDimension;
                // PNG/WebP within limits are kept (transparency); the server keeps them as uploaded.
                if (fits && type is "image/png" or "image/webp") return Original(file, source, w, h, null, ImagePreview(source));
                if (fits && !options.StripMetadata && type == "image/jpeg") return Original(file, source, w, h, null, ImagePreview(source));

                using var bitmap = Decode(source, options.MaxDimension);
                if (bitmap is null) return Original(file, source, w, h, null, null);
                try
                {
                    var png = bitmap.HasAlpha && type is "image/png" or "image/webp";
                    var output = AppPaths.NewTempFile(png ? ".png" : ".jpg");
                    await using (var stream = File.Create(output))
                    {
                        var format = png ? Bitmap.CompressFormat.Png! : Bitmap.CompressFormat.Jpeg!;
                        if (!bitmap.Compress(format, png ? 100 : Math.Clamp(options.Quality, 1, 100), stream))
                            throw new IOException("Bitmap encoder failed");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var preview = PreviewFrom(bitmap);
                    return new CompressedMedia
                    {
                        FileName = FileNames.WithExtension(file.FileName, png ? ".png" : ".jpg"),
                        ContentType = png ? "image/png" : "image/jpeg",
                        Size = new FileInfo(output).Length,
                        Width = bitmap.Width,
                        Height = bitmap.Height,
                        PreviewJpeg = preview,
                        TempFilePath = output,
                        OpenReadAsync = _ => Task.FromResult<Stream>(File.OpenRead(output)),
                    };
                }
                finally
                {
                    bitmap.Recycle();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Image compression failed; sending the original");
                return Original(file, source, width, height, null, null);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompressedMedia> CompressVideoAsync(PickedFile file, VideoCompressionOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        // Fallbacks return file.OpenReadAsync, so the temp copy (if any) can always be deleted here.
        using var source = await MediaSource.OpenAsync(file, cancellationToken).ConfigureAwait(false);
        try
        {
            var info = await Task.Run(() => VideoInfo(source, withFrame: true), cancellationToken).ConfigureAwait(false);
            if (info.Width is not { } w || info.Height is not { } h)
            {
                return Original(file, source, null, null, info.DurationMs, info.Preview);
            }

            var longSide = Math.Max(w, h);
            var shortSide = Math.Min(w, h);
            var needsScale = longSide > options.MaxDimension;
            var sourceKbps = info.BitrateKbps ?? EstimateKbps(file.Size, info.DurationMs);
            var needsBitrate = sourceKbps is { } kbps && kbps > options.VideoBitrateKbps * 1.25;
            var isMp4 = string.Equals(info.MimeType, "video/mp4", StringComparison.OrdinalIgnoreCase);
            if (!needsScale && !needsBitrate && isMp4)
            {
                return Original(file, source, w, h, info.DurationMs, info.Preview);
            }

            var targetShort = needsScale ? Even(shortSide * (double)options.MaxDimension / longSide) : shortSide;
            var output = AppPaths.NewTempFile(".mp4");
            var exported = await ExportAsync(source.Uri, needsScale ? targetShort : null, options, output, progress, cancellationToken).ConfigureAwait(false);
            if (!exported || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                TryDelete(output);
                return Original(file, source, w, h, info.DurationMs, info.Preview);
            }

            var outputSize = new FileInfo(output).Length;
            if (!needsScale && file.Size > 0 && outputSize >= file.Size && isMp4)
            {
                TryDelete(output);
                return Original(file, source, w, h, info.DurationMs, info.Preview);
            }

            using var result = MediaSource.ForFile(output);
            var outInfo = VideoInfo(result, withFrame: false);
            progress?.Report(1);
            return new CompressedMedia
            {
                FileName = FileNames.WithExtension(file.FileName, ".mp4"),
                ContentType = "video/mp4",
                Size = outputSize,
                Width = outInfo.Width ?? (needsScale ? null : w),
                Height = outInfo.Height ?? (needsScale ? null : h),
                DurationMs = outInfo.DurationMs ?? info.DurationMs,
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
            return Original(file, source, null, null, null, null);
        }
    }

    private async Task<bool> ExportAsync(AndroidUri input, int? targetShortSide, VideoCompressionOptions options, string output,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Transformer? transformer = null;
        Handler? handler = null;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var context = Android.App.Application.Context;
            var videoEffects = new List<IEffect>();
            if (targetShortSide is { } side) videoEffects.Add(Presentation.CreateForShortSide(side)!);
            var edited = new EditedMediaItem.Builder(MediaItem.FromUri(input)!)
                .SetEffects(new Effects(new List<IAudioProcessor>(), videoEffects))!
                .Build()!;
#pragma warning disable CS0618 // The list constructor infers track types from the item (works for videos without audio).
            var sequence = new EditedMediaItemSequence.Builder(new List<EditedMediaItem> { edited }).Build()!;
#pragma warning restore CS0618
            var composition = new Composition.Builder(new List<EditedMediaItemSequence> { sequence })
                .SetHdrMode(Composition.HdrModeToneMapHdrToSdrUsingOpenGl)!
                .Build()!;
            var encoderFactory = new DefaultEncoderFactory.Builder(context)
                .SetRequestedVideoEncoderSettings(new VideoEncoderSettings.Builder().SetBitrate(options.VideoBitrateKbps * 1000)!.Build())!
                .SetRequestedAudioEncoderSettings(new AudioEncoderSettings.Builder().SetBitrate(options.AudioBitrateKbps * 1000)!.Build())!
                .SetEnableFallback(true)!
                .Build()!;
            transformer = new Transformer.Builder(context)
                .SetVideoMimeType(MimeTypes.VideoH264)!
                .SetAudioMimeType(MimeTypes.AudioAac)!
                .SetEncoderFactory(encoderFactory)!
                .AddListener(new ExportListener(done, logger))!
                .Build()!;
            transformer.Start(composition, output);

            handler = new Handler(Looper.MainLooper!);
            var holder = new ProgressHolder();
            Java.Lang.Runnable? poll = null;
            poll = new Java.Lang.Runnable(() =>
            {
                if (done.Task.IsCompleted || transformer is null) return;
                try
                {
                    if (transformer.GetProgress(holder) == Transformer.ProgressStateAvailable)
                        progress?.Report(Math.Clamp(holder.Progress / 100.0, 0, 0.99));
                }
                catch (Exception)
                {
                }
                handler.PostDelayed(poll!, 250);
            });
            handler.PostDelayed(poll, 250);
        }).ConfigureAwait(false);

        using var registration = cancellationToken.Register(() =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    transformer?.Cancel();
                }
                catch (Exception)
                {
                }
                TryDelete(output);
            });
            done.TrySetCanceled(cancellationToken);
        });
        return await done.Task.ConfigureAwait(false);
    }

    private static int? EstimateKbps(long size, int? durationMs) =>
        size > 0 && durationMs is > 0 ? (int)(size * 8 / (durationMs.Value / 1000.0) / 1000) : null;

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    private static CompressedMedia Original(PickedFile file, MediaSource source, int? width, int? height, int? durationMs, byte[]? preview) => new()
    {
        FileName = file.FileName,
        ContentType = file.ContentType,
        Size = file.Size >= 0 ? file.Size : source.Length,
        Width = width,
        Height = height,
        DurationMs = durationMs,
        PreviewJpeg = preview,
        OpenReadAsync = file.OpenReadAsync,
        TempFilePath = null,
    };

    // ---- Images ----------------------------------------------------------------------------------

    private static (int? Width, int? Height) ImageBounds(MediaSource source)
    {
        using var stream = source.OpenRead();
        var options = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeStream(stream, null, options);
        if (options.OutWidth <= 0 || options.OutHeight <= 0) return (null, null);
        var rotated = ExifOrientation(source) is ExifInterface.OrientationRotate90 or ExifInterface.OrientationRotate270
            or ExifInterface.OrientationTranspose or ExifInterface.OrientationTransverse;
        return rotated ? (options.OutHeight, options.OutWidth) : (options.OutWidth, options.OutHeight);
    }

    private static int ExifOrientation(MediaSource source)
    {
        try
        {
            using var stream = source.OpenRead();
            return new ExifInterface(stream).GetAttributeInt(ExifInterface.TagOrientation, ExifInterface.OrientationNormal);
        }
        catch (Exception)
        {
            return ExifInterface.OrientationNormal;
        }
    }

    /// <summary>Decodes to fit <paramref name="maxSide"/> with orientation applied; null when undecodable.</summary>
    private static Bitmap? Decode(MediaSource source, int maxSide)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            try
            {
                var decoderSource = ImageDecoder.CreateSource(Android.App.Application.Context.ContentResolver!, source.Uri);
                return ImageDecoder.DecodeBitmap(decoderSource, new HeaderListener(maxSide));
            }
            catch (Exception)
            {
                // Fall back to BitmapFactory (some providers/codecs reject ImageDecoder).
            }
        }
        return DecodeLegacy(source, maxSide);
    }

    private static Bitmap? DecodeLegacy(MediaSource source, int maxSide)
    {
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        using (var stream = source.OpenRead()) BitmapFactory.DecodeStream(stream, null, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

        var sample = 1;
        var longSide = Math.Max(bounds.OutWidth, bounds.OutHeight);
        while (longSide / (sample * 2) >= maxSide) sample *= 2;

        Bitmap? bitmap;
        using (var stream = source.OpenRead())
            bitmap = BitmapFactory.DecodeStream(stream, null, new BitmapFactory.Options { InSampleSize = sample, InPreferredConfig = Bitmap.Config.Argb8888 });
        if (bitmap is null) return null;

        var currentLong = Math.Max(bitmap.Width, bitmap.Height);
        if (currentLong > maxSide)
        {
            var scale = maxSide / (double)currentLong;
            var scaled = Bitmap.CreateScaledBitmap(bitmap, Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)), true)!;
            if (!ReferenceEquals(scaled, bitmap)) bitmap.Recycle();
            bitmap = scaled;
        }

        var matrix = OrientationMatrix(ExifOrientation(source));
        if (matrix is null) return bitmap;
        var rotated = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true)!;
        if (!ReferenceEquals(rotated, bitmap)) bitmap.Recycle();
        return rotated;
    }

    private static Matrix? OrientationMatrix(int orientation)
    {
        var matrix = new Matrix();
        switch (orientation)
        {
            case ExifInterface.OrientationFlipHorizontal: matrix.SetScale(-1, 1); break;
            case ExifInterface.OrientationRotate180: matrix.SetRotate(180); break;
            case ExifInterface.OrientationFlipVertical: matrix.SetScale(1, -1); break;
            case ExifInterface.OrientationTranspose: matrix.SetRotate(90); matrix.PostScale(-1, 1); break;
            case ExifInterface.OrientationRotate90: matrix.SetRotate(90); break;
            case ExifInterface.OrientationTransverse: matrix.SetRotate(-90); matrix.PostScale(-1, 1); break;
            case ExifInterface.OrientationRotate270: matrix.SetRotate(-90); break;
            default: return null;
        }
        return matrix;
    }

    private static byte[]? ImagePreview(MediaSource source)
    {
        try
        {
            using var bitmap = Decode(source, PreviewMaxSide);
            if (bitmap is null) return null;
            try
            {
                return PreviewFrom(bitmap);
            }
            finally
            {
                bitmap.Recycle();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>≤ 320 px JPEG, transparent areas composited on white.</summary>
    private static byte[]? PreviewFrom(Bitmap bitmap)
    {
        try
        {
            var longSide = Math.Max(bitmap.Width, bitmap.Height);
            var scale = longSide > PreviewMaxSide ? PreviewMaxSide / (double)longSide : 1.0;
            var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
            using var preview = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
            using (var canvas = new Canvas(preview))
            {
                canvas.DrawColor(Android.Graphics.Color.White);
                using var paint = new Paint(PaintFlags.FilterBitmap);
                canvas.DrawBitmap(bitmap, null, new Rect(0, 0, width, height), paint);
            }
            using var stream = new MemoryStream();
            preview.Compress(Bitmap.CompressFormat.Jpeg!, 70, stream);
            preview.Recycle();
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Video -----------------------------------------------------------------------------------

    private static VideoProbe VideoInfo(MediaSource source, bool withFrame)
    {
        using var retriever = new MediaMetadataRetriever();
        try
        {
            if (source.Path is { } path) retriever.SetDataSource(path);
            else retriever.SetDataSource(Android.App.Application.Context, source.Uri);

            var width = ParseInt(retriever.ExtractMetadata(MetadataKey.VideoWidth));
            var height = ParseInt(retriever.ExtractMetadata(MetadataKey.VideoHeight));
            var rotation = ParseInt(retriever.ExtractMetadata(MetadataKey.VideoRotation)) ?? 0;
            var duration = ParseInt(retriever.ExtractMetadata(MetadataKey.Duration));
            var bitrate = ParseInt(retriever.ExtractMetadata(MetadataKey.Bitrate));
            var mime = retriever.ExtractMetadata(MetadataKey.Mimetype);
            if (rotation is 90 or 270) (width, height) = (height, width);

            byte[]? preview = null;
            if (withFrame)
            {
                try
                {
                    var frame = OperatingSystem.IsAndroidVersionAtLeast(27)
                        ? retriever.GetScaledFrameAtTime(0, Option.ClosestSync, PreviewMaxSide, PreviewMaxSide)
                        : retriever.GetFrameAtTime(0, Option.ClosestSync);
                    if (frame is not null)
                    {
                        preview = PreviewFrom(frame);
                        frame.Recycle();
                        frame.Dispose();
                    }
                }
                catch (Exception)
                {
                    // Some codecs cannot produce a frame; the server makes a poster.
                }
            }
            return new VideoProbe(width, height, duration, bitrate is { } bps ? bps / 1000 : null, mime, preview);
        }
        catch (Exception)
        {
            return new VideoProbe(null, null, null, null, null, null);
        }
        finally
        {
            try
            {
                retriever.Release();
            }
            catch (Exception)
            {
            }
        }
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

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

    private sealed record VideoProbe(int? Width, int? Height, int? DurationMs, int? BitrateKbps, string? MimeType, byte[]? Preview);

    /// <summary>
    /// A file or content URI the platform decoders can read. Data that only exists as a .NET stream (e.g. a
    /// recording) is copied to a temp file first, deleted on dispose.
    /// </summary>
    private sealed class MediaSource : IDisposable
    {
        private readonly string? _tempCopy;

        private MediaSource(AndroidUri uri, string? path, string? tempCopy)
        {
            Uri = uri;
            Path = path;
            _tempCopy = tempCopy;
        }

        public AndroidUri Uri { get; }

        public string? Path { get; }

        public long Length => Path is not null && File.Exists(Path) ? new FileInfo(Path).Length : -1;

        public static MediaSource ForFile(string path) => new(AndroidUri.FromFile(new Java.IO.File(path))!, path, null);

        public static async Task<MediaSource> OpenAsync(PickedFile file, CancellationToken cancellationToken)
        {
            if (file.LocalPath is { } local && File.Exists(local)) return ForFile(local);
            if (ContentUriHelper.IsContentUri(file.PlatformRef)) return new MediaSource(AndroidUri.Parse(file.PlatformRef)!, null, null);

            var temp = AppPaths.NewTempFile(System.IO.Path.GetExtension(file.FileName) is { Length: > 0 and <= 8 } ext ? ext : ".bin");
            await using (var input = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(temp))
                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            return new MediaSource(AndroidUri.FromFile(new Java.IO.File(temp))!, temp, temp);
        }

        public Stream OpenRead() =>
            Path is not null
                ? File.OpenRead(Path)
                : Android.App.Application.Context.ContentResolver!.OpenInputStream(Uri) ?? throw new FileNotFoundException();

        public void Dispose()
        {
            if (_tempCopy is null) return;
            try
            {
                File.Delete(_tempCopy);
            }
            catch (Exception)
            {
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    private sealed class HeaderListener(int maxSide) : Java.Lang.Object, ImageDecoder.IOnHeaderDecodedListener
    {
        public void OnHeaderDecoded(ImageDecoder decoder, ImageDecoder.ImageInfo info, ImageDecoder.Source source)
        {
            decoder.Allocator = ImageDecoderAllocator.Software;
            var size = info.Size;
            var longSide = Math.Max(size.Width, size.Height);
            if (longSide > maxSide)
            {
                var scale = maxSide / (double)longSide;
                decoder.SetTargetSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
            }
        }
    }

    private sealed class ExportListener(TaskCompletionSource<bool> done, ILogger logger) : Java.Lang.Object, Transformer.IListener
    {
        public void OnCompleted(Composition? composition, ExportResult? exportResult) => done.TrySetResult(true);

        public void OnError(Composition? composition, ExportResult? exportResult, ExportException? exportException)
        {
            logger.LogWarning("Video export failed: {Error}", exportException?.ErrorCodeName);
            done.TrySetResult(false);
        }
    }
}
