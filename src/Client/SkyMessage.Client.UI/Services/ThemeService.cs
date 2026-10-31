using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Applies the appearance settings to the page (CSS variables, light/dark, reduced motion, compact list) and
/// keeps the native system bars in sync. boot.js re-applies the last state before Blazor starts, so there is
/// no flash on launch. Scoped; Routes.razor calls <see cref="InitializeAsync"/> once.
/// </summary>
public sealed class ThemeService : IAsyncDisposable
{
    private readonly ISettingsService _settings;
    private readonly ISystemUiService _systemUi;
    private readonly AppJs _js;
    private readonly ILogger<ThemeService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DotNetObjectReference<ThemeService>? _self;
    private JsHandle? _systemWatch;
    private AppearanceSettings? _preview;
    private string? _appliedSignature;
    private bool _initialized;
    private bool _disposed;

    public ThemeService(ThemeCatalog catalog, ISettingsService settings, ISystemUiService systemUi, AppJs js, ILogger<ThemeService> logger)
    {
        Catalog = catalog;
        _settings = settings;
        _systemUi = systemUi;
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
            var state = Catalog.BuildState(appearance);
            var signature = $"{state.Mode}|{state.ReduceMotion}|{state.Compact}|{state.Css}";
            if (!force && signature == _appliedSignature) return;
            _appliedSignature = signature;

            var dark = await _js.ApplyThemeAsync(state);
            var accent = Catalog.AccentHexOf(appearance);
            var changed = dark != IsDark || accent != AccentHex || force;
            IsDark = dark;
            AccentHex = accent;
            try
            {
                await _systemUi.SetSystemBarsStyleAsync(darkContent: !dark, ThemeCatalog.SystemBarColor(dark));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "System bar style not applied");
            }
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
