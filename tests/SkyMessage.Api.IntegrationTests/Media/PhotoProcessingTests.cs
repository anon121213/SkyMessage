using System.Diagnostics;
using System.Net;
using Shouldly;
using SkiaSharp;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>Photo processing end to end: EXIF orientation, previews, keep-as-is vs downscale (SPEC §6.4).</summary>
[Collection(ApiCollection.Name)]
public sealed class PhotoProcessingTests(ApiFactory factory)
{
    [Theory]
    [InlineData((ushort)6, true)]  // RightTop: rotate 90° clockwise to display → the stored left half ends up on top
    [InlineData((ushort)8, false)] // LeftBottom: rotate 90° counter-clockwise → the stored left half ends up at the bottom
    public async Task Exif_orientation_is_applied_to_dimensions_and_previews(ushort orientation, bool redOnTop)
    {
        var user = await factory.RegisterUserAsync();
        // Stored 400×200 (landscape sensor image), displayed 200×400.
        var upload = TestImages.WithExifOrientation(TestImages.Halves(400, 200, SKEncodedImageFormat.Jpeg, TestImages.Red, TestImages.Blue), orientation);

        var media = await user.UploadReadyAsync(MediaKind.Photo, upload, "image/jpeg", "portrait.jpg");

        (media.Width, media.Height).ShouldBe((200, 400));
        media.ContentType.ShouldBe("image/jpeg");
        media.FileName.ShouldBe("portrait.jpg");
        // Within the limits a JPEG is kept byte for byte (orientation tag included, browsers apply it).
        (await user.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(upload, "kept photo");

        Func<SKColor, bool> top = redOnTop ? TestImages.IsRed : TestImages.IsBlue;
        Func<SKColor, bool> bottom = redOnTop ? TestImages.IsBlue : TestImages.IsRed;
        media.HasThumbnail.ShouldBeTrue();
        var thumbnailBytes = await user.Client.DownloadThumbnailAsync(media.Id);
        TestImages.Inspect(thumbnailBytes).Origin.ShouldBe(SKEncodedOrigin.TopLeft, "previews are rendered upright");
        using (var thumbnail = TestImages.Decode(thumbnailBytes))
        {
            (thumbnail.Width, thumbnail.Height).ShouldBe((200, 400));
            TestImages.ShouldBeTopBottom(thumbnail, top, bottom, "thumbnail");
        }

        using var mini = TestImages.Decode(media.MiniThumbnail.ShouldNotBeNull());
        (mini.Width, mini.Height).ShouldBe((12, 24));
        TestImages.ShouldBeTopBottom(mini, top, bottom, "mini thumbnail");
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png", "photo.png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg", "photo.jpg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp", "photo.webp")]
    public async Task Web_formats_within_the_limit_are_kept_byte_for_byte_with_previews(SKEncodedImageFormat format, string contentType, string fileName)
    {
        var user = await factory.RegisterUserAsync();
        var upload = TestImages.Halves(640, 480, format, TestImages.Red, TestImages.Blue);

        var media = await user.UploadReadyAsync(MediaKind.Photo, upload, contentType, fileName);

        media.ContentType.ShouldBe(contentType);
        media.FileName.ShouldBe(fileName);
        media.SizeBytes.ShouldBe(upload.Length);
        (media.Width, media.Height).ShouldBe((640, 480));
        (await user.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(upload, "kept photo");

        media.HasThumbnail.ShouldBeTrue();
        var thumbnailBytes = await user.Client.DownloadThumbnailAsync(media.Id);
        TestImages.Inspect(thumbnailBytes).Format.ShouldBe(SKEncodedImageFormat.Jpeg);
        using (var thumbnail = TestImages.Decode(thumbnailBytes))
        {
            (thumbnail.Width, thumbnail.Height).ShouldBe((480, 360));
            TestImages.IsRed(thumbnail.GetPixel(60, 180)).ShouldBeTrue();
            TestImages.IsBlue(thumbnail.GetPixel(420, 180)).ShouldBeTrue();
        }
        var mini = media.MiniThumbnail.ShouldNotBeNull();
        mini.Length.ShouldBeLessThan(2048, "the mini thumbnail travels inside every DTO");
        using var miniBitmap = TestImages.Decode(mini);
        (miniBitmap.Width, miniBitmap.Height).ShouldBe((24, 18));
    }

    [Fact]
    public async Task Small_images_are_never_enlarged_for_previews()
    {
        var user = await factory.RegisterUserAsync();

        var media = await user.UploadReadyAsync(MediaKind.Photo, TestImages.Png(300, 200), "image/png", "small.png");

        using var thumbnail = TestImages.Decode(await user.Client.DownloadThumbnailAsync(media.Id));
        (thumbnail.Width, thumbnail.Height).ShouldBe((300, 200));
    }

    [Fact]
    public async Task Photo_larger_than_the_limit_is_downscaled_to_jpeg()
    {
        var user = await factory.RegisterUserAsync();
        var max = factory.Processing().PhotoMaxDimension;
        var upload = TestImages.Png(2 * max, 100);

        var media = await user.UploadReadyAsync(MediaKind.Photo, upload, "image/png", "panorama.png");

        media.ContentType.ShouldBe("image/jpeg");
        media.FileName.ShouldBe("panorama.jpg");
        (media.Width, media.Height).ShouldBe((max, 50));
        var stored = await user.Client.DownloadAsync(media.Id);
        stored.Length.ShouldBe((int)media.SizeBytes);
        var info = TestImages.Inspect(stored);
        (info.Format, info.Width, info.Height).ShouldBe((SKEncodedImageFormat.Jpeg, max, 50));
        using var thumbnail = TestImages.Decode(await user.Client.DownloadThumbnailAsync(media.Id));
        thumbnail.Width.ShouldBe(factory.Processing().ThumbnailMaxDimension);
    }

    [Fact]
    public async Task Oversized_rotated_jpeg_is_reencoded_upright_without_its_orientation_tag()
    {
        var user = await factory.RegisterUserAsync();
        var max = factory.Processing().PhotoMaxDimension;
        // Stored (max + 304) × (max/2 + 152) landscape, tagged "rotate 90° clockwise": displayed portrait, too tall.
        var storedWidth = max + 304;
        var storedHeight = storedWidth / 2;
        var upload = TestImages.WithExifOrientation(
            TestImages.Halves(storedWidth, storedHeight, SKEncodedImageFormat.Jpeg, TestImages.Red, TestImages.Blue), orientation: 6);

        var media = await user.UploadReadyAsync(MediaKind.Photo, upload, "image/jpeg", "tall.jpg");

        (media.Width, media.Height).ShouldBe((max / 2, max));
        var stored = await user.Client.DownloadAsync(media.Id);
        var info = TestImages.Inspect(stored);
        (info.Format, info.Width, info.Height, info.Origin).ShouldBe((SKEncodedImageFormat.Jpeg, max / 2, max, SKEncodedOrigin.TopLeft));
        stored.AsSpan().IndexOf("Exif\0\0"u8).ShouldBe(-1, "re-encoding drops all metadata");
        using var decoded = TestImages.Decode(stored);
        TestImages.ShouldBeTopBottom(decoded, TestImages.IsRed, TestImages.IsBlue, "re-encoded photo");
    }

    [Fact]
    public async Task Gif_is_kept_as_uploaded()
    {
        var user = await factory.RegisterUserAsync();
        var gif = TestImages.Gif();

        var media = await user.UploadReadyAsync(MediaKind.Photo, gif, "image/gif", "pixel.gif");

        media.ContentType.ShouldBe("image/gif");
        (media.Width, media.Height).ShouldBe((1, 1));
        (await user.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(gif, "kept gif");
        media.HasThumbnail.ShouldBeTrue();
    }

    [Fact]
    public async Task Stored_content_type_and_file_name_follow_the_detected_image_format()
    {
        var user = await factory.RegisterUserAsync();
        var png = TestImages.Png(120, 80);

        var media = await user.UploadReadyAsync(MediaKind.Photo, png, "image/jpeg", "mislabeled.jpg");

        media.ContentType.ShouldBe("image/png");
        media.FileName.ShouldBe("mislabeled.png");
        using var response = await user.Client.GetContentAsync(media.Id, download: true);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
        response.Content.Headers.ContentDisposition?.FileNameStar.ShouldBe("mislabeled.png", "a saved file must open as what it is");
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(png, "kept photo");
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/heic", "IMG_0001.HEIC", "image/jpeg", "IMG_0001.jpg")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg", "holiday.JPEG", "image/jpeg", "holiday.JPEG")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg", "scan.jfif", "image/jpeg", "scan.jfif")]
    [InlineData(SKEncodedImageFormat.Png, "image/png", "Screenshot", "image/png", "Screenshot.png")]
    [InlineData(SKEncodedImageFormat.Webp, "image/png", "sticker.png", "image/webp", "sticker.webp")]
    [InlineData(SKEncodedImageFormat.Png, "image/png", "v1.2 final.PNG", "image/png", "v1.2 final.PNG")]
    public async Task Kept_photos_get_an_extension_that_matches_their_format(SKEncodedImageFormat format, string declaredType, string declaredName,
        string storedType, string storedName)
    {
        var user = await factory.RegisterUserAsync();
        var bytes = TestImages.Halves(160, 120, format, TestImages.Red, TestImages.Blue);

        var media = await user.UploadReadyAsync(MediaKind.Photo, bytes, declaredType, declaredName);

        media.ContentType.ShouldBe(storedType);
        media.FileName.ShouldBe(storedName);
        (await user.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(bytes, "kept photo");
    }

    [Fact]
    public async Task Undecodable_photo_fails_processing()
    {
        var user = await factory.RegisterUserAsync();

        var media = await user.UploadProcessedAsync(MediaKind.Photo, "<html><script>alert(1)</script></html>"u8.ToArray(), "image/png", "evil.png");

        media.Status.ShouldBe(MediaStatus.Failed);
        media.HasThumbnail.ShouldBeFalse();
        using (var content = await user.Client.GetContentAsync(media.Id))
            await content.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.MediaNotReady);
        using (var thumbnail = await user.Client.GetThumbnailAsync(media.Id))
            await thumbnail.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.MediaNotReady);
    }

    [Theory]
    [InlineData(20_000, 20_000)]
    [InlineData(4_000, 100_000)]
    public async Task Decompression_bombs_fail_instead_of_exhausting_memory(int width, int height)
    {
        var user = await factory.RegisterUserAsync();
        var bomb = TestImages.PngHeaderClaiming(width, height);
        TestImages.Inspect(bomb).ShouldBe((SKEncodedImageFormat.Png, width, height, SKEncodedOrigin.TopLeft), "precondition: a valid header");

        var media = await user.UploadProcessedAsync(MediaKind.Photo, bomb, "image/png", "bomb.png");

        media.Status.ShouldBe(MediaStatus.Failed);
        // The server is still fine afterwards.
        (await user.UploadPhotoAsync()).Status.ShouldBe(MediaStatus.Ready);
    }

    [Fact]
    public async Task Declared_sha256_is_verified_before_the_media_becomes_ready()
    {
        var user = await factory.RegisterUserAsync();
        var photo = TestImages.Png(50, 50);
        var file = RandomBytes(4096, seed: 21);

        var goodPhoto = await user.UploadProcessedAsync(MediaKind.Photo, photo, "image/png", "a.png", r => r with { Sha256 = Sha256Hex(photo) });
        var badPhoto = await user.UploadProcessedAsync(MediaKind.Photo, photo, "image/png", "b.png", r => r with { Sha256 = Sha256Hex(file) });
        var goodFile = await user.UploadProcessedAsync(MediaKind.File, file, "application/octet-stream", "c.bin", r => r with { Sha256 = Sha256Hex(file) });
        var badFile = await user.UploadProcessedAsync(MediaKind.File, file, "application/octet-stream", "d.bin", r => r with { Sha256 = Sha256Hex(photo) });

        goodPhoto.Status.ShouldBe(MediaStatus.Ready);
        badPhoto.Status.ShouldBe(MediaStatus.Failed);
        goodFile.Status.ShouldBe(MediaStatus.Ready);
        badFile.Status.ShouldBe(MediaStatus.Failed);
    }

    [Fact]
    public async Task Heic_photo_is_converted_to_jpeg()
    {
        Ffmpeg.Require();
        Assert.SkipUnless(OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sips"), "Creating a HEIC test image needs macOS sips.");
        var user = await factory.RegisterUserAsync();
        var heic = await ToHeicAsync(TestImages.Png(1200, 900));

        var media = await user.UploadReadyAsync(MediaKind.Photo, heic, "image/heic", "IMG_0001.HEIC");

        media.ContentType.ShouldBe("image/jpeg");
        media.FileName.ShouldBe("IMG_0001.jpg");
        (media.Width, media.Height).ShouldBe((1200, 900));
        var info = TestImages.Inspect(await user.Client.DownloadAsync(media.Id));
        (info.Format, info.Width, info.Height).ShouldBe((SKEncodedImageFormat.Jpeg, 1200, 900));
        using (var thumbnail = TestImages.Decode(await user.Client.DownloadThumbnailAsync(media.Id)))
            (thumbnail.Width, thumbnail.Height).ShouldBe((480, 360));

        // iOS sometimes labels HEIC uploads as JPEG: the content decides.
        var labeledJpeg = await user.UploadReadyAsync(MediaKind.Photo, heic, "image/jpeg", "IMG_0002.JPG");
        labeledJpeg.ContentType.ShouldBe("image/jpeg");
        labeledJpeg.FileName.ShouldBe("IMG_0002.jpg");
        TestImages.Inspect(await user.Client.DownloadAsync(labeledJpeg.Id)).Format.ShouldBe(SKEncodedImageFormat.Jpeg);
    }

    private static async Task<byte[]> ToHeicAsync(byte[] png)
    {
        var directory = Path.Combine(Path.GetTempPath(), "skymessage-media-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.png");
            var target = Path.Combine(directory, "photo.heic");
            await File.WriteAllBytesAsync(source, png, Ct);
            await Ffmpeg.RunAsync("/usr/bin/sips", ["-s", "format", "heic", source, "--out", target]);
            return await File.ReadAllBytesAsync(target, Ct);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
