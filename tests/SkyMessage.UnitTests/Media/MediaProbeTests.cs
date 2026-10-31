using Shouldly;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using Xunit;

namespace SkyMessage.UnitTests.Media;

public sealed class MediaProbeTests
{
    [Fact]
    public void Parses_ffprobe_json_with_display_matrix_rotation()
    {
        const string json = """
            {
              "streams": [
                { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1920, "height": 1080, "pix_fmt": "yuv420p",
                  "duration": "12.500000", "disposition": { "default": 1, "attached_pic": 0 },
                  "side_data_list": [ { "side_data_type": "Display Matrix", "displaymatrix": "...", "rotation": -90 } ] },
                { "index": 1, "codec_name": "aac", "codec_type": "audio", "sample_rate": "48000", "channels": 2, "duration": "12.480000" }
              ],
              "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "12.512000", "bit_rate": "4000000" }
            }
            """;

        var probe = MediaProbe.Parse(json);

        probe.IsMp4Family.ShouldBeTrue();
        probe.DurationMs.ShouldBe(12512);
        var video = probe.Video.ShouldNotBeNull();
        video.CodecName.ShouldBe("h264");
        video.PixelFormat.ShouldBe("yuv420p");
        video.Rotation.ShouldBe(270);
        video.DisplayWidth.ShouldBe(1080);
        video.DisplayHeight.ShouldBe(1920);
        var audio = probe.Audio.ShouldNotBeNull();
        audio.SampleRate.ShouldBe(48000);
        audio.Channels.ShouldBe(2);
    }

    [Fact]
    public void Reads_legacy_rotate_tags_and_skips_cover_art()
    {
        const string json = """
            {
              "streams": [
                { "index": 0, "codec_name": "mjpeg", "codec_type": "video", "width": 600, "height": 600,
                  "disposition": { "attached_pic": 1 } },
                { "index": 1, "codec_name": "mp3", "codec_type": "audio", "sample_rate": "44100", "channels": 2 },
                { "index": 2, "codec_name": "h264", "codec_type": "video", "width": 640, "height": 480, "tags": { "rotate": "90" } }
              ],
              "format": { "format_name": "matroska,webm", "duration": "N/A" }
            }
            """;

        var probe = MediaProbe.Parse(json);

        probe.IsMp4Family.ShouldBeFalse();
        probe.DurationSeconds.ShouldBeNull();
        probe.DurationMs.ShouldBeNull();
        var video = probe.Video.ShouldNotBeNull();
        video.Index.ShouldBe(2);
        video.Rotation.ShouldBe(90);
        video.DisplayWidth.ShouldBe(480);
    }

    [Fact]
    public void Falls_back_to_stream_durations()
    {
        const string json = """
            { "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "opus", "duration": "3.25" } ], "format": { "format_name": "ogg" } }
            """;

        MediaProbe.Parse(json).DurationMs.ShouldBe(3250);
    }

    [Fact]
    public void Tolerates_missing_sections()
    {
        var probe = MediaProbe.Parse("{}");

        probe.FormatName.ShouldBe("");
        probe.Streams.ShouldBeEmpty();
        probe.Video.ShouldBeNull();
        probe.Audio.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(-90, 270)]
    [InlineData(180, 180)]
    [InlineData(-180, 180)]
    [InlineData(270, 270)]
    [InlineData(450, 90)]
    [InlineData(89.9, 90)]
    public void Rotation_is_normalized_to_quarter_turns(double degrees, int expected) =>
        MediaProbe.NormalizeRotation(degrees).ShouldBe(expected);
}
