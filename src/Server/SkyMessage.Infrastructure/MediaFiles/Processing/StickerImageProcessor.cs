using SkiaSharp;
using SkyMessage.Contracts;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>A sticker's image as stored: WebP with transparency, plus a smaller WebP preview for panels and grids.</summary>
internal sealed record StickerImage(byte[] Webp, int Width, int Height, byte[] Thumbnail, int ThumbnailWidth, int ThumbnailHeight);

/// <summary>Why an image cannot become a sticker (the message is for logs; the API answers sticker_image_invalid).</summary>
internal sealed class StickerImageException(string message) : Exception(message);

/// <summary>
/// Makes stickers out of uploaded images (SkiaSharp): only PNG, WebP and JPEG (by their bytes, not by what the upload
/// claims), each side from <see cref="Limits.StickerSourceMinSide"/> to <see cref="Limits.StickerSourceMaxSide"/> px (it
/// runs while the request waits: a few bytes of PNG can describe a huge canvas, and PNG cannot be decoded at a smaller
/// scale). EXIF orientation is applied; the result fits within <see cref="Limits.StickerMaxSide"/> px (never enlarged) and
/// keeps its transparency: drawn on a transparent canvas and encoded as WebP (lossy with an alpha plane), which also drops
/// every piece of metadata.
/// </summary>
internal static class StickerImageProcessor
{
    public const int Quality = 90;
    public const int ThumbnailMaxSide = 256;
    public const int ThumbnailQuality = 80;

    public static StickerImage Process(byte[] source)
    {
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data) ?? throw new StickerImageException("The image cannot be decoded.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Jpeg))
            throw new StickerImageException($"{codec.EncodedFormat} images cannot be stickers.");

        var (width, height) = ImageProcessor.OrientedSize(codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);
        if (width < Limits.StickerSourceMinSide || height < Limits.StickerSourceMinSide)
            throw new StickerImageException($"The image is too small ({width}×{height}).");
        if (width > Limits.StickerSourceMaxSide || height > Limits.StickerSourceMaxSide)
            throw new StickerImageException($"The image is too large ({width}×{height}).");

        using var decoded = Decode(codec, Limits.StickerMaxSide);
        // Drawn from its own pixels (no copy of a possibly large bitmap).
        decoded.SetImmutable();
        using var sticker = Render(decoded, codec.EncodedOrigin, Limits.StickerMaxSide);
        sticker.SetImmutable();
        using var thumbnail = Render(sticker, SKEncodedOrigin.TopLeft, ThumbnailMaxSide);
        return new StickerImage(
            EncodeWebp(sticker, Quality), sticker.Width, sticker.Height,
            EncodeWebp(thumbnail, ThumbnailQuality), thumbnail.Width, thumbnail.Height);
    }

    /// <summary>Decodes with alpha, scaled down natively where the codec can (JPEG, WebP).</summary>
    private static SKBitmap Decode(SKCodec codec, int maxSide)
    {
        var native = codec.Info;
        var size = new SKSizeI(native.Width, native.Height);
        var longSide = Math.Max(native.Width, native.Height);
        if (longSide > maxSide)
        {
            var scaled = codec.GetScaledDimensions((float)maxSide / longSide);
            if (scaled.Width > 0 && scaled.Height > 0 && Math.Max(scaled.Width, scaled.Height) >= maxSide) size = scaled;
        }
        if ((long)size.Width * size.Height > ImageProcessor.MaxDecodedPixels)
            throw new StickerImageException($"The image is too large to process ({native.Width}×{native.Height}).");

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        if (bitmap.GetPixels() == IntPtr.Zero)
        {
            bitmap.Dispose();
            throw new StickerImageException("Not enough memory to decode the image.");
        }
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new StickerImageException($"The image could not be decoded ({result}).");
        }
        return bitmap;
    }

    /// <summary>Upright (per <paramref name="origin"/>), within <paramref name="maxSide"/>, on a transparent canvas.</summary>
    private static SKBitmap Render(SKBitmap source, SKEncodedOrigin origin, int maxSide)
    {
        var (orientedWidth, orientedHeight) = ImageProcessor.OrientedSize(source.Width, source.Height, origin);
        var (width, height) = ImageProcessor.FitWithin(orientedWidth, orientedHeight, maxSide);

        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        var scale = SKMatrix.CreateScale((float)width / orientedWidth, (float)height / orientedHeight);
        canvas.SetMatrix(SKMatrix.Concat(scale, ImageProcessor.OrientationMatrix(origin, source.Width, source.Height)));
        using var image = SKImage.FromBitmap(source);
        var sampling = width < orientedWidth
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        canvas.DrawImage(image, 0, 0, sampling);
        canvas.Flush();
        return target;
    }

    private static byte[] EncodeWebp(SKBitmap bitmap, int quality)
    {
        using var pixmap = bitmap.PeekPixels() ?? throw new StickerImageException("The sticker has no pixels.");
        using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, quality))
                         ?? throw new StickerImageException("The sticker could not be encoded as WebP.");
        return data.ToArray();
    }
}
