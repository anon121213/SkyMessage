using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Keeps the media file cache within DataSettings.CacheLimitMb (SPEC §8.1: LRU file cache). Every write to the cache
/// (downloads, and the thumbnails and small media the app-origin proxy keeps) requests a trim; requests are coalesced
/// because a trim walks the whole cache directory. Also trims shortly after start and when the limit changes.
/// </summary>
internal sealed class MediaCacheTrimmer
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

    private readonly IMediaFileStore _files;
    private readonly ISettingsService _settings;
    private readonly CoalescingAction _trim;
    private int _limitMb;

    public MediaCacheTrimmer(IMediaFileStore files, ISettingsService settings, TimeProvider time, ILogger<MediaCacheTrimmer> logger)
    {
        _files = files;
        _settings = settings;
        _limitMb = settings.Current.Data.CacheLimitMb;
        _trim = new CoalescingAction(Delay, TrimAsync, time, logger);
        settings.Changed += OnSettingsChanged;
        Request();
    }

    public void Request()
    {
        if (_files.IsSupported) _trim.Request();
    }

    private void OnSettingsChanged()
    {
        var limitMb = _settings.Current.Data.CacheLimitMb;
        if (Interlocked.Exchange(ref _limitMb, limitMb) != limitMb) Request();
    }

    private Task TrimAsync(CancellationToken ct)
    {
        var limitMb = Math.Max(50, _settings.Current.Data.CacheLimitMb);
        return _files.TrimAsync(limitMb * 1024L * 1024L);
    }
}
