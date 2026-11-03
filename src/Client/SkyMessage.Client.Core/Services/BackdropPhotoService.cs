using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>Why a picked picture was not taken as a background.</summary>
public enum BackdropPhotoProblem
{
    None,
    /// <summary>Not a picture a page can show (e.g. a HEIC photo the device could not convert).</summary>
    Format,
    /// <summary>Larger than <see cref="BackdropRules.MaxBytes"/> even after downscaling.</summary>
    TooLarge,
    /// <summary>Reading, converting or storing it failed.</summary>
    Failed,
}

/// <summary>The outcome of <see cref="BackdropPhotoService.ImportAsync"/>: a stored picture, a problem, or neither (the picker was cancelled).</summary>
public sealed record BackdropPhotoImport(BackdropPhoto? Photo, BackdropPhotoProblem Problem)
{
    public static BackdropPhotoImport Cancelled { get; } = new(null, BackdropPhotoProblem.None);

    public bool IsCancelled => Photo is null && Problem == BackdropPhotoProblem.None;
}

/// <summary>
/// Pictures for the backgrounds: picking one from the gallery, downscaling it to the screen (metadata stripped) and
/// keeping it on this device (<see cref="IBackdropImageStore"/>), and forgetting pictures no setting refers to any
/// more — whenever a background is replaced or reset, however the settings changed, and once after start for leftovers
/// of an interrupted edit. A picture just picked belongs to the editor that picked it until a setting refers to it or
/// the editor discards it. A chat that is gone for me (deleted, left, removed from) takes its background along.
/// Singleton.
/// </summary>
public sealed class BackdropPhotoService : IDisposable
{
    /// <summary>The long side a picture is downscaled to stays within these bounds, whatever the screen reports.</summary>
    public const int MinDimension = 720;
    public const int MaxDimension = 4096;
    public const int Quality = 86;

    private readonly IFilePickerService _picker;
    private readonly IMediaCompressor _compressor;
    private readonly IBackdropImageStore _store;
    private readonly ISettingsService _settings;
    private readonly IPreferencesStore _preferences;
    private readonly IRealtimeService _realtime;
    private readonly ILogger<BackdropPhotoService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private HashSet<string> _referenced;
    private int _started;
    private bool _disposed;

    public BackdropPhotoService(IFilePickerService picker, IMediaCompressor compressor, IBackdropImageStore store, ISettingsService settings,
        IPreferencesStore preferences, IRealtimeService realtime, ILogger<BackdropPhotoService> logger)
    {
        _picker = picker;
        _compressor = compressor;
        _store = store;
        _settings = settings;
        _preferences = preferences;
        _realtime = realtime;
        _logger = logger;
        _referenced = BackdropRules.FileNamesIn(settings.Current.Appearance);
        settings.Changed += OnSettingsChanged;
        realtime.ChatRemoved += OnChatRemoved;
    }

    public bool IsSupported => _store.IsSupported;

    /// <summary>
    /// Once the settings are loaded: after <paramref name="cleanupDelay"/> (a quiet moment after start), stored pictures
    /// that no setting refers to and no editor holds are deleted. Repeated calls do nothing.
    /// </summary>
    public void Start(TimeSpan cleanupDelay)
    {
        if (!IsSupported || Interlocked.Exchange(ref _started, 1) == 1) return;
        CleanupLaterAsync(cleanupDelay).Forget(_logger);
    }

    /// <summary>
    /// Opens the gallery; the picture is downscaled so its long side is at most <paramref name="maxDimension"/> pixels
    /// (the screen's, clamped to <see cref="MinDimension"/>..<see cref="MaxDimension"/>) and stored. It is not used by any
    /// background yet: put it into one and save, or <see cref="DiscardAsync"/> it.
    /// </summary>
    public async Task<BackdropPhotoImport> ImportAsync(int maxDimension, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return new BackdropPhotoImport(null, BackdropPhotoProblem.Failed);
        var file = await _picker.PickImageAsync(cancellationToken).ConfigureAwait(false);
        if (file is null) return BackdropPhotoImport.Cancelled;

        CompressedMedia media;
        try
        {
            var options = new ImageCompressionOptions
            {
                MaxDimension = Math.Clamp(maxDimension, MinDimension, MaxDimension),
                Quality = Quality,
                StripMetadata = true,
            };
            media = _compressor.CanCompressImages
                ? await _compressor.CompressImageAsync(file, options, cancellationToken).ConfigureAwait(false)
                : await _compressor.ProbeAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A background picture could not be prepared");
            return new BackdropPhotoImport(null, BackdropPhotoProblem.Failed);
        }

        try
        {
            var extension = BackdropRules.ExtensionOf(media.ContentType, media.FileName);
            if (extension is null) return new BackdropPhotoImport(null, BackdropPhotoProblem.Format);
            if (media.Size > BackdropRules.MaxBytes) return new BackdropPhotoImport(null, BackdropPhotoProblem.TooLarge);

            var bytes = await ReadAsync(media, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return new BackdropPhotoImport(null, BackdropPhotoProblem.TooLarge);
            if (bytes.Length == 0) return new BackdropPhotoImport(null, BackdropPhotoProblem.Failed);

            var photo = new BackdropPhoto
            {
                Id = BackdropRules.NewId(),
                Extension = extension,
                Width = Math.Max(0, media.Width ?? 0),
                Height = Math.Max(0, media.Height ?? 0),
            };
            // Held before it exists: a cleanup running meanwhile must not take it.
            lock (_lock) _pending.Add(photo.FileName);
            try
            {
                await _store.SaveAsync(photo.FileName, bytes, BackdropRules.Extensions[extension], cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_lock) _pending.Remove(photo.FileName);
                throw;
            }
            return new BackdropPhotoImport(photo, BackdropPhotoProblem.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A background picture could not be stored");
            return new BackdropPhotoImport(null, BackdropPhotoProblem.Failed);
        }
        finally
        {
            DeleteTempFile(media.TempFilePath);
        }
    }

    /// <summary>Where the page loads <paramref name="photo"/> from (<see cref="IBackdropImageStore.PageSource"/>).</summary>
    public string SourceOf(BackdropPhoto photo) => _store.PageSource(photo.FileName);

    /// <summary>Makes <paramref name="background"/> the app's background (and selects it instead of a sky).</summary>
    public Task SetAppBackgroundAsync(AppBackground background) => UpdateAsync(s =>
    {
        s.Appearance.CustomBackground = background.Clone();
        s.Appearance.Wallpaper = BackdropRules.CustomWallpaper;
    });

    /// <summary>The chats' background unless a chat has its own; null = the app's background.</summary>
    public Task SetChatDefaultAsync(Backdrop? backdrop) => UpdateAsync(s => s.Appearance.ChatBackground = backdrop?.Clone());

    /// <summary>One chat's own background; null = the chats' background again.</summary>
    public Task SetChatAsync(Guid chatId, Backdrop? backdrop) => UpdateAsync(s =>
    {
        if (backdrop is null) s.Appearance.ChatBackgrounds.Remove(chatId);
        else s.Appearance.ChatBackgrounds[chatId] = backdrop.Clone();
    });

    /// <summary>Saves, then waits until the pictures the change left behind are deleted.</summary>
    private async Task UpdateAsync(Action<AppSettings> change)
    {
        await _settings.UpdateAsync(change).ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    }

    /// <summary>An editor is done with a picture it picked: deleted unless a setting refers to it.</summary>
    public async Task DiscardAsync(BackdropPhoto photo)
    {
        var name = photo.FileName;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool referenced;
            lock (_lock)
            {
                _pending.Remove(name);
                referenced = _referenced.Contains(name) || BackdropRules.FileNamesIn(_settings.Current.Appearance).Contains(name);
            }
            if (!referenced) await DeleteQuietlyAsync(name).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes stored pictures that no setting refers to and no editor holds. The settings as stored count too: another
    /// browser tab of the same device may have saved a picture this one's settings do not know yet.
    /// </summary>
    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            if (stored.Count == 0) return;
            var saved = await StoredFileNamesAsync().ConfigureAwait(false);
            if (saved is null) return;
            List<string> orphans;
            lock (_lock)
            {
                var keep = BackdropRules.FileNamesIn(_settings.Current.Appearance);
                orphans = stored.Where(name => !keep.Contains(name) && !saved.Contains(name) && !_pending.Contains(name)).ToList();
            }
            foreach (var name in orphans) await DeleteQuietlyAsync(name).ConfigureAwait(false);
            if (orphans.Count > 0) _logger.LogInformation("Removed {Count} unused background pictures", orphans.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Background pictures were not cleaned up");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The pictures the stored settings refer to; null when they cannot be read (then nothing is deleted).</summary>
    private async Task<HashSet<string>?> StoredFileNamesAsync()
    {
        try
        {
            var json = await _preferences.GetAsync(SettingsService.Key).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>(StringComparer.Ordinal);
            var stored = JsonSerializer.Deserialize<AppSettings>(json, SkyJson.Options);
            return stored?.Appearance is { } appearance ? BackdropRules.FileNamesIn(appearance) : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The stored settings could not be read for the picture cleanup");
            return null;
        }
    }

    private async Task CleanupLaterAsync(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
        if (!_disposed) await CleanupAsync().ConfigureAwait(false);
    }

    private void OnSettingsChanged() => SyncAsync().Forget(_logger);

    private void OnChatRemoved(ChatRefEvent e)
    {
        if (_settings.Current.Appearance.ChatBackgrounds.ContainsKey(e.ChatId)) SetChatAsync(e.ChatId, null).Forget(_logger);
    }

    /// <summary>Pictures the settings referred to and no longer do (replaced, reset) are deleted.</summary>
    private async Task SyncAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<string> gone;
            lock (_lock)
            {
                var now = BackdropRules.FileNamesIn(_settings.Current.Appearance);
                gone = _referenced.Where(name => !now.Contains(name) && !_pending.Contains(name)).ToList();
                _pending.ExceptWith(now);
                _referenced = now;
            }
            foreach (var name in gone) await DeleteQuietlyAsync(name).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The picture's bytes, or null when it is larger than <see cref="BackdropRules.MaxBytes"/> (its size may have been unknown).</summary>
    private static async Task<byte[]?> ReadAsync(CompressedMedia media, CancellationToken cancellationToken)
    {
        await using var stream = await media.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(media.Size is > 0 and <= BackdropRules.MaxBytes ? (int)media.Size : 256 * 1024);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > BackdropRules.MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task DeleteQuietlyAsync(string fileName)
    {
        try
        {
            await _store.DeleteAsync(fileName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A background picture was not deleted");
        }
    }

    private void DeleteTempFile(string? path)
    {
        // Temp files only exist on native hosts (their compressors create them).
        if (string.IsNullOrEmpty(path) || OperatingSystem.IsBrowser()) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete a temporary picture");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _realtime.ChatRemoved -= OnChatRemoved;
    }
}
