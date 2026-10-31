using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;
#if IOS
using Foundation;
#endif
#if IOS || MACCATALYST
using Network;
#endif

namespace SkyMessage.App.Services;

/// <summary>
/// Native HTTP stack: <see cref="SocketsHttpHandler"/> with Happy Eyeballs, connect timeouts, keep-alive and
/// the user's HTTP/SOCKS5 proxy. iOS uses NSURLSession when there is no proxy (it wakes the cellular radio and
/// follows the OS network path); with a proxy it uses NSURLSession proxy configurations on iOS 17+ and the
/// sockets handler on older versions. WebSockets always go through the sockets handler.
/// </summary>
public sealed class HttpHandlerFactory(ILogger<HttpHandlerFactory> logger) : IHttpHandlerFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    // The invoker must outlive the upgraded WebSocket connection it created.
    private static readonly ConditionalWeakTable<WebSocket, HttpMessageInvoker> SocketInvokers = new();

    public bool SupportsProxy => true;

    public HttpMessageHandler CreateHandler(ConnectionSettings settings)
    {
        var proxy = ProxyOf(settings);
#if IOS
        if (proxy is null) return CreateUrlSessionHandler(null);
        if (OperatingSystem.IsIOSVersionAtLeast(17)) return CreateUrlSessionHandler(proxy);
#endif
        return CreateSocketsHandler(proxy);
    }

    public IWebProxy? CreateProxy(ConnectionSettings settings) => ProxyOf(settings) is { } proxy ? ToWebProxy(proxy) : null;

    public Func<Uri, Action<ClientWebSocketOptions>?, CancellationToken, ValueTask<WebSocket>>? CreateWebSocketConnector(ConnectionSettings settings)
    {
        var proxy = ProxyOf(settings);
        return async (uri, configure, cancellationToken) =>
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            configure?.Invoke(socket.Options);

            // With an HttpMessageInvoker these options must be unset; the handler carries them instead.
            var cookies = socket.Options.Cookies;
            socket.Options.Cookies = null;
            socket.Options.Proxy = null;
            socket.Options.Credentials = null;
            socket.Options.UseDefaultCredentials = false;

            var handler = CreateSocketsHandler(proxy);
            if (cookies is not null)
            {
                handler.CookieContainer = cookies;
                handler.UseCookies = true;
            }
            var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(uri, invoker, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                invoker.Dispose();
                throw new WebSocketException(WebSocketError.Faulted, "WebSocket connect timed out");
            }
            catch
            {
                socket.Dispose();
                invoker.Dispose();
                throw;
            }
            SocketInvokers.AddOrUpdate(socket, invoker);
            return socket;
        };
    }

    private SocketsHttpHandler CreateSocketsHandler(ProxySettings? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = HappyEyeballs.ConnectAsync,
            ConnectTimeout = ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = 16,
            UseCookies = false,
            // Null = the OS proxy where .NET can read it (Apple); otherwise the user's proxy.
            UseProxy = true,
            Proxy = proxy is null ? null : ToWebProxy(proxy),
        };
        if (proxy is not null) logger.LogInformation("HTTP stack uses a {Type} proxy", proxy.Type);
        return handler;
    }

#if IOS
    private static HttpMessageHandler CreateUrlSessionHandler(ProxySettings? proxy)
    {
        var configuration = NSUrlSessionConfiguration.DefaultSessionConfiguration;
        // Long polling / SSE keep requests open; the default 60 s idle timeout would cut them.
        configuration.TimeoutIntervalForRequest = 120;
        configuration.TimeoutIntervalForResource = 7 * 24 * 3600;
        configuration.HttpMaximumConnectionsPerHost = 8;
        configuration.WaitsForConnectivity = false;
        configuration.RequestCachePolicy = NSUrlRequestCachePolicy.ReloadIgnoringLocalCacheData;
        if (proxy is not null && OperatingSystem.IsIOSVersionAtLeast(17) && CreateNetworkProxy(proxy) is { } config)
            configuration.ProxyConfigurations = [config];
        return new NSUrlSessionHandler(configuration)
        {
            DisableCaching = true,
            AllowAutoRedirect = true,
            UseCookies = false,
        };
    }
#endif

#if IOS || MACCATALYST
    /// <summary>Network-framework proxy (HTTP CONNECT or SOCKS5, with credentials) for NSURLSession and WKWebView (17+).</summary>
    internal static NWProxyConfig? CreateNetworkProxy(ProxySettings proxy)
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(17) && !OperatingSystem.IsMacCatalystVersionAtLeast(17)) return null;
        var endpoint = NWEndpoint.Create(proxy.Host.Trim(), proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (endpoint is null) return null;
        var config = proxy.Type == ProxyType.Socks5
            ? NWProxyConfig.CreateSocksV5(endpoint)
            : NWProxyConfig.CreateHttpConnect(endpoint, null);
        if (config is not null && !string.IsNullOrEmpty(proxy.Username)) config.SetUsernameAndPassword(proxy.Username, proxy.Password ?? "");
        return config;
    }
#endif

    internal static ProxySettings? ProxyOf(ConnectionSettings settings)
    {
        var proxy = settings.Proxy;
        if (proxy.Type == ProxyType.None || string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is <= 0 or > 65535) return null;
        // A host the URI parser rejects (e.g. "1.2.3.4:1080" typed into the host field) is as unusable as a bad
        // port; building a handler or a realtime connection with it would throw.
        return ProxyUri(proxy) is null ? null : proxy;
    }

    /// <summary>Null when the host cannot form a proxy URI (see <see cref="ProxyOf"/>, which filters those out).</summary>
    internal static WebProxy? ToWebProxy(ProxySettings proxy) => ProxyUri(proxy) is { } uri
        ? new WebProxy(uri)
        {
            BypassProxyOnLocal = false,
            Credentials = string.IsNullOrEmpty(proxy.Username) ? null : new NetworkCredential(proxy.Username, proxy.Password ?? ""),
        }
        : null;

    private static Uri? ProxyUri(ProxySettings proxy)
    {
        try
        {
            // UriBuilder brackets IPv6 literals itself.
            return new UriBuilder(proxy.Type == ProxyType.Socks5 ? "socks5" : "http", proxy.Host.Trim(), proxy.Port).Uri;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return null;
        }
    }
}
