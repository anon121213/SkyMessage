using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Features.Profile;

/// <summary>
/// Reports whether the large title of a profile / chat info header is still on screen (an IntersectionObserver via
/// <see cref="AppJs.ObserveVisibilityAsync{T}"/>; clipping by the page's scroll area counts), so the app bar can swap
/// in the compact title (<see cref="InfoBarTitle"/>) once it has scrolled away. The pages watch a 1px mark placed one
/// header height above the title's bottom, because the floating header overlays the top of the scroll area: the title
/// is gone once it has slid under the header. Call <see cref="WatchAsync"/> after each render (cheap when the element
/// did not change) and dispose with the component.
/// </summary>
public sealed class HeaderTitleWatcher(AppJs js, Func<bool, Task> hiddenChanged) : IAsyncDisposable
{
    private Observation? _current;
    private string? _watching;
    private bool _disposed;

    /// <summary>Observes <paramref name="title"/>; a re-rendered (new) element replaces the old observation.</summary>
    public async Task WatchAsync(ElementReference title)
    {
        if (_disposed || string.IsNullOrEmpty(title.Id) || title.Id == _watching) return;
        _watching = title.Id;
        var previous = _current;
        _current = null;
        if (previous is not null) await previous.DisposeAsync();
        var observation = new Observation(this, title.Id);
        await observation.StartAsync(js, title);
        if (_disposed || _watching != title.Id) await observation.DisposeAsync();
        else _current = observation;
    }

    // Reports of an element that is no longer the watched one (it was just replaced) are ignored.
    private Task ReportAsync(string elementId, bool visible) =>
        _disposed || elementId != _watching ? Task.CompletedTask : hiddenChanged(!visible);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_current is not null) await _current.DisposeAsync();
    }

    /// <summary>One observed element: its own callback target, so late reports can be told apart.</summary>
    private sealed class Observation(HeaderTitleWatcher owner, string elementId) : IAsyncDisposable
    {
        private DotNetObjectReference<Observation>? _ref;
        private JsHandle? _handle;

        public async Task StartAsync(AppJs js, ElementReference element)
        {
            _ref = DotNetObjectReference.Create(this);
            _handle = await js.ObserveVisibilityAsync(element, _ref, nameof(OnVisible));
        }

        [JSInvokable]
        public Task OnVisible(bool visible, double ratio) => owner.ReportAsync(elementId, visible);

        public async ValueTask DisposeAsync()
        {
            if (_handle is not null) await _handle.DisposeAsync();
            _ref?.Dispose();
        }
    }
}
