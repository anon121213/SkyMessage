using System.Buffers.Binary;
using System.Text;
using Shouldly;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using Xunit;

namespace SkyMessage.UnitTests.Media;

public sealed class HeifConverterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "skymessage-tests", Guid.NewGuid().ToString("N"));

    public HeifConverterTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public void Reads_the_primary_item_size()
    {
        var path = Write(Heif(primaryItem: 1, rotation: null, mdatFirst: false));

        HeifConverter.LooksLikeHeif(path).ShouldBeTrue();
        HeifConverter.ReadPrimaryImageSize(path).ShouldBe((4032, 3024));
    }

    [Fact]
    public void Quarter_turn_rotation_swaps_the_displayed_size()
    {
        var path = Write(Heif(primaryItem: 1, rotation: 1, mdatFirst: true));

        HeifConverter.ReadPrimaryImageSize(path).ShouldBe((3024, 4032));
    }

    [Fact]
    public void Properties_of_other_items_are_ignored()
    {
        // Item 2 is a 512×512 tile; only the primary item's ispe counts.
        var path = Write(Heif(primaryItem: 2, rotation: null, mdatFirst: false));

        HeifConverter.ReadPrimaryImageSize(path).ShouldBe((512, 512));
    }

    [Fact]
    public void Non_heif_files_have_no_size()
    {
        var path = Write("not an iso-bmff file at all"u8.ToArray());

        HeifConverter.LooksLikeHeif(path).ShouldBeFalse();
        HeifConverter.ReadPrimaryImageSize(path).ShouldBeNull();
    }

    [Fact]
    public void Truncated_boxes_are_rejected_safely()
    {
        // ftyp (24 bytes) followed by the first 16 bytes of the meta box only.
        var bytes = Heif(primaryItem: 1, rotation: null, mdatFirst: false);
        var path = Write(bytes[..40]);

        HeifConverter.ReadPrimaryImageSize(path).ShouldBeNull();
    }

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>ftyp + meta(pitm, iprp(ipco(ispe 4032×3024, ispe 512×512, [irot]), ipma)) [+ mdat before meta].</summary>
    private static byte[] Heif(ushort primaryItem, byte? rotation, bool mdatFirst)
    {
        var ftyp = Box("ftyp", [.. "heic"u8, 0, 0, 0, 0, .. "mif1heic"u8]);
        var pitm = FullBox("pitm", [(byte)(primaryItem >> 8), (byte)primaryItem]);
        List<byte[]> properties = [Ispe(4032, 3024), Ispe(512, 512)];
        if (rotation is { } r) properties.Add(Box("irot", [r]));
        var ipco = Box("ipco", [.. properties.SelectMany(p => p)]);

        // Item 1 → properties 1 (+3 irot, essential flag set); item 2 → property 2.
        List<byte> associations = [0, 0, 0, 2, 0, 1, (byte)(rotation is null ? 1 : 2), 0x81];
        if (rotation is not null) associations.Add(0x83);
        associations.AddRange([0, 2, 1, 0x02]);
        var ipma = FullBox("ipma", [.. associations]);

        var meta = FullBox("meta", [.. pitm, .. Box("iprp", [.. ipco, .. ipma])]);
        var mdat = Box("mdat", new byte[64]);
        return mdatFirst ? [.. ftyp, .. mdat, .. meta] : [.. ftyp, .. meta, .. mdat];
    }

    private static byte[] Ispe(uint width, uint height)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload, width);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), height);
        return FullBox("ispe", payload);
    }

    private static byte[] FullBox(string type, byte[] payload) => Box(type, [0, 0, 0, 0, .. payload]);

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }
}
