using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>Persisted endpoint memory ("endpoint.v1" in preferences; nothing secret).</summary>
internal sealed class EndpointState
{
    /// <summary>Deployment the client is bound to (learned from the primary address).</summary>
    public Guid? InstanceId { get; set; }
    /// <summary>Address in use when the app last ran (a verified mirror after a failover).</summary>
    public string? ActiveUrl { get; set; }
    /// <summary>Session for which <see cref="ActiveUrl"/> passed the server proof (non-primary only).</summary>
    public Guid? ActiveVerifiedSessionId { get; set; }
    /// <summary>Server-advertised mirrors per InstanceId.</summary>
    public Dictionary<Guid, List<string>> Mirrors { get; set; } = [];
}

/// <summary>
/// Active server address and failover (SPEC §8.1 Endpoints + failover hardening). The primary address
/// (custom ?? build default, or the page origin) is trusted by configuration; any other candidate must report
/// the same InstanceId, pass a throughput probe and — when tokens exist — prove it holds the session's media
/// key (<see cref="MediaSignature.VerifyProof"/>) before a token is sent to it.
/// </summary>
internal sealed class ServerEndpointService : IServerEndpointService, IEndpointRouting
{
    public const string StateKey = "endpoint.v1";
    private const int ProbeBytes = 64 * 1024;
    private static readonly TimeSpan InfoProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ThroughputProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProofTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailedFailoverCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StallWindow = TimeSpan.FromMinutes(2);
    private const int StallsBeforeFailover = 2;

    private readonly ClientCoreOptions _options;
    private readonly ISettingsService _settings;
    private readonly HttpClientProvider _clients;
    private readonly IPreferencesStore _preferences;
    private readonly AuthSessionStore _sessions;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerEndpointService> _logger;
    private readonly object _lock = new();
    private readonly Queue<long> _stalls = new();

    private EndpointState _state = new();
    /// <summary>InitializeAsync restored <see cref="_state"/> (until then settings changes must not persist the empty one).</summary>
    private bool _initialized;
    private Uri _primary;
    private IReadOnlyList<Uri> _candidates;
    private Uri _baseUri;
    private ServerInfoDto? _info;
    private string _connectionKey;
    private bool _allowTransportFallback;
    private int _connectionVersion;
    private Task<bool>? _failover;
    private long _lastFailedFailover;
    private bool _identityCheckPending;
    private Task? _identityCheck;
    private Uri? _preverifiedPrimary;
    private Task? _infoRefresh;

    public ServerEndpointService(
        ClientCoreOptions options,
        ISettingsService settings,
        HttpClientProvider clients,
        IPreferencesStore preferences,
        AuthSessionStore sessions,
        IServiceProvider services,
        TimeProvider time,
        ILogger<ServerEndpointService> logger)
    {
        _options = options;
        _settings = settings;
        _clients = clients;
        _preferences = preferences;
        _sessions = sessions;
        _services = services;
        _time = time;
        _logger = logger;

        var connection = settings.Current.Connection;
        _connectionKey = ConnectionKey(connection);
        _allowTransportFallback = connection.AllowTransportFallback;
        (_primary, _candidates) = ComputeCandidates(connection, _state);
        _baseUri = _primary;
        settings.Changed += OnSettingsChanged;
    }

    public Uri BaseUri => _baseUri;

    public IReadOnlyList<Uri> Candidates => _candidates;

    public ServerInfoDto? Info => _info;

    public event Action? Changed;

    /// <summary>Bumped whenever the base address or the HTTP stack changes (realtime reconnects on a new value).</summary>
    public int ConnectionVersion => Volatile.Read(ref _connectionVersion);

    public Guid? KnownInstanceId
    {
        get
        {
            lock (_lock) return _state.InstanceId;
        }
    }

    internal bool IsPrimaryActive => SameUri(_baseUri, _primary);

    /// <summary>Restores the remembered state (no network). Call after the settings and the session are loaded.</summary>
    internal async Task InitializeAsync()
    {
        EndpointState? stored = null;
        try
        {
            var json = await _preferences.GetAsync(StateKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
                stored = JsonSerializer.Deserialize<EndpointState>(json, SkyJson.Options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored endpoint state is unreadable; starting fresh");
        }

        lock (_lock)
        {
            _state = stored ?? new EndpointState();
            _state.Mirrors ??= [];
            _initialized = true;
            var connection = _settings.Current.Connection;
            _connectionKey = ConnectionKey(connection);
            // Settings changes before this point were skipped (see OnSettingsChanged): apply the proxy they may carry.
            try
            {
                _clients.RebuildIfProxyChanged(connection);
            }
            catch (Exception ex)
            {
                // A proxy the platform cannot build must not stop the app from starting; requests report it.
                _logger.LogWarning(ex, "The HTTP stack could not be rebuilt for the stored proxy settings");
            }
            _allowTransportFallback = connection.AllowTransportFallback;
            (_primary, _candidates) = ComputeCandidates(connection, _state);
            _baseUri = _primary;
            if (_state.ActiveUrl is { } active && TryNormalize(active, out var activeUri)
                && _candidates.Any(c => SameUri(c, activeUri))
                && (SameUri(activeUri, _primary) || (_state.ActiveVerifiedSessionId is { } sid && sid == _sessions.Current?.SessionId)))
            {
                _baseUri = activeUri;
            }
            _connectionVersion++;
        }
        RaiseChanged();
    }

    /// <summary>Reads api/server/info from the active address (InstanceId, mirrors). Never throws.</summary>
    internal Task RefreshInfoAsync()
    {
        lock (_lock)
        {
            if (_infoRefresh is { IsCompleted: false } running) return running;
            _infoRefresh = RefreshInfoCoreAsync();
            return _infoRefresh;
        }
    }

    private async Task RefreshInfoCoreAsync()
    {
        var baseUri = _baseUri;
        var info = await ProbeInfoAsync(_clients.Client, baseUri, TestTimeout, CancellationToken.None).ConfigureAwait(false);
        if (info is not null) await ApplyInfoAsync(baseUri, info).ConfigureAwait(false);
    }

    public async Task<ServerInfoDto?> TestAsync(Uri baseUri, ConnectionSettings? candidate = null, CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(baseUri.ToString(), out var normalized)) return null;
        if (candidate is null)
            return await ProbeInfoAsync(_clients.Client, normalized, TestTimeout, cancellationToken).ConfigureAwait(false);

        using var client = _clients.CreateTemporary(candidate);
        return await ProbeInfoAsync(client, normalized, TestTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCustomServerAsync(string? url)
    {
        Uri? target = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!TryNormalize(url, out var parsed))
                throw new ArgumentException("Invalid server address.", nameof(url));
            target = parsed;
        }

        var effective = target ?? DefaultPrimary();
        var info = await ProbeInfoAsync(_clients.Client, effective, TestTimeout, CancellationToken.None).ConfigureAwait(false);

        Guid? known;
        lock (_lock) known = _state.InstanceId;
        if (_sessions.Current is not null && info is not null && known is { } id && info.InstanceId != id)
        {
            // A different deployment: end the session on the old server before switching.
            _logger.LogInformation("Switching to a different deployment; signing out first");
            await _services.GetRequiredService<IAuthService>().LogoutAsync(revokeOnServer: true).ConfigureAwait(false);
        }

        lock (_lock)
        {
            if (info is not null)
            {
                _state.InstanceId = info.InstanceId;
                _preverifiedPrimary = effective;
            }
            else
            {
                _preverifiedPrimary = null;
            }
        }

        await _settings.UpdateAsync(s => s.Connection.CustomServerUrl = target?.ToString()).ConfigureAwait(false);
        // UpdateAsync raised Changed synchronously, so the switch has happened; make sure it did even when the
        // stored value was unchanged.
        SwitchToPrimaryIfNeeded();
        if (info is not null)
            await ApplyInfoAsync(effective, info).ConfigureAwait(false);
        else
            RefreshInfoAsync().Forget(_logger);
    }

    public Task<bool> FailoverAsync(CancellationToken cancellationToken = default)
    {
        Task<bool> task;
        lock (_lock)
        {
            if (_failover is null || _failover.IsCompleted)
                _failover = FailoverCoreAsync();
            task = _failover;
        }
        return task.WaitAsync(cancellationToken);
    }

    public async Task<bool> FailoverFromAsync(Uri failedBase, CancellationToken cancellationToken)
    {
        if (!SameUri(_baseUri, failedBase)) return true;
        return await FailoverAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task EnsureTrustedAsync(CancellationToken cancellationToken)
    {
        Task check;
        lock (_lock)
        {
            if (!_identityCheckPending) return Task.CompletedTask;
            if (_identityCheck is null || _identityCheck.IsCompleted)
                _identityCheck = VerifyIdentityAsync();
            check = _identityCheck;
        }
        return check.WaitAsync(cancellationToken);
    }

    public void ReportStall()
    {
        var trigger = false;
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            _stalls.Enqueue(now);
            while (_stalls.Count > 0 && _time.GetElapsedTime(_stalls.Peek()) > StallWindow)
                _stalls.Dequeue();
            if (_stalls.Count >= StallsBeforeFailover)
            {
                _stalls.Clear();
                trigger = true;
            }
        }
        if (!trigger) return;
        _logger.LogWarning("Repeated transfer stalls on the active server; trying another address");
        FailoverAsync().Forget(_logger);
    }

    public Uri Resolve(string relativePath) => new(_baseUri, relativePath.TrimStart('/'));

    public string ResolveLiveKitUrl(string liveKitUrl)
    {
        if (string.IsNullOrWhiteSpace(liveKitUrl)) liveKitUrl = HubRoutes.LiveKitProxy;
        Uri target;
        if (Uri.TryCreate(liveKitUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "ws" or "wss" or "http" or "https")
        {
            if (absolute.Scheme is "ws" or "wss") return liveKitUrl;
            target = absolute;
        }
        else
        {
            target = Resolve(liveKitUrl);
        }

        var builder = new UriBuilder(target) { Scheme = target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" };
        builder.Port = target.IsDefaultPort ? -1 : target.Port;
        return builder.Uri.ToString();
    }

    private async Task<bool> FailoverCoreAsync()
    {
        Uri current;
        Uri primary;
        IReadOnlyList<Uri> candidates;
        Guid? known;
        lock (_lock)
        {
            if (_lastFailedFailover != 0 && _time.GetElapsedTime(_lastFailedFailover) < FailedFailoverCooldown)
                return false;
            current = _baseUri;
            primary = _primary;
            candidates = _candidates.Where(c => !SameUri(c, current)).ToList();
            known = _state.InstanceId;
        }
        if (candidates.Count == 0)
        {
            MarkFailedFailover();
            return false;
        }

        var session = _sessions.Current;
        var client = _clients.Client;
        _logger.LogInformation("Failing over: probing {Count} other addresses", candidates.Count);
        var infos = await Task.WhenAll(candidates.Select(c => ProbeInfoAsync(client, c, InfoProbeTimeout, CancellationToken.None))).ConfigureAwait(false);

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var info = infos[i];
            if (info is null) continue;
            var isPrimary = SameUri(candidate, primary);
            if (known is { } id && info.InstanceId != id) continue;
            // Without a known deployment id a mirror cannot be checked; only the configured primary is acceptable.
            if (known is null && session is not null && !isPrimary) continue;
            if (!await ProbeThroughputAsync(client, candidate).ConfigureAwait(false)) continue;
            if (!isPrimary && session is not null && !await VerifyProofAsync(client, candidate, session, known ?? info.InstanceId).ConfigureAwait(false))
            {
                _logger.LogWarning("Address {Index} failed the server proof; skipping it", i);
                continue;
            }

            Switch(current, candidate, info, isPrimary ? null : session?.SessionId);
            return true;
        }

        MarkFailedFailover();
        return false;
    }

    private void MarkFailedFailover()
    {
        lock (_lock) _lastFailedFailover = _time.GetTimestamp();
    }

    private void Switch(Uri from, Uri to, ServerInfoDto info, Guid? verifiedFor)
    {
        lock (_lock)
        {
            if (!SameUri(_baseUri, from)) return; // someone else switched meanwhile
            _baseUri = to;
            _info = info;
            _state.ActiveUrl = to.ToString();
            _state.ActiveVerifiedSessionId = verifiedFor;
            _lastFailedFailover = 0;
            _stalls.Clear();
            _connectionVersion++;
        }
        _logger.LogInformation("Switched to another server address (primary: {IsPrimary})", verifiedFor is null);
        PersistAsync().Forget(_logger);
        RaiseChanged();
    }

    private async Task VerifyIdentityAsync()
    {
        var baseUri = _baseUri;
        var info = await ProbeInfoAsync(_clients.Client, baseUri, TestTimeout, CancellationToken.None).ConfigureAwait(false)
                   ?? throw HttpErrors.Network();

        Guid? known;
        lock (_lock) known = _state.InstanceId;
        if (known is null || known == info.InstanceId)
        {
            lock (_lock) _identityCheckPending = false;
            await ApplyInfoAsync(baseUri, info).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning("The configured server is a different deployment; signing out");
        await _services.GetRequiredService<IAuthService>().LogoutAsync(revokeOnServer: false).ConfigureAwait(false);
        lock (_lock)
        {
            _identityCheckPending = false;
            _state.InstanceId = info.InstanceId;
        }
        await ApplyInfoAsync(baseUri, info).ConfigureAwait(false);
        throw new ApiException(401, ErrorCodes.SessionRevoked, "Signed out: different server");
    }

    private async Task ApplyInfoAsync(Uri source, ServerInfoDto info)
    {
        var signOut = false;
        var backToPrimary = false;
        lock (_lock)
        {
            if (!SameUri(source, _baseUri)) return;
            _info = info;
            var isPrimary = SameUri(source, _primary);
            if (_state.InstanceId is null)
            {
                _state.InstanceId = info.InstanceId;
            }
            else if (_state.InstanceId != info.InstanceId)
            {
                if (isPrimary)
                {
                    // The configured address now serves another deployment (re-installed server or a new address).
                    signOut = _sessions.Current is not null;
                    _state.InstanceId = info.InstanceId;
                }
                else
                {
                    backToPrimary = true;
                }
            }

            if (!backToPrimary && _state.InstanceId == info.InstanceId)
            {
                var mirrors = info.MirrorUrls
                    .Select(m => TryNormalize(m, out var u) ? u.ToString() : null)
                    .Where(m => m is not null)
                    .Select(m => m!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _state.Mirrors[info.InstanceId] = mirrors;
                (_primary, _candidates) = ComputeCandidates(_settings.Current.Connection, _state);
            }

            if (backToPrimary)
            {
                _baseUri = _primary;
                _info = null;
                _state.ActiveUrl = _primary.ToString();
                _state.ActiveVerifiedSessionId = null;
                _connectionVersion++;
            }
        }

        if (signOut)
        {
            _logger.LogWarning("The server reports a different InstanceId; signing out");
            await _services.GetRequiredService<IAuthService>().LogoutAsync(revokeOnServer: false).ConfigureAwait(false);
        }
        await PersistAsync().ConfigureAwait(false);
        RaiseChanged();
        if (backToPrimary) RefreshInfoAsync().Forget(_logger);
    }

    private void OnSettingsChanged()
    {
        var connection = _settings.Current.Connection;
        var key = ConnectionKey(connection);
        bool bump;
        lock (_lock)
        {
            // Settings load (and raise Changed) before InitializeAsync restores the remembered state; persisting the
            // still-empty state here would wipe the InstanceId and mirrors. InitializeAsync applies the settings itself.
            if (!_initialized || key == _connectionKey) return;
            _connectionKey = key;
            var proxyRebuilt = _clients.RebuildIfProxyChanged(connection);
            var fallbackChanged = _allowTransportFallback != connection.AllowTransportFallback;
            _allowTransportFallback = connection.AllowTransportFallback;
            var oldPrimary = _primary;
            (_primary, _candidates) = ComputeCandidates(connection, _state);
            var baseChanged = false;
            if (!SameUri(oldPrimary, _primary))
            {
                _baseUri = _primary;
                _info = null;
                _state.ActiveUrl = _primary.ToString();
                _state.ActiveVerifiedSessionId = null;
                baseChanged = true;
                // A token may only follow the user to the new address once it is known to be the same deployment.
                _identityCheckPending = _sessions.Current is not null && !(_preverifiedPrimary is { } pre && SameUri(pre, _primary));
                _preverifiedPrimary = null;
            }
            else if (!_candidates.Any(c => SameUri(c, _baseUri)))
            {
                _baseUri = _primary;
                _info = null;
                _state.ActiveUrl = _primary.ToString();
                _state.ActiveVerifiedSessionId = null;
                baseChanged = true;
            }
            bump = proxyRebuilt || fallbackChanged || baseChanged;
            if (bump) _connectionVersion++;
        }

        PersistAsync().Forget(_logger);
        RaiseChanged();
        if (bump) RefreshInfoAsync().Forget(_logger);
    }

    private void SwitchToPrimaryIfNeeded()
    {
        bool changed;
        lock (_lock)
        {
            changed = !SameUri(_baseUri, _primary);
            if (changed)
            {
                _baseUri = _primary;
                _info = null;
                _state.ActiveUrl = _primary.ToString();
                _state.ActiveVerifiedSessionId = null;
                _connectionVersion++;
            }
        }
        if (changed) RaiseChanged();
    }

    private async Task PersistAsync()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_state, SkyJson.Options);
        try
        {
            await _preferences.SetAsync(StateKey, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the endpoint state");
        }
    }

    private void RaiseChanged() => EventRaiser.Raise(Changed, _logger);

    private async Task<ServerInfoDto?> ProbeInfoAsync(HttpClient client, Uri baseUri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await client.GetAsync(new Uri(baseUri, ApiRoutes.ServerInfo), cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var info = await response.Content.ReadFromJsonAsync<ServerInfoDto>(SkyJson.Options, cts.Token).ConfigureAwait(false);
            return info is { InstanceId: var id } && id != Guid.Empty ? info : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // "Can't reach the server" in the UI: the cause (TLS, DNS, timeout, parsing) only shows up here.
            _logger.LogWarning(ex, "Server info probe failed for {Server}", baseUri);
            return null;
        }
    }

    private async Task<bool> ProbeThroughputAsync(HttpClient client, Uri baseUri)
    {
        using var cts = new CancellationTokenSource(ThroughputProbeTimeout);
        try
        {
            using var response = await client.GetAsync(new Uri(baseUri, ApiRoutes.ServerProbePath(ProbeBytes)), HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                total += read;
            return total >= ProbeBytes;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> VerifyProofAsync(HttpClient client, Uri baseUri, StoredSession session, Guid instanceId)
    {
        if (string.IsNullOrEmpty(session.MediaKey)) return false;
        using var cts = new CancellationTokenSource(ProofTimeout);
        try
        {
            var nonce = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
            using var response = await client.GetAsync(new Uri(baseUri, ApiRoutes.ServerProofPath(session.SessionId, nonce)), cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var proof = await response.Content.ReadFromJsonAsync<ServerProofDto>(SkyJson.Options, cts.Token).ConfigureAwait(false);
            if (proof is null || proof.InstanceId != instanceId || string.IsNullOrEmpty(proof.Proof)) return false;
            return MediaSignature.VerifyProof(MediaSignature.DecodeKey(session.MediaKey), nonce, ProofHost(baseUri), proof.Proof);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The host name as the server sees it (Host header without port): punycode for IDN names.</summary>
    internal static string ProofHost(Uri uri) => uri.HostNameType == UriHostNameType.Dns ? uri.IdnHost : uri.Host;

    private Uri DefaultPrimary()
    {
        if (_options.UseHostOrigin && TryNormalize(_options.HostOrigin, out var origin)) return origin;
        return TryNormalize(_options.DefaultServerUrl, out var fallback) ? fallback : new Uri("http://localhost:8080/");
    }

    private (Uri Primary, IReadOnlyList<Uri> Candidates) ComputeCandidates(ConnectionSettings connection, EndpointState state)
    {
        Uri primary;
        if (_options.UseHostOrigin)
            primary = DefaultPrimary();
        else
            primary = TryNormalize(connection.CustomServerUrl, out var custom) ? custom : DefaultPrimary();

        var list = new List<Uri> { primary };
        void Add(string? url)
        {
            if (TryNormalize(url, out var uri) && !list.Any(u => SameUri(u, uri))) list.Add(uri);
        }

        // The web client is served by the API: other origins would need CORS and the page itself came from here.
        if (!_options.UseHostOrigin)
        {
            foreach (var url in connection.ExtraServerUrls) Add(url);
            foreach (var url in _options.FallbackServerUrls) Add(url);
            if (state.InstanceId is { } id && state.Mirrors.TryGetValue(id, out var mirrors))
                foreach (var url in mirrors) Add(url);
        }
        return (primary, list);
    }

    private static string ConnectionKey(ConnectionSettings connection) => string.Join('\u001e',
        connection.CustomServerUrl ?? "",
        string.Join('\u001f', connection.ExtraServerUrls),
        HttpClientProvider.ProxyKey(connection),
        connection.AllowTransportFallback);

    internal static bool SameUri(Uri a, Uri b) => string.Equals(a.AbsoluteUri, b.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    /// <summary>Absolute http(s) URL with a trailing slash, no query/fragment. Bare hosts get https.</summary>
    internal static bool TryNormalize(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host)) return false;
        var builder = new UriBuilder(parsed) { Query = "", Fragment = "" };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        builder.Port = parsed.IsDefaultPort ? -1 : parsed.Port;
        uri = builder.Uri;
        return true;
    }
}
