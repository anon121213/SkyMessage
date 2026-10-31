using System.Buffers.Binary;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>
/// The user's own sounds before they are decoded: the length their headers tell, and the order of the checks (a decoder
/// turns a whole file into raw samples, so a long file must be refused before it gets there).
/// </summary>
public sealed class CustomSoundImportTests
{
    private static readonly string Fixtures = Path.Combine(FindRepo(), "tests", "SkyMessage.Client.Core.Tests", "Fixtures", "Audio");

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkyMessage.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run inside the repository.");
    }

    /// <summary>One-second (WAV: half-second) 880 Hz tones written by ffmpeg.</summary>
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures, name));

    private static byte[] Repeat(byte[] bytes, int times) => Enumerable.Repeat(bytes, times).SelectMany(b => b).ToArray();

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    // ---- What the headers say -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("tone-pcm16.wav", AudioContainer.Wave, "pcm", 0.5)]
    [InlineData("tone-vbr.mp3", AudioContainer.Mp3, "mp3", 1.07)]      // LAME VBR with a Xing header
    [InlineData("tone-cbr.mp3", AudioContainer.Mp3, "mp3", 1.152)]     // MPEG-2.5 at 8 kbps, no header: the bitrate tells
    [InlineData("tone-id3.mp3", AudioContainer.Mp3, "mp3", 1.045)]     // behind an ID3v2 tag
    [InlineData("tone.m4a", AudioContainer.Mp4, null, 1.0)]
    [InlineData("tone.aac", AudioContainer.Adts, "aac", 1.068)]
    [InlineData("tone-vorbis.ogg", AudioContainer.Ogg, "vorbis", 1.0)]
    [InlineData("tone.opus", AudioContainer.Ogg, "opus", 1.0)]
    public void The_headers_of_every_accepted_container_tell_its_length(string file, AudioContainer container, string? codec, double seconds)
    {
        var info = AudioFileProbe.Probe(Fixture(file));

        info.Container.ShouldBe(container);
        info.Codec.ShouldBe(codec);
        info.Seconds.ShouldNotBeNull();
        info.Seconds.Value.ShouldBe(seconds, 0.03);
        CustomSoundRules.CheckHeaders(CustomSoundSlot.Message, info).ShouldBe(CustomSoundProblem.None);
    }

    [Fact]
    public void An_hour_of_low_bitrate_mp3_is_known_to_be_long_without_decoding()
    {
        // 3.6 MB of 8 kbps frames, under the 4 MB size limit: the case that used to be decoded to ~700 MB of samples.
        var hour = Repeat(Fixture("tone-cbr.mp3"), 3125);
        hour.Length.ShouldBeLessThan((int)CustomSoundRules.MaxBytes);

        var info = AudioFileProbe.Probe(hour);

        info.Seconds!.Value.ShouldBe(3600, 1);
        CustomSoundRules.CheckHeaders(CustomSoundSlot.Ringtone, info).ShouldBe(CustomSoundProblem.TooLong);
    }

    [Fact]
    public void Files_joined_end_to_end_count_all_their_parts()
    {
        // The first part's own header (Xing frame count, Ogg stream) describes only that part.
        AudioFileProbe.Probe(Repeat(Fixture("tone-vbr.mp3"), 60)).Seconds!.Value.ShouldBe(64, 4);
        AudioFileProbe.Probe(Repeat(Fixture("tone-vorbis.ogg"), 30)).Seconds!.Value.ShouldBe(30, 2);
        AudioFileProbe.Probe(Repeat(Fixture("tone.aac"), 30)).Seconds!.Value.ShouldBe(32, 1);
    }

    [Fact]
    public void A_long_m4a_or_wav_says_so_in_its_header()
    {
        var m4a = Fixture("tone.m4a");
        var mvhd = m4a.AsSpan().IndexOf("mvhd"u8) + 4;
        m4a[mvhd].ShouldBe((byte)0, "version 0: 32-bit times");
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(m4a.AsSpan(mvhd + 12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(m4a.AsSpan(mvhd + 16, 4), timescale * 3960);
        AudioFileProbe.Probe(m4a).Seconds.ShouldBe(3960);

        // 4 MB of 8 kHz 16-bit mono: about four minutes.
        var wav = Fixture("tone-pcm16.wav").Concat(new byte[4_000_000]).ToArray();
        var data = wav.AsSpan().IndexOf("data"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(data + 4, 4), (uint)(wav.Length - data - 8));
        AudioFileProbe.Probe(wav).Seconds!.Value.ShouldBe(250.5, 0.1);
        CustomSoundRules.CheckHeaders(CustomSoundSlot.Ringtone, AudioFileProbe.Probe(wav)).ShouldBe(CustomSoundProblem.TooLong);
    }

    [Fact]
    public void A_wav_that_claims_more_than_it_holds_is_as_long_as_what_is_there()
    {
        var wav = Fixture("tone-pcm16.wav");
        var data = wav.AsSpan().IndexOf("data"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(data + 4, 4), 0x7FFF_FFFF);
        AudioFileProbe.Probe(wav).Seconds!.Value.ShouldBe(0.5, 0.01);
    }

    [Theory]
    [InlineData("random")]
    [InlineData("text")]
    [InlineData("flac")]
    [InlineData("cut")]
    public void Anything_else_is_refused_as_the_wrong_format_or_unreadable(string kind)
    {
        var bytes = kind switch
        {
            "random" => RandomBytes(50_000),
            "text" => "ID3 is not an audio file, just some text that ends up named .mp3"u8.ToArray(),
            "flac" => [.. "fLaC"u8, .. new byte[4000]],
            _ => Fixture("tone.m4a")[..40],   // an MP4 cut before its movie header
        };

        var info = AudioFileProbe.Probe(bytes);

        CustomSoundRules.CheckHeaders(CustomSoundSlot.Message, info).ShouldBeOneOf(CustomSoundProblem.Format, CustomSoundProblem.Unreadable);
        info.Seconds.ShouldBeNull();
    }

    [Theory]
    [InlineData(CustomSoundSlot.Message, 13.4, CustomSoundProblem.None)]          // decoded, then measured exactly
    [InlineData(CustomSoundSlot.Message, 13.6, CustomSoundProblem.TooLong)]       // 10 s + a quarter + a second
    [InlineData(CustomSoundSlot.Ringtone, 75.9, CustomSoundProblem.None)]
    [InlineData(CustomSoundSlot.Ringtone, 76.1, CustomSoundProblem.TooLong)]
    [InlineData(CustomSoundSlot.Ringtone, null, CustomSoundProblem.Unreadable)]   // the headers do not say
    public void The_headers_may_be_a_little_over_the_limit_before_the_exact_check(CustomSoundSlot slot, double? seconds, CustomSoundProblem expected) =>
        CustomSoundRules.CheckHeaders(slot, new AudioFileInfo(AudioContainer.Mp3, "mp3", seconds)).ShouldBe(expected);

    // ---- The import --------------------------------------------------------------------------------------------------

    private sealed class Harness
    {
        public ICustomSoundStore Store { get; } = Substitute.For<ICustomSoundStore>();
        public IAudioDecoder Decoder { get; } = Substitute.For<IAudioDecoder>();
        public SettingsService Settings { get; }
        public CustomSoundService Service { get; }

        public Harness(string fileName, byte[] bytes, bool systemPlays = true, double? decodedSeconds = 1.0)
        {
            var prefs = new InMemorySecureStore();
            Settings = new SettingsService(prefs, prefs, Substitute.For<IPlatformInfo>(), Log.For<SettingsService>());
            Store.IsSupported.Returns(true);
            Store.PickAsync(Arg.Any<CancellationToken>()).Returns(new PickedFile
            {
                FileName = fileName,
                ContentType = "",
                Size = bytes.Length,
                OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(bytes)),
            });
            Store.IsSystemPlayableAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(systemPlays);
            Decoder.MeasureAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(decodedSeconds);
            Service = new CustomSoundService(Store, Decoder, Settings, Log.For<CustomSoundService>());
        }

        public async Task NothingWasKeptAsync()
        {
            await Store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
            Settings.Current.Notifications.CustomRingtone.ShouldBeNull();
            Settings.Current.Notifications.CustomMessageTone.ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_long_file_is_refused_before_it_is_decoded()
    {
        var h = new Harness("audiobook.mp3", Repeat(Fixture("tone-cbr.mp3"), 3125));

        (await h.Service.ImportAsync(CustomSoundSlot.Ringtone)).ShouldBe(CustomSoundProblem.TooLong);

        await h.Decoder.DidNotReceiveWithAnyArgs().MeasureAsync(default!, default);
        await h.Store.DidNotReceiveWithAnyArgs().IsSystemPlayableAsync(default!, default!, default);
        await h.NothingWasKeptAsync();
    }

    [Fact]
    public async Task A_file_whose_bytes_are_not_audio_is_refused_before_it_is_decoded()
    {
        var h = new Harness("notes.mp3", "just some text"u8.ToArray());

        (await h.Service.ImportAsync(CustomSoundSlot.Message)).ShouldBe(CustomSoundProblem.Format);

        await h.Decoder.DidNotReceiveWithAnyArgs().MeasureAsync(default!, default);
        await h.NothingWasKeptAsync();
    }

    [Fact]
    public async Task A_file_the_system_player_cannot_ring_with_is_refused()
    {
        // E.g. Ogg Opus on an Android version whose media framework does not open it, though the WebView decodes it.
        var h = new Harness("voice.opus", Fixture("tone.opus"), systemPlays: false);

        (await h.Service.ImportAsync(CustomSoundSlot.Message)).ShouldBe(CustomSoundProblem.SystemCannotPlay);

        await h.Store.Received(1).IsSystemPlayableAsync(Arg.Any<byte[]>(), ".opus", Arg.Any<CancellationToken>());
        await h.Decoder.DidNotReceiveWithAnyArgs().MeasureAsync(default!, default);
        await h.NothingWasKeptAsync();
    }

    [Fact]
    public async Task A_good_file_is_decoded_kept_and_selected_and_replaces_the_previous_one()
    {
        var h = new Harness("Звонок.m4a", Fixture("tone.m4a"), decodedSeconds: 1.0);
        (await h.Service.ImportAsync(CustomSoundSlot.Ringtone)).ShouldBe(CustomSoundProblem.None);
        var first = h.Settings.Current.Notifications.CustomRingtone!;

        (await h.Service.ImportAsync(CustomSoundSlot.Ringtone)).ShouldBe(CustomSoundProblem.None);

        var n = h.Settings.Current.Notifications;
        n.Ringtone.ShouldBe(SoundCatalog.Custom);
        n.CustomRingtone!.Name.ShouldBe("Звонок");
        n.CustomRingtone.Extension.ShouldBe(".m4a");
        n.CustomRingtone.DurationMs.ShouldBe(1000);
        n.CustomRingtone.Id.ShouldNotBe(first.Id);
        await h.Decoder.Received(2).MeasureAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await h.Store.Received(1).DeleteAsync(Arg.Is<CustomSound>(s => s.Id == first.Id));
    }

    [Fact]
    public async Task A_file_the_headers_pass_is_still_measured_exactly_after_decoding()
    {
        // Half a second by its headers; the decoder finds 13 s (the headers were wrong): the decoded length decides.
        var h = new Harness("long.wav", Fixture("tone-pcm16.wav"), decodedSeconds: 13);

        (await h.Service.ImportAsync(CustomSoundSlot.Message)).ShouldBe(CustomSoundProblem.TooLong);
        await h.NothingWasKeptAsync();
    }
}
