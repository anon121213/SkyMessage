using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Responsive layout classes: phone (&lt; 768 px, and touch phones in landscape too), tablet (768–1279 px),
/// desktop (≥ 1280 px).
/// </summary>
public enum LayoutMode
{
    Phone,
    Tablet,
    Desktop,
}

/// <summary>
/// Viewport size, on-screen keyboard inset and pointer type reported by app.js, mapped to a
/// <see cref="LayoutMode"/>. Scoped; Routes.razor initializes it before the shell renders.
/// </summary>
public sealed class LayoutService(AppJs js, IPlatformInfo platform) : IAsyncDisposable
{
    public const double TabletMinWidth = 768;
    public const double DesktopMinWidth = 1280;
    /// <summary>Touch devices whose screen is narrower than this on its short side are phones (Android's sw600dp).</summary>
    public const double PhoneMaxShortSide = 600;

    private DotNetObjectReference<LayoutService>? _self;
    private JsHandle? _watch;
    private bool _initialized;

    public double Width { get; private set; } = platform.IsMobile ? 390 : 1280;
    public double Height { get; private set; } = 800;
    /// <summary>Height of the on-screen keyboard covering the page (0 when hidden or when the WebView resizes).</summary>
    public double KeyboardInset { get; private set; }
    public bool CoarsePointer { get; private set; } = platform.IsMobile;
    /// <summary>Short side of the device screen (0 = unknown); the same in both orientations.</summary>
    public double ScreenShortSide { get; private set; }

    /// <remarks>
    /// A phone turned sideways is as wide as a small tablet, but it stays in the phone layout: switching the shell
    /// remounts every page (the media viewer closes, a group being created starts over) on each rotation.
    /// </remarks>
    public LayoutMode Mode =>
        Width < TabletMinWidth || IsHandset ? LayoutMode.Phone : Width < DesktopMinWidth ? LayoutMode.Tablet : LayoutMode.Desktop;
    public bool IsPhone => Mode == LayoutMode.Phone;
    /// <summary>The viewport is wider than tall (a phone turned sideways keeps <see cref="LayoutMode.Phone"/>).</summary>
    public bool IsLandscape => Width > Height;
    /// <summary>Two or more columns (tablet and desktop).</summary>
    public bool IsWide => Mode != LayoutMode.Phone;
    /// <summary>A third (info) column fits next to the chat.</summary>
    public bool HasInfoColumn => Mode == LayoutMode.Desktop;
    /// <summary>Touch-first device: bigger targets, sheets instead of popovers.</summary>
    public bool IsTouch => CoarsePointer || platform.IsMobile;
    public bool IsKeyboardOpen => KeyboardInset > 80;

    /// <summary>A touch phone in either orientation (the viewport is the fallback when the screen size is unknown).</summary>
    private bool IsHandset => IsTouch && (ScreenShortSide > 0 ? ScreenShortSide : Math.Min(Width, Height)) < PhoneMaxShortSide;

    /// <summary>Raised when the layout mode, size or keyboard inset changed. Fires on the renderer's dispatcher.</summary>
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        if (await js.GetViewportAsync() is { } initial) Update(initial, notify: false);
        _self = DotNetObjectReference.Create(this);
        _watch = await js.ObserveViewportAsync(_self, nameof(OnViewportChanged));
    }

    [JSInvokable]
    public void OnViewportChanged(ViewportInfo info) => Update(info, notify: true);

    private void Update(ViewportInfo info, bool notify)
    {
        var oldMode = Mode;
        var oldKeyboard = KeyboardInset;
        var oldWidth = Width;
        var oldHeight = Height;
        var oldCoarse = CoarsePointer;
        if (info.Width > 0) Width = info.Width;
        if (info.Height > 0) Height = info.Height;
        if (info.ScreenShortSide > 0) ScreenShortSide = info.ScreenShortSide;
        KeyboardInset = Math.Max(0, info.KeyboardInset);
        CoarsePointer = info.CoarsePointer || platform.IsMobile;
        if (!notify) return;
        if (oldMode != Mode || Math.Abs(oldKeyboard - KeyboardInset) > 1 || Math.Abs(oldWidth - Width) > 1
            || Math.Abs(oldHeight - Height) > 1 || oldCoarse != CoarsePointer)
        {
            foreach (var handler in Changed?.GetInvocationList() ?? [])
            {
                try { ((Action)handler)(); }
                catch { /* isolated subscriber */ }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_watch is not null) await _watch.DisposeAsync();
        _self?.Dispose();
    }
}
