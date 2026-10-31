using Microsoft.Extensions.Options;
using Shouldly;
using SkiaSharp;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using Xunit;

namespace SkyMessage.UnitTests.Media;

public sealed class ImageProcessorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "skymessage-tests", Guid.NewGuid().ToString("N"));

    public ImageProcessorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static TheoryData<SKEncodedOrigin> Origins() =>
    [
        SKEncodedOrigin.TopLeft, SKEncodedOrigin.TopRight, SKEncodedOrigin.BottomRight, SKEncodedOrigin.BottomLeft,
        SKEncodedOrigin.LeftTop, SKEncodedOrigin.RightTop, SKEncodedOrigin.RightBottom, SKEncodedOrigin.LeftBottom,
    ];

    [Theory]
    [MemberData(nameof(Origins))]
    public void Orientation_maps_every_stored_pixel_to_its_upright_position(SKEncodedOrigin origin)
    {
        const int w = 3, h = 2;
        using var source = new SKBitmap(w, h);
        for (var x = 0; x < w; x++)
        for (var y = 0; y < h; y++)
            source.SetPixel(x, y, ColorAt(x, y));

        using var upright = ImageProcessor.Render(source, origin, maxSide: 100);

        var transposed = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        upright.Width.ShouldBe(transposed ? h : w);
        upright.Height.ShouldBe(transposed ? w : h);
        for (var vx = 0; vx < upright.Width; vx++)
        for (var vy = 0; vy < upright.Height; vy++)
        {
            var (x, y) = origin switch
            {
                SKEncodedOrigin.TopRight => (w - 1 - vx, vy),
                SKEncodedOrigin.BottomRight => (w - 1 - vx, h - 1 - vy),
                SKEncodedOrigin.BottomLeft => (vx, h - 1 - vy),
                SKEncodedOrigin.LeftTop => (vy, vx),
                SKEncodedOrigin.RightTop => (vy, h - 1 - vx),          // rotate 90° clockwise
                SKEncodedOrigin.RightBottom => (w - 1 - vy, h - 1 - vx),
                SKEncodedOrigin.LeftBottom => (w - 1 - vy, vx),        // rotate 90° counter-clockwise
                _ => (vx, vy),
            };
            ShouldBeClose(upright.GetPixel(vx, vy), ColorAt(x, y), $"{origin} at ({vx},{vy})");
        }
    }

    [Fact]
    public void Exif_rotated_jpeg_is_kept_and_reported_upright()
    {
        // A landscape sensor image tagged "rotate 90° clockwise" (orientation 6) is a portrait photo.
        var path = WriteFile("rotated.jpg", WithExifOrientation(EncodeTestImage(400, 300, SKEncodedImageFormat.Jpeg), orientation: 6));

        var photo = Processor(photoMax: 1000).ProcessPhoto(path, _directory).ShouldNotBeNull();

        photo.OutputPath.ShouldBeNull();
        photo.ContentType.ShouldBe(MediaContentTypes.Jpeg);
        photo.Width.ShouldBe(300);
        photo.Height.ShouldBe(400);
        photo.Thumbnails.Width.ShouldBe(300);
        photo.Thumbnails.Height.ShouldBe(400);
    }

    [Fact]
    public void Exif_rotated_jpeg_that_is_too_large_is_reencoded_upright_without_metadata()
    {
        var path = WriteFile("rotated.jpg", WithExifOrientation(EncodeTestImage(1600, 1200, SKEncodedImageFormat.Jpeg), orientation: 8));

        var photo = Processor(photoMax: 800).ProcessPhoto(path, _directory).ShouldNotBeNull();

        photo.OutputPath.ShouldNotBeNull();
        photo.ContentType.ShouldBe(MediaContentTypes.Jpeg);
        (photo.Width, photo.Height).ShouldBe((600, 800));
        using var codec = SKCodec.Create(photo.OutputPath);
        codec.EncodedFormat.ShouldBe(SKEncodedImageFormat.Jpeg);
        codec.EncodedOrigin.ShouldBe(SKEncodedOrigin.TopLeft);
        (codec.Info.Width, codec.Info.Height).ShouldBe((600, 800));
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    public void Small_web_formats_are_kept_byte_for_byte(SKEncodedImageFormat format, string contentType)
    {
        var path = WriteFile("small.img", EncodeTestImage(640, 480, format));

        var photo = Processor(photoMax: 1024).ProcessPhoto(path, _directory).ShouldNotBeNull();

        photo.OutputPath.ShouldBeNull();
        photo.ContentType.ShouldBe(contentType);
        (photo.Width, photo.Height).ShouldBe((640, 480));
        (photo.Thumbnails.Width, photo.Thumbnails.Height).ShouldBe((480, 360));
    }

    [Fact]
    public void Large_png_is_downscaled_to_jpeg_on_white()
    {
        using (var bitmap = new SKBitmap(new SKImageInfo(1200, 600, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            bitmap.Erase(SKColors.Transparent);
            WriteFile("large.png", Encode(bitmap, SKEncodedImageFormat.Png));
        }

        var photo = Processor(photoMax: 600).ProcessPhoto(Path.Combine(_directory, "large.png"), _directory).ShouldNotBeNull();

        photo.OutputPath.ShouldNotBeNull();
        photo.ContentType.ShouldBe(MediaContentTypes.Jpeg);
        (photo.Width, photo.Height).ShouldBe((600, 300));
        using var output = SKBitmap.Decode(photo.OutputPath);
        ShouldBeClose(output.GetPixel(300, 150), SKColors.White, "transparent areas become white");
    }

    [Fact]
    public void Huge_jpeg_is_decoded_scaled_and_fits_the_limit()
    {
        var path = WriteFile("huge.jpg", EncodeTestImage(4000, 3000, SKEncodedImageFormat.Jpeg));

        var photo = Processor(photoMax: 1000).ProcessPhoto(path, _directory).ShouldNotBeNull();

        (photo.Width, photo.Height).ShouldBe((1000, 750));
        (photo.Thumbnails.Width, photo.Thumbnails.Height).ShouldBe((480, 360));
    }

    [Fact]
    public void Mini_thumbnail_is_tiny_and_valid()
    {
        var path = WriteFile("photo.jpg", EncodeTestImage(1024, 768, SKEncodedImageFormat.Jpeg));

        var photo = Processor(photoMax: 4096).ProcessPhoto(path, _directory).ShouldNotBeNull();

        photo.Thumbnails.Mini.Length.ShouldBeLessThan(1500);
        using var mini = SKBitmap.Decode(photo.Thumbnails.Mini);
        Math.Max(mini.Width, mini.Height).ShouldBe(ImageProcessor.MiniMaxDimension);
        (mini.Width, mini.Height).ShouldBe((24, 18));
        using var thumbnail = SKBitmap.Decode(photo.Thumbnails.Jpeg);
        (thumbnail.Width, thumbnail.Height).ShouldBe((480, 360));
    }

    [Fact]
    public void Gif_is_always_kept()
    {
        // The classic 1×1 GIF89a.
        byte[] gif =
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
            0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02,
            0x02, 0x44, 0x01, 0x00, 0x3B,
        ];
        var path = WriteFile("pixel.gif", gif);

        var photo = Processor(photoMax: 4096).ProcessPhoto(path, _directory).ShouldNotBeNull();

        photo.OutputPath.ShouldBeNull();
        photo.ContentType.ShouldBe(MediaContentTypes.Gif);
        (photo.Width, photo.Height).ShouldBe((1, 1));
    }

    [Fact]
    public void Undecodable_files_return_null()
    {
        var path = WriteFile("noise.bin", "definitely not an image"u8.ToArray());

        Processor(photoMax: 4096).ProcessPhoto(path, _directory).ShouldBeNull();
        Processor(photoMax: 4096).CreateThumbnails(path).ShouldBeNull();
        HeifConverter.LooksLikeHeif(path).ShouldBeFalse();
    }

    [Fact]
    public void Fit_within_keeps_aspect_ratio_and_never_enlarges()
    {
        ImageProcessor.FitWithin(4000, 3000, 480).ShouldBe((480, 360));
        ImageProcessor.FitWithin(300, 200, 480).ShouldBe((300, 200));
        ImageProcessor.FitWithin(10_000, 1, 24).ShouldBe((24, 1));
    }

    internal static ImageProcessor Processor(int photoMax) =>
        new(Options.Create(new MediaProcessingOptions { PhotoMaxDimension = photoMax, ThumbnailMaxDimension = 480 }));

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static SKColor ColorAt(int x, int y) => new((byte)(20 + x * 80), (byte)(30 + y * 150), 90);

    internal static byte[] EncodeTestImage(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SkyBlue);
            using var paint = new SKPaint { Color = SKColors.OrangeRed };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        }
        return Encode(bitmap, format);
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>Inserts an APP1 Exif segment (big-endian TIFF, IFD0 with only Orientation) right after SOI.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // header, IFD0 at offset 8
            0x00, 0x01,                                               // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,           // Orientation, SHORT, count 1
            (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,  // value (left-justified)
            0x00, 0x00, 0x00, 0x00,                                   // no next IFD
        ];
        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = payload.Length + 2;
        return [.. jpeg[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }

    private static void ShouldBeClose(SKColor actual, SKColor expected, string because)
    {
        Math.Abs(actual.Red - expected.Red).ShouldBeLessThanOrEqualTo(3, because);
        Math.Abs(actual.Green - expected.Green).ShouldBeLessThanOrEqualTo(3, because);
        Math.Abs(actual.Blue - expected.Blue).ShouldBeLessThanOrEqualTo(3, because);
    }
}
