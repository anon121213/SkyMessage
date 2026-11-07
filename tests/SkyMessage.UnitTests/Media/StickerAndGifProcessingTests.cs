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
