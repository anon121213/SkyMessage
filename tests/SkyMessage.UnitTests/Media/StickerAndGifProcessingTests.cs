using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SkiaSharp;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using Xunit;

namespace SkyMessage.UnitTests.Media;

/// <summary>
/// Stickers (SkiaSharp: transparency kept, WebP, at most 512 px) and GIFs (real ffmpeg: a muted looping MP4 with a
/// poster). The GIF tests are skipped when FFmpeg is not installed.
/// </summary>
public sealed class StickerAndGifProcessingTests : IDisposable
{
    private static readonly string? Ffmpeg = FindTool("ffmpeg");
    private static readonly string? Ffprobe = FindTool("ffprobe");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "skymessage-tests", Guid.NewGuid().ToString("N"));
    private readonly MediaProcessingOptions _options = new()
    {
        FfmpegPath = Ffmpeg ?? "ffmpeg",
        FfprobePath = Ffprobe ?? "ffprobe",
        ThumbnailMaxDimension = 480,
    };

    public StickerAndGifProcessingTests() => Directory.CreateDirectory(_directory);

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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- stickers

    [Fact]
    public void Transparent_png_stays_transparent_as_a_webp_sticker()
    {
        var png = Circle(300, 300, SKEncodedImageFormat.Png);

        var sticker = StickerImageProcessor.Process(png);

        (sticker.Width, sticker.Height).ShouldBe((300, 300));
        using var decoded = Decode(sticker.Webp, out var format);
        format.ShouldBe(SKEncodedImageFormat.Webp);
        decoded.GetPixel(4, 4).Alpha.ShouldBe((byte)0, "the corner outside the circle must stay transparent");
        decoded.GetPixel(150, 150).Alpha.ShouldBe((byte)255);
        var center = decoded.GetPixel(150, 150);
        center.Red.ShouldBeGreaterThan((byte)200);
        center.Blue.ShouldBeLessThan((byte)60);

        using var thumbnail = Decode(sticker.Thumbnail, out var thumbFormat);
        thumbFormat.ShouldBe(SKEncodedImageFormat.Webp);
        (sticker.ThumbnailWidth, sticker.ThumbnailHeight).ShouldBe((256, 256));
        thumbnail.GetPixel(2, 2).Alpha.ShouldBe((byte)0);
    }

    [Fact]
    public void Large_webp_is_fitted_within_512_px_and_keeps_its_shape()
    {
        var webp = Circle(1600, 800, SKEncodedImageFormat.Webp);

        var sticker = StickerImageProcessor.Process(webp);

        (sticker.Width, sticker.Height).ShouldBe((512, 256));
        (sticker.ThumbnailWidth, sticker.ThumbnailHeight).ShouldBe((256, 128));
        using var decoded = Decode(sticker.Webp, out _);
        decoded.GetPixel(2, 2).Alpha.ShouldBe((byte)0);
    }

    [Fact]
    public void Jpeg_is_accepted_and_small_images_are_not_enlarged()
    {
        var jpeg = Circle(100, 80, SKEncodedImageFormat.Jpeg);

        var sticker = StickerImageProcessor.Process(jpeg);

        (sticker.Width, sticker.Height).ShouldBe((100, 80));
    }

    [Theory]
    [InlineData(63, 200)]
    [InlineData(200, 40)]
    public void Images_with_a_side_under_64_px_are_refused(int width, int height)
    {
        var png = Circle(width, height, SKEncodedImageFormat.Png);

        Should.Throw<StickerImageException>(() => StickerImageProcessor.Process(png));
    }

    [Theory]
    [InlineData(4097, 100)]
    [InlineData(100, 4097)]
    [InlineData(20000, 20000)]
    public void Images_with_a_side_over_4096_px_are_refused_before_decoding(int width, int height)
    {
        // A PNG header that claims the canvas (the pixels never come): the check reads only the header.
        var png = PngHeader(width, height);
        var started = Stopwatch.GetTimestamp();

        Should.Throw<StickerImageException>(() => StickerImageProcessor.Process(png)).Message.ShouldContain("too large");
        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void An_image_at_the_size_limit_becomes_a_512_px_sticker()
    {
        var png = Circle(Limits.StickerSourceMaxSide, 512, SKEncodedImageFormat.Png);

        var sticker = StickerImageProcessor.Process(png);

        (sticker.Width, sticker.Height).ShouldBe((Limits.StickerMaxSide, 64));
    }

    [Fact]
    public void Gifs_and_unknown_bytes_are_refused()
    {
        byte[] gif =
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
            0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02,
            0x02, 0x44, 0x01, 0x00, 0x3B,
        ];
        Should.Throw<StickerImageException>(() => StickerImageProcessor.Process(gif));
        Should.Throw<StickerImageException>(() => StickerImageProcessor.Process("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()));
    }

    // ---------------------------------------------------------------- GIFs

    [Fact]
    public void Only_gifs_with_more_than_one_frame_are_animations()
    {
        RequireFfmpeg();
        var animated = Generate("moving.gif", "-f", "lavfi", "-i", "testsrc=size=120x90:rate=10", "-t", "1");
        var still = Generate("still.gif", "-f", "lavfi", "-i", "color=c=red:size=64x64", "-frames:v", "1");
        var png = Path.Combine(_directory, "photo.png");
        File.WriteAllBytes(png, Circle(80, 80, SKEncodedImageFormat.Png));

        AnimationProcessor.IsAnimatedGif(animated).ShouldBeTrue();
        AnimationProcessor.IsAnimatedGif(still).ShouldBeFalse();
        AnimationProcessor.IsAnimatedGif(png).ShouldBeFalse();
    }

    [Fact]
    public void A_gif_is_measured_from_its_header_without_decoding_it()
    {
        var huge = Write("huge.gif", CraftedGif(12000, 12000, 50));
        var started = Stopwatch.GetTimestamp();

        var info = AnimationProcessor.Inspect(huge).ShouldNotBeNull();

        (info.Width, info.Height, info.Frames).ShouldBe((12000, 12000, 50));
        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(2));
        AnimationProcessor.IsWithinBudget(info).ShouldBeFalse();
        AnimationProcessor.Inspect(Write("photo.png", Circle(80, 80, SKEncodedImageFormat.Png))).ShouldBeNull();
    }

    [Theory]
    [InlineData(480, 270, 300, true)]
    [InlineData(1920, 1080, 600, true)]
    [InlineData(2048, 2048, 2, true)]
    [InlineData(4096, 1024, 2, true)]
    [InlineData(4097, 16, 2, false)]
    [InlineData(16, 4097, 2, false)]
    [InlineData(2100, 2100, 2, false)]
    [InlineData(8000, 8000, 50, false)]
    [InlineData(1024, 1024, 1500, false)]
    [InlineData(2048, 2048, 400, false)]
    // Frames past the duration cap are never decoded.
    [InlineData(500, 500, 100000, true)]
    public void Only_gifs_within_the_decoding_budget_become_loops(int width, int height, int frames, bool within) =>
        AnimationProcessor.IsWithinBudget(new GifInfo(width, height, frames)).ShouldBe(within);

    [Fact]
    public async Task A_large_canvas_is_scaled_down_before_it_is_worked_on()
    {
        RequireFfmpeg();
        var gif = Write("canvas.gif", CraftedGif(2048, 2048, 40));
        AnimationProcessor.IsWithinBudget(AnimationProcessor.Inspect(gif)!).ShouldBeTrue();

        var result = await Animations().ConvertGifAsync(gif, WorkDirectory(), Ct);

        (result.Width, result.Height).ShouldBe((Limits.AnimationMaxSide, Limits.AnimationMaxSide));
        var probe = await Runner().ProbeAsync(result.Path, "mov", Ct);
        probe.Video.ShouldNotBeNull().PixelFormat.ShouldBe("yuv420p");
        probe.Audio.ShouldBeNull();
    }

    [Fact]
    public async Task Gif_becomes_a_muted_h264_mp4_loop_with_a_poster()
    {
        RequireFfmpeg();
        var gif = Generate("anim.gif", "-f", "lavfi", "-i", "testsrc=size=321x241:rate=10", "-t", "2");

        var result = await Animations().ConvertGifAsync(gif, WorkDirectory(), Ct);

        (result.Width % 2, result.Height % 2).ShouldBe((0, 0));
        result.Width.ShouldBeInRange(318, 322);
        result.DurationMs.ShouldNotBeNull().ShouldBeInRange(1800, 2200);
        var poster = result.Thumbnails.ShouldNotBeNull();
        poster.Jpeg.Length.ShouldBeGreaterThan(0);
        poster.Mini.Length.ShouldBeGreaterThan(0);

        var probe = await Runner().ProbeAsync(result.Path, "mov", Ct);
        probe.IsMp4Family.ShouldBeTrue();
        probe.Audio.ShouldBeNull("a GIF loop is muted: no audio track at all");
        var video = probe.Video.ShouldNotBeNull();
        video.CodecName.ShouldBe("h264");
        video.PixelFormat.ShouldBe("yuv420p");
    }

    [Fact]
    public async Task Large_gif_is_fitted_within_720_px()
    {
        RequireFfmpeg();
        var gif = Generate("wide.gif", "-f", "lavfi", "-i", "testsrc=size=1000x400:rate=5", "-t", "1");

        var result = await Animations().ConvertGifAsync(gif, WorkDirectory(), Ct);

        result.Width.ShouldBe(Limits.AnimationMaxSide);
        (result.Height % 2).ShouldBe(0);
        result.Height.ShouldBeInRange(286, 290);
    }

    [Fact]
    public async Task Transparent_gif_lands_on_white()
    {
        RequireFfmpeg();
        // A red square moving over a fully transparent background.
        // Finite sources: palettegen needs the end of its input.
        var gif = Generate("transparent.gif",
            "-f", "lavfi", "-i", "color=c=black@0.0:size=96x96:rate=5:duration=1,format=rgba",
            "-f", "lavfi", "-i", "color=c=red:size=24x24:rate=5:duration=1",
            "-filter_complex", "[0][1]overlay=x='t*20':y=36:format=auto,split[a][b];[a]palettegen=reserve_transparent=1[p];[b][p]paletteuse=alpha_threshold=128");
        AnimationProcessor.IsAnimatedGif(gif).ShouldBeTrue();

        var result = await Animations().ConvertGifAsync(gif, WorkDirectory(), Ct);

        var frame = Path.Combine(_directory, "frame.png");
        Run(Ffmpeg!, "-nostdin", "-v", "error", "-y", "-i", result.Path, "-frames:v", "1", frame);
        using var decoded = SKBitmap.Decode(frame);
        var corner = decoded.GetPixel(90, 4);
        corner.Red.ShouldBeGreaterThan((byte)230);
        corner.Green.ShouldBeGreaterThan((byte)230);
        corner.Blue.ShouldBeGreaterThan((byte)230);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A red disc on a transparent background (JPEG flattens it, which is fine for that case).</summary>
    private static byte[] Circle(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(230, 20, 20), IsAntialias = true };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) * 0.4f, paint);
        }
        using var data = bitmap.Encode(format, 95) ?? throw new InvalidOperationException($"Skia cannot encode {format}.");
        return data.ToArray();
    }

    /// <summary>
    /// A GIF of a few hundred bytes that describes a <paramref name="width"/> × <paramref name="height"/> canvas with
    /// <paramref name="frames"/> frames of one pixel each.
    /// </summary>
    private static byte[] CraftedGif(int width, int height, int frames)
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0xF1, 0, 0]);
        gif.AddRange([255, 255, 255, 255, 0, 0, 0, 0, 255, 0, 0, 0]);
        gif.AddRange([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8.ToArray(), 0x03, 0x01, 0x00, 0x00, 0x00]);
        for (var i = 0; i < frames; i++)
        {
            var (x, y) = (i * 7 % width, i * 13 % height);
            gif.AddRange([0x21, 0xF9, 0x04, 0x04, 4, 0, 0, 0]);
            gif.AddRange([0x2C, (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8), 1, 0, 1, 0, 0]);
            // LZW, 2-bit codes widened to 3: clear, one red or blue pixel, end.
            gif.AddRange([0x02, 0x02, (byte)(i % 2 == 0 ? 0x4C : 0x54), 0x01, 0x00]);
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }

    /// <summary>
    /// A PNG of a few dozen bytes claiming <paramref name="width"/> × <paramref name="height"/> RGBA pixels: its header and
    /// an empty image stream (decoding it would fail, reading its size does not).
    /// </summary>
    private static byte[] PngHeader(int width, int height)
    {
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        var png = new List<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk("IHDR", ihdr);
        Chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Chunk("IEND", []);
        return png.ToArray();

        void Chunk(string type, byte[] data)
        {
            byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
            png.AddRange([.. length, .. typed, .. crc]);
        }
    }

    /// <summary>PNG's CRC-32 (ISO 3309, reflected 0xEDB88320).</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static SKBitmap Decode(byte[] bytes, out SKEncodedImageFormat format)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes)) ?? throw new InvalidOperationException("Not an image.");
        format = codec.EncodedFormat;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        codec.GetPixels(info, bitmap.GetPixels()).ShouldBe(SKCodecResult.Success);
        return bitmap;
    }

    private FfmpegRunner Runner() => new(Options.Create(_options), NullLogger<FfmpegRunner>.Instance);

    private AnimationProcessor Animations() =>
        new(Runner(), new ImageProcessor(Options.Create(_options)), Options.Create(_options), NullLogger<AnimationProcessor>.Instance);

    private string WorkDirectory()
    {
        var path = Path.Combine(_directory, "work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RequireFfmpeg() =>
        Assert.SkipUnless(Ffmpeg is not null && Ffprobe is not null, "ffmpeg/ffprobe are not installed.");

    private string Generate(string name, params string[] arguments)
    {
        var output = Path.Combine(_directory, name);
        Run(Ffmpeg!, ["-nostdin", "-hide_banner", "-v", "error", "-y", .. arguments, output]);
        File.Exists(output).ShouldBeTrue();
        return output;
    }

    private static void Run(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{executable} timed out");
        }
        process.ExitCode.ShouldBe(0, $"{executable} {string.Join(' ', arguments)}: {stderr.Result}");
    }

    private static string? FindTool(string name)
    {
        var resolved = FfmpegRunner.ResolveExecutable(name, name);
        return Path.IsPathRooted(resolved) && File.Exists(resolved) ? resolved : null;
    }
}
