using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Mapping;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using SkyMessage.Infrastructure.MediaFiles.Storage;
using SkyMessage.Infrastructure.Persistence;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>What processing produced; null members keep the current value (client hints included).</summary>
internal sealed record ProcessedMedia
{
    public static ProcessedMedia Unchanged { get; } = new();

    /// <summary>New content replacing the uploaded bytes; null keeps the stored chunks untouched.</summary>
    public string? ReplacementPath { get; init; }
    public string? ContentType { get; init; }
    public string? FileName { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationMs { get; init; }
    public byte[]? Waveform { get; init; }
    public ThumbnailSet? Thumbnail { get; init; }
}

/// <summary>
/// Processes one completed upload (scoped; run by <see cref="MediaProcessingWorker"/>): assemble the chunks into a
/// temp file, verify the optional SHA-256, transform per kind, then atomically replace the chunks, store metadata
/// and previews, mark the media Ready (or Failed) and send <c>MediaProcessed</c> to the owner after the commit.
/// Missing ffmpeg/ffprobe means "store as uploaded". Transient database errors propagate so the worker retries.
/// </summary>
internal sealed class MediaProcessor(
    AppDbContext db,
    IClock clock,
    IRealtimeNotifier notifier,
    MediaEncryption encryption,
    IMediaChunkStore chunkStore,
    MediaTempStorage temp,
    ImageProcessor images,
    HeifConverter heif,
    VideoProcessor videos,
    AudioProcessor audio,
    ILogger<MediaProcessor> logger)
{
    private const int MaxErrorLength = 1000;

    public async Task ProcessAsync(Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await db.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId, cancellationToken);
        if (media is null || media.Status != MediaStatus.Processing) return;

        var started = Stopwatch.GetTimestamp();
        string error;
        using (var job = temp.CreateJob(media.Id))
        {
            try
            {
                var result = await RunAsync(media, job, cancellationToken);
                if (await CommitReadyAsync(media, result, cancellationToken))
                {
                    logger.LogInformation("Media {MediaId} ({Kind}) is ready: {SizeBytes} bytes, processed in {ElapsedMs} ms",
                        media.Id, media.Kind, media.SizeBytes, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    await NotifyAsync(media);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Shutdown: the row stays Processing and is re-queued on the next start.
            }
            catch (MediaProcessingException ex)
            {
                error = ex.Message;
                logger.LogWarning("Media {MediaId} ({Kind}) could not be processed: {Error}", media.Id, media.Kind, ex.Message);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
            {
                error = "The stored upload is corrupt.";
                logger.LogError(ex, "Media {MediaId} has corrupt stored chunks", media.Id);
            }
            catch (Exception ex) when (!IsTransient(ex))
            {
                error = "Internal processing error.";
                logger.LogError(ex, "Processing media {MediaId} ({Kind}) failed", media.Id, media.Kind);
            }
        }

        db.ChangeTracker.Clear();
        if (await CommitFailedAsync(media, error, cancellationToken))
            await NotifyAsync(media);
    }

    /// <summary>Last attempt after repeated transient failures (called by the worker in a fresh scope).</summary>
    public async Task MarkFailedAsync(Guid mediaId, string error, CancellationToken cancellationToken)
    {
        var media = await db.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId, cancellationToken);
        if (media is null || media.Status != MediaStatus.Processing) return;
        if (await CommitFailedAsync(media, error, cancellationToken))
            await NotifyAsync(media);
    }

    internal static bool IsTransient(Exception exception) => exception switch
    {
        NpgsqlException { IsTransient: true } => true,
        TimeoutException => true,
        DbUpdateException { InnerException: { } inner } => IsTransient(inner),
        InvalidOperationException { InnerException: { } inner } => IsTransient(inner),
        _ => false,
    };

    private async Task<ProcessedMedia> RunAsync(Media media, MediaJobDirectory job, CancellationToken cancellationToken)
    {
        if (media.Kind == MediaKind.File)
        {
            // Original bytes are sacred ("send without compression" uploads files): only verify the checksum.
            if (media.Sha256 is not null) await CopyStoredAsync(media, Stream.Null, cancellationToken);
            return ProcessedMedia.Unchanged;
        }

        var source = job.File("source");
        await using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            await CopyStoredAsync(media, file, cancellationToken);

        try
        {
            return media.Kind switch
            {
                MediaKind.Photo => await ProcessPhotoAsync(media, source, job, cancellationToken),
                MediaKind.Video => FromVideo(media, await videos.ProcessVideoAsync(source, job.Path, cancellationToken)),
                MediaKind.VideoNote => FromVideo(media, await videos.ProcessVideoNoteAsync(source, job.Path, cancellationToken)),
                MediaKind.Voice => await ProcessVoiceAsync(media, source, job, cancellationToken),
                MediaKind.Audio => await ProcessAudioAsync(media, source, cancellationToken),
                _ => ProcessedMedia.Unchanged,
            };
        }
        catch (MediaToolMissingException ex)
        {
            logger.LogWarning("{Tool} is not available ({Message}); media {MediaId} ({Kind}) is stored as uploaded",
                ex.Tool, ex.Message, media.Id, media.Kind);
            return ProcessedMedia.Unchanged;
        }
    }

    private async Task<ProcessedMedia> ProcessPhotoAsync(Media media, string source, MediaJobDirectory job, CancellationToken cancellationToken)
    {
        var photo = images.ProcessPhoto(source, job.Path);
        if (photo is null)
        {
            if (!MediaContentTypes.IsHeif(media.ContentType) && !HeifConverter.LooksLikeHeif(source))
                throw new MediaProcessingException("The image format is not supported or the file is corrupt.");
            var converted = await heif.ConvertToPngAsync(source, job.Path, cancellationToken);
            photo = images.ProcessPhoto(converted, job.Path, forceReencode: true)
                    ?? throw new MediaProcessingException("The HEIC image could not be converted.");
        }

        return new ProcessedMedia
        {
            ReplacementPath = photo.OutputPath,
            ContentType = photo.ContentType,
            // Kept bytes are stored with the detected format, which may not be what the name claims (iOS labels
            // converted photos "IMG_0001.HEIC", renamed files): the extension has to agree with the content type.
            FileName = photo.OutputPath is null
                ? MediaFileNames.WithExtensionFor(media.FileName, photo.ContentType)
                : MediaFileNames.WithExtension(media.FileName, ".jpg"),
            Width = photo.Width,
            Height = photo.Height,
            Thumbnail = photo.Thumbnails,
        };
    }

    private static ProcessedMedia FromVideo(Media media, VideoOutput video) => new()
    {
        ReplacementPath = video.Path,
        ContentType = MediaContentTypes.Mp4Video,
        FileName = MediaFileNames.WithExtension(media.FileName, ".mp4"),
        Width = video.Width,
        Height = video.Height,
        DurationMs = video.DurationMs,
        Thumbnail = video.Thumbnails,
    };

    private async Task<ProcessedMedia> ProcessVoiceAsync(Media media, string source, MediaJobDirectory job, CancellationToken cancellationToken)
    {
        var voice = await audio.ProcessVoiceAsync(source, job.Path, cancellationToken);
        return new ProcessedMedia
        {
            ReplacementPath = voice.Path,
            ContentType = MediaContentTypes.Mp4Audio,
            FileName = MediaFileNames.WithExtension(media.FileName, ".m4a"),
            DurationMs = voice.DurationMs,
            Waveform = voice.Waveform,
        };
    }

    private async Task<ProcessedMedia> ProcessAudioAsync(Media media, string source, CancellationToken cancellationToken)
    {
        try
        {
            return new ProcessedMedia { DurationMs = await audio.ProbeDurationAsync(source, cancellationToken) };
        }
        catch (MediaProcessingException ex)
        {
            // Still a perfectly downloadable file; players just will not know the duration up front.
            logger.LogInformation("Audio {MediaId} could not be probed: {Error}", media.Id, ex.Message);
            return ProcessedMedia.Unchanged;
        }
    }

    /// <summary>Decrypts the stored chunks into <paramref name="destination"/>, checking the size and the optional SHA-256.</summary>
    private async Task CopyStoredAsync(Media media, Stream destination, CancellationToken cancellationToken)
    {
        await using var stored = chunkStore.OpenRead(new MediaStorageInfo(media.Id, media.WrappedKey, media.SizeBytes, media.ChunkSize, media.ChunkCount));
        using var hash = media.Sha256 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long total = 0;
            int read;
            while ((read = await stored.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash?.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
            }
            if (total != media.SizeBytes)
                throw new MediaProcessingException("The stored upload is incomplete.");
            if (hash is not null && !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), media.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new MediaProcessingException("SHA-256 checksum mismatch: the file was corrupted during upload.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <returns>False when the row was no longer Processing (finished elsewhere); nothing was changed then.</returns>
    private async Task<bool> CommitReadyAsync(Media media, ProcessedMedia result, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var final = new Media
        {
            ContentType = result.ContentType ?? media.ContentType,
            FileName = result.FileName ?? media.FileName,
            SizeBytes = media.SizeBytes,
            ChunkCount = media.ChunkCount,
            Sha256 = media.Sha256,
            Width = result.Width ?? media.Width,
            Height = result.Height ?? media.Height,
            DurationMs = result.DurationMs ?? media.DurationMs,
            Waveform = result.Waveform ?? media.Waveform,
            HasThumbnail = result.Thumbnail is not null,
            ThumbnailWidth = result.Thumbnail?.Width,
            ThumbnailHeight = result.Thumbnail?.Height,
            MiniThumbnail = result.Thumbnail?.Mini,
        };

        using var cipher = encryption.CreateCipher(media.Id, media.WrappedKey);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (result.ReplacementPath is not null)
            {
                await db.MediaChunks.Where(c => c.MediaId == media.Id).ExecuteDeleteAsync(cancellationToken);
                await using var content = new FileStream(result.ReplacementPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (content.Length == 0) throw new MediaProcessingException("Processing produced an empty file.");
                var stored = await PostgresMediaChunkStore.WriteContentAsync(db, media.Id, cipher, content, media.ChunkSize, cancellationToken);
                final.SizeBytes = stored.SizeBytes;
                final.ChunkCount = stored.ChunkCount;
                final.Sha256 = stored.Sha256;
            }

            await db.MediaThumbnails.Where(t => t.MediaId == media.Id).ExecuteDeleteAsync(cancellationToken);
            if (result.Thumbnail is { } thumbnail)
            {
                db.MediaThumbnails.Add(new MediaThumbnail
                {
                    MediaId = media.Id,
                    Data = PostgresMediaChunkStore.ProtectThumbnail(cipher, thumbnail.Jpeg),
                    ContentType = MediaContentTypes.Jpeg,
                });
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            var updated = await db.Media
                .Where(m => m.Id == media.Id && m.Status == MediaStatus.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, MediaStatus.Ready)
                    .SetProperty(m => m.ProcessingError, (string?)null)
                    .SetProperty(m => m.UpdatedAt, now)
                    .SetProperty(m => m.ContentType, final.ContentType)
                    .SetProperty(m => m.FileName, final.FileName)
                    .SetProperty(m => m.SizeBytes, final.SizeBytes)
                    .SetProperty(m => m.ChunkCount, final.ChunkCount)
                    .SetProperty(m => m.Sha256, final.Sha256)
                    .SetProperty(m => m.Width, final.Width)
                    .SetProperty(m => m.Height, final.Height)
                    .SetProperty(m => m.DurationMs, final.DurationMs)
                    .SetProperty(m => m.Waveform, final.Waveform)
                    .SetProperty(m => m.HasThumbnail, final.HasThumbnail)
                    .SetProperty(m => m.ThumbnailWidth, final.ThumbnailWidth)
                    .SetProperty(m => m.ThumbnailHeight, final.ThumbnailHeight)
                    .SetProperty(m => m.MiniThumbnail, final.MiniThumbnail), cancellationToken);
            if (updated == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogInformation("Media {MediaId} was no longer processing; result discarded", media.Id);
                return false;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        media.Status = MediaStatus.Ready;
        media.ProcessingError = null;
        media.UpdatedAt = now;
        media.ContentType = final.ContentType;
        media.FileName = final.FileName;
        media.SizeBytes = final.SizeBytes;
        media.ChunkCount = final.ChunkCount;
        media.Sha256 = final.Sha256;
        media.Width = final.Width;
        media.Height = final.Height;
        media.DurationMs = final.DurationMs;
        media.Waveform = final.Waveform;
        media.HasThumbnail = final.HasThumbnail;
        media.ThumbnailWidth = final.ThumbnailWidth;
        media.ThumbnailHeight = final.ThumbnailHeight;
        media.MiniThumbnail = final.MiniThumbnail;
        return true;
    }

    private async Task<bool> CommitFailedAsync(Media media, string error, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var message = error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
        var updated = await db.Media
            .Where(m => m.Id == media.Id && m.Status == MediaStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MediaStatus.Failed)
                .SetProperty(m => m.ProcessingError, message)
                .SetProperty(m => m.UpdatedAt, now), cancellationToken);
        if (updated == 0) return false;

        media.Status = MediaStatus.Failed;
        media.ProcessingError = message;
        media.UpdatedAt = now;
        return true;
    }

    private async Task NotifyAsync(Media media)
    {
        try
        {
            await notifier.User(media.OwnerId).MediaProcessed(media.ToDto());
        }
        catch (Exception ex)
        {
            // The client also polls the upload, so a lost event only delays it.
            logger.LogWarning(ex, "Could not send MediaProcessed for media {MediaId}", media.Id);
        }
    }
}
