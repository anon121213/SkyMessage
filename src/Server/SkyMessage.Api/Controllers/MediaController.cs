using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

/// <summary>
/// Chunked uploads, media metadata and content (SPEC §6.4). Content and thumbnails are anonymous endpoints so
/// &lt;img&gt;/&lt;video&gt; can load them with a per-file <c>?t=</c> signature instead of an Authorization header.
/// </summary>
[ApiController]
public sealed class MediaController(IMediaUploadService uploads, IMediaContentService media) : ControllerBase
{
    // Content behind a media id never changes once it is Ready.
    private const string ImmutableCacheControl = "private, max-age=31536000, immutable";

    [HttpPost(ApiRoutes.Media.Uploads)]
    public Task<UploadSessionDto> CreateUpload(CreateUploadRequest request, CancellationToken cancellationToken) =>
        uploads.CreateAsync(request, cancellationToken);

    [HttpGet(ApiRoutes.Media.Upload)]
    public Task<UploadSessionDto> GetUpload(Guid mediaId, CancellationToken cancellationToken) =>
        uploads.GetAsync(mediaId, cancellationToken);

    /// <summary>Raw chunk bytes (application/octet-stream) of exactly the expected length; retries overwrite.</summary>
    [HttpPut(ApiRoutes.Media.UploadChunk)]
    [RequestSizeLimit(MediaUploadLimits.MaxChunkRequestBytes)]
    public async Task<IActionResult> PutChunk(Guid mediaId, int index, CancellationToken cancellationToken)
    {
        await uploads.PutChunkAsync(mediaId, index, Request.Body, Request.ContentLength, cancellationToken);
        return NoContent();
    }

    [HttpPost(ApiRoutes.Media.UploadComplete)]
    public Task<MediaDto> CompleteUpload(Guid mediaId, CancellationToken cancellationToken) =>
        uploads.CompleteAsync(mediaId, cancellationToken);

    [HttpGet(ApiRoutes.Media.ById)]
    public Task<MediaDto> Get(Guid mediaId, CancellationToken cancellationToken) =>
        media.GetAsync(mediaId, cancellationToken);

    /// <summary>Supports HTTP Range. <c>download=1</c> forces an attachment.</summary>
    [AllowAnonymous]
    [HttpGet(ApiRoutes.Media.Content)]
    public async Task<IActionResult> GetContent(Guid mediaId, [FromQuery] string? t, [FromQuery] string? download, CancellationToken cancellationToken)
    {
        var viewerId = await media.ResolveViewerAsync(mediaId, t, cancellationToken);
        var content = await media.OpenContentAsync(viewerId, mediaId, cancellationToken);
        return Serve(content, asAttachment: content.ForceAttachment || IsSet(download));
    }

    [AllowAnonymous]
    [HttpGet(ApiRoutes.Media.Thumbnail)]
    public async Task<IActionResult> GetThumbnail(Guid mediaId, [FromQuery] string? t, CancellationToken cancellationToken)
    {
        var viewerId = await media.ResolveViewerAsync(mediaId, t, cancellationToken);
        var thumbnail = await media.OpenThumbnailAsync(viewerId, mediaId, cancellationToken);
        return Serve(thumbnail, asAttachment: thumbnail.ForceAttachment);
    }

    private FileStreamResult Serve(MediaContentResult content, bool asAttachment)
    {
        var headers = Response.Headers;
        headers.CacheControl = ImmutableCacheControl;
        headers.XContentTypeOptions = "nosniff";
        // Opened directly in a tab, the response is a unique opaque origin without scripts.
        headers.ContentSecurityPolicy = "sandbox";

        // filename= (ASCII fallback) + filename*=UTF-8''… (RFC 5987/6266) for non-ASCII names.
        var disposition = new ContentDispositionHeaderValue(asAttachment ? "attachment" : "inline");
        disposition.SetHttpFileName(content.FileName);
        headers.ContentDisposition = disposition.ToString();

        return File(content.Content, content.ContentType, lastModified: null,
            entityTag: new EntityTagHeaderValue($"\"{content.ETag}\""), enableRangeProcessing: true);
    }

    private static bool IsSet(string? flag) =>
        flag is not null && (flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase));
}
