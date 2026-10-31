using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Kit;

/// <summary>
/// Attaches right-click + touch long-press detection (app.js) to an element and forwards it as a
/// <see cref="MenuPosition"/>. Used by ListItem and ContextMenuArea; dispose with the component.
/// </summary>
public sealed class ContextMenuBinding : IAsyncDisposable
{
    private readonly Func<MenuPosition, Task> _onOpen;
    private readonly HapticFeedback _haptics;
    private DotNetObjectReference<ContextMenuBinding>? _self;
    private JsHandle? _handle;
    private bool _disposed;

    public ContextMenuBinding(Func<MenuPosition, Task> onOpen, HapticFeedback haptics)
    {
        _onOpen = onOpen;
        _haptics = haptics;
    }

    public bool IsAttached => _handle is not null;

    public async Task AttachAsync(AppJs js, ElementReference element, int longPressMs = 450)
    {
        if (_disposed || _handle is not null) return;
        _self = DotNetObjectReference.Create(this);
        _handle = await js.AttachContextMenuAsync(element, _self, nameof(OnContextMenu), longPressMs);
        if (_disposed) await _handle.DisposeAsync();
    }

    [JSInvokable]
    public async Task OnContextMenu(double x, double y)
    {
        if (_disposed) return;
        _haptics.LongPress();
        await _onOpen(new MenuPosition(x, y));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle is not null) await _handle.DisposeAsync();
        _self?.Dispose();
    }
}
