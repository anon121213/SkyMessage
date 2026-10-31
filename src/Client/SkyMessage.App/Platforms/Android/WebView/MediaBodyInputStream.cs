using Android.Runtime;
using Android.Webkit;
using Microsoft.Extensions.Logging;

namespace SkyMessage.App.Droid;

/// <summary>
/// Body of an app-origin media response (<see cref="Services.MediaProxy"/>). Android's WebView applies a request's
/// Range to an intercepted body on its own: it skip()s to the first requested byte before reading. The media source
/// already returns exactly the bytes the status line describes (206 bodies start at the range start, 200 bodies
/// are complete), so skip() discards nothing and reports the requested count; without this every seek read data
/// from twice the offset. Reads wait for the body (the WebView reads on a worker thread), and failures surface as
/// IOException — the only exception the WebView handles; any other one would crash the app.
/// </summary>
internal sealed class MediaBodyInputStream : Java.IO.InputStream
{
    private readonly Task<Stream?> _body;
    private readonly ILogger _logger;
    private int _closed;

    private MediaBodyInputStream(Task<Stream?> body, ILogger logger)
    {
        _body = body;
        _logger = logger;
    }

    public static WebResourceResponse CreateResponse(int status, string reason, IDictionary<string, string> headers, Task<Stream?> body, ILogger logger)
    {
        var mimeType = headers.TryGetValue("Content-Type", out var type) ? type : "application/octet-stream";
        // InputStreamInvoker hands the Java stream itself to the WebView (a plain Stream would be wrapped again).
        return new WebResourceResponse(mimeType, "UTF-8", status, reason, headers,
            new InputStreamInvoker(new MediaBodyInputStream(body, logger)));
    }

    public override int Read()
    {
        var one = new byte[1];
        return Read(one, 0, 1) == 1 ? one[0] : -1;
    }

    public override int Read(byte[]? buffer, int offset, int length)
    {
        if (buffer is null || length <= 0) return 0;
        try
        {
            var stream = _body.GetAwaiter().GetResult();
            var read = stream?.Read(buffer, offset, length) ?? 0;
            return read > 0 ? read : -1;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Media body read failed");
            throw new Java.IO.IOException(ex.Message);
        }
    }

    public override long Skip(long n) => Math.Max(n, 0);

    public override void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        // A request cancelled right away (seeking) may close before the body is open: dispose it once it is.
        _body.ContinueWith(static t =>
        {
            if (t.IsCompletedSuccessfully) t.Result?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
