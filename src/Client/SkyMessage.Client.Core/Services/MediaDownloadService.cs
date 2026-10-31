using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Downloads, the media file cache and the auto-download policy (SPEC §8.1 IMediaDownloadService). Native hosts
/// download into <see cref="IMediaFileStore"/> (deduped per media, cancellable, LRU-trimmed to CacheLimitMb) and
/// open/save from there. Browsers have no file store: the browser itself downloads a signed URL with download=1
/// (through <see cref="IExternalLauncher"/>; the attachment disposition keeps the page where it is).
/// </summary>
internal sealed class MediaDownloadService : IMediaDownloadService, IAccountScoped
{
    private readonly MediaFetcher _fetcher;
    private readonly IMediaFileStore _files;
    private readonly IFileOpener _opener;
    private readonly IFileSaveService _saver;
    private readonly IExternalLauncher _launcher;
    private readonly INetworkStatusService _network;
    private readonly ISettingsService _settings;
    private readonly MediaUrlBuilder _urls;
    private readonly MediaCacheTrimmer _trimmer;
    private readonly TimeProvider _time;
    private readonly ILogger<MediaDownloadService> _logger;
    private readonly ConcurrentDictionary<Guid, MediaDownloadState> _states = new();
    private readonly ConcurrentDictionary<Guid, byte> _probed = new();
    private readonly Dictionary<Guid, Download> _running = [];
    private readonly object _runningLock = new();

    public MediaDownloadService(
        MediaFetcher fetcher,
        IMediaFileStore files,
        IFileOpener opener,
        IFileSaveService saver,
        IExternalLauncher launcher,
        INetworkStatusService network,
        ISettingsService settings,
        MediaUrlBuilder urls,
        MediaCacheTrimmer trimmer,
        AccountLifecycle lifecycle,
        TimeProvider time,
        ILogger<MediaDownloadService> logger)
    {
        _fetcher = fetcher;
        _files = files;
        _opener = opener;
        _saver = saver;
        _launcher = launcher;
        _network = network;
        _settings = settings;
        _urls = urls;
        _trimmer = trimmer;
        _time = time;
        _logger = logger;
        lifecycle.Register(this);
    }

    public event Action<Guid>? StateChanged;

    public MediaDownloadState GetState(Guid mediaId)
    {
        if (_states.TryGetValue(mediaId, out var state)) return state;
        // First look at this media: find out in the background whether it is cached already.
        if (_files.IsSupported && _probed.TryAdd(mediaId, 0))
            ProbeCacheAsync(mediaId).Forget(_logger);
        return new MediaDownloadState(MediaDownloadPhase.NotDownloaded);
    }

    public async Task<string?> DownloadAsync(MediaDto media, CancellationToken cancellationToken = default)
    {
        if (!_files.IsSupported)
        {
            await BrowserDownloadAsync(media).ConfigureAwait(false);
            return null;
        }

        var cached = await TryGetCachedPathAsync(media.Id).ConfigureAwait(false);
        if (cached is not null)
        {
            SetState(media.Id, new MediaDownloadState(MediaDownloadPhase.Downloaded, 1, cached), final: true);
            return cached;
        }

        Download download;
        lock (_runningLock)
        {
            if (!_running.TryGetValue(media.Id, out download!))
            {
                download = StartDownload(media);
                _running[media.Id] = download;
            }
        }
        return await download.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Cancel(Guid mediaId)
    {
        lock (_runningLock)
        {
            if (_running.TryGetValue(mediaId, out var download)) download.Cancellation.Cancel();
        }
    }

    public async Task OpenAsync(MediaDto media, CancellationToken cancellationToken = default)
    {
        if (!_files.IsSupported)
        {
            await BrowserDownloadAsync(media).ConfigureAwait(false);
            return;
        }

        var path = await DownloadAsync(media, cancellationToken).ConfigureAwait(false);
        if (path is null) return;
        if (_opener.IsSupported && await _opener.OpenAsync(path, media.ContentType).ConfigureAwait(false)) return;

        // No app can open it: offer to save it instead.
        await using var content = OpenLocal(path);
        await _saver.SaveAsync(MediaTypes.FileNameFor(media), media.ContentType, content, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SaveToDeviceAsync(MediaDto media, CancellationToken cancellationToken = default)
    {
        if (!_files.IsSupported)
        {
            await BrowserDownloadAsync(media).ConfigureAwait(false);
            return true;
        }

        var path = await DownloadAsync(media, cancellationToken).ConfigureAwait(false);
        if (path is null) return false;
        var fileName = MediaTypes.FileNameFor(media);
        await using var content = OpenLocal(path);
        var visual = media.Kind is MediaKind.Photo or MediaKind.Video or MediaKind.VideoNote;
        if (visual && _settings.Current.Data.SaveToGallery && _saver.CanSaveToGallery)
            return await _saver.SaveToGalleryAsync(fileName, media.ContentType, content, cancellationToken).ConfigureAwait(false);
        return await _saver.SaveAsync(fileName, media.ContentType, content, cancellationToken).ConfigureAwait(false);
    }

    public bool ShouldAutoDownload(MediaDto media)
    {
        var data = _settings.Current.Data;
        var policy = _network.Kind switch
        {
            NetworkKind.None => null,
            NetworkKind.Wifi or NetworkKind.Ethernet when !_network.IsMetered => data.AutoDownloadWifi,
            _ => data.AutoDownloadMobile,
        };
        if (policy is null) return false;

        var withinLimit = media.SizeBytes <= (long)policy.MaxFileMb * 1024 * 1024;
        return media.Kind switch
        {
            // Voice messages are tiny and useless without their audio.
            MediaKind.Voice => true,
            // The size cap is meant for files and videos; a zero cap must not switch photos off.
            MediaKind.Photo => policy.Photos && (policy.MaxFileMb <= 0 || withinLimit),
            MediaKind.Video or MediaKind.VideoNote => policy.Videos && withinLimit,
            _ => policy.Files && withinLimit,
        };
    }

    public async Task<long> GetCacheSizeAsync()
    {
        if (!_files.IsSupported) return 0;
        try
        {
            return await _files.GetSizeBytesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the media cache size");
            return 0;
        }
    }

    public async Task ClearCacheAsync()
    {
        CancelAll();
        if (_files.IsSupported) await _files.ClearAsync().ConfigureAwait(false);
        var ids = _states.Keys.ToList();
        _states.Clear();
        _probed.Clear();
        foreach (var id in ids) EventRaiser.Raise(StateChanged, id, _logger);
    }

    Task IAccountScoped.OnSignedInAsync(Guid userId) => Task.CompletedTask;

    Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        CancelAll();
        _states.Clear();
        _probed.Clear();
        return Task.CompletedTask;
    }

    private void CancelAll()
    {
        lock (_runningLock)
        {
            foreach (var download in _running.Values) download.Cancellation.Cancel();
        }
    }

    private Download StartDownload(MediaDto media)
    {
        var cts = new CancellationTokenSource();
        var download = new Download(cts);
        download.Task = RunDownloadAsync(media, cts.Token);
        return download;
    }

    private async Task<string?> RunDownloadAsync(MediaDto media, CancellationToken ct)
    {
        await Task.Yield();
        var throttle = new ProgressThrottle(_time);
        try
        {
            SetState(media.Id, new MediaDownloadState(MediaDownloadPhase.Downloading, 0), final: true);
            using var response = await _fetcher.OpenAsync(media.Id, thumbnail: false, rangeHeader: null, download: false, ct).ConfigureAwait(false);
            var total = response.ContentLength ?? (media.SizeBytes > 0 ? media.SizeBytes : (long?)null);
            await using var counting = new ProgressReadStream(response.Body, read =>
            {
                if (total is not > 0 || !throttle.ShouldReport()) return;
                SetState(media.Id, new MediaDownloadState(MediaDownloadPhase.Downloading, Math.Clamp(read / (double)total.Value, 0, 0.99)), final: false);
            });

            var path = await _files.SaveAsync(media.Id, thumbnail: false, MediaTypes.FileNameFor(media), counting, ct).ConfigureAwait(false);
            SetState(media.Id, new MediaDownloadState(MediaDownloadPhase.Downloaded, 1, path), final: true);
            _trimmer.Request();
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetState(media.Id, new MediaDownloadState(MediaDownloadPhase.NotDownloaded), final: true);
            throw;
        }
        catch (Exception ex)
        {
            var code = ex is ApiException api ? api.Code : ClientErrorCodes.Unknown;
            _logger.LogInformation("Download of {MediaId} failed ({Code})", media.Id, code);
            SetState(media.Id, new MediaDownloadState(MediaDownloadPhase.Failed, 0, null, code), final: true);
            throw;
        }
        finally
        {
            lock (_runningLock)
            {
                if (_running.Remove(media.Id, out var finished)) finished.Cancellation.Dispose();
            }
        }
    }

    private async Task ProbeCacheAsync(Guid mediaId)
    {
        var path = await TryGetCachedPathAsync(mediaId).ConfigureAwait(false);
        if (path is null || _states.ContainsKey(mediaId)) return;
        SetState(mediaId, new MediaDownloadState(MediaDownloadPhase.Downloaded, 1, path), final: true);
    }

    private async Task<string?> TryGetCachedPathAsync(Guid mediaId)
    {
        try
        {
            var path = await _files.GetLocalPathAsync(mediaId).ConfigureAwait(false);
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Media cache lookup failed");
            return null;
        }
    }

    private async Task BrowserDownloadAsync(MediaDto media)
    {
        var url = _urls.DirectContentUrl(media.Id, download: true);
        await _launcher.OpenUriAsync(new Uri(url)).ConfigureAwait(false);
    }

    private static FileStream OpenLocal(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);

    /// <param name="final">Phase changes; progress updates are throttled by the caller (~5 Hz).</param>
    private void SetState(Guid mediaId, MediaDownloadState state, bool final)
    {
        _states[mediaId] = state;
        EventRaiser.Raise(StateChanged, mediaId, _logger);
    }

    private sealed class Download(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<string?> Task { get; set; } = System.Threading.Tasks.Task.FromResult<string?>(null);
    }
}
