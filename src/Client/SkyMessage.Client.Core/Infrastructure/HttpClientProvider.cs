using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>
/// The shared HttpClient for API and media traffic, built from <see cref="IHttpHandlerFactory"/> with the
/// current connection settings. Rebuilt when the proxy changes; the previous client is disposed after a
/// grace period so in-flight transfers can finish. Requests always use absolute URIs (the base URI changes
/// on failover) and per-request timeouts (Timeout is infinite).
/// </summary>
internal sealed class HttpClientProvider : IDisposable
{
    private static readonly TimeSpan RetireDelay = TimeSpan.FromMinutes(2);

    private readonly IHttpHandlerFactory _factory;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<HttpClientProvider> _logger;
    private readonly object _lock = new();
    private HttpClient? _client;
    private string? _proxyKey;
    private bool _disposed;

    public HttpClientProvider(IHttpHandlerFactory factory, ISettingsService settings, TimeProvider time, ILogger<HttpClientProvider> logger)
    {
        _factory = factory;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    public HttpClient Client
    {
        get
        {
            var client = _client;
            if (client is not null) return client;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_client is null)
                {
                    var connection = _settings.Current.Connection;
                    _client = Build(connection);
                    _proxyKey = ProxyKey(connection);
                }
                return _client;
            }
        }
    }

    /// <summary>Rebuilds the client when the proxy part of <paramref name="connection"/> changed. Returns true if rebuilt.</summary>
    public bool RebuildIfProxyChanged(ConnectionSettings connection)
    {
        HttpClient? retired;
        lock (_lock)
        {
            if (_disposed) return false;
            var key = ProxyKey(connection);
            if (_client is null)
            {
                _proxyKey = key;
                return false;
            }
            if (key == _proxyKey) return false;
            retired = _client;
            _client = Build(connection);
            _proxyKey = key;
        }
        _logger.LogInformation("HTTP stack rebuilt for new proxy settings");
        RetireAsync(retired).Forget(_logger);
        return true;
    }

    /// <summary>A throwaway client for testing candidate connection settings (caller disposes).</summary>
    public HttpClient CreateTemporary(ConnectionSettings connection) => Build(connection);

    public static string ProxyKey(ConnectionSettings connection)
    {
        var p = connection.Proxy;
        if (p.Type == ProxyType.None) return "none";
        return string.Join('\u001f', p.Type, p.Host.Trim().ToLowerInvariant(), p.Port, p.Username ?? "", p.Password ?? "");
    }

    private HttpClient Build(ConnectionSettings connection) =>
        new(_factory.CreateHandler(connection), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    private async Task RetireAsync(HttpClient client)
    {
        await Task.Delay(RetireDelay, _time).ConfigureAwait(false);
        client.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _client?.Dispose();
            _client = null;
        }
    }
}
