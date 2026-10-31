using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Web.Platform;

/// <summary>localStorage ("sm.p.*"). Synchronous in the browser, exposed as completed tasks.</summary>
public sealed class WebPreferencesStore(WebBridge bridge) : IPreferencesStore
{
    public Task<string?> GetAsync(string key) => Task.FromResult(bridge.Sync?.Invoke<string?>("skyPlatform.prefs.get", key));

    public Task SetAsync(string key, string? value)
    {
        bridge.Sync?.InvokeVoid("skyPlatform.prefs.set", key, value);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Browsers have no OS keystore: tokens live in localStorage ("sm.s.*") of this origin only. The CSP forbids
/// inline and third-party scripts, which is what protects them.
/// </summary>
public sealed class WebSecureStore(WebBridge bridge) : ISecureStore
{
    public Task<string?> GetAsync(string key) => Task.FromResult(bridge.Sync?.Invoke<string?>("skyPlatform.secure.get", key));

    public Task SetAsync(string key, string value)
    {
        bridge.Sync?.InvokeVoid("skyPlatform.secure.set", key, value);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        bridge.Sync?.InvokeVoid("skyPlatform.secure.remove", key);
        return Task.CompletedTask;
    }
}

/// <summary>IndexedDB key/value cache; falls back to memory when IndexedDB is unavailable (some private modes).</summary>
public sealed class WebLocalCacheStore(WebBridge bridge, ILogger<WebLocalCacheStore> logger) : ILocalCacheStore
{
    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);
    private bool _memoryOnly;

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_memoryOnly) return _memory.GetValueOrDefault(key);
        try
        {
            return await bridge.Js.InvokeAsync<string?>("skyPlatform.cache.read", cancellationToken, key).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            Degrade(ex);
            return _memory.GetValueOrDefault(key);
        }
    }

    public async Task WriteAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        if (_memoryOnly)
        {
            _memory[key] = content;
            return;
        }
        try
        {
            await bridge.Js.InvokeVoidAsync("skyPlatform.cache.write", cancellationToken, key, content).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            Degrade(ex);
            _memory[key] = content;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _memory.TryRemove(key, out _);
        if (_memoryOnly) return;
        try
        {
            await bridge.Js.InvokeVoidAsync("skyPlatform.cache.del", cancellationToken, key).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            Degrade(ex);
        }
    }

    public async Task ClearAsync(string prefix = "", CancellationToken cancellationToken = default)
    {
        foreach (var key in _memory.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _memory.TryRemove(key, out _);
        if (_memoryOnly) return;
        try
        {
            await bridge.Js.InvokeVoidAsync("skyPlatform.cache.clear", cancellationToken, prefix).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            Degrade(ex);
        }
    }

    public async Task<long> GetSizeBytesAsync(CancellationToken cancellationToken = default)
    {
        if (_memoryOnly) return _memory.Sum(p => (long)p.Key.Length + p.Value.Length);
        try
        {
            return await bridge.Js.InvokeAsync<long>("skyPlatform.cache.size", cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            Degrade(ex);
            return 0;
        }
    }

    private void Degrade(JSException ex)
    {
        if (_memoryOnly) return;
        _memoryOnly = true;
        logger.LogWarning(ex, "IndexedDB unavailable; the offline cache lives in memory for this session");
    }
}

/// <summary>Outbox attachments as IndexedDB blobs; picked/compressed files move JS-side without passing through .NET.</summary>
public sealed class WebOutboxFileStore(WebBridge bridge, WebBlobs blobs, ILogger<WebOutboxFileStore> logger) : IOutboxFileStore
{
    public bool IsSupported => true;

    public async Task<PickedFile> PersistAsync(Guid itemKey, PickedFile source, CancellationToken cancellationToken = default)
    {
        var key = itemKey.ToString("N");
        long? size = null;
        if (source.PlatformRef is { } reference && !reference.StartsWith("o:", StringComparison.Ordinal))
            size = await bridge.Js.InvokeAsync<long?>("skyPlatform.outbox.persistRef", cancellationToken, key, reference, source.FileName, source.ContentType).ConfigureAwait(false);
        else if (source.PlatformRef == "o:" + key)
            size = source.Size;

        if (size is null)
        {
            // Not a JS blob (or it was evicted): stream the bytes from .NET.
            await using var stream = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            using var streamReference = new DotNetStreamReference(stream, leaveOpen: true);
            size = await bridge.Js.InvokeAsync<long>("skyPlatform.outbox.persistStream", cancellationToken, key, streamReference, source.FileName, source.ContentType).ConfigureAwait(false);
        }
        return blobs.ToPickedFile("o:" + key, source.FileName, source.ContentType, size ?? source.Size);
    }

    public async Task<PickedFile?> TryOpenAsync(Guid itemKey)
    {
        var key = itemKey.ToString("N");
        try
        {
            var info = await bridge.Js.InvokeAsync<OutboxInfo?>("skyPlatform.outbox.info", key).ConfigureAwait(false);
            return info is null ? null : blobs.ToPickedFile("o:" + key, info.Name, info.Type, info.Size);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Outbox item {Key} is unreadable", itemKey);
            return null;
        }
    }

    public async Task DeleteAsync(Guid itemKey)
    {
        try
        {
            await bridge.Js.InvokeVoidAsync("skyPlatform.outbox.del", itemKey.ToString("N")).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            logger.LogDebug(ex, "Outbox delete failed");
        }
    }

    public async Task<long> GetSizeBytesAsync()
    {
        try
        {
            return await bridge.Js.InvokeAsync<long>("skyPlatform.outbox.size").ConfigureAwait(false);
        }
        catch (JSException)
        {
            return 0;
        }
    }

    private sealed record OutboxInfo(string Name, string Type, long Size);
}

/// <summary>The browser's HTTP cache serves media (signed, immutable URLs); there is no file cache.</summary>
public sealed class WebMediaFileStore : IMediaFileStore
{
    public bool IsSupported => false;

    public Task<string?> GetLocalPathAsync(Guid mediaId, bool thumbnail = false) => Task.FromResult<string?>(null);

    public Task<string> SaveAsync(Guid mediaId, bool thumbnail, string fileName, Stream content, CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new NotSupportedException("The browser has no media file cache (IsSupported = false)."));

    public Task<long> GetSizeBytesAsync() => Task.FromResult(0L);

    public Task TrimAsync(long maxBytes) => Task.CompletedTask;

    public Task ClearAsync() => Task.CompletedTask;
}

/// <summary>Blobs held by web-platform.js, streamed into .NET on demand.</summary>
public sealed class WebBlobs(WebBridge bridge)
{
    public PickedFile ToPickedFile(string reference, string fileName, string contentType, long size) => new()
    {
        FileName = string.IsNullOrWhiteSpace(fileName) ? "file" : fileName,
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
        Size = size,
        PlatformRef = reference,
        OpenReadAsync = cancellationToken => OpenAsync(reference, cancellationToken),
    };

    public async Task<Stream> OpenAsync(string reference, CancellationToken cancellationToken)
    {
        var streamReference = await bridge.Js.InvokeAsync<IJSStreamReference>("skyPlatform.blobs.open", cancellationToken, reference).ConfigureAwait(false);
        var stream = await streamReference.OpenReadStreamAsync(long.MaxValue, cancellationToken).ConfigureAwait(false);
        return new OwnedStream(stream, streamReference);
    }

    /// <summary>Registers .NET bytes as a JS blob (for canvas work) and returns its reference.</summary>
    public async Task<string> RegisterAsync(PickedFile file, CancellationToken cancellationToken)
    {
        if (file.PlatformRef is { } existing) return existing;
        await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var streamReference = new DotNetStreamReference(stream, leaveOpen: true);
        return await bridge.Js.InvokeAsync<string>("skyPlatform.blobs.fromStream", cancellationToken, streamReference, file.ContentType).ConfigureAwait(false);
    }

    /// <summary>Disposes the JS stream reference together with the stream.</summary>
    private sealed class OwnedStream(Stream inner, IJSStreamReference owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _ = owner.DisposeAsync().AsTask();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await owner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
