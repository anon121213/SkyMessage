using System.Buffers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Mapping;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.MediaFiles;

/// <summary>Resumable chunked uploads (SPEC §6.4). Every operation is owner-only; other users get 404.</summary>
public interface IMediaUploadService
{
    Task<UploadSessionDto> CreateAsync(CreateUploadRequest request, CancellationToken cancellationToken = default);

    Task<UploadSessionDto> GetAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <param name="body">Raw chunk bytes. At most the expected chunk length + 1 byte is read.</param>
    /// <param name="declaredLength">Content-Length of the request, when sent.</param>
    Task PutChunkAsync(Guid mediaId, int index, Stream body, long? declaredLength, CancellationToken cancellationToken = default);

    /// <summary>Idempotent: completing an upload that is already Processing/Ready/Failed returns its current state.</summary>
    Task<MediaDto> CompleteAsync(Guid mediaId, CancellationToken cancellationToken = default);
}

internal sealed class MediaUploadService(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IMediaChunkStore chunks,
    IMediaProcessingQueue queue,
    IOptions<StorageOptions> storageOptions,
    ILogger<MediaUploadService> logger) : IMediaUploadService
{
    public async Task<UploadSessionDto> CreateAsync(CreateUploadRequest request, CancellationToken cancellationToken = default)
    {
        var ownerId = currentUser.UserId;
        var storage = storageOptions.Value;

        if (request.SizeBytes <= 0)
            throw new BadRequestException(ErrorCodes.Validation, "The file is empty.");
        if (request.SizeBytes > storage.MaxUploadBytes)
            throw new AppException(ErrorCodes.MediaTooLarge, "The file is too large.", 413);
        var contentType = MediaContentTypes.ResolveForUpload(request.Kind, request.ContentType)
                          ?? throw new BadRequestException(ErrorCodes.MediaUnsupported, "This content type is not accepted for this kind of media.");

        var visual = request.Kind is MediaKind.Photo or MediaKind.Video or MediaKind.VideoNote;
        var timed = request.Kind is MediaKind.Video or MediaKind.VideoNote or MediaKind.Voice or MediaKind.Audio;
        var now = clock.UtcNow;
        var id = Guid.CreateVersion7();
        var media = new Media
        {
            Id = id,
            OwnerId = ownerId,
            Kind = request.Kind,
            Status = MediaStatus.Uploading,
            FileName = MediaFileNames.Sanitize(request.FileName, request.Kind, contentType),
            ContentType = contentType,
            SizeBytes = request.SizeBytes,
            DeclaredSizeBytes = request.SizeBytes,
            ChunkSize = storage.ChunkSize,
            ChunkCount = ChunkMath.ChunkCount(request.SizeBytes, storage.ChunkSize),
            Sha256 = request.Sha256?.ToLowerInvariant(),
            // Client-provided metadata is only a hint until processing recomputes it (kept when it cannot).
            Width = visual ? request.Width : null,
            Height = visual ? request.Height : null,
            DurationMs = timed ? request.DurationMs : null,
            Waveform = request.Kind == MediaKind.Voice && request.Waveform is { Length: Limits.WaveformSamples } waveform ? waveform : null,
            WrappedKey = chunks.CreateWrappedKey(id),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Media.Add(media);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Upload {MediaId} started by {UserId}: {Kind}, {SizeBytes} bytes in {ChunkCount} chunks",
            media.Id, ownerId, media.Kind, media.DeclaredSizeBytes, media.ChunkCount);
        return new UploadSessionDto
        {
            MediaId = media.Id,
            ChunkSize = media.ChunkSize,
            ChunkCount = media.ChunkCount,
            ReceivedChunks = [],
            Status = media.Status,
        };
    }

    public async Task<UploadSessionDto> GetAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        var ownerId = currentUser.UserId;
        var media = await db.Media.AsNoTracking()
                        .Where(m => m.Id == mediaId && m.OwnerId == ownerId)
                        .Select(m => new { m.Id, m.Status, m.ChunkSize, m.DeclaredSizeBytes })
                        .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new NotFoundException(ErrorCodes.MediaNotFound);

        // The upload geometry is derived from the declared size: processing may replace the stored chunks.
        var chunkCount = ChunkMath.ChunkCount(media.DeclaredSizeBytes, media.ChunkSize);
        IReadOnlyList<int> received = media.Status == MediaStatus.Uploading
            ? await db.MediaChunks.AsNoTracking()
                .Where(c => c.MediaId == mediaId)
                .OrderBy(c => c.Index)
                .Select(c => c.Index)
                .ToListAsync(cancellationToken)
            : Enumerable.Range(0, chunkCount).ToArray();

        return new UploadSessionDto
        {
            MediaId = media.Id,
            ChunkSize = media.ChunkSize,
            ChunkCount = chunkCount,
            ReceivedChunks = received,
            Status = media.Status,
        };
    }

    public async Task PutChunkAsync(Guid mediaId, int index, Stream body, long? declaredLength, CancellationToken cancellationToken = default)
    {
        var ownerId = currentUser.UserId;
        var media = await db.Media.AsNoTracking()
                        .Where(m => m.Id == mediaId && m.OwnerId == ownerId)
                        .Select(m => new { m.Status, m.ChunkSize, m.DeclaredSizeBytes, m.WrappedKey })
                        .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new NotFoundException(ErrorCodes.MediaNotFound);

        if (media.Status != MediaStatus.Uploading)
            throw new ConflictException(ErrorCodes.ChunkInvalid, "The upload is already complete.");
        if (!ChunkMath.IsValidIndex(media.DeclaredSizeBytes, media.ChunkSize, index))
            throw new BadRequestException(ErrorCodes.ChunkInvalid, "Chunk index is out of range.");
        var expected = ChunkMath.ChunkLength(media.DeclaredSizeBytes, media.ChunkSize, index);
        if (declaredLength is { } length && length != expected)
            throw new BadRequestException(ErrorCodes.ChunkInvalid, $"Chunk {index} must be exactly {expected} bytes.");

        // Exactly one chunk is buffered (pool buffers are powers of two, so no "+1" slack here).
        var buffer = ArrayPool<byte>.Shared.Rent(expected);
        try
        {
            var read = await ReadUpToAsync(body, buffer.AsMemory(0, expected), cancellationToken);
            if (read != expected || await ReadUpToAsync(body, new byte[1], cancellationToken) != 0)
                throw new BadRequestException(ErrorCodes.ChunkInvalid, $"Chunk {index} must be exactly {expected} bytes.");

            var stored = await chunks.WriteUploadChunkAsync(mediaId, ownerId, media.WrappedKey, index, buffer.AsMemory(0, expected), cancellationToken);
            if (!stored)
                throw new ConflictException(ErrorCodes.ChunkInvalid, "The upload is already complete.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<MediaDto> CompleteAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        var ownerId = currentUser.UserId;
        var media = await db.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId && m.OwnerId == ownerId, cancellationToken)
                    ?? throw new NotFoundException(ErrorCodes.MediaNotFound);

        if (media.Status != MediaStatus.Uploading)
        {
            // A retried complete (lost response) or a restart: make sure a pending job is queued and report the state.
            if (media.Status == MediaStatus.Processing) queue.Enqueue(media.Id);
            return media.ToDto();
        }

        var now = clock.UtcNow;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // The status flip takes the row lock first; chunk writes hold FOR SHARE on the same row, so the count
            // below sees every chunk that will ever be written to this upload.
            var flipped = await db.Media
                .Where(m => m.Id == mediaId && m.OwnerId == ownerId && m.Status == MediaStatus.Uploading)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, MediaStatus.Processing)
                    .SetProperty(m => m.CompletedAt, now)
                    .SetProperty(m => m.UpdatedAt, now), cancellationToken);
            if (flipped == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await db.Media.AsNoTracking().Where(m => m.Id == mediaId).Select(MediaMapping.Projection).FirstOrDefaultAsync(cancellationToken);
                return current ?? throw new NotFoundException(ErrorCodes.MediaNotFound);
            }

            var received = await db.MediaChunks.CountAsync(c => c.MediaId == mediaId, cancellationToken);
            if (received != media.ChunkCount)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new ConflictException(ErrorCodes.UploadIncomplete, $"{received} of {media.ChunkCount} chunks were received.");
            }

            await transaction.CommitAsync(cancellationToken);
        }

        queue.Enqueue(media.Id);
        logger.LogInformation("Upload {MediaId} completed; queued for processing", media.Id);

        media.Status = MediaStatus.Processing;
        media.CompletedAt = now;
        media.UpdatedAt = now;
        return media.ToDto();
    }

    /// <summary>Fills <paramref name="buffer"/> as far as the body allows; returns the number of bytes read.</summary>
    private static async Task<int> ReadUpToAsync(Stream body, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var read = await body.ReadAsync(buffer[total..], cancellationToken);
                if (read == 0) break;
                total += read;
            }
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away mid-chunk (common on bad networks): not a server error.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (IOException)
        {
            throw new BadRequestException(ErrorCodes.ChunkInvalid, "The chunk body was cut short.");
        }
        return total;
    }
}
