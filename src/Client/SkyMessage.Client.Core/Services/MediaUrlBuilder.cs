using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Media URLs for the UI (SPEC §8.1 Media URLs). Direct mode: absolute server URLs carrying a per-file signature
/// (<see cref="MediaSignature.Create(ReadOnlySpan{byte}, Guid, Guid)"/>, cached per media id). AppProxy mode:
/// app-origin paths "/_sm/media/{id}/content?s={size}&amp;ct={type}" and "/_sm/media/{id}/thumbnail" served by
/// <see cref="IMediaContentSource"/>; kinds outside <see cref="ClientCoreOptions.AppProxyKinds"/> use Direct URLs.
/// <see cref="Changed"/> fires when the server address or the session changes.
/// </summary>
internal sealed class MediaUrlBuilder : IMediaUrlBuilder
{
    public const string AppProxyPrefix = "/_sm/media/";
    private const int MaxMiniThumbnails = 2048;

    private readonly ClientCoreOptions _options;
    private readonly IServerEndpointService _endpoints;
    private readonly AuthSessionStore _sessions;
    private readonly ILogger<MediaUrlBuilder> _logger;
    private readonly ConcurrentDictionary<Guid, string> _signatures = new();
    private readonly ConcurrentDictionary<Guid, string> _miniThumbnails = new();
    private readonly object _keyLock = new();
    private (Guid SessionId, byte[] Key)? _key;
    private Guid? _lastSessionId;
    private Uri? _baseUri;

    public MediaUrlBuilder(ClientCoreOptions options, IServerEndpointService endpoints, AuthSessionStore sessions, ILogger<MediaUrlBuilder> logger)
    {
        _options = options;
        _endpoints = endpoints;
        _sessions = sessions;
        _logger = logger;
        _baseUri = endpoints.BaseUri;
        _lastSessionId = sessions.Current?.SessionId;
        endpoints.Changed += OnEndpointChanged;
        sessions.Changed += OnSessionChanged;
    }

    public event Action? Changed;

    public string ContentUrl(MediaDto media, bool download = false)
    {
        if (UsesAppProxy(media.Kind))
        {
            var url = $"{AppProxyPrefix}{media.Id}/content?s={media.SizeBytes}&ct={Uri.EscapeDataString(media.ContentType ?? "")}";
            return download ? url + "&dl=1" : url;
        }
        return DirectContentUrl(media.Id, download);
    }

    public string ContentUrl(Guid mediaId, bool download = false)
    {
        if (_options.MediaUrlMode == MediaUrlMode.AppProxy)
            return download ? $"{AppProxyPrefix}{mediaId}/content?dl=1" : $"{AppProxyPrefix}{mediaId}/content";
        return DirectContentUrl(mediaId, download);
    }

    public string ThumbnailUrl(Guid mediaId)
    {
        if (_options.MediaUrlMode == MediaUrlMode.AppProxy) return $"{AppProxyPrefix}{mediaId}/thumbnail";
        return new Uri(_endpoints.BaseUri, ApiRoutes.Media.ThumbnailPath(mediaId, Signature(mediaId))).ToString();
    }

    public string? AvatarUrl(Guid? avatarMediaId) => avatarMediaId is { } id && id != Guid.Empty ? ThumbnailUrl(id) : null;

    public string? MiniThumbnailDataUrl(MediaDto media)
    {
        if (media.MiniThumbnail is not { Length: > 0 } bytes) return null;
        if (_miniThumbnails.TryGetValue(media.Id, out var cached)) return cached;
        var url = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        if (_miniThumbnails.Count >= MaxMiniThumbnails) _miniThumbnails.Clear();
        _miniThumbnails[media.Id] = url;
        return url;
    }

    /// <summary>Signed server URL regardless of the mode (browser downloads, kinds excluded from the app proxy).</summary>
    internal string DirectContentUrl(Guid mediaId, bool download) =>
        new Uri(_endpoints.BaseUri, ApiRoutes.Media.ContentPath(mediaId, Signature(mediaId), download)).ToString();

    private bool UsesAppProxy(MediaKind kind) =>
        _options.MediaUrlMode == MediaUrlMode.AppProxy && (_options.AppProxyKinds is null || _options.AppProxyKinds.Contains(kind));

    /// <summary>Null while signed out (the URL then simply fails to load).</summary>
    private string? Signature(Guid mediaId)
    {
        if (_signatures.TryGetValue(mediaId, out var cached)) return cached;
        var key = CurrentKey();
        if (key is null) return null;
        var signature = MediaSignature.Create(key.Value.Key, key.Value.SessionId, mediaId);
        _signatures[mediaId] = signature;
        return signature;
    }

    private (Guid SessionId, byte[] Key)? CurrentKey()
    {
        lock (_keyLock)
        {
            if (_key is not null) return _key;
            var session = _sessions.Current;
            if (session is null || string.IsNullOrEmpty(session.MediaKey)) return null;
            try
            {
                _key = (session.SessionId, MediaSignature.DecodeKey(session.MediaKey));
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "The session's media key is malformed");
                return null;
            }
            return _key;
        }
    }

    private void OnSessionChanged()
    {
        var sessionId = _sessions.Current?.SessionId;
        lock (_keyLock)
        {
            // Token refreshes keep the session (and its key): nothing to invalidate.
            if (_lastSessionId == sessionId) return;
            _lastSessionId = sessionId;
            _key = null;
            _signatures.Clear();
        }
        EventRaiser.Raise(Changed, _logger);
    }

    private void OnEndpointChanged()
    {
        var current = _endpoints.BaseUri;
        var previous = Interlocked.Exchange(ref _baseUri, current);
        if (previous is not null && previous == current) return;
        EventRaiser.Raise(Changed, _logger);
    }
}
