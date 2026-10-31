using System.Buffers.Binary;
using SkyMessage.Contracts;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>
/// Peak waveform of a voice message from mono signed 16-bit PCM: absolute peaks per fixed block of samples
/// (constant memory per second of audio, independent of the final resolution), folded into N buckets and scaled
/// to 0..255 relative to the loudest bucket. Quiet recordings are not blown up to full scale: the scale never
/// goes below <see cref="NormalizationFloor"/>.
/// </summary>
internal sealed class WaveformBuilder
{
    /// <summary>10 ms at the 8 kHz rate the PCM is decoded at.</summary>
    public const int BlockSamples = 80;

    /// <summary>≈ −30 dBFS.</summary>
    public const int NormalizationFloor = 1024;

    private readonly List<int> _blockPeaks = [];
    private int _currentPeak;
    private int _currentCount;
    private int _pendingLowByte = -1;

    public long SampleCount { get; private set; }

    /// <summary>Little-endian s16 bytes; a sample may be split across calls.</summary>
    public void AddPcm16(ReadOnlySpan<byte> data)
    {
        var i = 0;
        if (_pendingLowByte >= 0 && data.Length > 0)
        {
            AddSample((short)(_pendingLowByte | (data[0] << 8)));
            _pendingLowByte = -1;
            i = 1;
        }
        for (; i + 1 < data.Length; i += 2)
            AddSample(BinaryPrimitives.ReadInt16LittleEndian(data[i..]));
        if (i < data.Length) _pendingLowByte = data[i];
    }

    public void AddSamples(ReadOnlySpan<short> samples)
    {
        foreach (var sample in samples) AddSample(sample);
    }

    public byte[] Build(int buckets = Limits.WaveformSamples)
    {
        if (_currentCount > 0) FlushBlock();
        return Bucket(_blockPeaks, buckets);
    }

    /// <summary>Folds block peaks into <paramref name="buckets"/> maxima (stretching when there are fewer blocks) and scales them to bytes.</summary>
    internal static byte[] Bucket(IReadOnlyList<int> blockPeaks, int buckets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);
        var result = new byte[buckets];
        if (blockPeaks.Count == 0) return result;

        var peaks = new int[buckets];
        var loudest = 0;
        for (var b = 0; b < buckets; b++)
        {
            var start = (int)((long)b * blockPeaks.Count / buckets);
            var end = Math.Max(start + 1, (int)((long)(b + 1) * blockPeaks.Count / buckets));
            var peak = 0;
            for (var i = start; i < end && i < blockPeaks.Count; i++) peak = Math.Max(peak, blockPeaks[i]);
            peaks[b] = peak;
            loudest = Math.Max(loudest, peak);
        }

        var scale = 255.0 / Math.Max(loudest, NormalizationFloor);
        for (var b = 0; b < buckets; b++)
            result[b] = (byte)Math.Clamp((int)Math.Round(peaks[b] * scale), 0, 255);
        return result;
    }

    private void AddSample(short sample)
    {
        var amplitude = Math.Abs((int)sample);
        if (amplitude > _currentPeak) _currentPeak = amplitude;
        SampleCount++;
        if (++_currentCount == BlockSamples) FlushBlock();
    }

    private void FlushBlock()
    {
        _blockPeaks.Add(_currentPeak);
        _currentPeak = 0;
        _currentCount = 0;
    }
}
