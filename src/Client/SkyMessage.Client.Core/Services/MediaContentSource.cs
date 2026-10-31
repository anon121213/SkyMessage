using System.Globalization;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Client.Core.Services;

/// <summary>A parsed app-origin media path ("/_sm/media/{id}/content?s=&amp;ct=&amp;dl=" or "/_sm/media/{id}/thumbnail").</summary>
internal readonly record struct AppMediaPath(Guid MediaId, bool Thumbnail, long? Size, string? ContentType, bool Download)
{
    public static bool TryParse(string? appRelativePath, out AppMediaPath result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(appRelativePath)) return false;
        var text = appRelativePath.Trim();

        // Hosts may hand over the full app-origin URL.
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var absolute)) return false;
            text = absolute.PathAndQuery;
        }
        if (!text.StartsWith('/')) text = "/" + text;
        if (!text.StartsWith(MediaUrlBuilder.AppProxyPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var queryStart = text.IndexOf('?');
        var path = queryStart >= 0 ? text[..queryStart] : text;
        var query = queryStart >= 0 ? text[(queryStart + 1)..] : "";

        var segments = path[MediaUrlBuilder.AppProxyPrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !Guid.TryParse(segments[0], out var mediaId) || mediaId == Guid.Empty) return false;
        bool thumbnail;
        if (segments[1].Equals("content", StringComparison.OrdinalIgnoreCase)) thumbnail = false;
        else if (segments[1].Equals("thumbnail", StringComparison.OrdinalIgnoreCase)) thumbnail = true;
        else return false;

        long? size = null;
        string? contentType = null;
        var download = false;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq >= 0 ? pair[..eq] : pair;
            var value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
            switch (name)
            {
                case "s" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0:
                    size = s;
                    break;
                case "ct" when value.Length is > 0 and <= 200:
                    contentType = value;
                    break;
                case "dl":
                    download = value is "1" or "true";
                    break;
            }
        }

        result = new AppMediaPath(mediaId, thumbnail, size, contentType, download);
        return true;
    }
}

/// <summary>
/// App-origin media for the MAUI WebView (SPEC §8.1 Media URLs, §8.3 media proxy). <see cref="GetHead"/> is
/// synchronous and I/O-free: size and type travel in the URL, so 200/206/416 and Content-Range are computed
/// up front. The body comes from <see cref="IMediaFileStore"/> or a ranged, authorized server fetch (failover,
/// stall detection). Thumbnails and small media are written through the file cache (trimmed to CacheLimitMb by
/// <see cref="MediaCacheTrimmer"/>). Active content types are served as opaque attachments with a sandbox CSP — the
/// app origin is privileged.
/// </summary>
internal sealed class MediaContentSource(MediaFetcher fetcher, IMediaFileStore files, MediaCacheTrimmer trimmer, ILogger<MediaContentSource> logger)
    : IMediaContentSource
{
    /// <summary>Media up to this size are fetched whole and cached (photos, voice notes, short clips).</summary>
    internal const long SmallMediaBytes = 2 * 1024 * 1024;
    private const long MaxThumbnailBytes = 8 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> NotFoundHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "text/plain", ["Cache-Control"] = "no-store" };

    public string PathPrefix => MediaUrlBuilder.AppProxyPrefix;

    public bool CanHandle(string appRelativePath) => AppMediaPath.TryParse(appRelativePath, out _);

    public MediaResponseHead GetHead(string appRelativePath, string? rangeHeader)
    {
        try
        {
            if (!AppMediaPath.TryParse(appRelativePath, out var path))
                return new MediaResponseHead(404, "Not Found", NotFoundHeaders);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cache-Control"] = "no-store",
                ["X-Content-Type-Options"] = "nosniff",
                ["Content-Security-Policy"] = "sandbox",
            };

            if (path.Thumbnail)
            {
                headers["Content-Type"] = "image/jpeg";
                return new MediaResponseHead(200, "OK", headers);
            }

            var (contentType, attachment) = MediaTypes.Safe(path.ContentType);
            headers["Content-Type"] = contentType;
            if (attachment || path.Download) headers["Content-Disposition"] = "attachment";
            if (path.Size is not { } size) return new MediaResponseHead(200, "OK", headers);

            headers["Accept-Ranges"] = "bytes";
            switch (HttpRange.Parse(rangeHeader, size, out var range))
            {
                case RangeOutcome.Partial:
                    headers["Content-Range"] = range.ContentRange(size);
                    headers["Content-Length"] = range.Length.ToString(CultureInfo.InvariantCulture);
                    return new MediaResponseHead(206, "Partial Content", headers);
                case RangeOutcome.Unsatisfiable:
                    headers["Content-Range"] = $"bytes */{size.ToString(CultureInfo.InvariantCulture)}";
                    headers["Content-Length"] = "0";
                    return new MediaResponseHead(416, "Range Not Satisfiable", headers);
                default:
                    headers["Content-Length"] = size.ToString(CultureInfo.InvariantCulture);
                    return new MediaResponseHead(200, "OK", headers);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not compute a media response head");
            return new MediaResponseHead(404, "Not Found", NotFoundHeaders);
        }
    }

    public async Task<Stream> OpenBodyAsync(string appRelativePath, string? rangeHeader, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!AppMediaPath.TryParse(appRelativePath, out var path)) return EmptyStream();
            if (path.Thumbnail) return await OpenThumbnailAsync(path.MediaId, cancellationToken).ConfigureAwait(false);
            return await OpenContentAsync(path, rangeHeader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The head is already on its way: a failure just ends the body.
            logger.LogDebug(ex, "Media body failed");
            return EmptyStream();
        }
    }

    private async Task<Stream> OpenThumbnailAsync(Guid mediaId, CancellationToken ct)
    {
        if (await OpenCachedAsync(mediaId, thumbnail: true).ConfigureAwait(false) is { } cached) return cached;

        byte[] bytes;
        using (var response = await fetcher.OpenAsync(mediaId, thumbnail: true, rangeHeader: null, download: false, ct).ConfigureAwait(false))
            bytes = await MediaFetcher.ReadAllAsync(response.Body, MaxThumbnailBytes, ct).ConfigureAwait(false);

        await SaveToCacheAsync(mediaId, thumbnail: true, $"{mediaId:N}.jpg", bytes, ct).ConfigureAwait(false);
        return new MemoryStream(bytes, writable: false);
    }

    private async Task<Stream> OpenContentAsync(AppMediaPath path, string? rangeHeader, CancellationToken ct)
    {
        var mediaId = path.MediaId;
        ByteRange? range = null;
        if (path.Size is { } size)
        {
            switch (HttpRange.Parse(rangeHeader, size, out var parsed))
            {
                case RangeOutcome.Unsatisfiable:
                    return EmptyStream();
                case RangeOutcome.Partial:
                    range = parsed;
                    break;
            }
        }

        if (await OpenCachedAsync(mediaId, thumbnail: false).ConfigureAwait(false) is { } cached)
            return range is { } r ? Slice(cached, r) : cached;

        // Small media: one whole fetch, cached for every later request (scrolling back, replays).
        if (files.IsSupported && path.Size is { } known && known <= SmallMediaBytes)
        {
            byte[] bytes;
            using (var response = await fetcher.OpenAsync(mediaId, thumbnail: false, rangeHeader: null, download: false, ct).ConfigureAwait(false))
                bytes = await MediaFetcher.ReadAllAsync(response.Body, SmallMediaBytes * 2, ct).ConfigureAwait(false);
            await SaveToCacheAsync(mediaId, thumbnail: false, mediaId.ToString("N") + MediaTypes.Extension(path.ContentType), bytes, ct).ConfigureAwait(false);
            if (range is not { } slice) return new MemoryStream(bytes, writable: false);
            if (slice.Start >= bytes.Length) return EmptyStream();
            var length = (int)Math.Min(slice.Length, bytes.Length - slice.Start);
            return new MemoryStream(bytes, (int)slice.Start, length, writable: false);
        }

        var fetched = await fetcher.OpenAsync(mediaId, thumbnail: false, range?.RangeHeader(), path.Download, ct).ConfigureAwait(false);
        if (range is not { } requested) return fetched.Body;

        if (fetched.IsPartial && fetched.RangeStart is { } start && start <= requested.Start)
            return new BoundedReadStream(fetched.Body, requested.Start - start, requested.Length);
        // The server ignored the range (200): skip to the requested offset ourselves.
        return new BoundedReadStream(fetched.Body, requested.Start, requested.Length);
    }

    private async Task<Stream?> OpenCachedAsync(Guid mediaId, bool thumbnail)
    {
        if (!files.IsSupported) return null;
        try
        {
            var localPath = await files.GetLocalPathAsync(mediaId, thumbnail).ConfigureAwait(false);
            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return null;
            return new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cached media is unreadable");
            return null;
        }
    }

    private async Task SaveToCacheAsync(Guid mediaId, bool thumbnail, string fileName, byte[] bytes, CancellationToken ct)
    {
        if (!files.IsSupported || bytes.Length == 0) return;
        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            await files.SaveAsync(mediaId, thumbnail, fileName, content, ct).ConfigureAwait(false);
            trimmer.Request();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not cache media {MediaId}", mediaId);
        }
    }

    private static Stream Slice(Stream stream, ByteRange range)
    {
        if (stream.CanSeek)
        {
            stream.Seek(range.Start, SeekOrigin.Begin);
            return new BoundedReadStream(stream, 0, range.Length);
        }
        return new BoundedReadStream(stream, range.Start, range.Length);
    }

    private static MemoryStream EmptyStream() => new([], writable: false);
}
