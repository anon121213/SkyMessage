using System.Net;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>An open media response. Disposing the body releases the HTTP response.</summary>
internal sealed record MediaFetchResponse(
    HttpStatusCode StatusCode,
    Stream Body,
    long? ContentLength,
    long? RangeStart,
    long? TotalLength,
    string? ContentType) : IDisposable
{
    public bool IsPartial => StatusCode == HttpStatusCode.PartialContent;

    public void Dispose() => Body.Dispose();
}

/// <summary>
/// Authorized media fetches (bearer header, never a signed URL) through the failover-aware transport. Bodies are
/// wrapped in <see cref="StallDetectingStream"/>: a read without progress for 15 s aborts and is reported to the
/// endpoint service, which fails over when stalls repeat.
/// </summary>
internal sealed class MediaFetcher(AuthorizedSender sender, IEndpointRouting endpoints, TimeProvider time)
{
    public async Task<MediaFetchResponse> OpenAsync(Guid mediaId, bool thumbnail, string? rangeHeader, bool download, CancellationToken cancellationToken)
    {
        var path = thumbnail ? ApiRoutes.Media.ThumbnailPath(mediaId, null) : ApiRoutes.Media.ContentPath(mediaId, null, download);
        var request = ApiRequest.Get(path, stream: true, range: rangeHeader, timeout: TransferDefaults.HeadersTimeout);

        var response = await sender.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            var content = response.Content;
            var body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var range = content.Headers.ContentRange;
            var watched = new StallDetectingStream(body, response, time, TransferDefaults.StallTimeout, endpoints.ReportStall);
            return new MediaFetchResponse(
                response.StatusCode,
                watched,
                content.Headers.ContentLength,
                response.StatusCode == HttpStatusCode.PartialContent ? range?.From : null,
                range?.Length ?? (response.StatusCode == HttpStatusCode.OK ? content.Headers.ContentLength : null),
                content.Headers.ContentType?.MediaType);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Reads a whole (small) body into memory, failing when it exceeds <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]> ReadAllAsync(Stream body, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("The media body is larger than expected.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

/// <summary>Safe content types and file extensions for media served on the app origin or saved to disk.</summary>
internal static class MediaTypes
{
    // Path.GetInvalidFileNameChars is platform-specific; files may later be shared to Windows machines.
    private static readonly char[] UnsafeNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0'];

    /// <summary>
    /// Types rendered inline. Everything else (html, svg, xml, js, pdf, ...) is served as an opaque attachment: the
    /// app origin is privileged, so user-uploaded active content must never execute there.
    /// </summary>
    public static (string ContentType, bool Attachment) Safe(string? contentType)
    {
        var type = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (type is "image/jpeg" or "image/png" or "image/webp" or "image/gif" or "image/heic" or "image/heif" or "image/avif" or "image/bmp")
            return (type, false);
        if (type.StartsWith("video/", StringComparison.Ordinal) || type.StartsWith("audio/", StringComparison.Ordinal))
            return (type, false);
        return ("application/octet-stream", true);
    }

    public static string Extension(string? contentType) => (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/heic" => ".heic",
        "image/heif" => ".heif",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
        "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/webm" => ".webm",
        "audio/wav" or "audio/x-wav" => ".wav",
        "application/pdf" => ".pdf",
        _ => ".bin",
    };

    /// <summary>A file name for the cache/save: the media's own name, or its id with an extension from the type.</summary>
    public static string FileNameFor(MediaDto media)
    {
        var name = Path.GetFileName(media.FileName ?? "").Trim();
        if (name.Length == 0) name = media.Id.ToString("N") + Extension(media.ContentType);
        foreach (var invalid in Path.GetInvalidFileNameChars().Concat(UnsafeNameChars)) name = name.Replace(invalid, '_');
        return name.Length > Limits.FileNameMaxLength ? name[^Limits.FileNameMaxLength..] : name;
    }
}
