using System.Buffers.Binary;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using Xunit;

namespace SkyMessage.UnitTests.Media;

public sealed class WaveformBuilderTests
{
    [Fact]
    public void Silence_is_flat_zero()
    {
        var builder = new WaveformBuilder();
        builder.AddSamples(new short[8000]);

        var waveform = builder.Build();

        waveform.Length.ShouldBe(Limits.WaveformSamples);
        waveform.ShouldAllBe(v => v == 0);
    }

    [Fact]
    public void No_audio_gives_an_empty_waveform_of_the_right_length()
    {
        new WaveformBuilder().Build().ShouldBe(new byte[Limits.WaveformSamples]);
    }

    [Fact]
    public void Loudest_bucket_is_full_scale_and_others_are_relative()
    {
        // First half at amplitude 8000, second half at 16000.
        var samples = new short[16_000];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)((i % 2 == 0 ? 1 : -1) * (i < 8_000 ? 8_000 : 16_000));
        var builder = new WaveformBuilder();
        builder.AddSamples(samples);

        var waveform = builder.Build();

        waveform[..50].ShouldAllBe(v => v == 128);
        waveform[50..].ShouldAllBe(v => v == 255);
    }

    [Fact]
    public void Peaks_are_kept_inside_their_bucket()
    {
        var samples = new short[100 * WaveformBuilder.BlockSamples];
        samples[37 * WaveformBuilder.BlockSamples + 5] = short.MinValue; // |−32768| must not overflow
        var builder = new WaveformBuilder();
        builder.AddSamples(samples);

        var waveform = builder.Build();

        waveform[37].ShouldBe((byte)255);
        waveform.Where((_, i) => i != 37).ShouldAllBe(v => v == 0);
    }

    [Fact]
    public void Quiet_recordings_are_not_amplified_to_full_scale()
    {
        var samples = Enumerable.Repeat((short)100, 8_000).ToArray();
        var builder = new WaveformBuilder();
        builder.AddSamples(samples);

        var waveform = builder.Build();

        waveform.ShouldAllBe(v => v == (byte)Math.Round(100 * 255.0 / WaveformBuilder.NormalizationFloor));
    }

    [Fact]
    public void Short_recordings_are_stretched_over_all_buckets()
    {
        var builder = new WaveformBuilder();
        // 3 blocks only: loud, silent, loud.
        builder.AddSamples(Enumerable.Repeat((short)20_000, WaveformBuilder.BlockSamples).ToArray());
        builder.AddSamples(new short[WaveformBuilder.BlockSamples]);
        builder.AddSamples(Enumerable.Repeat((short)20_000, WaveformBuilder.BlockSamples).ToArray());

        var waveform = builder.Build();

        waveform.Length.ShouldBe(100);
        waveform[0].ShouldBe((byte)255);
        waveform[50].ShouldBe((byte)0);
        waveform[99].ShouldBe((byte)255);
    }

    [Fact]
    public void Pcm_bytes_split_mid_sample_give_the_same_result()
    {
        var random = new Random(42);
        var samples = Enumerable.Range(0, 12_345).Select(_ => (short)random.Next(short.MinValue, short.MaxValue)).ToArray();
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);

        var whole = new WaveformBuilder();
        whole.AddSamples(samples);
        var split = new WaveformBuilder();
        var offset = 0;
        foreach (var size in new[] { 1, 2, 3, 1001, 7, 9_999 })
        {
            var take = Math.Min(size, bytes.Length - offset);
            split.AddPcm16(bytes.AsSpan(offset, take));
            offset += take;
        }
        split.AddPcm16(bytes.AsSpan(offset));

        split.SampleCount.ShouldBe(samples.Length);
        split.Build().ShouldBe(whole.Build());
    }

    [Fact]
    public void Bucketing_takes_the_maximum_of_each_range()
    {
        var peaks = Enumerable.Range(0, 1000).Select(i => i % 10 == 9 ? 2000 : 10).ToList();

        var waveform = WaveformBuilder.Bucket(peaks, 100);

        waveform.ShouldAllBe(v => v == 255);
    }
}
