using Microsoft.AspNetCore.Components;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Kit;

/// <summary>
/// Base class for components that listen to Core events (which fire on any thread). Provides the localizer,
/// a coalesced, thread-safe <see cref="RequestRender"/> and cleanup registration for event subscriptions.
/// </summary>
public abstract class UiComponentBase : ComponentBase, IDisposable
{
    private readonly List<Action> _cleanup = [];
    private int _renderPending;

    [Inject] protected ILocalizer L { get; set; } = default!;

    protected bool IsDisposed { get; private set; }

    /// <summary>Delay used to coalesce bursts of store events into one render.</summary>
    protected virtual int RenderCoalesceMs => 30;

    /// <summary>Schedules one re-render on the renderer's dispatcher (safe from any thread, ignored after dispose).</summary>
    protected void RequestRender()
    {
        if (IsDisposed || Interlocked.Exchange(ref _renderPending, 1) == 1) return;
        _ = InvokeAsync(async () =>
        {
            if (RenderCoalesceMs > 0) await Task.Delay(RenderCoalesceMs);
            Interlocked.Exchange(ref _renderPending, 0);
            if (!IsDisposed) StateHasChanged();
        });
    }

    /// <summary>Re-renders right away on the dispatcher (safe from any thread).</summary>
    protected Task RenderNowAsync() => IsDisposed ? Task.CompletedTask : InvokeAsync(() =>
    {
        if (!IsDisposed) StateHasChanged();
    });

    /// <summary>Registers work to run on dispose (typically an event unsubscription).</summary>
    protected void OnDispose(Action cleanup)
    {
        lock (_cleanup) _cleanup.Add(cleanup);
    }

    /// <summary>Subscribes <see cref="RequestRender"/> to a parameterless event and unsubscribes on dispose.</summary>
    protected void RenderOn(Action<Action> subscribe, Action<Action> unsubscribe)
    {
        Action handler = RequestRender;
        subscribe(handler);
        OnDispose(() => unsubscribe(handler));
    }

    /// <summary>Subscribes <see cref="RequestRender"/> to an event with an argument and unsubscribes on dispose.</summary>
    protected void RenderOn<T>(Action<Action<T>> subscribe, Action<Action<T>> unsubscribe, Func<T, bool>? filter = null)
    {
        Action<T> handler = value =>
        {
            if (filter is null || filter(value)) RequestRender();
        };
        subscribe(handler);
        OnDispose(() => unsubscribe(handler));
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Action[] cleanup;
        lock (_cleanup)
        {
            cleanup = [.. _cleanup];
            _cleanup.Clear();
        }
        foreach (var action in cleanup)
        {
            try { action(); }
            catch (ObjectDisposedException) { }
        }
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
