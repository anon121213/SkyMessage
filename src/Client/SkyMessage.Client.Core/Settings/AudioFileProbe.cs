using System.Buffers.Binary;

namespace SkyMessage.Client.Core.Settings;

/// <summary>The container an audio file's bytes are in (whatever its name says).</summary>
public enum AudioContainer
{
    Unknown,
    Wave,
    Mp3,
    /// <summary>MPEG-4 / ISO base media (.m4a, .mp4, .3gp).</summary>
    Mp4,
    Ogg,
    /// <summary>Raw AAC with ADTS frame headers (.aac).</summary>
    Adts,
}

/// <summary>What a file's headers say. <see cref="Seconds"/> is null when they do not tell the length.</summary>
public sealed record AudioFileInfo(AudioContainer Container, string? Codec, double? Seconds)
{
    public static readonly AudioFileInfo Unknown = new(AudioContainer.Unknown, null, null);
}

/// <summary>
/// Reads the length of an audio file from its headers, without decoding it: the player decodes a whole file into raw
/// samples at once, so a long file must be refused before that (<see cref="CustomSoundRules.CheckHeaders"/>). Knows
/// the containers custom sounds may come in: WAV (data size), MP4 (movie header), Ogg Vorbis/Opus (granule position of
/// the last page), MP3 (Xing/Info or VBRI frame count, else the constant bitrate) and ADTS (frame count). The format is
/// told by the bytes, not the name. Never throws; whatever it cannot read is unknown.
/// </summary>
public static class AudioFileProbe
{
    public static AudioFileInfo Probe(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WAVE"u8)) return Wave(data);
            if (data.Length >= 27 && data[..4].SequenceEqual("OggS"u8)) return Ogg(data);
            if (data.Length >= 8 && data.Slice(4, 4).SequenceEqual("ftyp"u8)) return Mp4(data);
            return MpegAudio(data, Id3Length(data));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // A header that claims more than the file holds.
            return AudioFileInfo.Unknown;
        }
    }

    // ---- WAV -------------------------------------------------------------------------------------------------------

    private static AudioFileInfo Wave(ReadOnlySpan<byte> d)
    {
        long pos = 12;
        string? codec = null;
        long bytesPerSecond = 0;
        while (pos + 8 <= d.Length)
        {
            var id = d.Slice((int)pos, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice((int)pos + 4, 4));
            var body = (int)pos + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= d.Length)
            {
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(body, 2));
                var rate = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(body + 4, 4));
                var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(body + 8, 4));
                var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(body + 12, 2));
                codec = tag switch { 1 => "pcm", 3 => "float", 6 => "alaw", 7 => "mulaw", 0xFFFE => "extensible", _ => $"0x{tag:x4}" };
                // PCM-like formats: what decoders go by (frames of blockAlign bytes at the sample rate); else the byte rate.
                bytesPerSecond = tag is 1 or 3 or 6 or 7 or 0xFFFE && blockAlign > 0 ? (long)rate * blockAlign : byteRate;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (bytesPerSecond <= 0) return new AudioFileInfo(AudioContainer.Wave, codec, null);
                // A streamed or truncated file states no size, or more than it holds: the decoder takes what is there.
                long available = d.Length - body;
                var bytes = size == 0 || size == uint.MaxValue || size > available ? available : size;
                return new AudioFileInfo(AudioContainer.Wave, codec, bytes / (double)bytesPerSecond);
            }
            pos = body + size + (size & 1);
        }
        return new AudioFileInfo(AudioContainer.Wave, codec, null);
    }

    // ---- Ogg -------------------------------------------------------------------------------------------------------

    private static AudioFileInfo Ogg(ReadOnlySpan<byte> d)
    {
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(14, 4));
        var packet = d[(27 + d[26])..];
        string codec;
        double rate;
        long preSkip = 0;
        if (packet.Length >= 16 && packet[0] == 1 && packet.Slice(1, 6).SequenceEqual("vorbis"u8))
        {
            codec = "vorbis";
            rate = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12, 4));
        }
        else if (packet.Length >= 19 && packet[..8].SequenceEqual("OpusHead"u8))
        {
            // Opus granules count 48 kHz samples whatever the input rate was, after a pre-skip of decoder warm-up.
            codec = "opus";
            rate = 48000;
            preSkip = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2));
        }
        else
        {
            return new AudioFileInfo(AudioContainer.Ogg, null, null);
        }
        if (rate <= 0) return new AudioFileInfo(AudioContainer.Ogg, codec, null);

        // Walk the pages: the last granule position of this stream is the position of its last sample. A stream that
        // begins after the data has started is a file joined on the end (a chained Ogg), and a damaged page ends the
        // walk early; whatever is left unread is assumed to sound at the rate of what was read.
        long pos = 0, granule = -1;
        var data = false;
        while (pos + 27 <= d.Length && d.Slice((int)pos, 4).SequenceEqual("OggS"u8))
        {
            var page = (int)pos;
            var segments = d[page + 26];
            if (page + 27 + segments > d.Length) break;
            var beginsStream = (d[page + 5] & 2) != 0;
            if (beginsStream && data) break;
            data |= !beginsStream;
            if (BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(page + 14, 4)) == serial)
            {
                var position = BinaryPrimitives.ReadInt64LittleEndian(d.Slice(page + 6, 8));
                if (position >= 0) granule = position;
            }
            long body = 0;
            for (var s = 0; s < segments; s++) body += d[page + 27 + s];
            pos += 27 + segments + body;
        }
        if (granule < 0) return new AudioFileInfo(AudioContainer.Ogg, codec, null);
        var seconds = Math.Max(0, granule - preSkip) / rate;
        if (pos < d.Length) seconds *= d.Length / (double)pos;
        return new AudioFileInfo(AudioContainer.Ogg, codec, seconds);
    }

    // ---- MP4 -------------------------------------------------------------------------------------------------------

    private static AudioFileInfo Mp4(ReadOnlySpan<byte> d)
    {
        if (Box(d, 0, d.Length, "moov"u8) is not (var moov, var moovEnd) || Box(d, moov, moovEnd, "mvhd"u8) is not (var mvhd, _))
            return new AudioFileInfo(AudioContainer.Mp4, null, null);
        // Movie header: version 0 has 32-bit times, version 1 64-bit.
        var v1 = d[mvhd] == 1;
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(mvhd + (v1 ? 20 : 12), 4));
        var duration = v1 ? BinaryPrimitives.ReadUInt64BigEndian(d.Slice(mvhd + 24, 8)) : BinaryPrimitives.ReadUInt32BigEndian(d.Slice(mvhd + 16, 4));
        var unknown = v1 ? ulong.MaxValue : uint.MaxValue;
        if ((duration == 0 || duration == unknown) && Box(d, moov, moovEnd, "mvex"u8) is (var mvex, var mvexEnd)
            && Box(d, mvex, mvexEnd, "mehd"u8) is (var mehd, _))
        {
            // A fragmented file: the movie header has no duration, the movie-extends header has the whole one.
            duration = d[mehd] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(d.Slice(mehd + 4, 8)) : BinaryPrimitives.ReadUInt32BigEndian(d.Slice(mehd + 4, 4));
        }
        return timescale == 0 || duration == 0 || duration == unknown
            ? new AudioFileInfo(AudioContainer.Mp4, null, null)
            : new AudioFileInfo(AudioContainer.Mp4, null, duration / (double)timescale);
    }

    /// <summary>The body (start, end) of the first box of <paramref name="type"/> among the boxes in [start, end).</summary>
    private static (int Start, int End)? Box(ReadOnlySpan<byte> d, int start, int end, ReadOnlySpan<byte> type)
    {
        long pos = start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)pos, 4));
            var header = 8;
            if (size == 1)
            {
                if (pos + 16 > end) return null;
                size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(d.Slice((int)pos + 8, 8)), long.MaxValue);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < header) return null;
            var boxEnd = Math.Min(pos + size, end);
            if (d.Slice((int)pos + 4, 4).SequenceEqual(type)) return ((int)pos + header, (int)boxEnd);
            pos += size;
        }
        return null;
    }

    // ---- MP3 and ADTS ----------------------------------------------------------------------------------------------

    /// <summary>Length of the ID3v2 tags at the start (MP3 and AAC files often carry them).</summary>
    private static int Id3Length(ReadOnlySpan<byte> d)
    {
        var pos = 0;
        while (pos + 10 <= d.Length && d.Slice(pos, 3).SequenceEqual("ID3"u8))
        {
            // Sizes are "syncsafe": 7 bits per byte.
            var size = (d[pos + 6] & 0x7F) << 21 | (d[pos + 7] & 0x7F) << 14 | (d[pos + 8] & 0x7F) << 7 | d[pos + 9] & 0x7F;
            var footer = (d[pos + 5] & 0x10) != 0 ? 10 : 0;
            pos += 10 + size + footer;
        }
        return Math.Min(pos, d.Length);
    }

    private static readonly int[][] Bitrates =
    [
        [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],  // MPEG-1 layer I
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],     // MPEG-1 layer II
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],      // MPEG-1 layer III
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],     // MPEG-2/2.5 layer I
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],          // MPEG-2/2.5 layers II and III
    ];

    /// <summary>Sample rates by version (MPEG-2.5, reserved, MPEG-2, MPEG-1) and rate index.</summary>
    private static readonly int[][] Mp3Rates = [[11025, 12000, 8000], [], [22050, 24000, 16000], [44100, 48000, 32000]];

    private static readonly int[] AdtsRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>Real MP3 and AAC files start right after their ID3 tags; a first frame further in is a chance pattern.</summary>
    private const int SyncWindow = 64 * 1024;

    private readonly record struct MpegFrame(int Version, int Layer, int SampleRate, int Bitrate, int Length, int SamplesPerFrame, bool Mono);

    /// <summary>An MPEG audio frame header (version 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5; layer 1-3) at <paramref name="i"/>.</summary>
    private static MpegFrame? Mp3Frame(ReadOnlySpan<byte> d, int i)
    {
        if (i + 4 > d.Length || d[i] != 0xFF || (d[i + 1] & 0xE0) != 0xE0) return null;
        var version = d[i + 1] >> 3 & 3;
        var layer = 4 - (d[i + 1] >> 1 & 3);
        var bitrateIndex = d[i + 2] >> 4;
        var rateIndex = d[i + 2] >> 2 & 3;
        if (version == 1 || layer == 4 || bitrateIndex is 0 or 15 || rateIndex == 3) return null;
        var rate = Mp3Rates[version][rateIndex];
        var table = version == 3 ? layer - 1 : layer == 1 ? 3 : 4;
        var bitrate = Bitrates[table][bitrateIndex] * 1000;
        var padding = d[i + 2] >> 1 & 1;
        var samples = layer == 1 ? 384 : layer == 3 && version != 3 ? 576 : 1152;
        var length = layer == 1 ? (12 * bitrate / rate + padding) * 4 : samples / 8 * bitrate / rate + padding;
        return new MpegFrame(version, layer, rate, bitrate, length, samples, d[i + 3] >> 6 == 3);
    }

    private static bool IsAdts(ReadOnlySpan<byte> d, int i) => i + 7 <= d.Length && d[i] == 0xFF && (d[i + 1] & 0xF6) == 0xF0;

    private static AudioFileInfo MpegAudio(ReadOnlySpan<byte> d, int start)
    {
        // The first frame header that two more follow, each where the one before ends (a sync pattern alone also
        // turns up by chance in tags and junk).
        for (var i = start; i + 4 <= d.Length && i < start + SyncWindow; i++)
        {
            if (d[i] != 0xFF || (d[i + 1] & 0xE0) != 0xE0) continue;
            if (IsAdts(d, i))
            {
                if (Adts(d, i) is { } adts) return adts;
                continue;
            }
            if (Mp3Frame(d, i) is { } frame && Follows(d, i, frame)) return Mp3(d, i, frame);
        }
        return AudioFileInfo.Unknown;
    }

    private static bool Follows(ReadOnlySpan<byte> d, int i, MpegFrame first)
    {
        var end = d.Length >= 128 && d.Slice(d.Length - 128, 3).SequenceEqual("TAG"u8) ? d.Length - 128 : d.Length;
        var pos = i + first.Length;
        for (var n = 0; n < 2; n++)
        {
            if (pos == end) return true;   // a file of one or two frames
            if (Mp3Frame(d, pos) is not { } next || next.Version != first.Version || next.Layer != first.Layer || next.SampleRate != first.SampleRate)
                return false;
            pos += next.Length;
        }
        return true;
    }

    private static AudioFileInfo Mp3(ReadOnlySpan<byte> d, int i, MpegFrame frame)
    {
        // A VBR file's first frame holds a Xing/Info header (after the side info) or a VBRI header (at 32 bytes) with
        // the frame count; without one the bitrate is constant.
        var xing = i + 4 + (frame.Version == 3 ? frame.Mono ? 17 : 32 : frame.Mono ? 9 : 17);
        var end = d.Length >= 128 && d.Slice(d.Length - 128, 3).SequenceEqual("TAG"u8) ? d.Length - 128 : d.Length;
        long audioBytes = Math.Max(0, end - i);
        uint frames = 0, bytes = 0;
        if (xing + 12 <= d.Length && (d.Slice(xing, 4).SequenceEqual("Xing"u8) || d.Slice(xing, 4).SequenceEqual("Info"u8)))
        {
            var flags = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(xing + 4, 4));
            var field = xing + 8;
            if ((flags & 1) != 0 && field + 4 <= d.Length)
            {
                frames = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(field, 4));
                field += 4;
            }
            if ((flags & 2) != 0 && field + 4 <= d.Length) bytes = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(field, 4));
        }
        else if (i + 36 + 18 <= d.Length && d.Slice(i + 36, 4).SequenceEqual("VBRI"u8))
        {
            bytes = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i + 36 + 10, 4));
            frames = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i + 36 + 14, 4));
        }
        if (frames == 0) return new AudioFileInfo(AudioContainer.Mp3, "mp3", audioBytes * 8.0 / frame.Bitrate);
        var seconds = frames * (double)frame.SamplesPerFrame / frame.SampleRate;
        // Files joined end to end keep the first one's header: more bytes than it counts sound on at its bitrate.
        if (bytes > 0 && audioBytes > bytes * 1.1) seconds *= audioBytes / (double)bytes;
        return new AudioFileInfo(AudioContainer.Mp3, "mp3", seconds);
    }

    /// <summary>
    /// Counts the ADTS frames from <paramref name="i"/> (1024 samples per raw data block); when the chain breaks before
    /// the end, the rest of the file is assumed to sound at the same rate. Null when the first two do not chain.
    /// </summary>
    private static AudioFileInfo? Adts(ReadOnlySpan<byte> d, int i)
    {
        var rateIndex = d[i + 2] >> 2 & 0xF;
        if (rateIndex >= AdtsRates.Length) return null;
        long samples = 0;
        var frames = 0;
        var pos = i;
        while (IsAdts(d, pos) && (d[pos + 2] >> 2 & 0xF) == rateIndex)
        {
            var length = (d[pos + 3] & 3) << 11 | d[pos + 4] << 3 | d[pos + 5] >> 5;
            if (length < 7) break;
            samples += 1024L * ((d[pos + 6] & 3) + 1);
            frames++;
            pos += length;
        }
        if (frames < 2 && pos < d.Length) return null;
        var seconds = samples / (double)AdtsRates[rateIndex];
        var end = d.Length >= 128 && d.Slice(d.Length - 128, 3).SequenceEqual("TAG"u8) ? d.Length - 128 : d.Length;
        if (pos < end) seconds *= (end - i) / (double)(pos - i);
        return new AudioFileInfo(AudioContainer.Adts, "aac", seconds);
    }
}
