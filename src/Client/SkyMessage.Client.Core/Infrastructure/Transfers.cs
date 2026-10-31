using System.Net.Http.Headers;

namespace SkyMessage.Client.Core.Infrastructure;

internal static class TransferDefaults
{
    /// <summary>A pending read/write with no progress for this long is a stall (SPEC: failover hardening).</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Time allowed for response headers of media/download requests.</summary>
    public static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Upload bytes whose progress is invisible: browsers buffer request bodies (fetch), and on native stacks a write
    /// returns once the kernel's send buffer (hundreds of KB on phones) took the slice, so the tail of a written body
    /// is still on its way. Allow the stall timeout plus the time a slow 16 KB/s link needs for the payload.
    /// </summary>
    public static TimeSpan BufferedUploadTimeout(long bytes) => StallTimeout + TimeSpan.FromSeconds(bytes / 16384.0);
}

/// <summary>
/// Wraps a network stream and aborts a read that makes no progress for the stall timeout. Only time spent
/// inside a pending read counts, so a consumer that pauses (a &lt;video&gt; with a full buffer) never
/// trips it. A stall throws <see cref="IOException"/> and reports through <paramref name="onStall"/>.
/// </summary>
internal sealed class StallDetectingStream : Stream
{
    private readonly Stream _inner;
    private readonly IDisposable? _owner;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly Action? _onStall;
    private readonly Action<int>? _onBytes;
    private readonly CancellationTokenSource _abort = new();
    private readonly ITimer _timer;
    private long _pendingSince;
    private volatile bool _stalled;
    private int _disposed;

    public StallDetectingStream(Stream inner, IDisposable? owner, TimeProvider time, TimeSpan timeout, Action? onStall, Action<int>? onBytes = null)
    {
        _inner = inner;
        _owner = owner;
        _time = time;
        _timeout = timeout;
        _onStall = onStall;
        _onBytes = onBytes;
        var period = TimeSpan.FromSeconds(1);
        _timer = time.CreateTimer(static s => ((StallDetectingStream)s!).Check(), this, period, period);
    }

    public bool Stalled => _stalled;

    private void Check()
    {
        var since = Interlocked.Read(ref _pendingSince);
        if (since == 0 || _stalled) return;
        if (_time.GetElapsedTime(since) < _timeout) return;
        _stalled = true;
        try
        {
            _onStall?.Invoke();
        }
        catch
        {
            // Reporting is best effort.
        }
        try
        {
            _abort.Cancel();
            // A synchronous read (native WebView IO thread) cannot observe the token: disposing breaks it.
            _inner.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_stalled) throw new IOException("The transfer stalled.");
        Interlocked.Exchange(ref _pendingSince, _time.GetTimestamp());
        try
        {
            int read;
            if (cancellationToken.CanBeCanceled)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abort.Token);
                read = await _inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            else
            {
                read = await _inner.ReadAsync(buffer, _abort.Token).ConfigureAwait(false);
            }
            if (read > 0) _onBytes?.Invoke(read);
            return read;
        }
        catch (OperationCanceledException) when (_stalled && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The transfer stalled.");
        }
        finally
        {
            Interlocked.Exchange(ref _pendingSince, 0);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Core never reads synchronously (WASM-safe); native WebViews do, on their own IO thread.
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_stalled) throw new IOException("The transfer stalled.");
        Interlocked.Exchange(ref _pendingSince, _time.GetTimestamp());
        try
        {
            var read = _inner.Read(buffer, offset, count);
            if (read > 0) _onBytes?.Invoke(read);
            return read;
        }
        catch (Exception ex) when (_stalled && ex is ObjectDisposedException or IOException or OperationCanceledException)
        {
            throw new IOException("The transfer stalled.");
        }
        finally
        {
            Interlocked.Exchange(ref _pendingSince, 0);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.CanSeek ? _inner.Length : throw new NotSupportedException();
    public override long Position { get => _inner.CanSeek ? _inner.Position : 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
            _abort.Dispose();
            _inner.Dispose();
            _owner?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            _abort.Dispose();
            await _inner.DisposeAsync().ConfigureAwait(false);
            _owner?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Upload body written in small slices so progress (and stalls) are observable on native stacks. A stall
/// watchdog cancels its token when no slice was accepted for the stall timeout; after the body is written
/// (<c>onWritten</c>) the same watchdog bounds the wait for the response headers, which also covers the tail
/// of the body still in the kernel's send buffer.
/// </summary>
internal sealed class ProgressContent : HttpContent
{
    private const int SliceSize = 16 * 1024;
    private readonly ReadOnlyMemory<byte> _data;
    private readonly Action<long>? _progress;
    private readonly Action? _onProgress;
    private readonly Action? _onWritten;

    public ProgressContent(ReadOnlyMemory<byte> data, Action<long>? progress, Action? onProgress, Action? onWritten = null)
    {
        _data = data;
        _progress = progress;
        _onProgress = onProgress;
        _onWritten = onWritten;
        Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < _data.Length)
        {
            var count = Math.Min(SliceSize, _data.Length - offset);
            await stream.WriteAsync(_data.Slice(offset, count), cancellationToken).ConfigureAwait(false);
            offset += count;
            _onProgress?.Invoke();
            _progress?.Invoke(offset);
        }
        _onWritten?.Invoke();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _data.Length;
        return true;
    }
}

/// <summary>Restartable inactivity watchdog: cancels <see cref="Token"/> unless <see cref="Touch()"/> is called within the timeout.</summary>
internal sealed class StallWatchdog : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly ITimer _timer;
    private readonly TimeProvider _time;
    private long _timeoutTicks;
    private long _lastProgress;
    private volatile bool _stalled;

    public StallWatchdog(TimeProvider time, TimeSpan timeout, CancellationToken outer)
    {
        _time = time;
        _timeoutTicks = timeout.Ticks;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _lastProgress = time.GetTimestamp();
        var period = TimeSpan.FromSeconds(1);
        _timer = time.CreateTimer(static s => ((StallWatchdog)s!).Check(), this, period, period);
    }

    public CancellationToken Token => _cts.Token;

    public bool Stalled => _stalled;

    public void Touch() => Interlocked.Exchange(ref _lastProgress, _time.GetTimestamp());

    /// <summary>Restarts the wait with a new allowance (the next phase needs more time without visible progress).</summary>
    public void Touch(TimeSpan timeout)
    {
        Interlocked.Exchange(ref _timeoutTicks, timeout.Ticks);
        Touch();
    }

    private void Check()
    {
        if (_stalled) return;
        if (_time.GetElapsedTime(Interlocked.Read(ref _lastProgress)) < TimeSpan.FromTicks(Interlocked.Read(ref _timeoutTicks))) return;
        _stalled = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Read-only view of <c>length</c> bytes of a stream, optionally after skipping a prefix (server ignored a Range).</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private long _skip;
    private long _remaining;

    public BoundedReadStream(Stream inner, long skip, long length)
    {
        _inner = inner;
        _skip = skip;
        _remaining = length;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_skip > 0)
        {
            var scratch = new byte[(int)Math.Min(64 * 1024, _skip)];
            while (_skip > 0)
            {
                var n = await _inner.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, _skip)), cancellationToken).ConfigureAwait(false);
                if (n == 0) return 0;
                _skip -= n;
            }
        }
        if (_remaining <= 0 || buffer.Length == 0) return 0;
        var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Synchronous path for native WebView IO threads (Core itself only reads asynchronously).
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_skip > 0)
        {
            var scratch = new byte[(int)Math.Min(64 * 1024, _skip)];
            while (_skip > 0)
            {
                var n = _inner.Read(scratch, 0, (int)Math.Min(scratch.Length, _skip));
                if (n == 0) return 0;
                _skip -= n;
            }
        }
        if (_remaining <= 0 || count == 0) return 0;
        var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Counts bytes flowing through a read-only stream (download progress).</summary>
internal sealed class ProgressReadStream(Stream inner, Action<long> progress) : Stream
{
    private long _total;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _total += read;
            progress(_total);
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _total += read;
            progress(_total);
        }
        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => _total; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Throttles progress callbacks to ~5 Hz (always lets the final value through).</summary>
internal sealed class ProgressThrottle(TimeProvider time, TimeSpan? interval = null)
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMilliseconds(200);
    private long _last;

    public bool ShouldReport(bool final = false)
    {
        if (final)
        {
            Interlocked.Exchange(ref _last, time.GetTimestamp());
            return true;
        }
        var last = Interlocked.Read(ref _last);
        if (last != 0 && time.GetElapsedTime(last) < _interval) return false;
        return Interlocked.CompareExchange(ref _last, time.GetTimestamp(), last) == last;
    }
}
