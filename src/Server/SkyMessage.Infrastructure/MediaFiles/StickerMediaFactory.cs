using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Application.Stickers;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using SkyMessage.Infrastructure.MediaFiles.Storage;
using SkyMessage.Infrastructure.Persistence;

namespace SkyMessage.Infrastructure.MediaFiles;

/// <summary>
/// Sticker images (<see cref="IStickerMediaFactory"/>): the creator's upload is read back from the chunk store, made into
/// a WebP by <see cref="StickerImageProcessor"/> in memory (at most 5 MB in), then stored as a media of its own (encrypted
/// at rest like every other) carrying the sticker's attributes, with a WebP preview as its thumbnail.
/// </summary>
internal sealed class StickerMediaFactory(
    AppDbContext db,
    IMediaChunkStore chunks,
    MediaEncryption encryption,
    IClock clock,
    IOptions<StorageOptions> storage,
    ILogger<StickerMediaFactory> logger) : IStickerMediaFactory
{
    public async Task<PreparedSticker> PrepareAsync(Guid ownerId, Guid sourceMediaId, CancellationToken cancellationToken)
    {
        var source = await db.Media.AsNoTracking()
            .Where(m => m.Id == sourceMediaId && m.OwnerId == ownerId && m.Status == MediaStatus.Ready)
            .Select(m => new { m.Id, m.WrappedKey, m.SizeBytes, m.ChunkSize, m.ChunkCount })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.MediaNotFound);
        if (source.SizeBytes is <= 0 or > Limits.StickerSourceMaxBytes)
            throw new BadRequestException(ErrorCodes.StickerImageInvalid, $"A sticker's image must be at most {Limits.StickerSourceMaxBytes / (1024 * 1024)} MB.");

        var bytes = new byte[source.SizeBytes];
        await using (var stream = chunks.OpenRead(new MediaStorageInfo(source.Id, source.WrappedKey, source.SizeBytes, source.ChunkSize, source.ChunkCount)))
            await stream.ReadExactlyAsync(bytes, cancellationToken);

        StickerImage image;
        try
        {
            image = StickerImageProcessor.Process(bytes);
        }
        catch (StickerImageException ex)
        {
            logger.LogInformation("Upload {MediaId} cannot become a sticker: {Reason}", sourceMediaId, ex.Message);
            throw new BadRequestException(ErrorCodes.StickerImageInvalid, ex.Message);
        }
        return new PreparedSticker(sourceMediaId, image.Webp, image.Width, image.Height, image.Thumbnail, image.ThumbnailWidth, image.ThumbnailHeight);
    }

    public async Task<Guid> StoreAsync(PreparedSticker prepared, Guid ownerId, StickerInfoDto sticker, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var chunkSize = storage.Value.ChunkSize;
        var media = new Media
        {
            OwnerId = ownerId,
            Kind = MediaKind.Photo,
            Status = MediaStatus.Ready,
            FileName = "sticker.webp",
            ContentType = MediaContentTypes.Webp,
            SizeBytes = prepared.Webp.Length,
            DeclaredSizeBytes = prepared.Webp.Length,
            ChunkSize = chunkSize,
            ChunkCount = ChunkMath.ChunkCount(prepared.Webp.Length, chunkSize),
            Width = prepared.Width,
            Height = prepared.Height,
            HasThumbnail = true,
            ThumbnailWidth = prepared.ThumbnailWidth,
            ThumbnailHeight = prepared.ThumbnailHeight,
            StickerPackId = sticker.PackId,
            StickerId = sticker.StickerId,
            StickerEmoji = sticker.Emoji,
            CreatedAt = now,
            CompletedAt = now,
            UpdatedAt = now,
        };
        media.WrappedKey = encryption.CreateWrappedKey(media.Id);
        db.Media.Add(media);
        // The row first: its chunks and thumbnail point at it.
        await db.SaveChangesAsync(cancellationToken);

        using var cipher = encryption.CreateCipher(media.Id, media.WrappedKey);
        await using (var content = new MemoryStream(prepared.Webp, writable: false))
        {
            var stored = await PostgresMediaChunkStore.WriteContentAsync(db, media.Id, cipher, content, chunkSize, cancellationToken);
            media.SizeBytes = stored.SizeBytes;
            media.ChunkCount = stored.ChunkCount;
            media.Sha256 = stored.Sha256;
        }
        db.MediaThumbnails.Add(new MediaThumbnail
        {
            MediaId = media.Id,
            Data = PostgresMediaChunkStore.ProtectThumbnail(cipher, prepared.Thumbnail),
            ContentType = MediaContentTypes.Webp,
        });
        await db.SaveChangesAsync(cancellationToken);
        return media.Id;
    }

    public async Task DropSourceAsync(Guid ownerId, Guid sourceMediaId)
    {
        try
        {
            await db.Media
                .Where(m => m.Id == sourceMediaId && m.OwnerId == ownerId && m.StickerId == null
                            && !db.MessageAttachments.Any(a => a.MediaId == m.Id)
                            && !db.Users.Any(u => u.AvatarMediaId == m.Id || u.CoverMediaId == m.Id)
                            && !db.Chats.Any(c => c.AvatarMediaId == m.Id)
                            && !db.AvatarPhotos.Any(a => a.MediaId == m.Id)
                            && !db.Stickers.Any(s => s.MediaId == m.Id)
                            && !db.SavedGifs.Any(g => g.MediaId == m.Id))
                .ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "The upload {MediaId} a sticker was made from is left to media cleanup", sourceMediaId);
        }
    }
}
