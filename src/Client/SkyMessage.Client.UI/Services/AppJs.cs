using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Viewport metrics reported by app.js (CSS pixels). <paramref name="ScreenShortSide"/> is the device screen's shorter
/// side (0 = unknown): unlike the viewport it stays the same when the device rotates or the keyboard opens.
/// </summary>
public sealed record ViewportInfo(double Width, double Height, double KeyboardInset, bool CoarsePointer, double ScreenShortSide = 0);

public sealed record ElementRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public sealed record ScrollInfo(double Top, double Height, double ClientHeight)
{
    public double FromBottom => Math.Max(0, Height - ClientHeight - Top);
}

/// <summary>One entry of a batched <see cref="AppJs.CreateIntersectionObserverAsync{T}"/> callback.</summary>
public sealed record VisibilityChange(string Key, bool Visible, double Ratio);

/// <summary>
/// A JS-side subscription (observer, gesture, listener). Disposing detaches it; safe to dispose twice and
/// after the page is gone.
/// </summary>
public sealed class JsHandle : IAsyncDisposable
{
    private IJSObjectReference? _reference;

    internal JsHandle(IJSObjectReference? reference) => _reference = reference;

    public static JsHandle Empty => new(null);

    public bool IsActive => _reference is not null;

    /// <summary>Calls a method on the JS handle (e.g. <c>observe(el, key)</c> on an intersection observer).</summary>
    public async ValueTask InvokeVoidAsync(string method, params object?[] args)
    {
        if (_reference is null) return;
        try { await _reference.InvokeVoidAsync(method, args); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (TaskCanceledException) { }
        catch (JSException) { }
    }

    public async ValueTask DisposeAsync()
    {
        var reference = Interlocked.Exchange(ref _reference, null);
        if (reference is null) return;
        try { await reference.InvokeVoidAsync("dispose"); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (TaskCanceledException) { }
        catch (JSException) { }
        try { await reference.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (TaskCanceledException) { }
    }
}

/// <summary>
/// Typed wrapper over <c>wwwroot/js/app.js</c> (an ES module, imported once per scope). Every call is
/// resilient to a disconnected page (it returns defaults instead of throwing). Scoped.
/// </summary>
public sealed class AppJs(IJSRuntime js, ILogger<AppJs> logger) : IAsyncDisposable
{
    public const string ModulePath = "./_content/SkyMessage.Client.UI/js/app.js";

    private Task<IJSObjectReference>? _module;
    private bool _disposed;

    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    // ---- Generic access (for helpers not wrapped below) ----

    public async ValueTask InvokeVoidAsync(string identifier, params object?[] args)
    {
        if (_disposed) return;
        try
        {
            var module = await Module;
            await module.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (IsBenign(ex)) { }
        catch (JSException ex) { logger.LogWarning(ex, "app.js {Function} failed", identifier); }
    }

    public async ValueTask<T?> InvokeAsync<T>(string identifier, params object?[] args)
    {
        if (_disposed) return default;
        try
        {
            var module = await Module;
            return await module.InvokeAsync<T>(identifier, args);
        }
        catch (Exception ex) when (IsBenign(ex)) { return default; }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "app.js {Function} failed", identifier);
            return default;
        }
    }

    /// <summary>Calls a function returning a JS object with <c>dispose()</c> and wraps it.</summary>
    public async ValueTask<JsHandle> AttachAsync(string identifier, params object?[] args)
    {
        var reference = await InvokeAsync<IJSObjectReference>(identifier, args);
        return new JsHandle(reference);
    }

    // ---- Startup ----

    /// <summary>Installs global listeners (Escape = Back while overlays are open, keyboard inset). Idempotent.</summary>
    public ValueTask InitAsync() => InvokeVoidAsync("init");

    // ---- History entries for overlays (see NavigationService) ----

    internal ValueTask HistoryAttachAsync<T>(DotNetObjectReference<T> target) where T : class => InvokeVoidAsync("historyAttach", target);
    internal ValueTask HistoryPushAsync(string id) => InvokeVoidAsync("historyPush", id);
    /// <summary>Removes the overlay's history entry (history.go(-n)); completes when the browser settled.</summary>
    internal ValueTask HistoryCloseAsync(string id) => InvokeVoidAsync("historyClose", id);
    /// <summary>
    /// Goes back past the open overlays and <paramref name="pages"/> page entries without the router seeing it (the
    /// caller replaces the entry it lands on); completes when the browser settled.
    /// </summary>
    internal ValueTask HistoryRewindAsync(int pages) => InvokeVoidAsync("historyRewind", pages);
    internal ValueTask HistoryForgetAsync() => InvokeVoidAsync("historyForget");
    public ValueTask HistoryBackAsync() => InvokeVoidAsync("historyBack");
    /// <summary>history.go(<paramref name="delta"/>): several pages back in one step.</summary>
    internal ValueTask HistoryGoAsync(int delta) => InvokeVoidAsync("historyGo", delta);

    // ---- Focus & clipboard ----

    public ValueTask FocusAsync(ElementReference element, bool selectText = false) => InvokeVoidAsync("focusElement", element, selectText);
    /// <summary>Focuses the element with [autofocus] inside the container, else the first focusable one.</summary>
    public ValueTask FocusFirstAsync(ElementReference container) => InvokeVoidAsync("focusFirst", container);
    public ValueTask BlurActiveAsync() => InvokeVoidAsync("blurActive");
    /// <summary>Keeps Tab focus inside <paramref name="container"/>; disposing restores the previous focus.</summary>
    public ValueTask<JsHandle> TrapFocusAsync(ElementReference container) => AttachAsync("trapFocus", container);
    /// <summary>navigator.clipboard with a textarea/execCommand fallback.</summary>
    public async ValueTask<bool> CopyTextAsync(string text) => await InvokeAsync<bool>("copyText", text);

    // ---- Observers & gestures (dispose the returned handle) ----

    /// <summary>Calls <c>method(bool visible, double ratio)</c> on the target when the element enters/leaves the viewport.</summary>
    public ValueTask<JsHandle> ObserveVisibilityAsync<T>(ElementReference element, DotNetObjectReference<T> target, string method, string rootMargin = "0px", double threshold = 0) where T : class =>
        AttachAsync("observeVisibility", element, target, method, rootMargin, threshold);

    /// <summary>
    /// One observer for many elements: <c>handle.InvokeVoidAsync("observe", element, key)</c> /
    /// <c>("unobserve", element)</c>; callbacks are batched per animation frame as
    /// <c>method(VisibilityChange[] changes)</c>. <paramref name="root"/> null = the viewport.
    /// </summary>
    public ValueTask<JsHandle> CreateIntersectionObserverAsync<T>(ElementReference? root, DotNetObjectReference<T> target, string method, string rootMargin = "0px", double threshold = 0) where T : class =>
        AttachAsync("createIntersectionObserver", root, target, method, rootMargin, threshold);

    /// <summary>Right-click and touch long-press → <c>method(double clientX, double clientY)</c>.</summary>
    public ValueTask<JsHandle> AttachContextMenuAsync<T>(ElementReference element, DotNetObjectReference<T> target, string method, int longPressMs = 450) where T : class =>
        AttachAsync("attachContextMenu", element, target, method, longPressMs);

    /// <summary>Touch long-press only → <c>method(double clientX, double clientY)</c>.</summary>
    public ValueTask<JsHandle> AttachLongPressAsync<T>(ElementReference element, DotNetObjectReference<T> target, string method, int longPressMs = 450) where T : class =>
        AttachAsync("attachLongPress", element, target, method, longPressMs);

    /// <summary>Pointer down outside the element → <c>method()</c>.</summary>
    public ValueTask<JsHandle> OnClickOutsideAsync<T>(ElementReference element, DotNetObjectReference<T> target, string method) where T : class =>
        AttachAsync("onClickOutside", element, target, method);

    /// <summary>
    /// Drag in <paramref name="direction"/> ("down", "up", "left", "right"): the element follows the finger and
    /// <c>method()</c> runs when released beyond <paramref name="threshold"/> px (or flicked).
    /// </summary>
    public ValueTask<JsHandle> AttachSwipeAsync<T>(ElementReference element, DotNetObjectReference<T> target, string method, string direction, double threshold = 80) where T : class =>
        AttachAsync("attachSwipe", element, target, method, direction, threshold);

    /// <summary>
    /// <see cref="AttachSwipeAsync{T}(ElementReference, DotNetObjectReference{T}, string, string, double)"/> with options:
    /// <paramref name="direction"/> may also be "x" (either side); <paramref name="fling"/> makes the element fly out the
    /// way it was thrown before <c>method(arg)</c> runs (<paramref name="arg"/> null = no argument).
    /// </summary>
    public ValueTask<JsHandle> AttachSwipeAsync<T>(ElementReference element, DotNetObjectReference<T> target, string method, string direction, double threshold,
        bool fling, string? arg = null) where T : class =>
        AttachAsync("attachSwipe", element, target, method, direction, threshold, new SwipeOptions(fling, arg));

    private sealed record SwipeOptions(bool Fling, string? Arg);

    /// <summary>
    /// Phone page stack (MainLayout): rightward drags take the layer marked <c>[data-swipe-back]</c> away and reveal
    /// <c>[data-swipe-under]</c>; on commit the target's <c>OnSwipeBackCommit(bool revealed)</c> runs, then the layer's
    /// <c>[data-shell-back]</c> button is clicked. <c>OnSwipeBackThreshold()</c> marks the commit point (haptic).
    /// </summary>
    public ValueTask<JsHandle> AttachSwipeBackAsync<T>(ElementReference container, DotNetObjectReference<T> target) where T : class =>
        AttachAsync("attachSwipeBack", container, target);

    /// <summary>Reports viewport size/keyboard inset now and on every change → <c>method(ViewportInfo)</c>.</summary>
    public ValueTask<JsHandle> ObserveViewportAsync<T>(DotNetObjectReference<T> target, string method) where T : class =>
        AttachAsync("observeViewport", target, method);

    public ValueTask<ViewportInfo?> GetViewportAsync() => InvokeAsync<ViewportInfo>("getViewport");

    // ---- Scrolling & layout ----

    public ValueTask ScrollToTopAsync(ElementReference element, bool smooth = true) => InvokeVoidAsync("scrollToTop", element, smooth);
    public ValueTask ScrollToBottomAsync(ElementReference element, bool smooth = false) => InvokeVoidAsync("scrollToBottom", element, smooth);
    /// <summary><paramref name="block"/>: "start", "center", "end" or "nearest".</summary>
    public ValueTask ScrollIntoViewAsync(ElementReference element, string block = "nearest", bool smooth = true) => InvokeVoidAsync("scrollIntoView", element, block, smooth);
    public ValueTask<ScrollInfo?> GetScrollAsync(ElementReference element) => InvokeAsync<ScrollInfo>("getScroll", element);
    public ValueTask<ElementRect?> GetRectAsync(ElementReference element) => InvokeAsync<ElementRect>("elementRect", element);
    /// <summary>Places a fixed-position element at (x, y), flipping it to stay inside the viewport.</summary>
    public ValueTask PositionPopoverAsync(ElementReference element, double x, double y) => InvokeVoidAsync("positionPopover", element, x, y);
    /// <summary>Grows a textarea with its content up to <paramref name="maxRows"/> lines (handle method "refresh" re-measures).</summary>
    public ValueTask<JsHandle> AutoGrowAsync(ElementReference textarea, int maxRows) => AttachAsync("autoGrow", textarea, maxRows);
    /// <summary>Drag handle that sets a CSS variable on &lt;html&gt; (clamped) and remembers it in localStorage.</summary>
    public ValueTask<JsHandle> InitResizerAsync(ElementReference handle, string cssVariable, int min, int max, string storageKey) =>
        AttachAsync("initResizer", handle, cssVariable, min, max, storageKey);

    // ---- Small per-device UI preferences (never account data) ----

    public ValueTask<string?> LocalGetAsync(string key) => InvokeAsync<string?>("localGet", key);
    public ValueTask LocalSetAsync(string key, string? value) => InvokeVoidAsync("localSet", key, value);

    // ---- Theme & document ----

    /// <summary>Applies and persists the theme (boot.js re-applies it on the next start); returns whether dark is in effect.</summary>
    public async ValueTask<bool> ApplyThemeAsync(ThemeState state) => await InvokeAsync<bool>("applyTheme", state);
    /// <summary>OS light/dark changes → <c>method(bool dark)</c>.</summary>
    public ValueTask<JsHandle> ObserveSystemThemeAsync<T>(DotNetObjectReference<T> target, string method) where T : class =>
        AttachAsync("observeSystemTheme", target, method);

    public ValueTask SetDocumentTitleAsync(string title) => InvokeVoidAsync("setTitle", title);

    /// <summary>Sets &lt;html lang&gt; and remembers it for boot.js.</summary>
    public ValueTask SetLanguageAsync(string language) => InvokeVoidAsync("setLang", language);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is null) return;
        try
        {
            var module = await _module;
            await module.DisposeAsync();
        }
        catch (Exception ex) when (IsBenign(ex) || ex is JSException) { }
    }

    private static bool IsBenign(Exception ex) =>
        ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException or OperationCanceledException;
}
