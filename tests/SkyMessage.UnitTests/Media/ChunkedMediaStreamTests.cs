using System.Security.Cryptography;
using Shouldly;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Infrastructure.MediaFiles.Storage;
using Xunit;

namespace SkyMessage.UnitTests.Media;

public sealed class ChunkedMediaStreamTests
{
    private const int ChunkSize = 1000;

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(5432)]
    public async Task Reads_everything_in_odd_sized_reads(int length)
    {
        var data = RandomNumberGenerator.GetBytes(length);
        await using var stream = Create(data, encrypted: false, out _);

        var result = new MemoryStream();
        var buffer = new byte[333];
        int read;
        while ((read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken)) > 0)
        {
            read.ShouldBeLessThanOrEqualTo(buffer.Length);
            result.Write(buffer, 0, read);
        }

        result.ToArray().ShouldBe(data);
        stream.Position.ShouldBe(length);
    }

    [Fact]
    public void Synchronous_reads_work_too()
    {
        var data = RandomNumberGenerator.GetBytes(2500);
        using var stream = Create(data, encrypted: true, out _);

        var result = new MemoryStream();
        stream.CopyTo(result, 128);

        result.ToArray().ShouldBe(data);
    }

    [Fact]
    public async Task Seeks_anywhere_and_reads_across_chunk_boundaries()
    {
        var data = RandomNumberGenerator.GetBytes(4321);
        await using var stream = Create(data, encrypted: true, out _);
        var ct = TestContext.Current.CancellationToken;

        foreach (var (position, count) in new[] { (0L, 10), (995L, 10), (1000L, 1000), (3999L, 400), (4320L, 5), (2500L, 1) })
        {
            stream.Seek(position, SeekOrigin.Begin).ShouldBe(position);
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
                if (read == 0) break;
                total += read;
            }
            var expected = (int)Math.Min(count, data.Length - position);
            total.ShouldBe(expected);
            buffer.AsSpan(0, total).SequenceEqual(data.AsSpan((int)position, total)).ShouldBeTrue($"at {position}");
        }

        stream.Seek(-10, SeekOrigin.End).ShouldBe(4311);
        stream.Seek(5, SeekOrigin.Current).ShouldBe(4316);
        Should.Throw<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public async Task Loads_one_chunk_at_a_time_and_each_only_once_when_reading_sequentially()
    {
        var data = RandomNumberGenerator.GetBytes(3500);
        await using var stream = Create(data, encrypted: false, out var source);

        await stream.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);

        source.Loads.ShouldBe(new[] { 0, 1, 2, 3 });
    }

    [Fact]
    public async Task Range_read_only_loads_the_overlapping_chunks()
    {
        var data = RandomNumberGenerator.GetBytes(10_000);
        await using var stream = Create(data, encrypted: false, out var source);
        var ct = TestContext.Current.CancellationToken;

        stream.Position = 6_500;
        var buffer = new byte[1_000];
        await stream.ReadExactlyAsync(buffer, ct);

        buffer.ShouldBe(data.AsSpan(6_500, 1_000).ToArray());
        source.Loads.ShouldBe(new[] { 6, 7 });
    }

    [Fact]
    public async Task CopyTo_from_the_middle_copies_the_rest()
    {
        var data = RandomNumberGenerator.GetBytes(2_750);
        await using var stream = Create(data, encrypted: true, out _);
        stream.Position = 1_234;

        var result = new MemoryStream();
        await stream.CopyToAsync(result, TestContext.Current.CancellationToken);

        result.ToArray().ShouldBe(data.AsSpan(1_234).ToArray());
    }

    [Fact]
    public async Task Reading_at_or_past_the_end_returns_zero()
    {
        var data = RandomNumberGenerator.GetBytes(1_500);
        await using var stream = Create(data, encrypted: false, out var source);
        var ct = TestContext.Current.CancellationToken;

        stream.Position = 1_500;
        (await stream.ReadAsync(new byte[10], ct)).ShouldBe(0);
        stream.Position = 99_999;
        (await stream.ReadAsync(new byte[10], ct)).ShouldBe(0);
        stream.Read(new byte[10], 0, 10).ShouldBe(0);
        source.Loads.ShouldBeEmpty();
    }

    [Fact]
    public void Describes_itself_as_a_seekable_read_only_stream()
    {
        using var stream = Create(new byte[10], encrypted: false, out _);

        stream.CanRead.ShouldBeTrue();
        stream.CanSeek.ShouldBeTrue();
        stream.CanWrite.ShouldBeFalse();
        stream.Length.ShouldBe(10);
        Should.Throw<NotSupportedException>(() => stream.Write([1], 0, 1));
        Should.Throw<NotSupportedException>(() => stream.SetLength(1));
    }

    [Fact]
    public async Task A_chunk_with_the_wrong_length_is_rejected()
    {
        var data = RandomNumberGenerator.GetBytes(2_000);
        var source = new FakeChunkSource(data, ChunkSize, cipher: null) { Corrupt = index => index == 1 };
        await using var stream = new ChunkedMediaStream(source, data.Length, ChunkSize);

        await Should.ThrowAsync<InvalidDataException>(async () =>
            await stream.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_tampered_encrypted_chunk_fails_the_read()
    {
        var data = RandomNumberGenerator.GetBytes(2_000);
        var mediaId = Guid.CreateVersion7();
        using var cipher = new MediaChunkCipher(MediaKeyWrap.GenerateDataKey(), mediaId);
        var source = new FakeChunkSource(data, ChunkSize, cipher);
        source.Stored[1][40] ^= 0xFF;
        await using var stream = new ChunkedMediaStream(source, data.Length, ChunkSize);

        var first = new byte[ChunkSize];
        await stream.ReadExactlyAsync(first, TestContext.Current.CancellationToken);
        first.ShouldBe(data.AsSpan(0, ChunkSize).ToArray());
        await Should.ThrowAsync<CryptographicException>(async () =>
            await stream.ReadExactlyAsync(new byte[10], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispose_releases_the_source()
    {
        var source = new FakeChunkSource(new byte[10], ChunkSize, cipher: null);
        var stream = new ChunkedMediaStream(source, 10, ChunkSize);
        _ = await stream.ReadAsync(new byte[4], TestContext.Current.CancellationToken);

        await stream.DisposeAsync();

        source.Disposed.ShouldBeTrue();
        stream.CanRead.ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
    }

    private static ChunkedMediaStream Create(byte[] data, bool encrypted, out FakeChunkSource source)
    {
        var cipher = encrypted ? new MediaChunkCipher(MediaKeyWrap.GenerateDataKey(), Guid.CreateVersion7()) : null;
        source = new FakeChunkSource(data, ChunkSize, cipher);
        return new ChunkedMediaStream(source, data.Length, ChunkSize);
    }

    /// <summary>Stores chunks like the database does (optionally encrypted) and records which chunks were loaded.</summary>
    private sealed class FakeChunkSource : IMediaChunkSource
    {
        private readonly MediaChunkCipher? _cipher;

        public FakeChunkSource(byte[] data, int chunkSize, MediaChunkCipher? cipher)
        {
            _cipher = cipher;
            var count = ChunkMath.ChunkCount(data.Length, chunkSize);
            for (var i = 0; i < count; i++)
            {
                var plain = data.AsSpan(i * chunkSize, ChunkMath.ChunkLength(data.Length, chunkSize, i));
                Stored.Add(cipher is null ? plain.ToArray() : cipher.Encrypt(i, plain));
            }
        }

        public List<byte[]> Stored { get; } = [];
        public List<int> Loads { get; } = [];
        public Func<int, bool> Corrupt { get; init; } = _ => false;
        public bool Disposed { get; private set; }

        public ValueTask<int> ReadChunkAsync(int index, Memory<byte> destination, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ReadChunk(index, destination.Span));

        public int ReadChunk(int index, Span<byte> destination)
        {
            Loads.Add(index);
            var stored = Stored[index];
            var length = _cipher is null ? Copy(stored, destination) : _cipher.Decrypt(index, stored, destination);
            return Corrupt(index) ? length - 1 : length;
        }

        public void Dispose()
        {
            Disposed = true;
            _cipher?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private static int Copy(byte[] stored, Span<byte> destination)
        {
            stored.CopyTo(destination);
            return stored.Length;
        }
    }
}
