using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SkiaSharp;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using Xunit;

namespace SkyMessage.UnitTests.Media;

/// <summary>Real ffmpeg/ffprobe on tiny generated media (lavfi testsrc/sine). Skipped when FFmpeg is not installed.</summary>
public sealed class MediaProcessingTests : IDisposable
{
    private static readonly string? Ffmpeg = FindTool("ffmpeg");
    private static readonly string? Ffprobe = FindTool("ffprobe");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "skymessage-tests", Guid.NewGuid().ToString("N"));
    private readonly MediaProcessingOptions _options = new()
    {
        FfmpegPath = Ffmpeg ?? "ffmpeg",
        FfprobePath = Ffprobe ?? "ffprobe",
        VideoMaxDimension = 640,
        VideoNoteSize = 240,
        ThumbnailMaxDimension = 480,
        PhotoMaxDimension = 64,
    };

    public MediaProcessingTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public async Task H264_aac_mp4_is_remuxed_and_keeps_its_rotation()
    {
        RequireFfmpeg();
        var plain = Generate("plain.mp4", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "2.5", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
        var rotated = Generate("rotated.mp4", "-display_rotation", "90", "-i", plain, "-c", "copy");
        var work = WorkDirectory();

        var result = await Videos().ProcessVideoAsync(rotated, work, Ct);

        result.Remuxed.ShouldBeTrue();
        (result.Width, result.Height).ShouldBe((240, 320));
        result.DurationMs.ShouldNotBeNull().ShouldBeInRange(2400, 2600);
        var thumbnails = result.Thumbnails.ShouldNotBeNull();
        (thumbnails.Width, thumbnails.Height).ShouldBe((240, 320));
        var probe = await Runner().ProbeAsync(result.Path, "mov", Ct);
        probe.Video!.CodecName.ShouldBe("h264");
        probe.Video.Rotation.ShouldNotBe(0);
        probe.Audio!.CodecName.ShouldBe("aac");
    }

    [Fact]
    public async Task Webm_is_transcoded_to_h264_aac_mp4()
    {
        RequireFfmpeg();
        var webm = Generate("clip.webm", "-f", "lavfi", "-i", "testsrc=size=480x270:rate=25", "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000",
            "-t", "1.5", "-c:v", "libvpx", "-b:v", "200k", "-c:a", "libopus", "-shortest");

        var result = await Videos().ProcessVideoAsync(webm, WorkDirectory(), Ct);

        result.Remuxed.ShouldBeFalse();
        (result.Width, result.Height).ShouldBe((480, 270));
        var probe = await Runner().ProbeAsync(result.Path, "mov", Ct);
        probe.IsMp4Family.ShouldBeTrue();
        probe.Video!.CodecName.ShouldBe("h264");
        probe.Video.PixelFormat.ShouldBe("yuv420p");
        probe.Audio!.CodecName.ShouldBe("aac");
        result.Thumbnails.ShouldNotBeNull();
    }

    [Fact]
    public async Task Video_larger_than_the_limit_is_downscaled_with_even_dimensions()
    {
        RequireFfmpeg();
        // Odd source dimensions (VP8 allows them) must still produce even H.264 dimensions.
        var large = Generate("large.webm", "-f", "lavfi", "-i", "testsrc=size=1281x721:rate=10", "-t", "1", "-c:v", "libvpx", "-b:v", "300k");

        var result = await Videos().ProcessVideoAsync(large, WorkDirectory(), Ct);

        result.Remuxed.ShouldBeFalse();
        result.Width.ShouldBe(640);
        (result.Height % 2).ShouldBe(0);
        result.Height.ShouldBeInRange(358, 362);
    }

    [Fact]
    public async Task Video_note_becomes_a_centered_square_with_mono_audio()
    {
        RequireFfmpeg();
        var source = Generate("note.webm", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000",
            "-t", "2", "-c:v", "libvpx", "-b:v", "300k", "-c:a", "libopus", "-shortest");

        var result = await Videos().ProcessVideoNoteAsync(source, WorkDirectory(), Ct);

        (result.Width, result.Height).ShouldBe((240, 240));
        result.DurationMs.ShouldNotBeNull().ShouldBeInRange(1900, 2100);
        var probe = await Runner().ProbeAsync(result.Path, "mov", Ct);
        probe.Video!.CodecName.ShouldBe("h264");
        probe.Audio!.CodecName.ShouldBe("aac");
        probe.Audio.Channels.ShouldBe(1);
        result.Thumbnails.ShouldNotBeNull().Width.ShouldBe(240);
    }

    [Fact]
    public async Task Voice_becomes_aac_lc_mono_48k_m4a_with_a_waveform()
    {
        RequireFfmpeg();
        var recording = Generate("voice.webm", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000", "-t", "2.5", "-c:a", "libopus");

        var result = await Audio().ProcessVoiceAsync(recording, WorkDirectory(), Ct);

        result.DurationMs.ShouldNotBeNull().ShouldBeInRange(2400, 2700);
        var waveform = result.Waveform.ShouldNotBeNull();
        waveform.Length.ShouldBe(100);
        waveform.Max().ShouldBe((byte)255);
        waveform.Count(v => v > 150).ShouldBeGreaterThanOrEqualTo(90);
        var probe = await Runner().ProbeAsync(result.Path, "mov", Ct);
        var audio = probe.Audio.ShouldNotBeNull();
        audio.CodecName.ShouldBe("aac");
        audio.SampleRate.ShouldBe(48000);
        audio.Channels.ShouldBe(1);
        probe.Video.ShouldBeNull();
    }

    [Fact]
    public async Task Voice_waveform_follows_loudness()
    {
        RequireFfmpeg();
        // 1 s of silence, then 1 s of a loud tone.
        var recording = Generate("steps.wav", "-f", "lavfi", "-i", "aevalsrc='if(lt(t,1),0,0.8*sin(2*PI*440*t))':s=48000:d=2", "-c:a", "pcm_s16le");

        var waveform = await Audio().ComputeWaveformAsync(recording, AudioProcessor.InputFormats, Ct);

        waveform[..45].ShouldAllBe(v => v < 10);
        waveform[55..].ShouldAllBe(v => v > 200);
    }

    [Fact]
    public async Task Audio_duration_is_probed()
    {
        RequireFfmpeg();
        var song = Generate("song.m4a", "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=44100", "-t", "1.5", "-c:a", "aac");

        var duration = await Audio().ProbeDurationAsync(song, Ct);

        duration.ShouldNotBeNull().ShouldBeInRange(1400, 1600);
    }

    [Fact]
    public void Gif_of_any_size_is_kept_as_is()
    {
        RequireFfmpeg();
        var gif = Generate("anim.gif", "-f", "lavfi", "-i", "testsrc=size=200x100:rate=5", "-t", "1");

        var photo = new ImageProcessor(Options.Create(_options)).ProcessPhoto(gif, WorkDirectory()).ShouldNotBeNull();

        photo.OutputPath.ShouldBeNull();
        photo.ContentType.ShouldBe(MediaContentTypes.Gif);
        (photo.Width, photo.Height).ShouldBe((200, 100));
        (photo.Thumbnails.Width, photo.Thumbnails.Height).ShouldBe((200, 100));
    }

    [Fact]
    public async Task Heic_is_converted_to_jpeg()
    {
        RequireFfmpeg();
        Assert.SkipUnless(OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sips"), "Creating a HEIC test image needs macOS sips.");
        var png = Path.Combine(_directory, "source.png");
        await File.WriteAllBytesAsync(png, ImageProcessorTests.EncodeTestImage(1200, 900, SKEncodedImageFormat.Png), Ct);
        var heic = Path.Combine(_directory, "photo.heic");
        Run("/usr/bin/sips", "-s", "format", "heic", png, "--out", heic);
        HeifConverter.LooksLikeHeif(heic).ShouldBeTrue();
        HeifConverter.ReadPrimaryImageSize(heic).ShouldBe((1200, 900));
        var work = WorkDirectory();

        var converted = await new HeifConverter(Runner()).ConvertToPngAsync(heic, work, Ct);
        var photo = ImageProcessorTests.Processor(photoMax: 1000).ProcessPhoto(converted, work, forceReencode: true).ShouldNotBeNull();

        photo.OutputPath.ShouldNotBeNull();
        photo.ContentType.ShouldBe(MediaContentTypes.Jpeg);
        // Tiled HEIC grids need ffmpeg ≥ 7.1 to be assembled into the full picture.
        (photo.Width, photo.Height).ShouldBe((1000, 750));
    }

    [Fact]
    public async Task Files_without_a_video_stream_fail_as_video()
    {
        RequireFfmpeg();
        var audioOnly = Generate("audio.m4a", "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=44100", "-t", "1", "-c:a", "aac");

        var error = await Should.ThrowAsync<MediaProcessingException>(() => Videos().ProcessVideoAsync(audioOnly, WorkDirectory(), Ct));

        error.Message.ShouldContain("no video stream");
    }

    [Fact]
    public async Task Playlists_disguised_as_media_are_rejected()
    {
        RequireFfmpeg();
        // Classic ffmpeg SSRF/LFI vector: an HLS playlist uploaded as a "video" that points at local files.
        var playlist = Path.Combine(_directory, "source");
        await File.WriteAllTextAsync(playlist, "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nfile:///etc/hosts\n#EXT-X-ENDLIST\n", Ct);

        await Should.ThrowAsync<MediaProcessingException>(() => Videos().ProcessVideoAsync(playlist, WorkDirectory(), Ct));
        await Should.ThrowAsync<MediaProcessingException>(() => Audio().ProcessVoiceAsync(playlist, WorkDirectory(), Ct));
    }

    [Fact]
    public async Task Missing_tools_are_reported_as_such()
    {
        var runner = new FfmpegRunner(Options.Create(new MediaProcessingOptions
        {
            FfmpegPath = "/nonexistent/skymessage/ffmpeg",
            FfprobePath = "/nonexistent/skymessage/ffprobe",
        }), NullLogger<FfmpegRunner>.Instance);

        var ffmpeg = await Should.ThrowAsync<MediaToolMissingException>(() => runner.RunFfmpegAsync(["-version"], TimeSpan.FromSeconds(10), Ct));
        ffmpeg.Tool.ShouldBe("ffmpeg");
        var ffprobe = await Should.ThrowAsync<MediaToolMissingException>(() => runner.ProbeAsync("/tmp/x", "mov", Ct));
        ffprobe.Tool.ShouldBe("ffprobe");
    }

    [Fact]
    public async Task Failures_carry_the_stderr_tail()
    {
        RequireFfmpeg();

        var error = await Should.ThrowAsync<MediaProcessingException>(() =>
            Runner().RunFfmpegAsync(["-i", Path.Combine(_directory, "missing.mp4"), "-f", "null", "-"], TimeSpan.FromSeconds(30), Ct));

        error.Message.ShouldContain("exit code");
        error.Message.ShouldContain("missing.mp4");
    }

    [Fact]
    public async Task Timeout_kills_the_process()
    {
        RequireFfmpeg();
        var stopwatch = Stopwatch.StartNew();

        var error = await Should.ThrowAsync<MediaProcessingException>(() => Runner().RunFfmpegAsync(
            ["-re", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10", "-f", "null", "-"], TimeSpan.FromSeconds(1), Ct));

        error.Message.ShouldContain("timed out");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Cancellation_stops_the_process()
    {
        RequireFfmpeg();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Should.ThrowAsync<OperationCanceledException>(() => Runner().RunFfmpegAsync(
            ["-re", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10", "-f", "null", "-"], TimeSpan.FromMinutes(1), cts.Token));
    }

    private FfmpegRunner Runner() => new(Options.Create(_options), NullLogger<FfmpegRunner>.Instance);

    private VideoProcessor Videos() =>
        new(Runner(), new ImageProcessor(Options.Create(_options)), Options.Create(_options), NullLogger<VideoProcessor>.Instance);

    private AudioProcessor Audio() => new(Runner(), NullLogger<AudioProcessor>.Instance);

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
