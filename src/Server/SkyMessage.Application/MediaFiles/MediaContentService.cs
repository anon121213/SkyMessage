using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Mapping;
using SkyMessage.Contracts;

namespace SkyMessage.Application.MediaFiles;

/// <summary>Media metadata and bytes for viewers allowed by <see cref="IMediaAccessService"/>; everything else is 404.</summary>
public interface IMediaContentService
{
    /// <summary>
    /// The viewer of an anonymous-endpoint request: the bearer user when an access token was sent, otherwise the
    /// owner of a valid <c>?t=</c> signature for this media id. Throws 401 when there is neither.
    /// </summary>
    Task<Guid> ResolveViewerAsync(Guid mediaId, string? signature, CancellationToken cancellationToken = default);

    /// <summary>Metadata for the current (bearer) user.</summary>
    Task<MediaDto> GetAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>The stored content (Ready only; the owner gets 409 <c>media_not_ready</c> before that).</summary>
    Task<MediaContentResult> OpenContentAsync(Guid viewerId, Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>The ≤480 px JPEG preview; photos without one fall back to the image itself.</summary>
    Task<MediaContentResult> OpenThumbnailAsync(Guid viewerId, Guid mediaId, CancellationToken cancellationToken = default);
}

/// <summary>A readable media response. The caller owns (and must dispose) <see cref="Content"/>.</summary>
public sealed record MediaContentResult
{
    /// <summary>Seekable stream (HTTP Range support).</summary>
    public required Stream Content { get; init; }

    /// <summary>Content-Type to send: <c>application/octet-stream</c> for dangerous types.</summary>
    public required string ContentType { get; init; }

    public required string FileName { get; init; }

    /// <summary>Must be sent as an attachment whatever the client asked (files and dangerous types).</summary>
    public required bool ForceAttachment { get; init; }

    /// <summary>Opaque strong validator without quotes; the bytes behind it never change.</summary>
    public required string ETag { get; init; }
}

internal sealed class MediaContentService(
    IAppDbContext db,
    ICurrentUser currentUser,
    ISessionValidator sessions,
    IMediaAccessService access,
    IMediaChunkStore chunks) : IMediaContentService
{
    public async Task<Guid> ResolveViewerAsync(Guid mediaId, string? signature, CancellationToken cancellationToken = default)
    {
        if (currentUser.IsAuthenticated) return currentUser.UserId;
        if (!string.IsNullOrEmpty(signature)
            && await sessions.ValidateMediaSignatureAsync(signature, mediaId, cancellationToken) is { } session)
            return session.UserId;
        throw new UnauthorizedException();
    }

    public async Task<MediaDto> GetAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        var viewerId = currentUser.UserId;
        var media = await db.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId, cancellationToken);
        if (media is null
            || (media.OwnerId != viewerId
                && (media.Status != MediaStatus.Ready || !await access.CanViewAsync(viewerId, media.Id, media.OwnerId, cancellationToken))))
            throw new NotFoundException(ErrorCodes.MediaNotFound);
        return media.ToDto();
    }

    public async Task<MediaContentResult> OpenContentAsync(Guid viewerId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        var media = await LoadReadableAsync(viewerId, mediaId, cancellationToken);
        return ContentOf(media);
    }

    public async Task<MediaContentResult> OpenThumbnailAsync(Guid viewerId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        var media = await LoadReadableAsync(viewerId, mediaId, cancellationToken);
        if (media.HasThumbnail)
        {
            var stored = await db.MediaThumbnails.AsNoTracking()
                .Where(t => t.MediaId == mediaId)
                .Select(t => new { t.Data, t.ContentType })
                .FirstOrDefaultAsync(cancellationToken);
            if (stored is not null)
            {
                var bytes = chunks.ReadThumbnail(media.Id, media.WrappedKey, stored.Data);
                return new MediaContentResult
                {
                    Content = new MemoryStream(bytes, writable: false),
                    ContentType = MediaContentTypes.IsInlineImage(stored.ContentType) ? stored.ContentType : MediaContentTypes.Jpeg,
                    FileName = MediaFileNames.WithExtension(media.FileName, ".jpg"),
                    ForceAttachment = false,
                    ETag = $"{media.Id:N}.t{media.UpdatedAt.UtcTicks:x}",
                };
            }
        }

        // Photos kept exactly as uploaded without a preview (e.g. processing tools were unavailable) are their own thumbnail.
        if (media.Kind == MediaKind.Photo && MediaContentTypes.IsInlineImage(media.ContentType))
            return ContentOf(media);

        throw new NotFoundException(ErrorCodes.MediaNotFound, "This media has no thumbnail.");
    }

    private MediaContentResult ContentOf(ReadableMedia media)
    {
        var dangerous = MediaContentTypes.IsDangerous(media.ContentType);
        return new MediaContentResult
        {
            Content = chunks.OpenRead(new MediaStorageInfo(media.Id, media.WrappedKey, media.SizeBytes, media.ChunkSize, media.ChunkCount)),
            ContentType = dangerous ? MediaContentTypes.OctetStream : media.ContentType,
            FileName = media.FileName,
            ForceAttachment = dangerous || media.Kind == MediaKind.File,
            ETag = $"{media.Id:N}.{media.UpdatedAt.UtcTicks:x}",
        };
    }

    private async Task<ReadableMedia> LoadReadableAsync(Guid viewerId, Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await db.Media.AsNoTracking()
            .Where(m => m.Id == mediaId)
            .Select(m => new ReadableMedia(m.Id, m.OwnerId, m.Kind, m.Status, m.FileName, m.ContentType, m.SizeBytes,
                m.ChunkSize, m.ChunkCount, m.WrappedKey, m.HasThumbnail, m.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken);
        if (media is null)
            throw new NotFoundException(ErrorCodes.MediaNotFound);

        if (media.OwnerId != viewerId)
        {
            // Only Ready media can be attached or set as an avatar, so anything else is invisible to non-owners.
            if (media.Status != MediaStatus.Ready || !await access.CanViewAsync(viewerId, media.Id, media.OwnerId, cancellationToken))
                throw new NotFoundException(ErrorCodes.MediaNotFound);
        }
        else if (media.Status != MediaStatus.Ready)
        {
            throw new ConflictException(ErrorCodes.MediaNotReady, "The media is not processed yet.");
        }

        return media;
    }

    private sealed record ReadableMedia(
        Guid Id,
        Guid OwnerId,
        MediaKind Kind,
        MediaStatus Status,
        string FileName,
        string ContentType,
        long SizeBytes,
        int ChunkSize,
        int ChunkCount,
        byte[]? WrappedKey,
        bool HasThumbnail,
        DateTimeOffset UpdatedAt);
}
