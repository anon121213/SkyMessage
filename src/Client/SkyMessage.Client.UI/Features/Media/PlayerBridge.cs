using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SkyMessage.Client.UI.Features.Chat;

namespace SkyMessage.Client.UI.Features.Media;

/// <summary>
/// .NET side of player.js for a screen that shows voice messages / round notes (the chat, shared media). Marks media
/// as played, applies the playback speed and owns the muted-autoplay observers. One per screen; the JS player itself
/// is shared, so only one sound plays at a time across screens.
/// </summary>
public sealed class PlayerBridge : IAsyncDisposable
{
    private readonly JsModule _module;
    private readonly PlayedMediaStore _played;
    private readonly Func<Guid, Task>? _onError;
    private DotNetObjectReference<PlayerBridge>? _self;
    private bool _disposed;

    public PlayerBridge(IJSRuntime js, PlayedMediaStore played, Func<Guid, Task>? onError = null)
    {
        _module = new JsModule(js, JsModule.Player);
        _played = played;
        _onError = onError;
    }

    public async Task InitAsync(double rate)
    {
        if (_disposed || _self is not null) return;
        _self = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("init", _self);
        await _module.InvokeVoidAsync("setRate", rate);
    }

    public ValueTask SetRateAsync(double rate) => _module.InvokeVoidAsync("setRate", rate);

    /// <summary>Stops any sound playback (voice, audio file, round note with sound).</summary>
    public ValueTask StopAsync() => _module.InvokeVoidAsync("stop");

    public ValueTask PauseAsync() => _module.InvokeVoidAsync("pause");

    /// <summary>
    /// Muted autoplay of video[data-autoplay] inside <paramref name="root"/> while visible (handle: setEnabled, dispose).
    /// Disposing the handle means the screen is gone: sound playback that started inside <paramref name="root"/> stops.
    /// </summary>
    public ValueTask<JsObject> ObserveAsync(ElementReference root, bool enabled) => _module.AttachAsync("observe", root, enabled);

    [JSInvokable]
    public Task OnPlayed(string mediaId) =>
        Guid.TryParse(mediaId, out var id) ? _played.MarkPlayedAsync(id) : Task.CompletedTask;

    [JSInvokable]
    public Task OnPlaybackError(string mediaId) =>
        _onError is not null && Guid.TryParse(mediaId, out var id) ? _onError(id) : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_self is not null) await _module.InvokeVoidAsync("release", _self);
        await _module.DisposeAsync();
        _self?.Dispose();
    }
}
