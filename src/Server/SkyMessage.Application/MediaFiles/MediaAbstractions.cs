using SkyMessage.Contracts;

namespace SkyMessage.Application.MediaFiles;

/// <summary>What is needed to read a media's stored bytes.</summary>
public sealed record MediaStorageInfo(Guid MediaId, byte[]? WrappedKey, long SizeBytes, int ChunkSize, int ChunkCount);

/// <summary>
/// Media bytes in PostgreSQL (<c>media_chunks</c>), AES-256-GCM encrypted at rest when <c>Storage:MasterKey</c>
/// is set. Implemented by Infrastructure.
/// </summary>
public interface IMediaChunkStore
{
    /// <summary>A fresh per-media data key wrapped with the master key; null when encryption at rest is off.</summary>
    byte[]? CreateWrappedKey(Guid mediaId);

    /// <summary>
    /// Stores, or overwrites on a client retry, chunk <paramref name="index"/>. Atomic with respect to completing
    /// the upload: nothing is written and false is returned unless the media is still
    /// <see cref="MediaStatus.Uploading"/> and owned by <paramref name="ownerId"/>.
    /// </summary>
    Task<bool> WriteUploadChunkAsync(Guid mediaId, Guid ownerId, byte[]? wrappedKey, int index, ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Seekable, read-only plaintext stream over the stored content. It loads (and decrypts) one chunk at a time on
    /// demand through its own database scope, so it may outlive the caller's scope; dispose it when done.
    /// </summary>
    Stream OpenRead(MediaStorageInfo media);

    /// <summary>Plain preview bytes from a stored <c>media_thumbnails.data</c> value.</summary>
    byte[] ReadThumbnail(Guid mediaId, byte[]? wrappedKey, byte[] storedData);
}

/// <summary>Hands completed uploads to the background processing worker (in-memory; Processing rows are re-queued on start).</summary>
public interface IMediaProcessingQueue
{
    void Enqueue(Guid mediaId);
}
