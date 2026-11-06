using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Applies the appearance settings to the page (CSS variables, light/dark, reduced motion, compact list, the pictures
/// of the user's own backgrounds) and keeps the native system bars in sync with what is drawn under them: the app's
/// background, or a chat's own one while that chat holds them (<see cref="HoldSystemBars"/>). boot.js re-applies the last
/// state before Blazor starts, so there is no flash on launch (pictures load right after). Scoped; Routes.razor calls
/// <see cref="InitializeAsync"/> once, which also lets <see cref="BackdropPhotoService"/> tidy up unused pictures.
/// </summary>
public sealed class ThemeService : IAsyncDisposable
{
    private readonly ISettingsService _settings;
    private readonly ISystemUiService _systemUi;
    private readonly BackdropPhotoService _photos;
    private readonly AppJs _js;
    private readonly ILogger<ThemeService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _barsLock = new();
    private readonly List<BarsHold> _barHolds = [];
    private (bool DarkContent, string Hex) _themeBars = (true, ThemeCatalog.SystemBarColor(false));
    private (bool DarkContent, string Hex)? _shownBars;
    private DotNetObjectReference<ThemeService>? _self;
    private JsHandle? _systemWatch;
    private AppearanceSettings? _preview;
    private string? _appliedSignature;
    private bool _initialized;
    private bool _disposed;

    public ThemeService(ThemeCatalog catalog, ISettingsService settings, ISystemUiService systemUi, BackdropPhotoService photos, AppJs js,
        ILogger<ThemeService> logger)
    {
        Catalog = catalog;
        _settings = settings;
        _systemUi = systemUi;
        _photos = photos;
        _js = js;
        _logger = logger;
        AccentHex = catalog.DefaultAccent.Hex;
    }

    public ThemeCatalog Catalog { get; }

    /// <summary>The accent palette (violet first = default); names via <c>L[accent.NameKey]</c>.</summary>
    public IReadOnlyList<AccentInfo> Accents => Catalog.Accents;

    /// <summary>The built-in wallpapers ("none" last); names via <c>L[wallpaper.NameKey]</c>.</summary>
    public IReadOnlyList<WallpaperInfo> Wallpapers => Catalog.Wallpapers;

    /// <summary>Dark theme in effect (after resolving "system").</summary>
    public bool IsDark { get; private set; }

    /// <summary>Accent color in effect ("#RRGGBB").</summary>
    public string AccentHex { get; private set; }

    /// <summary>True while a live preview (appearance settings screen) overrides the saved settings.</summary>
    public bool IsPreviewing => _preview is not null;

    /// <summary>Raised after the theme was (re)applied. May fire on any thread.</summary>
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        _settings.Changed += OnSettingsChanged;
        _self = DotNetObjectReference.Create(this);
        _systemWatch = await _js.ObserveSystemThemeAsync(_self, nameof(OnSystemThemeChanged));
        await ApplyAsync(force: true);
        // The settings are loaded by now: pictures nothing refers to any more go, once the start has settled.
        _photos.Start(TimeSpan.FromSeconds(20));
    }

    /// <summary>Applies <paramref name="appearance"/> without saving it (live preview). Call <see cref="EndPreviewAsync"/> when done.</summary>
    public Task PreviewAsync(AppearanceSettings appearance)
    {
        _preview = appearance;
        return ApplyAsync(force: false);
    }

    /// <summary>Drops the preview and re-applies the saved settings.</summary>
    public Task EndPreviewAsync()
    {
        if (_preview is null) return Task.CompletedTask;
        _preview = null;
        return ApplyAsync(force: false);
    }

    [JSInvokable]
    public Task OnSystemThemeChanged(bool dark) => ApplyAsync(force: true);

    private void OnSettingsChanged() => _ = ApplyAsync(force: false);

    private async Task ApplyAsync(bool force)
    {
        if (_disposed) return;
        await _gate.WaitAsync();
        try
        {
            var appearance = _preview ?? _settings.Current.Appearance;
            var state = Catalog.BuildState(appearance, _photos.IsSupported ? _photos.SourceOf : null);
            var images = string.Join(",", state.Images.Select(i => $"{i.Slot}:{i.Theme}:{i.Src}:{i.Blur}"));
            var signature = $"{state.Mode}|{state.ReduceMotion}|{state.Compact}|{images}|{state.Css}";
            if (!force && signature == _appliedSignature) return;
            _appliedSignature = signature;

            var dark = await _js.ApplyThemeAsync(state);
            var accent = Catalog.AccentHexOf(appearance);
            var changed = dark != IsDark || accent != AccentHex || force;
            IsDark = dark;
            AccentHex = accent;
            lock (_barsLock)
            {
                _themeBars = Catalog.SystemBars(appearance, dark);
                if (force) _shownBars = null;
            }
            await ApplyBarsAsync();
            if (changed) Raise();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Theme could not be applied");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A screen that runs under the system bars with a background of its own (a chat with its own background, on a
    /// phone): the bars follow <paramref name="backgroundHex"/> (dark or light icons, whichever reads better) until the
    /// handle is disposed. The newest hold wins; with none, the bars follow the app's background.
    /// </summary>
    public IDisposable HoldSystemBars(string backgroundHex)
    {
        var hold = new BarsHold(this, ThemeCatalog.NormalizeHex(backgroundHex) ?? ThemeCatalog.SystemBarColor(IsDark));
        lock (_barsLock) _barHolds.Add(hold);
        _ = ApplyBarsAsync();
        return hold;
    }

    private void Release(BarsHold hold)
    {
        lock (_barsLock)
        {
            if (!_barHolds.Remove(hold)) return;
        }
        _ = ApplyBarsAsync();
    }

    private async Task ApplyBarsAsync()
    {
        (bool DarkContent, string Hex) bars;
        lock (_barsLock)
        {
            bars = _barHolds.Count > 0
                ? (ThemeCatalog.DarkContentOn(ThemeCatalog.Color.Parse(_barHolds[^1].Hex)), _barHolds[^1].Hex)
                : _themeBars;
            if (_shownBars == bars) return;
            _shownBars = bars;
        }
        try
        {
            await _systemUi.SetSystemBarsStyleAsync(bars.DarkContent, bars.Hex);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "System bar style not applied");
        }
    }

    private sealed class BarsHold(ThemeService owner, string hex) : IDisposable
    {
        public string Hex { get; } = hex;

        public void Dispose() => owner.Release(this);
    }

    private void Raise()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Theme subscriber failed"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        if (_systemWatch is not null) await _systemWatch.DisposeAsync();
        _self?.Dispose();
    }
}
