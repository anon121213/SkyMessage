using System.Buffers;
using SkyMessage.Application.MediaFiles;

namespace SkyMessage.Infrastructure.MediaFiles.Storage;

/// <summary>Plaintext chunks of one media, loaded on demand by <see cref="ChunkedMediaStream"/>.</summary>
internal interface IMediaChunkSource : IDisposable, IAsyncDisposable
{
    /// <summary>Copies the plaintext of chunk <paramref name="index"/> into <paramref name="destination"/> and returns its length.</summary>
    /// <exception cref="InvalidDataException">The chunk is missing or larger than <paramref name="destination"/>.</exception>
    ValueTask<int> ReadChunkAsync(int index, Memory<byte> destination, CancellationToken cancellationToken);

    /// <inheritdoc cref="ReadChunkAsync"/>
    int ReadChunk(int index, Span<byte> destination);
}

/// <summary>
/// Read-only, seekable stream over chunked content that keeps exactly one plaintext chunk in memory (a pooled
/// buffer). <see cref="Length"/> is known up front, so ASP.NET's range processing can seek to any offset and
/// only the chunks that overlap the requested range are loaded. Every loaded chunk must have exactly its
/// expected length, otherwise <see cref="InvalidDataException"/> is thrown.
/// </summary>
internal sealed class ChunkedMediaStream : Stream
{
    private readonly IMediaChunkSource _source;
    private readonly long _length;
    private readonly int _chunkSize;
    private byte[]? _buffer;
    private int _bufferedIndex = -1;
    private int _bufferedLength;
    private long _position;
    private bool _disposed;

    public ChunkedMediaStream(IMediaChunkSource source, long length, int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        _source = source;
        _length = length;
        _chunkSize = chunkSize;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        _position = target; // Past the end is allowed (reads then return 0), as with FileStream.
        return target;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= _length) return 0;
        var (index, offset) = ChunkMath.Locate(_position, _chunkSize);
        if (index != _bufferedIndex)
        {
            var target = PrepareBuffer();
            Accept(index, _source.ReadChunk(index, target.AsSpan(0, _chunkSize)));
        }
        return CopyBuffered(buffer, offset);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= _length) return 0;
        var (index, offset) = ChunkMath.Locate(_position, _chunkSize);
        if (index != _bufferedIndex)
        {
            var target = PrepareBuffer();
            Accept(index, await _source.ReadChunkAsync(index, target.AsMemory(0, _chunkSize), cancellationToken));
        }
        return CopyBuffered(buffer.Span, offset);
    }

    /// <summary>Writes the remaining content chunk by chunk without an intermediate copy buffer.</summary>
    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_position < _length)
        {
            var (index, offset) = ChunkMath.Locate(_position, _chunkSize);
            if (index != _bufferedIndex)
            {
                var target = PrepareBuffer();
                Accept(index, await _source.ReadChunkAsync(index, target.AsMemory(0, _chunkSize), cancellationToken));
            }
            var count = _bufferedLength - offset;
            await destination.WriteAsync(_buffer.AsMemory(offset, count), cancellationToken);
            _position += count;
        }
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            ReturnBuffer();
            _source.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            ReturnBuffer();
            await _source.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }

    private byte[] PrepareBuffer()
    {
        _bufferedIndex = -1; // Invalid until the new chunk is fully loaded and verified.
        return _buffer ??= ArrayPool<byte>.Shared.Rent(_chunkSize);
    }

    private void Accept(int index, int length)
    {
        var expected = ChunkMath.ChunkLength(_length, _chunkSize, index);
        if (length != expected)
            throw new InvalidDataException($"Stored chunk {index} has {length} bytes; {expected} were expected.");
        _bufferedIndex = index;
        _bufferedLength = length;
    }

    private int CopyBuffered(Span<byte> destination, int offset)
    {
        var count = Math.Min(_bufferedLength - offset, destination.Length);
        _buffer.AsSpan(offset, count).CopyTo(destination);
        _position += count;
        return count;
    }

    private void ReturnBuffer()
    {
        if (_buffer is null) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
        _bufferedIndex = -1;
    }
}
