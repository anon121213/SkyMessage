using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Client.UI.Features.Chat;

/// <summary>
/// One of the feature ES modules (chat.js, recorder.js, player.js, viewer.js), imported lazily per component. Calls
/// are resilient to a page that is gone (they return defaults instead of throwing); a failed import is retried.
/// </summary>
public sealed class JsModule(IJSRuntime js, string path) : IAsyncDisposable
{
    public const string Chat = "./_content/SkyMessage.Client.UI/js/chat.js";
    public const string Recorder = "./_content/SkyMessage.Client.UI/js/recorder.js";
    public const string Player = "./_content/SkyMessage.Client.UI/js/player.js";
    public const string Viewer = "./_content/SkyMessage.Client.UI/js/viewer.js";

    private Task<IJSObjectReference>? _module;
    private bool _disposed;

    public bool IsDisposed => _disposed;

    private Task<IJSObjectReference> Module
    {
        get
        {
            if (_module is { IsFaulted: true } or { IsCanceled: true }) _module = null;
            return _module ??= js.InvokeAsync<IJSObjectReference>("import", path).AsTask();
        }
    }

    public async ValueTask InvokeVoidAsync(string identifier, params object?[] args)
    {
        if (_disposed) return;
        try
        {
            var module = await Module;
            await module.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (IsBenign(ex) || ex is JSException)
        {
        }
    }

    public async ValueTask<T?> InvokeAsync<T>(string identifier, params object?[] args)
    {
        if (_disposed) return default;
        try
        {
            var module = await Module;
            return await module.InvokeAsync<T>(identifier, args);
        }
        catch (Exception ex) when (IsBenign(ex) || ex is JSException)
        {
            return default;
        }
    }

    /// <summary>Calls a function that returns a JS object with methods and <c>dispose()</c>.</summary>
    public async ValueTask<JsObject> AttachAsync(string identifier, params object?[] args)
    {
        var reference = await InvokeAsync<IJSObjectReference>(identifier, args);
        if (_disposed && reference is not null)
        {
            var orphan = new JsObject(reference);
            await orphan.DisposeAsync();
            return JsObject.Empty;
        }
        return new JsObject(reference);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_module is null) return;
        try
        {
            var module = await _module;
            await module.DisposeAsync();
        }
        catch (Exception ex) when (IsBenign(ex) || ex is JSException)
        {
        }
    }

    internal static bool IsBenign(Exception ex) =>
        ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException or OperationCanceledException or InvalidOperationException;
}

/// <summary>A JS object returned by a module (list keeper, composer binding, recorder...). Disposing calls its <c>dispose()</c>.</summary>
public sealed class JsObject : IAsyncDisposable
{
    private IJSObjectReference? _reference;

    internal JsObject(IJSObjectReference? reference) => _reference = reference;

    public static JsObject Empty => new(null);

    public bool IsActive => _reference is not null;

    public async ValueTask InvokeVoidAsync(string method, params object?[] args)
    {
        var reference = _reference;
        if (reference is null) return;
        try
        {
            await reference.InvokeVoidAsync(method, args);
        }
        catch (Exception ex) when (JsModule.IsBenign(ex) || ex is JSException)
        {
        }
    }

    public async ValueTask<T?> InvokeAsync<T>(string method, params object?[] args)
    {
        var reference = _reference;
        if (reference is null) return default;
        try
        {
            return await reference.InvokeAsync<T>(method, args);
        }
        catch (Exception ex) when (JsModule.IsBenign(ex) || ex is JSException)
        {
            return default;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var reference = Interlocked.Exchange(ref _reference, null);
        if (reference is null) return;
        try
        {
            await reference.InvokeVoidAsync("dispose");
        }
        catch (Exception ex) when (JsModule.IsBenign(ex) || ex is JSException)
        {
        }
        try
        {
            await reference.DisposeAsync();
        }
        catch (Exception ex) when (JsModule.IsBenign(ex) || ex is JSException)
        {
        }
    }
}

/// <summary>A blob kept in chat.js's registry (pasted, dropped or recorded), as reported to .NET.</summary>
public sealed record JsBlobInfo(string Ref, string Name, string Type, long Size);

/// <summary>
/// Turns a chat.js blob into a <see cref="PickedFile"/> that streams the bytes into .NET on every open (Core may open
/// it several times: probe, compression, durable outbox copy). Only the page's JS runtime is captured, so the file
/// stays readable after the chat screen that created it is gone.
/// </summary>
public static class JsBlobFile
{
    public static PickedFile Create(IJSRuntime js, JsBlobInfo blob) => new()
    {
        FileName = string.IsNullOrWhiteSpace(blob.Name) ? "file" : blob.Name.Trim(),
        ContentType = string.IsNullOrWhiteSpace(blob.Type) ? "application/octet-stream" : blob.Type.Split(';')[0].Trim(),
        Size = blob.Size,
        OpenReadAsync = ct => OpenAsync(js, blob.Ref, ct),
    };

    private static async Task<Stream> OpenAsync(IJSRuntime js, string reference, CancellationToken cancellationToken)
    {
        IJSObjectReference? module = null;
        try
        {
            module = await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, JsModule.Chat);
            var streamReference = await module.InvokeAsync<IJSStreamReference>("openBlob", cancellationToken, reference);
            var stream = await streamReference.OpenReadStreamAsync(long.MaxValue, cancellationToken);
            return new OwnedStream(stream, streamReference);
        }
        catch (JSException ex)
        {
            // Core maps IO errors to "attachment unavailable" (the page that held the blob was reloaded).
            throw new IOException("The attachment is no longer available.", ex);
        }
        catch (JSDisconnectedException ex)
        {
            throw new IOException("The attachment is no longer available.", ex);
        }
        finally
        {
            if (module is not null)
            {
                try
                {
                    await module.DisposeAsync();
                }
                catch (Exception ex) when (JsModule.IsBenign(ex) || ex is JSException)
                {
                }
            }
        }
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
                _ = DisposeOwnerAsync();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await DisposeOwnerAsync();
            await base.DisposeAsync();
        }

        private async Task DisposeOwnerAsync()
        {
            try
            {
                await owner.DisposeAsync();
            }
            catch (Exception ex) when (JsModule.IsBenign(ex) || ex is JSException)
            {
            }
        }
    }
}

/// <summary>What the message list shows right now (chat.js report, throttled).</summary>
public sealed record ViewportReport(bool AtBottom, bool NearTop, bool NearBottom, double FromBottom, long MaxSeq, long MinSeq);

/// <summary>A finished recording from recorder.js (the blob stays in chat.js's registry).</summary>
public sealed record RecordingResult(string Ref, string Name, string Type, long Size, int DurationMs, int[]? Waveform, int? Width, int? Height, string Mode);

/// <summary>Recorder state details: whether the camera can be flipped while recording.</summary>
public sealed record RecordStateInfo(bool CanFlip, string? Mode);
