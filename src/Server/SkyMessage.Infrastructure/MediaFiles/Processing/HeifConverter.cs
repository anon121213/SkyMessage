using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>
/// HEIC/HEIF (iPhone photos) → PNG through ffmpeg, then the regular photo pipeline makes the JPEG. Modern iPhone
/// photos are grids of 512 px tiles that only ffmpeg ≥ 7.1 assembles; older versions return a single tile, which
/// is detected by comparing with the primary item's size (<c>ispe</c>) and reported as a failure instead of
/// storing a fragment.
/// </summary>
internal sealed class HeifConverter(FfmpegRunner ffmpeg)
{
    /// <summary>HEIF is ISO-BMFF, read by ffmpeg's mov demuxer.</summary>
    internal const string InputFormats = "mov";

    private const int MaxMetaBoxBytes = 16 * 1024 * 1024;
    private static readonly string[] HeifBrands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "hevm", "hevs", "mif1", "msf1", "avif", "avis"];

    public async Task<string> ConvertToPngAsync(string inputPath, string workDirectory, CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(workDirectory, "heif.png");
        await ffmpeg.RunFfmpegAsync(
            [
                "-protocol_whitelist", "file", "-format_whitelist", InputFormats,
                "-i", inputPath,
                "-frames:v", "1", "-update", "1", "-f", "image2", "-c:v", "png", outputPath,
            ],
            MediaTimeouts.StillImage, cancellationToken);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new MediaProcessingException("The HEIC image could not be converted.");

        if (ReadPrimaryImageSize(inputPath) is { } expected)
        {
            using var codec = SKCodec.Create(outputPath)
                              ?? throw new MediaProcessingException("The HEIC image could not be converted.");
            // A real crop (clean aperture) removes a few pixels; a single tile of a grid is a small fraction.
            if ((long)codec.Info.Width * codec.Info.Height * 2 < (long)expected.Width * expected.Height)
                throw new MediaProcessingException("This HEIC photo is tiled; converting it needs FFmpeg 7.1 or newer on the server.");
        }
        return outputPath;
    }

    /// <summary>ISO-BMFF "ftyp" box with a HEIF/AVIF brand (iOS sometimes labels HEIC uploads as image/jpeg).</summary>
    public static bool LooksLikeHeif(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using (var file = File.OpenRead(path))
        {
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return false;
        }
        if (!header.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;
        var brand = Encoding.ASCII.GetString(header.Slice(8, 4));
        return HeifBrands.Contains(brand, StringComparer.Ordinal);
    }

    /// <summary>
    /// Displayed size of the primary image: its <c>ispe</c> property, swapped by a 90°/270° <c>irot</c>. Null when the
    /// file is not HEIF or the boxes cannot be read.
    /// </summary>
    internal static (int Width, int Height)? ReadPrimaryImageSize(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        while (file.Length - file.Position >= 8)
        {
            var start = file.Position;
            file.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerLength = 8;
            if (size == 1)
            {
                if (file.Length - file.Position < 8) return null;
                file.ReadExactly(header.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = file.Length - start;
            }
            if (size < headerLength || size > file.Length - start) return null;

            if (header.Slice(4, 4).SequenceEqual("meta"u8))
            {
                var length = size - headerLength;
                if (length > MaxMetaBoxBytes) return null;
                var meta = new byte[length];
                file.ReadExactly(meta);
                return ParseMeta(meta);
            }
            file.Position = start + size;
        }
        return null;
    }

    private static (int Width, int Height)? ParseMeta(ReadOnlySpan<byte> meta)
    {
        if (meta.Length < 4) return null;
        var content = meta[4..]; // FullBox version + flags

        uint? primaryItem = null;
        List<(string Type, int Offset, int Length)>? properties = null; // ipco children, offsets relative to content
        (int Offset, int Length)? ipma = null;
        foreach (var (type, offset, length) in Children(content))
        {
            var body = content.Slice(offset, length);
            if (type == "pitm")
            {
                primaryItem = body.Length >= 6 && body[0] == 0 ? BinaryPrimitives.ReadUInt16BigEndian(body[4..])
                    : body.Length >= 8 ? BinaryPrimitives.ReadUInt32BigEndian(body[4..])
                    : null;
            }
            else if (type == "iprp")
            {
                foreach (var (childType, childOffset, childLength) in Children(body))
                {
                    var childStart = offset + childOffset;
                    if (childType == "ipco")
                        properties = Children(body.Slice(childOffset, childLength))
                            .Select(p => (p.Type, childStart + p.Offset, p.Length))
                            .ToList();
                    else if (childType == "ipma")
                        ipma = (childStart, childLength);
                }
            }
        }
        if (primaryItem is null || properties is null || ipma is null) return null;

        int? width = null, height = null;
        var quarterTurns = 0;
        foreach (var index in PrimaryItemProperties(content.Slice(ipma.Value.Offset, ipma.Value.Length), primaryItem.Value))
        {
            if (index < 1 || index > properties.Count) continue; // 0 = "no property"
            var (propertyType, propertyOffset, propertyLength) = properties[index - 1];
            var property = content.Slice(propertyOffset, propertyLength);
            if (propertyType == "ispe" && property.Length >= 12)
            {
                width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(property[4..]));
                height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(property[8..]));
            }
            else if (propertyType == "irot" && property.Length >= 1)
            {
                quarterTurns = property[0] & 0x3;
            }
        }
        if (width is not > 0 || height is not > 0) return null;
        return quarterTurns % 2 == 1 ? (height.Value, width.Value) : (width.Value, height.Value);
    }

    /// <summary>1-based ipco indexes associated with <paramref name="itemId"/> in an ipma box.</summary>
    private static List<int> PrimaryItemProperties(ReadOnlySpan<byte> ipma, uint itemId)
    {
        var result = new List<int>();
        if (ipma.Length < 8) return result;
        var version = ipma[0];
        var wideIndexes = (ipma[3] & 0x1) != 0;
        var entries = BinaryPrimitives.ReadUInt32BigEndian(ipma[4..]);
        var position = 8;
        for (var e = 0; e < entries; e++)
        {
            uint item;
            if (version < 1)
            {
                if (position + 2 > ipma.Length) break;
                item = BinaryPrimitives.ReadUInt16BigEndian(ipma[position..]);
                position += 2;
            }
            else
            {
                if (position + 4 > ipma.Length) break;
                item = BinaryPrimitives.ReadUInt32BigEndian(ipma[position..]);
                position += 4;
            }
            if (position + 1 > ipma.Length) break;
            var count = ipma[position++];
            for (var a = 0; a < count; a++)
            {
                int index;
                if (wideIndexes)
                {
                    if (position + 2 > ipma.Length) return result;
                    index = BinaryPrimitives.ReadUInt16BigEndian(ipma[position..]) & 0x7FFF;
                    position += 2;
                }
                else
                {
                    if (position + 1 > ipma.Length) return result;
                    index = ipma[position++] & 0x7F;
                }
                if (item == itemId) result.Add(index);
            }
        }
        return result;
    }

    /// <summary>Child boxes of a box body: (type, body offset, body length), offsets relative to <paramref name="data"/>.</summary>
    private static List<(string Type, int Offset, int Length)> Children(ReadOnlySpan<byte> data)
    {
        var result = new List<(string, int, int)>();
        var offset = 0;
        while (data.Length - offset >= 8)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            var type = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
            var headerLength = 8;
            if (size == 1)
            {
                if (data.Length - offset < 16) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(offset + 8)..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = data.Length - offset;
            }
            if (size < headerLength || size > data.Length - offset) break;
            result.Add((type, offset + headerLength, (int)size - headerLength));
            offset += (int)size;
        }
        return result;
    }
}
