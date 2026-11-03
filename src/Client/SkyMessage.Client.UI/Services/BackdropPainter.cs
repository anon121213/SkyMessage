using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Paints the user's own backgrounds where the theme's CSS cannot: a chat with a background of its own (CSS custom
/// properties inline on its root, <see cref="ChatStyle"/>) and pictures for previews (<see cref="Url"/>: object URLs from
/// boot.js, which bakes the blur in; kept here per picture and blur). <see cref="Changed"/> fires when a picture arrived,
/// the settings changed or the theme flipped (on any thread). Scoped.
/// </summary>
public sealed class BackdropPainter : IDisposable
{
    private readonly ThemeService _theme;
    private readonly ISettingsService _settings;
    private readonly BackdropPhotoService _photos;
    private readonly AppJs _js;
    private readonly ILogger<BackdropPainter> _logger;
    /// <summary>"source|blur" → the picture; null = it could not be loaded (tried again after the next settings change).</summary>
    private readonly ConcurrentDictionary<string, BackdropImage?> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _loading = new(StringComparer.Ordinal);
    private bool _disposed;

    public BackdropPainter(ThemeService theme, ISettingsService settings, BackdropPhotoService photos, AppJs js, ILogger<BackdropPainter> logger)
    {
        _theme = theme;
        _settings = settings;
        _photos = photos;
        _js = js;
        _logger = logger;
        _settings.Changed += OnSettingsChanged;
        _theme.Changed += Raise;
    }

    /// <summary>A picture arrived, the settings changed or the theme flipped.</summary>
    public event Action? Changed;

    /// <summary>Pictures can be picked on this device.</summary>
    public bool PhotosSupported => _photos.IsSupported;

    /// <summary>Inline CSS for the root of a chat with a background of its own; null when it shows the chats' background (the theme's CSS).</summary>
    public string? ChatStyle(Guid chatId)
    {
        if (!_settings.Current.Appearance.ChatBackgrounds.TryGetValue(chatId, out var backdrop)) return null;
        var image = backdrop is { Kind: BackdropKind.Photo, Photo: { } photo } ? Image(photo, backdrop.Blur) : null;
        return _theme.Catalog.ChatStyle(backdrop, _theme.IsDark, image?.Url, image?.Tone);
    }

    /// <summary>
    /// The picture's URL as the backgrounds show it (blur 0..100 baked in), or null while it loads or when it cannot be
    /// loaded; <see cref="Changed"/> fires once it is there.
    /// </summary>
    public string? Url(BackdropPhoto photo, int blur) => Image(photo, blur)?.Url;

    /// <summary>The picture with its average color, as <see cref="Url"/> loads it.</summary>
    public BackdropImage? Image(BackdropPhoto photo, int blur)
    {
        if (!_photos.IsSupported || _disposed) return null;
        var source = _photos.SourceOf(photo);
        if (string.IsNullOrEmpty(source)) return null;
        var level = Math.Clamp(blur, 0, BackdropRules.MaxLevel);
        var key = $"{source}|{level}";
        if (_images.TryGetValue(key, out var image)) return image;
        if (_loading.TryAdd(key, 0)) _ = LoadAsync(key, source, level);
        return null;
    }

    private async Task LoadAsync(string key, string source, int level)
    {
        BackdropImage? image = null;
        try
        {
            image = await _js.BackdropUrlAsync(source, level);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A background picture did not load");
        }
        _images[key] = image;
        _loading.TryRemove(key, out _);
        if (image is not null) Raise();
    }

    private void OnSettingsChanged()
    {
        // A picture that failed may be there now (an edit stored it meanwhile).
        foreach (var (key, image) in _images)
        {
            if (image is null) _images.TryRemove(key, out _);
        }
        Raise();
    }

    private void Raise()
    {
        if (_disposed) return;
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Backdrop subscriber failed"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _theme.Changed -= Raise;
    }
}
