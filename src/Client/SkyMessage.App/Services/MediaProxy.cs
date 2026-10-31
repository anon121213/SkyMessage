using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Services;

namespace SkyMessage.App.Services;

/// <summary>
/// Serves app-origin media ("/_sm/media/...") to the WebView through <see cref="IMediaContentSource"/>.
/// <see cref="BlazorWebView.WebResourceRequested"/> is synchronous: the status line and headers are computed
/// without I/O and the body is handed over as a task that is never awaited here (Android blocks its network
/// thread on the stream; Apple delivers it when the task completes). Bodies are already sliced to the requested
/// range; on Android they go through <c>MediaBodyInputStream</c> so the WebView does not apply the range again.
/// </summary>
public sealed class MediaProxy(IServiceProvider services, ILogger<MediaProxy> logger)
{
    private IMediaContentSource? _source;

    private IMediaContentSource? Source => _source ??= services.GetService<IMediaContentSource>();

    /// <returns>True when the request was ours and a response was set.</returns>
    public bool TryHandle(WebViewWebResourceRequestedEventArgs e, CancellationToken lifetime)
    {
        var uri = e.Uri;
        if (uri is null || !uri.IsAbsoluteUri || !string.Equals(uri.Host, "0.0.0.1", StringComparison.OrdinalIgnoreCase)) return false;

        var source = Source;
        if (source is null) return false;
        var path = uri.PathAndQuery;
        if (!path.StartsWith(source.PathPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            if (!source.CanHandle(path)) return false;

            var range = Header(e.Headers, "Range");
            var head = source.GetHead(path, range);
            var status = head.StatusCode;
            var reason = string.IsNullOrWhiteSpace(head.ReasonPhrase) ? "OK" : head.ReasonPhrase;
            if (status is < 100 or > 599 or (>= 300 and <= 399))
            {
                // Android's WebResourceResponse rejects redirects and invalid codes outright.
                status = 502;
                reason = "Bad Gateway";
            }

            var headers = new Dictionary<string, string>(head.Headers, StringComparer.OrdinalIgnoreCase);
            headers.TryAdd("Content-Type", "application/octet-stream");

            e.Handled = true;
            var hasBody = status is >= 200 and < 300 and not 204
                          && !string.Equals(e.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
            if (!hasBody) e.SetResponse(status, reason, headers, Stream.Null);
#if ANDROID
            // The WebView skips a ranged body to the first requested byte itself; ours already starts there.
            else if (e.PlatformArgs is { } platform)
                platform.Response = SkyMessage.App.Droid.MediaBodyInputStream.CreateResponse(status, reason, headers,
                    OpenBodyAsync(source, path, range, lifetime), logger);
#endif
            else e.SetResponse(status, reason, headers, OpenBodyAsync(source, path, range, lifetime));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Media proxy failed for an app-origin request");
            if (!e.Handled)
            {
                e.Handled = true;
                e.SetResponse(500, "Internal Server Error", new Dictionary<string, string> { ["Content-Type"] = "text/plain" }, Stream.Null);
            }
            return true;
        }
    }

    private async Task<Stream?> OpenBodyAsync(IMediaContentSource source, string path, string? range, CancellationToken cancellationToken)
    {
        try
        {
            return await source.OpenBodyAsync(path, range, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The status line is already sent; an empty body ends the response and the element retries.
            logger.LogDebug(ex, "Media body failed");
            return Stream.Null;
        }
    }

    private static string? Header(IReadOnlyDictionary<string, string>? headers, string name)
    {
        if (headers is null) return null;
        if (headers.TryGetValue(name, out var value)) return value;
        foreach (var (key, v) in headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }
}
