using Microsoft.Extensions.Options;
using SkiaSharp;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.MediaFiles;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>JPEG preview (≤ ThumbnailMaxDimension) plus the ≈24 px blurred-placeholder JPEG.</summary>
internal sealed record ThumbnailSet(byte[] Jpeg, int Width, int Height, byte[] Mini);

/// <param name="OutputPath">Re-encoded JPEG, or null when the original bytes are kept.</param>
/// <param name="ContentType">Content type of what is stored (the detected format when kept).</param>
internal sealed record PhotoOutput(string? OutputPath, string ContentType, int Width, int Height, ThumbnailSet Thumbnails);

/// <summary>
/// SkiaSharp photo pipeline. EXIF orientation is applied to everything rendered; JPEG/PNG/WebP within
/// <c>PhotoMaxDimension</c> are kept byte-for-byte (clients already strip metadata), GIFs are always kept, anything
/// else or larger is re-encoded as JPEG q85, which also drops all metadata. JPEG/WebP are decoded at a reduced
/// scale when only a smaller image is needed, so huge camera photos never need full-size pixel buffers.
/// </summary>
internal sealed class ImageProcessor(IOptions<MediaProcessingOptions> options)
{
    public const int PhotoQuality = 85;
    public const int ThumbnailQuality = 75;
    public const int MiniQuality = 50;
    public const int MiniMaxDimension = 24;

    /// <summary>Largest decode buffer accepted (≈ 200 MB as RGBA); bigger images fail instead of exhausting memory.</summary>
    internal const long MaxDecodedPixels = 50_000_000;

    private int PhotoMaxDimension => Math.Max(1, options.Value.PhotoMaxDimension);
    private int ThumbnailMaxDimension => Math.Max(MiniMaxDimension, options.Value.ThumbnailMaxDimension);

    /// <summary>Null when Skia cannot decode the file (unknown format, e.g. HEIC on servers).</summary>
    /// <param name="forceReencode">Always write a JPEG (used for images converted from other formats).</param>
    /// <exception cref="MediaProcessingException">Decodable header but unusable image (too large, corrupt pixels).</exception>
    public PhotoOutput? ProcessPhoto(string inputPath, string workDirectory, bool forceReencode = false)
    {
        using var codec = SKCodec.Create(inputPath);
        if (codec is null) return null;

        var format = codec.EncodedFormat;
        var (width, height) = OrientedSize(codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);
        if (width <= 0 || height <= 0) throw new MediaProcessingException("The image has no pixels.");

        var keptType = KeptContentType(format, Math.Max(width, height));
        if (!forceReencode && keptType is not null)
        {
            using var preview = DecodeOriented(codec, ThumbnailMaxDimension);
            return new PhotoOutput(null, keptType, width, height, CreateThumbnails(preview));
        }

        using var full = DecodeOriented(codec, Math.Min(PhotoMaxDimension, Math.Max(width, height)));
        var outputPath = Path.Combine(workDirectory, "photo.jpg");
        File.WriteAllBytes(outputPath, EncodeJpeg(full, PhotoQuality));
        return new PhotoOutput(outputPath, MediaContentTypes.Jpeg, full.Width, full.Height, CreateThumbnails(full));
    }

    /// <summary>Preview + mini thumbnail of an image file (video posters); null when it cannot be decoded.</summary>
    public ThumbnailSet? CreateThumbnails(string imagePath)
    {
        using var codec = SKCodec.Create(imagePath);
        if (codec is null) return null;
        using var preview = DecodeOriented(codec, ThumbnailMaxDimension);
        return CreateThumbnails(preview);
    }

    private ThumbnailSet CreateThumbnails(SKBitmap oriented)
    {
        using var thumbnail = Render(oriented, SKEncodedOrigin.TopLeft, ThumbnailMaxDimension);
        using var mini = Render(thumbnail, SKEncodedOrigin.TopLeft, MiniMaxDimension);
        return new ThumbnailSet(EncodeJpeg(thumbnail, ThumbnailQuality), thumbnail.Width, thumbnail.Height, EncodeJpeg(mini, MiniQuality));
    }

    private string? KeptContentType(SKEncodedImageFormat format, int longSide) => format switch
    {
        SKEncodedImageFormat.Gif => MediaContentTypes.Gif,
        SKEncodedImageFormat.Jpeg when longSide <= PhotoMaxDimension => MediaContentTypes.Jpeg,
        SKEncodedImageFormat.Png when longSide <= PhotoMaxDimension => MediaContentTypes.Png,
        SKEncodedImageFormat.Webp when longSide <= PhotoMaxDimension => MediaContentTypes.Webp,
        _ => null,
    };

    /// <summary>Decodes (scaled down natively where the codec can) and renders oriented, fitting within <paramref name="maxSide"/>.</summary>
    private static SKBitmap DecodeOriented(SKCodec codec, int maxSide)
    {
        var native = codec.Info;
        var size = new SKSizeI(native.Width, native.Height);
        var longSide = Math.Max(native.Width, native.Height);
        if (longSide > maxSide)
        {
            var scaled = codec.GetScaledDimensions((float)maxSide / longSide);
            if (scaled.Width > 0 && scaled.Height > 0 && Math.Max(scaled.Width, scaled.Height) >= maxSide) size = scaled;
        }
        if ((long)size.Width * size.Height > MaxDecodedPixels)
            throw new MediaProcessingException($"The image is too large to process ({native.Width}×{native.Height}).");

        var info = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        if (decoded.GetPixels() == IntPtr.Zero)
            throw new MediaProcessingException("Not enough memory to decode the image.");
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new MediaProcessingException($"The image could not be decoded ({result}).");
        return Render(decoded, codec.EncodedOrigin, maxSide);
    }

    /// <summary>
    /// Draws <paramref name="source"/> upright (per <paramref name="origin"/>), scaled to fit within
    /// <paramref name="maxSide"/> (never enlarged), on white so transparent areas do not turn black in JPEG.
    /// </summary>
    internal static SKBitmap Render(SKBitmap source, SKEncodedOrigin origin, int maxSide)
    {
        var (orientedWidth, orientedHeight) = OrientedSize(source.Width, source.Height, origin);
        var (width, height) = FitWithin(orientedWidth, orientedHeight, maxSide);

        var target = new SKBitmap(new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.White);
        var scale = SKMatrix.CreateScale((float)width / orientedWidth, (float)height / orientedHeight);
        canvas.SetMatrix(SKMatrix.Concat(scale, OrientationMatrix(origin, source.Width, source.Height)));

        using var image = SKImage.FromBitmap(source);
        var sampling = width < orientedWidth
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        canvas.DrawImage(image, 0, 0, sampling);
        canvas.Flush();
        return target;
    }

    internal static (int Width, int Height) OrientedSize(int width, int height, SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom
            ? (height, width)
            : (width, height);

    internal static (int Width, int Height) FitWithin(int width, int height, int maxSide)
    {
        var longSide = Math.Max(width, height);
        if (longSide <= maxSide) return (width, height);
        var factor = (double)maxSide / longSide;
        return (Math.Clamp((int)Math.Round(width * factor), 1, maxSide), Math.Clamp((int)Math.Round(height * factor), 1, maxSide));
    }

    /// <summary>
    /// Maps stored pixel coordinates (w×h as encoded) to upright display coordinates. EXIF names say where the stored
    /// 0th row/0th column end up, e.g. RightTop (6): 0th row on the right, 0th column at the top = rotate 90° clockwise.
    /// </summary>
    internal static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),        // mirror horizontally
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1), // rotate 180°
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),      // mirror vertically
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),               // transpose
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),        // rotate 90° clockwise
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1), // transverse
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),       // rotate 90° counter-clockwise
        _ => SKMatrix.Identity,
    };

    internal static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality)
                         ?? throw new MediaProcessingException("The image could not be encoded as JPEG.");
        return data.ToArray();
    }
}
