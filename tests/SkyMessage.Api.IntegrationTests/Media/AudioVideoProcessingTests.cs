using System.Net;
using Shouldly;
using SkiaSharp;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>
/// Video (remux vs transcode, poster), video notes, voice, audio and files end to end through the real FFmpeg
/// (SPEC §6.4). FFmpeg-dependent tests are skipped when it is not installed.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AudioVideoProcessingTests(ApiFactory factory)
{
    [Fact]
    public async Task H264_aac_mp4_within_limits_is_remuxed_with_faststart_without_metadata_and_gets_a_poster()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var upload = await Ffmpeg.GenerateAsync(".mp4",
            "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "2.5", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
            "-metadata", "title=secret title", "-metadata", "location=+13.7563+100.5018/");
        Mp4.IsFastStart(upload).ShouldBeFalse("precondition: ffmpeg writes the index after the media data by default");

        var media = await user.UploadReadyAsync(MediaKind.Video, upload, "video/mp4", "clip.mp4");

        media.ContentType.ShouldBe("video/mp4");
        media.FileName.ShouldBe("clip.mp4");
        (media.Width, media.Height).ShouldBe((320, 240));
        media.DurationMs.ShouldNotBeNull().ShouldBeInRange(2400, 2700);
        var stored = await user.Client.DownloadAsync(media.Id);
        stored.Length.ShouldBe((int)media.SizeBytes);
        Mp4.IsFastStart(stored).ShouldBeTrue("remuxed with +faststart");
        (await Ffmpeg.StreamHashAsync(stored)).ShouldBe(await Ffmpeg.StreamHashAsync(upload), "remux copies the H.264 stream");
        var probe = await Ffmpeg.ProbeAsync(stored);
        probe.Video.ShouldNotBeNull().CodecName.ShouldBe("h264");
        probe.Audio.ShouldNotBeNull().CodecName.ShouldBe("aac");
        probe.Tags.Keys.ShouldNotContain("title");
        probe.Tags.Keys.ShouldNotContain(k => k.StartsWith("location", StringComparison.Ordinal), "location tags must be stripped");

        await ShouldHavePosterAsync(user, media, 320, 240);

        // Players probe with a tiny range first (AVPlayer asks for bytes 0-1), then seek into the processed file.
        using var probeRange = await user.Client.GetContentAsync(media.Id, configure: r => r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1));
        await probeRange.ShouldHaveStatusAsync(HttpStatusCode.PartialContent);
        probeRange.Content.Headers.ContentType?.MediaType.ShouldBe("video/mp4");
        probeRange.Content.Headers.ContentRange?.Length.ShouldBe(stored.Length);
        (await probeRange.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(stored[..2], "first two bytes");
    }

    [Fact]
    public async Task Video_note_without_a_microphone_track_is_processed_too()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var size = factory.Processing().VideoNoteSize;
        var silent = await Ffmpeg.GenerateAsync(".webm", "-f", "lavfi", "-i", "testsrc=size=360x640:rate=30", "-t", "1.5", "-c:v", "libvpx", "-b:v", "300k");

        var media = await user.UploadReadyAsync(MediaKind.VideoNote, silent, "video/webm", "selfie.webm");

        (media.Width, media.Height).ShouldBe((size, size));
        media.FileName.ShouldBe("selfie.mp4");
        var probe = await Ffmpeg.ProbeAsync(await user.Client.DownloadAsync(media.Id));
        probe.Video.ShouldNotBeNull().CodecName.ShouldBe("h264");
        probe.Audio.ShouldBeNull();
    }

    [Fact]
    public async Task Portrait_phone_video_is_remuxed_keeping_its_rotation_and_reported_upright()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var landscape = await Ffmpeg.GenerateAsync(".mp4",
            "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "2.5", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
        var portrait = await RotateAsync(landscape);

        var media = await user.UploadReadyAsync(MediaKind.Video, portrait, "video/mp4", "portrait.mp4");

        (media.Width, media.Height).ShouldBe((240, 320), "phones record landscape frames with a rotation matrix");
        var stored = await user.Client.DownloadAsync(media.Id);
        (await Ffmpeg.StreamHashAsync(stored)).ShouldBe(await Ffmpeg.StreamHashAsync(portrait), "remuxed, not re-encoded");
        await ShouldHavePosterAsync(user, media, 240, 320);
    }

    [Theory]
    [InlineData(MediaKind.Video, "video/mp4")]
    [InlineData(MediaKind.VideoNote, "video/mp4")]
    [InlineData(MediaKind.Voice, "audio/mpeg")]
    public async Task Playlists_and_scripts_disguised_as_media_never_make_ffmpeg_read_other_files(MediaKind kind, string contentType)
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        // Classic FFmpeg SSRF/LFI vectors: an HLS playlist and a concat script pointing at local files.
        var playlist = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-TARGETDURATION:10\n#EXTINF:10.0,\nfile:///etc/hosts\n#EXT-X-ENDLIST\n"u8.ToArray();
        var concat = "ffconcat version 1.0\nfile '/etc/hosts'\n"u8.ToArray();

        foreach (var payload in new[] { playlist, concat })
        {
            var media = await user.UploadProcessedAsync(kind, payload, contentType, "innocent");
            media.Status.ShouldBe(MediaStatus.Failed, $"{kind} built from a {(payload == playlist ? "playlist" : "concat script")}");
        }
    }

    [Fact]
    public async Task Webm_video_is_transcoded_to_faststart_h264_aac_mp4()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var upload = await Ffmpeg.GenerateAsync(".webm",
            "-f", "lavfi", "-i", "testsrc=size=480x270:rate=25", "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000",
            "-t", "1.5", "-c:v", "libvpx", "-b:v", "300k", "-c:a", "libopus", "-shortest");

        var media = await user.UploadReadyAsync(MediaKind.Video, upload, "video/webm", "screen recording.webm");

        media.ContentType.ShouldBe("video/mp4");
        media.FileName.ShouldBe("screen recording.mp4");
        (media.Width, media.Height).ShouldBe((480, 270));
        media.DurationMs.ShouldNotBeNull().ShouldBeInRange(1400, 1700);
        var stored = await user.Client.DownloadAsync(media.Id);
        Mp4.IsFastStart(stored).ShouldBeTrue();
        var probe = await Ffmpeg.ProbeAsync(stored);
        probe.FormatName.ShouldContain("mp4");
        var video = probe.Video.ShouldNotBeNull();
        (video.CodecName, video.PixelFormat).ShouldBe(("h264", "yuv420p"));
        probe.Audio.ShouldNotBeNull().CodecName.ShouldBe("aac");

        await ShouldHavePosterAsync(user, media, 480, 270);
    }

    [Fact]
    public async Task H264_video_larger_than_the_limit_is_transcoded_down_to_even_dimensions()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var max = factory.Processing().VideoMaxDimension;
        var upload = await Ffmpeg.GenerateAsync(".mp4",
            "-f", "lavfi", "-i", $"testsrc=size={max + 80}x100:rate=10", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p");

        var media = await user.UploadReadyAsync(MediaKind.Video, upload, "video/mp4", "wide.mp4");

        media.Width.ShouldBe(max);
        media.Height.ShouldNotBeNull().ShouldBeInRange(94, 98);
        (media.Height.Value % 2).ShouldBe(0);
        var stored = await user.Client.DownloadAsync(media.Id);
        (await Ffmpeg.StreamHashAsync(stored)).ShouldNotBe(await Ffmpeg.StreamHashAsync(upload), "the oversized stream must be re-encoded");
        var probe = await Ffmpeg.ProbeAsync(stored);
        probe.Video.ShouldNotBeNull().Width.ShouldBe(max);
        probe.Audio.ShouldBeNull();
    }

    [Fact]
    public async Task Video_note_becomes_a_centered_square_with_mono_aac()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var size = factory.Processing().VideoNoteSize;
        var upload = await Ffmpeg.GenerateAsync(".mp4",
            "-f", "lavfi", "-i", "testsrc=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000",
            "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", "-shortest");

        var media = await user.UploadReadyAsync(MediaKind.VideoNote, upload, "video/mp4", "note.mp4");

        media.ContentType.ShouldBe("video/mp4");
        (media.Width, media.Height).ShouldBe((size, size));
        media.DurationMs.ShouldNotBeNull().ShouldBeInRange(1900, 2200);
        var stored = await user.Client.DownloadAsync(media.Id);
        Mp4.IsFastStart(stored).ShouldBeTrue();
        var probe = await Ffmpeg.ProbeAsync(stored);
        var video = probe.Video.ShouldNotBeNull();
        (video.CodecName, video.Width, video.Height, video.PixelFormat).ShouldBe(("h264", size, size, "yuv420p"));
        var audio = probe.Audio.ShouldNotBeNull();
        (audio.CodecName, audio.Channels).ShouldBe(("aac", 1));

        await ShouldHavePosterAsync(user, media, Math.Min(size, factory.Processing().ThumbnailMaxDimension), Math.Min(size, factory.Processing().ThumbnailMaxDimension));
    }

    [Fact]
    public async Task Video_note_is_cut_to_sixty_seconds()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var upload = await Ffmpeg.GenerateAsync(".mp4",
            "-f", "lavfi", "-i", "testsrc=size=96x96:rate=2", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=16000",
            "-t", "65", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");

        var media = await user.UploadReadyAsync(MediaKind.VideoNote, upload, "video/mp4", "long note.mp4");

        media.DurationMs.ShouldNotBeNull().ShouldBeInRange(59_000, Limits.VideoNoteMaxDurationSeconds * 1000 + 500);
        var probe = await Ffmpeg.ProbeAsync(await user.Client.DownloadAsync(media.Id));
        probe.DurationSeconds.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(Limits.VideoNoteMaxDurationSeconds + 0.5);
    }

    [Fact]
    public async Task Voice_becomes_aac_lc_mono_48k_m4a_with_a_server_side_waveform()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        // Loudness ramps up from 5 % to 100 %, so the waveform must rise from left to right.
        var wav = TestAudio.Wav(2.0, envelope: p => 0.05 + 0.95 * p);
        var clientWaveform = Enumerable.Repeat((byte)7, Limits.WaveformSamples).ToArray();

        var media = await user.UploadReadyAsync(MediaKind.Voice, wav, "audio/wav", "memo.wav",
            r => r with { DurationMs = 99_000, Waveform = clientWaveform });

        media.ContentType.ShouldBe("audio/mp4");
        media.FileName.ShouldBe("memo.m4a");
        media.DurationMs.ShouldNotBeNull().ShouldBeInRange(1900, 2200, "the server measures the duration");
        var waveform = media.Waveform.ShouldNotBeNull();
        waveform.Length.ShouldBe(Limits.WaveformSamples);
        waveform.ShouldNotBe(clientWaveform, "the server recomputes the waveform");
        waveform.Max().ShouldBe((byte)255);
        waveform[..10].Average(v => (int)v).ShouldBeLessThan(80);
        waveform[^10..].Average(v => (int)v).ShouldBeGreaterThan(200);
        waveform[45..55].Average(v => (int)v).ShouldBeInRange(60, 210);
        media.HasThumbnail.ShouldBeFalse();

        var probe = await Ffmpeg.ProbeAsync(await user.Client.DownloadAsync(media.Id));
        probe.Video.ShouldBeNull();
        var audio = probe.Audio.ShouldNotBeNull();
        (audio.CodecName, audio.Profile, audio.Channels, audio.SampleRate).ShouldBe(("aac", "LC", 1, 48000));
        audio.BitRate.ShouldNotBeNull().ShouldBeInRange(30_000, 70_000);
    }

    [Fact]
    public async Task Voice_recorded_by_a_browser_as_webm_without_a_duration_is_converted_too()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        // MediaRecorder writes "live" WebM: no duration, no cues.
        var webm = await Ffmpeg.GenerateAsync(".webm",
            "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000", "-t", "1.5", "-c:a", "libopus", "-f", "webm", "-live", "1");
        (await Ffmpeg.ProbeAsync(webm)).DurationSeconds.ShouldBeNull("precondition: like a browser recording");

        var media = await user.UploadReadyAsync(MediaKind.Voice, webm, "video/webm", "voice.webm");

        media.ContentType.ShouldBe("audio/mp4");
        media.FileName.ShouldBe("voice.m4a");
        media.DurationMs.ShouldNotBeNull().ShouldBeInRange(1400, 1700);
        media.Waveform.ShouldNotBeNull().Length.ShouldBe(Limits.WaveformSamples);
    }

    [Fact]
    public async Task Video_note_recorded_by_a_browser_without_a_duration_gets_one()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var webm = await Ffmpeg.GenerateAsync(".webm",
            "-f", "lavfi", "-i", "testsrc=size=640x480:rate=30", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000",
            "-t", "2", "-c:v", "libvpx", "-b:v", "300k", "-c:a", "libopus", "-shortest", "-f", "webm", "-live", "1");
        (await Ffmpeg.ProbeAsync(webm)).DurationSeconds.ShouldBeNull("precondition: like a browser recording");

        var media = await user.UploadReadyAsync(MediaKind.VideoNote, webm, "video/webm", "note.webm");

        media.DurationMs.ShouldNotBeNull().ShouldBeInRange(1900, 2200);
        (media.Width, media.Height).ShouldBe((factory.Processing().VideoNoteSize, factory.Processing().VideoNoteSize));
        media.HasThumbnail.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Recording 2026-05-12 10.15.33", "Recording 2026-05-12 10.15.33.m4a")]
    [InlineData("Запись 12.05.wav", "Запись 12.05.m4a")]
    [InlineData("memo.v2.WAV", "memo.v2.m4a")]
    public async Task Converted_voice_keeps_dots_that_are_part_of_its_name(string uploaded, string stored)
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();

        var media = await user.UploadReadyAsync(MediaKind.Voice, TestAudio.Wav(0.5), "audio/wav", uploaded);

        media.FileName.ShouldBe(stored);
    }

    [Fact]
    public async Task Audio_files_keep_their_bytes_and_get_their_duration()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var wav = TestAudio.Wav(1.5, sampleRate: 44100);

        var media = await user.UploadReadyAsync(MediaKind.Audio, wav, "audio/wav", "song.wav");

        media.ContentType.ShouldBe("audio/wav");
        media.FileName.ShouldBe("song.wav");
        media.DurationMs.ShouldNotBeNull().ShouldBeInRange(1450, 1550);
        media.Waveform.ShouldBeNull();
        media.HasThumbnail.ShouldBeFalse();
        (await user.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(wav, "audio file");
    }

    [Fact]
    public async Task Files_are_stored_untouched_even_when_they_are_images_or_videos()
    {
        var user = await factory.RegisterUserAsync();
        // "Send without compression": a large photo sent as a file is never resized or re-encoded.
        var photo = TestImages.Jpeg(factory.Processing().PhotoMaxDimension + 100, 64);
        var random = RandomBytes(10_000, seed: 31);

        var photoFile = await user.UploadReadyAsync(MediaKind.File, photo, "image/jpeg", "original.jpg", r => r with { Width = 100, Height = 100 });
        var binary = await user.UploadReadyAsync(MediaKind.File, random, "application/x-custom", "data.custom");

        foreach (var (media, bytes) in new[] { (photoFile, photo), (binary, random) })
        {
            media.SizeBytes.ShouldBe(bytes.Length);
            media.HasThumbnail.ShouldBeFalse();
            media.MiniThumbnail.ShouldBeNull();
            media.Width.ShouldBeNull();
            media.Height.ShouldBeNull();
            media.DurationMs.ShouldBeNull();
            (await user.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(bytes, media.FileName);
            using var thumbnail = await user.Client.GetThumbnailAsync(media.Id);
            await thumbnail.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        }
        photoFile.ContentType.ShouldBe("image/jpeg");
        photoFile.FileName.ShouldBe("original.jpg");
        binary.ContentType.ShouldBe("application/x-custom");
    }

    [Fact]
    public async Task Video_files_sent_as_files_are_not_remuxed()
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();
        var video = await Ffmpeg.GenerateAsync(".mp4", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p");

        var media = await user.UploadReadyAsync(MediaKind.File, video, "video/mp4", "raw.mp4");

        (await user.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(video, "video sent as a file");
        media.HasThumbnail.ShouldBeFalse();
    }

    [Theory]
    [InlineData(MediaKind.Video, "video/mp4")]
    [InlineData(MediaKind.VideoNote, "video/webm")]
    [InlineData(MediaKind.Voice, "audio/ogg")]
    public async Task Undecodable_audio_and_video_fail_processing(MediaKind kind, string contentType)
    {
        Ffmpeg.Require();
        var user = await factory.RegisterUserAsync();

        var media = await user.UploadProcessedAsync(kind, RandomBytes(20_000, seed: 41), contentType, "broken");

        media.Status.ShouldBe(MediaStatus.Failed);
        using var content = await user.Client.GetContentAsync(media.Id);
        await content.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.MediaNotReady);
    }

    [Fact]
    public async Task Audio_that_cannot_be_probed_is_still_delivered_as_uploaded()
    {
        var user = await factory.RegisterUserAsync();
        var bytes = RandomBytes(5000, seed: 42);

        var media = await user.UploadReadyAsync(MediaKind.Audio, bytes, "audio/mpeg", "mystery.mp3");

        media.DurationMs.ShouldBeNull();
        (await user.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(bytes, "unprobeable audio");
    }

    /// <summary>Adds a 90° display rotation (as phones do for portrait recordings) without re-encoding.</summary>
    private static async Task<byte[]> RotateAsync(byte[] mp4)
    {
        var directory = Path.Combine(Path.GetTempPath(), "skymessage-media-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "landscape.mp4");
            await File.WriteAllBytesAsync(source, mp4, Ct);
            return await Ffmpeg.GenerateAsync(".mp4", "-display_rotation", "90", "-i", source, "-c", "copy");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ShouldHavePosterAsync(TestUser user, MediaDto media, int width, int height)
    {
        media.HasThumbnail.ShouldBeTrue("videos get a poster frame");
        using (var poster = TestImages.Decode(await user.Client.DownloadThumbnailAsync(media.Id)))
            (poster.Width, poster.Height).ShouldBe((width, height));
        using var mini = TestImages.Decode(media.MiniThumbnail.ShouldNotBeNull());
        Math.Max(mini.Width, mini.Height).ShouldBe(24);
    }
}
