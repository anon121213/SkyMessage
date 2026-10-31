using Shouldly;
using SkyMessage.Application.MediaFiles;
using Xunit;

namespace SkyMessage.UnitTests.Media;

public sealed class ChunkMathTests
{
    private const int Mb = 1024 * 1024;

    [Theory]
    [InlineData(0L, Mb, 0)]
    [InlineData(1L, Mb, 1)]
    [InlineData(Mb - 1L, Mb, 1)]
    [InlineData(Mb + 0L, Mb, 1)]
    [InlineData(Mb + 1L, Mb, 2)]
    [InlineData(2L * 1024 * Mb, Mb, 2048)]
    [InlineData(10L, 3, 4)]
    public void Chunk_count_is_ceiling_of_size_over_chunk_size(long size, int chunkSize, int expected) =>
        ChunkMath.ChunkCount(size, chunkSize).ShouldBe(expected);

    [Fact]
    public void Every_chunk_but_the_last_is_full()
    {
        ChunkMath.ChunkLength(10, 3, 0).ShouldBe(3);
        ChunkMath.ChunkLength(10, 3, 2).ShouldBe(3);
        ChunkMath.ChunkLength(10, 3, 3).ShouldBe(1);
        ChunkMath.ChunkLength(9, 3, 2).ShouldBe(3);
        ChunkMath.ChunkLength(2L * 1024 * Mb + 5, Mb, 2048).ShouldBe(5);
    }

    [Fact]
    public void Lengths_add_up_to_the_size()
    {
        const long size = 5 * Mb + 12345;
        var count = ChunkMath.ChunkCount(size, Mb);
        Enumerable.Range(0, count).Sum(i => (long)ChunkMath.ChunkLength(size, Mb, i)).ShouldBe(size);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Out_of_range_indexes_are_invalid(int index)
    {
        ChunkMath.IsValidIndex(10, 3, index).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => ChunkMath.ChunkLength(10, 3, index));
    }

    [Fact]
    public void Locate_maps_positions_to_chunk_and_offset()
    {
        ChunkMath.Locate(0, 3).ShouldBe((0, 0));
        ChunkMath.Locate(2, 3).ShouldBe((0, 2));
        ChunkMath.Locate(3, 3).ShouldBe((1, 0));
        ChunkMath.Locate(2L * 1024 * Mb + 7, Mb).ShouldBe((2048, 7));
        Should.Throw<ArgumentOutOfRangeException>(() => ChunkMath.Locate(-1, 3));
    }

    [Fact]
    public void Invalid_geometry_is_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ChunkMath.ChunkCount(-1, Mb));
        Should.Throw<ArgumentOutOfRangeException>(() => ChunkMath.ChunkCount(10, 0));
    }
}
