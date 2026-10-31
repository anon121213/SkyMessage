using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shouldly;
using SkiaSharp;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>Generated images (SkiaSharp) and helpers to inspect what the server returns.</summary>
internal static class TestImages
{
    public static readonly SKColor Red = new(255, 0, 0);
    public static readonly SKColor Blue = new(0, 0, 255);

    /// <summary>Left half <paramref name="left"/>, right half <paramref name="right"/> (orientation is then visible in the pixels).</summary>
    public static byte[] Halves(int width, int height, SKEncodedImageFormat format, SKColor left, SKColor right, int quality = 90)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(right);
            using var paint = new SKPaint { Color = left };
            canvas.DrawRect(0, 0, width / 2f, height, paint);
        }
        using var data = bitmap.Encode(format, quality) ?? throw new InvalidOperationException($"Skia cannot encode {format}.");
        return data.ToArray();
    }

    public static byte[] Png(int width, int height) => Halves(width, height, SKEncodedImageFormat.Png, Red, Blue);

    public static byte[] Jpeg(int width, int height) => Halves(width, height, SKEncodedImageFormat.Jpeg, Red, Blue);

    /// <summary>The classic 1×1 GIF89a (Skia has no GIF encoder).</summary>
    public static byte[] Gif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02,
        0x02, 0x44, 0x01, 0x00, 0x3B,
    ];

    /// <summary>
    /// A tiny PNG whose header claims <paramref name="width"/>×<paramref name="height"/> RGB pixels (a decompression
    /// bomb: decoding it would need width·height·4 bytes).
    /// </summary>
    public static byte[] PngHeaderClaiming(int width, int height)
    {
        using var file = new MemoryStream();
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // RGB
        WriteChunk(file, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(new byte[64 * 1024]); // the first rows only; the rest is "missing"
            WriteChunk(file, "IDAT", compressed.ToArray());
        }
        WriteChunk(file, "IEND", []);
        return file.ToArray();
    }

    private static void WriteChunk(Stream file, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        file.Write(buffer);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        file.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeAndData));
        file.Write(buffer);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>Inserts an APP1 Exif segment (big-endian TIFF, IFD0 holding only Orientation) right after SOI.</summary>
    public static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        jpeg[0].ShouldBe((byte)0xFF);
        jpeg[1].ShouldBe((byte)0xD8);
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // header, IFD0 at offset 8
            0x00, 0x01,                                               // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,           // Orientation, SHORT, count 1
            (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,  // value (left-justified)
            0x00, 0x00, 0x00, 0x00,                                   // no next IFD
        ];
        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = payload.Length + 2;
        return [.. jpeg[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }

    public static SKBitmap Decode(byte[] encoded) =>
        SKBitmap.Decode(encoded) ?? throw new ShouldAssertException("The returned bytes are not a decodable image.");

    /// <summary>Encoded format, stored size and EXIF origin, without decoding pixels.</summary>
    public static (SKEncodedImageFormat Format, int Width, int Height, SKEncodedOrigin Origin) Inspect(byte[] encoded)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data) ?? throw new ShouldAssertException("The returned bytes are not a decodable image.");
        return (codec.EncodedFormat, codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);
    }

    public static bool IsRed(SKColor c) => c.Red > 170 && c.Green < 90 && c.Blue < 90;

    public static bool IsBlue(SKColor c) => c.Blue > 170 && c.Red < 90 && c.Green < 90;

    /// <summary>Upright image whose top half is one colour and bottom half the other.</summary>
    public static void ShouldBeTopBottom(SKBitmap bitmap, Func<SKColor, bool> top, Func<SKColor, bool> bottom, string what)
    {
        var x = bitmap.Width / 2;
        var topPixel = bitmap.GetPixel(x, bitmap.Height / 8);
        var bottomPixel = bitmap.GetPixel(x, bitmap.Height - 1 - bitmap.Height / 8);
        top(topPixel).ShouldBeTrue($"{what}: unexpected top colour {topPixel}");
        bottom(bottomPixel).ShouldBeTrue($"{what}: unexpected bottom colour {bottomPixel}");
    }
}

/// <summary>PCM WAV files generated in memory.</summary>
internal static class TestAudio
{
    /// <summary>Mono 16-bit sine; <paramref name="envelope"/> maps the relative position 0..1 to an amplitude 0..1.</summary>
    public static byte[] Wav(double seconds, Func<double, double>? envelope = null, int sampleRate = 48000, double frequency = 440)
    {
        var samples = (int)(seconds * sampleRate);
        using var stream = new MemoryStream(44 + samples * 2);
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples * 2);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)1); // mono
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            for (var i = 0; i < samples; i++)
            {
                var amplitude = envelope?.Invoke((double)i / samples) ?? 0.8;
                writer.Write((short)Math.Round(amplitude * 32000 * Math.Sin(2 * Math.PI * frequency * i / sampleRate)));
            }
        }
        return stream.ToArray();
    }
}

/// <summary>ISO-BMFF (MP4) structure checks.</summary>
internal static class Mp4
{
    /// <summary>Types of the top-level boxes in file order (e.g. ftyp, moov, mdat).</summary>
    public static IReadOnlyList<string> TopLevelBoxes(byte[] file)
    {
        var boxes = new List<string>();
        long position = 0;
        while (file.Length - position >= 8)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)position));
            var type = Encoding.ASCII.GetString(file, (int)position + 4, 4);
            if (size == 1) size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan((int)position + 8));
            else if (size == 0) size = file.Length - position;
            if (size < 8) break;
            boxes.Add(type);
            position += size;
        }
        return boxes;
    }

    /// <summary>"Fast start": the index (moov) precedes the media data, so playback can begin while downloading.</summary>
    public static bool IsFastStart(byte[] file)
    {
        var boxes = TopLevelBoxes(file);
        var moov = boxes.ToList().IndexOf("moov");
        var mdat = boxes.ToList().IndexOf("mdat");
        return moov >= 0 && mdat >= 0 && moov < mdat;
    }
}

internal sealed record ProbedStream(string CodecType, string CodecName, int Width, int Height, string? PixelFormat, int? Channels, int? SampleRate,
    string? Profile, long? BitRate);

internal sealed record ProbedMedia(string FormatName, double? DurationSeconds, IReadOnlyDictionary<string, string> Tags, IReadOnlyList<ProbedStream> Streams)
{
    public ProbedStream? Video => Streams.FirstOrDefault(s => s.CodecType == "video");
    public ProbedStream? Audio => Streams.FirstOrDefault(s => s.CodecType == "audio");
}

/// <summary>
/// The FFmpeg the server would use (same resolution rules), to generate inputs and inspect outputs. Tests that need
/// it are skipped when it is missing (the server then stores audio/video as uploaded).
/// </summary>
internal static class Ffmpeg
{
    private static readonly Lazy<(string? Ffmpeg, string? Ffprobe)> Tools = new(() => (Find("ffmpeg"), Find("ffprobe")));

    public static bool IsAvailable => Tools.Value.Ffmpeg is not null && Tools.Value.Ffprobe is not null;

    public static void Require() =>
        Assert.SkipUnless(IsAvailable, "ffmpeg/ffprobe are not installed; the server stores audio and video as uploaded.");

    /// <summary>Runs ffmpeg with <paramref name="arguments"/> (inputs and options) and returns the output file's bytes.</summary>
    public static async Task<byte[]> GenerateAsync(string extension, params string[] arguments)
    {
        var directory = NewDirectory();
        try
        {
            var output = Path.Combine(directory, "output" + extension);
            await RunAsync(Tools.Value.Ffmpeg!, ["-nostdin", "-hide_banner", "-v", "error", "-y", .. arguments, output]);
            return await File.ReadAllBytesAsync(output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task<ProbedMedia> ProbeAsync(byte[] content)
    {
        var json = await WithFileAsync(content, path => RunAsync(Tools.Value.Ffprobe!,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path]));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        var tags = format.TryGetProperty("tags", out var t)
            ? t.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString())
            : new Dictionary<string, string>();
        var streams = root.GetProperty("streams").EnumerateArray().Select(s => new ProbedStream(
            Text(s, "codec_type") ?? "",
            Text(s, "codec_name") ?? "",
            (int)(Number(s, "width") ?? 0),
            (int)(Number(s, "height") ?? 0),
            Text(s, "pix_fmt"),
            Number(s, "channels") is { } channels ? (int)channels : null,
            Number(s, "sample_rate") is { } rate ? (int)rate : null,
            Text(s, "profile"),
            Number(s, "bit_rate") is { } bitRate ? (long)bitRate : null)).ToList();
        return new ProbedMedia(Text(format, "format_name") ?? "", Number(format, "duration"), tags, streams);
    }

    /// <summary>SHA-256 over the packets of one stream: equal hashes mean the stream was copied, not re-encoded.</summary>
    public static async Task<string> StreamHashAsync(byte[] content, string stream = "0:v:0")
    {
        var output = await WithFileAsync(content, path => RunAsync(Tools.Value.Ffmpeg!,
            ["-nostdin", "-v", "error", "-i", path, "-map", stream, "-c", "copy", "-f", "streamhash", "-hash", "sha256", "-"]));
        return output.Trim();
    }

    private static async Task<string> WithFileAsync(byte[] content, Func<string, Task<string>> action)
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "input");
            await File.WriteAllBytesAsync(path, content);
            return await action(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{executable} did not start.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{executable} {string.Join(' ', arguments)} timed out.");
        }
        var output = await stdout;
        var error = await stderr;
        process.ExitCode.ShouldBe(0, $"{executable} {string.Join(' ', arguments)}: {error}");
        return output;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "skymessage-media-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? Find(string name)
    {
        var resolved = FfmpegRunner.ResolveExecutable(name, name);
        return Path.IsPathRooted(resolved) && File.Exists(resolved) ? resolved : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }
}
