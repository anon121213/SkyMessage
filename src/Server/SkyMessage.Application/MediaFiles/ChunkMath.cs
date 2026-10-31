namespace SkyMessage.Application.MediaFiles;

/// <summary>
/// Geometry of chunked media content: chunk <c>i</c> holds bytes <c>[i·chunkSize, min((i+1)·chunkSize, size))</c>,
/// so every chunk except the last is exactly <c>chunkSize</c> bytes long.
/// </summary>
public static class ChunkMath
{
    public static int ChunkCount(long sizeBytes, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        return checked((int)((sizeBytes + chunkSize - 1) / chunkSize));
    }

    public static bool IsValidIndex(long sizeBytes, int chunkSize, int index) =>
        index >= 0 && index < ChunkCount(sizeBytes, chunkSize);

    /// <summary>Exact plaintext length of chunk <paramref name="index"/>.</summary>
    public static int ChunkLength(long sizeBytes, int chunkSize, int index)
    {
        if (!IsValidIndex(sizeBytes, chunkSize, index))
            throw new ArgumentOutOfRangeException(nameof(index), index, "Chunk index is outside of the content.");
        var start = (long)index * chunkSize;
        return (int)Math.Min(chunkSize, sizeBytes - start);
    }

    /// <summary>Chunk index and the offset inside that chunk for an absolute byte position.</summary>
    public static (int Index, int Offset) Locate(long position, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        return (checked((int)(position / chunkSize)), (int)(position % chunkSize));
    }
}

/// <summary>Server-side bounds for uploads that are not part of the shared <c>Limits</c>.</summary>
public static class MediaUploadLimits
{
    public const int MinChunkSize = 64 * 1024;
    public const int MaxChunkSize = 16 * 1024 * 1024;

    /// <summary>
    /// Outer cap of a chunk request body (<c>[RequestSizeLimit]</c>). The exact expected length of the chunk is
    /// enforced while reading, so a body can never be larger than one chunk + 1 byte in memory.
    /// </summary>
    public const long MaxChunkRequestBytes = MaxChunkSize;

    public const int MaxClientFileNameLength = 1024;
    public const int MaxClientContentTypeLength = 255;
    public const int MaxDimension = 100_000;
    public const int MaxDurationMs = 24 * 60 * 60 * 1000;
    public const int MaxClientWaveformLength = 1024;
}
