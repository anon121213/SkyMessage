using SkyMessage.Contracts;

namespace SkyMessage.Domain.Entities;

/// <summary>
/// An uploaded file. The bytes live in <see cref="MediaChunk"/> rows (PostgreSQL bytea), optionally
/// encrypted at rest with a per-media key wrapped by the server master key.
/// </summary>
public sealed class Media
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public MediaKind Kind { get; set; }
    public MediaStatus Status { get; set; }

    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    /// <summary>Size of the stored (final, possibly server-processed) content.</summary>
    public long SizeBytes { get; set; }
    /// <summary>Size announced by the client at upload start.</summary>
    public long DeclaredSizeBytes { get; set; }
    public int ChunkSize { get; set; }
    public int ChunkCount { get; set; }
    public string? Sha256 { get; set; }

    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationMs { get; set; }
    public byte[]? Waveform { get; set; }

    /// <summary>A <see cref="MediaThumbnail"/> row exists (kept separate so loading Media never loads preview bytes).</summary>
    public bool HasThumbnail { get; set; }
    public int? ThumbnailWidth { get; set; }
    public int? ThumbnailHeight { get; set; }
    /// <summary>≈24px JPEG for blurred placeholders, sent inside DTOs (well under 1 KB).</summary>
    public byte[]? MiniThumbnail { get; set; }

    /// <summary>Per-media data key wrapped with the master key (nonce|ciphertext|tag); null = not encrypted.</summary>
    public byte[]? WrappedKey { get; set; }
    public string? ProcessingError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Preview image (≤ 480px JPEG, a few tens of KB) for photos, videos and video notes.</summary>
public sealed class MediaThumbnail
{
    public Guid MediaId { get; set; }
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "image/jpeg";
}

public sealed class MediaChunk
{
    public Guid MediaId { get; set; }
    public int Index { get; set; }
    /// <summary>Plain bytes, or nonce(12)|ciphertext|tag(16) when the media is encrypted.</summary>
    public byte[] Data { get; set; } = [];
    /// <summary>Plaintext length of this chunk.</summary>
    public int Size { get; set; }
}
