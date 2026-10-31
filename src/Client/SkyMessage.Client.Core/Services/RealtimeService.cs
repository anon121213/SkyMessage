using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// SignalR connection to <see cref="HubRoutes.Realtime"/> (SPEC §8.1). Short drops are bridged by stateful
/// reconnect inside the WebSockets transport (buffered messages are replayed, nothing is lost). Anything longer
/// ends the hub connection; this service then reconnects with its own loop (0, 1, 2, 5, 10, 15, 30 s, then every
/// 30 s; <see cref="ReconnectNow"/> skips the wait), falls back WebSockets → Server-Sent Events → long polling when
/// allowed, fails over to another address of the deployment after repeated failures and rebuilds the connection
/// when the endpoint or the HTTP stack changes. Every new connection lost state, so <see cref="Resynced"/> follows.
/// </summary>
internal sealed class RealtimeService : IRealtimeService, IAsyncDisposable, IDisposable
{
    // Logging note: SignalR exception messages can contain the hub URL, which in browsers carries the access token
    // as a query parameter. Only exception types are logged here.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
    ];
    private static readonly TimeSpan WebSocketStartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FallbackStartTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SignedOutPoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private const int FailedCyclesBeforeFailover = 2;

    private readonly IServerEndpointService _endpoints;
    private readonly IEndpointRouting _routing;
    private readonly ITokenSource _tokens;
    private readonly IHttpHandlerFactory _handlers;
    private readonly ISettingsService _settings;
    private readonly IAppLifecycle _appLifecycle;
    private readonly INetworkStatusService _network;
    private readonly TimeProvider _time;
    private readonly ILogger<RealtimeService> _logger;
    private readonly object _lock = new();

    private CancellationTokenSource? _loopCts;
    private Task _loop = Task.CompletedTask;
    private TaskCompletionSource _wake = NewSignal();
    private HubConnection? _connection;
    private bool _connectionClosed;
    private int _connectionVersion;
    private HttpTransportType _preferredTransport = HttpTransportType.WebSockets;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private volatile string? _transportName;
    private volatile bool _isActive;
    private int _syncCount;

    public RealtimeService(
        IServerEndpointService endpoints,
        IEndpointRouting routing,
        ITokenSource tokens,
        IHttpHandlerFactory handlers,
        ISettingsService settings,
        IAppLifecycle appLifecycle,
        INetworkStatusService network,
        TimeProvider time,
        ILogger<RealtimeService> logger)
    {
        _endpoints = endpoints;
        _routing = routing;
        _tokens = tokens;
        _handlers = handlers;
        _settings = settings;
        _appLifecycle = appLifecycle;
        _network = network;
        _time = time;
        _logger = logger;
        endpoints.Changed += OnEndpointChanged;
        appLifecycle.ForegroundChanged += OnForegroundChanged;
        network.Changed += OnNetworkChanged;
        // Read after subscribing, so a change in between is not missed.
        _isActive = appLifecycle.IsForeground;
    }

    public ConnectionState State => _state;

    public string? TransportName => _transportName;

    public bool IsSyncing => Volatile.Read(ref _syncCount) > 0;

    public event Action? StateChanged;
    public event Action? SyncStateChanged;
    public event Action? Resynced;

    public event Action<MessageDto>? MessageCreated;
    public event Action<MessageDto>? MessageUpdated;
    public event Action<MessagesDeletedEvent>? MessagesDeleted;
    public event Action<MessagesReadEvent>? MessagesRead;
    public event Action<TypingEvent>? Typing;
    public event Action<ChatRefEvent>? ChatAdded;
    public event Action<ChatInfoDto>? ChatUpdated;
    public event Action<ChatRefEvent>? ChatRemoved;
    public event Action<ChatRefEvent>? ChatMembersChanged;
    public event Action<ChatSettingsChangedEvent>? ChatSettingsChanged;
    public event Action<HistoryClearedEvent>? HistoryCleared;
    public event Action<ChatRefEvent>? PinnedMessagesChanged;
    public event Action<PresenceChangedEvent>? PresenceChanged;
    public event Action<UserRefEvent>? UserChanged;
    public event Action<ContactsChangedEvent>? ContactsChanged;
    public event Action<MediaDto>? MediaProcessed;
    public event Action<CallDto>? IncomingCall;
    public event Action<CallDto>? CallUpdated;
    public event Action<SessionRevokedEvent>? SessionRevoked;

    /// <summary>Starts the connection loop and returns at once; the connection comes up in the background.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_loopCts is not null) return Task.CompletedTask;
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task loop;
        HubConnection? connection;
        lock (_lock)
        {
            cts = _loopCts;
            _loopCts = null;
            loop = _loop;
            connection = _connection;
            _connection = null;
            _wake.TrySetResult();
        }
        if (cts is null) return;

        cts.Cancel();
        try
        {
            await loop.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Realtime loop did not stop cleanly ({Error})", ex.GetType().Name);
        }
        finally
        {
            cts.Dispose();
        }

        if (connection is not null) await DisposeConnectionAsync(connection).ConfigureAwait(false);
        _transportName = null;
        SetState(ConnectionState.Disconnected);
    }

    public void ReconnectNow()
    {
        lock (_lock)
        {
            if (_loopCts is null || _state == ConnectionState.Connected) return;
            _wake.TrySetResult();
        }
    }

    public IDisposable BeginSync()
    {
        if (Interlocked.Increment(ref _syncCount) == 1)
            EventRaiser.Raise(SyncStateChanged, _logger);
        return new SyncHandle(this);
    }

    private void EndSync()
    {
        if (Interlocked.Decrement(ref _syncCount) == 0)
            EventRaiser.Raise(SyncStateChanged, _logger);
    }

    public async Task SendTypingAsync(Guid chatId, TypingAction action)
    {
        var connection = _connection;
        if (connection is not { State: HubConnectionState.Connected }) return;
        try
        {
            await connection.SendAsync(nameof(IRealtimeServer.SendTyping), chatId, action).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Typing notification was not sent ({Error})", ex.GetType().Name);
        }
    }

    public async Task SetActiveAsync(bool isActive)
    {
        _isActive = isActive;
        var connection = _connection;
        if (connection is not { State: HubConnectionState.Connected }) return;
        try
        {
            await connection.SendAsync(nameof(IRealtimeServer.SetActive), isActive).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Presence state was not sent ({Error})", ex.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Unsubscribe();
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Containers disposed synchronously: stop without blocking (the connection closes in the background).</summary>
    public void Dispose()
    {
        Unsubscribe();
        StopAsync().Forget(_logger);
    }

    private void Unsubscribe()
    {
        _endpoints.Changed -= OnEndpointChanged;
        _appLifecycle.ForegroundChanged -= OnForegroundChanged;
        _network.Changed -= OnNetworkChanged;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        var everConnected = false;
        while (!ct.IsCancellationRequested)
        {
            TaskCompletionSource wake;
            lock (_lock)
            {
                if (_wake.Task.IsCompleted) _wake = NewSignal();
                wake = _wake;
            }

            try
            {
                string? token;
                try
                {
                    token = await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("No access token for realtime yet ({Error})", ex.GetType().Name);
                    token = "";
                }

                if (token is null)
                {
                    // Signed out: nothing to connect with until a sign-in restarts the loop.
                    SetState(ConnectionState.Disconnected);
                    await WaitAsync(wake.Task, SignedOutPoll, ct).ConfigureAwait(false);
                    continue;
                }

                SetState(everConnected ? ConnectionState.Reconnecting : ConnectionState.Connecting);
                var connected = token.Length > 0 && await ConnectCycleAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                if (connected)
                {
                    failures = 0;
                    everConnected = true;
                    SetState(ConnectionState.Connected);
                    await AfterConnectedAsync().ConfigureAwait(false);
                    await WaitWhileConnectedAsync(ct).ConfigureAwait(false);
                    continue;
                }

                failures++;
                if (failures % FailedCyclesBeforeFailover == 0)
                {
                    try
                    {
                        if (await _endpoints.FailoverAsync(ct).ConfigureAwait(false))
                            continue;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Failover attempt failed ({Error})", ex.GetType().Name);
                    }
                }

                await WaitAsync(wake.Task, RetryDelays[Math.Min(failures, RetryDelays.Length - 1)], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Only StopAsync may end this loop: without it no message, typing or call would arrive until a restart
                // (StartAsync is a no-op while the loop is registered).
                _logger.LogWarning("Realtime connection cycle failed ({Error})", ex.GetType().Name);
                failures++;
                await WaitAsync(wake.Task, RetryDelays[Math.Min(failures, RetryDelays.Length - 1)], ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Tries each allowed transport once, preferred first. True when a connection is up.</summary>
    private async Task<bool> ConnectCycleAsync(CancellationToken ct)
    {
        foreach (var transport in TransportPlan())
        {
            if (ct.IsCancellationRequested) return false;
            var version = _routing.ConnectionVersion;
            HubConnection connection;
            try
            {
                connection = Build(transport);
            }
            catch (Exception ex)
            {
                // The HTTP stack cannot be set up (e.g. a proxy host the platform rejects): every transport would fail
                // the same way. A failed cycle: the loop waits and tries again (settings may be fixed meanwhile).
                _logger.LogWarning("Realtime connection could not be set up ({Error})", ex.GetType().Name);
                return false;
            }
            using var start = CancellationTokenSource.CreateLinkedTokenSource(ct);
            start.CancelAfter(transport == HttpTransportType.WebSockets ? WebSocketStartTimeout : FallbackStartTimeout);
            try
            {
                await connection.StartAsync(start.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (ct.IsCancellationRequested)
                    {
                        DisposeConnectionAsync(connection).Forget(_logger);
                        return false;
                    }
                    _connection = connection;
                    _connectionClosed = false;
                    _connectionVersion = version;
                    _preferredTransport = transport;
                }
                _transportName = transport.ToString();
                _logger.LogInformation("Realtime connected via {Transport}", _transportName);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await DisposeConnectionAsync(connection).ConfigureAwait(false);
                if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
                {
                    _logger.LogInformation("Realtime rejected the access token");
                    await RecoverTokenAsync(ct).ConfigureAwait(false);
                    return false;
                }
                // Negotiation itself failed (DNS, refused, TLS...): every transport would fail the same way.
                if (ex is HttpRequestException)
                {
                    _logger.LogDebug("Realtime negotiation failed ({Error})", ex.GetType().Name);
                    return false;
                }
                _logger.LogDebug("Realtime transport {Transport} failed ({Error})", transport, ex.GetType().Name);
            }
            catch (Exception)
            {
                await DisposeConnectionAsync(connection).ConfigureAwait(false);
                return false;
            }
        }
        return false;
    }

    private async Task RecoverTokenAsync(CancellationToken ct)
    {
        try
        {
            var token = await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (token is not null)
                await _tokens.HandleUnauthorizedAsync(token, ErrorCodes.Unauthorized, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            _logger.LogDebug("Token recovery for realtime failed ({Error})", ex.GetType().Name);
        }
    }

    private IEnumerable<HttpTransportType> TransportPlan()
    {
        if (!_settings.Current.Connection.AllowTransportFallback)
        {
            yield return HttpTransportType.WebSockets;
            yield break;
        }
        HttpTransportType preferred;
        lock (_lock) preferred = _preferredTransport;
        yield return preferred;
        foreach (var transport in new[] { HttpTransportType.WebSockets, HttpTransportType.ServerSentEvents, HttpTransportType.LongPolling })
            if (transport != preferred) yield return transport;
    }

    private async Task AfterConnectedAsync()
    {
        if (!_isActive)
            await SetActiveAsync(false).ConfigureAwait(false);
        // A new connection means missed events: stores refetch (lazily for chats not on screen).
        using (BeginSync())
            EventRaiser.Raise(Resynced, _logger);
    }

    private async Task WaitWhileConnectedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TaskCompletionSource wake;
            HubConnection? connection;
            bool closed;
            int version;
            lock (_lock)
            {
                if (_wake.Task.IsCompleted) _wake = NewSignal();
                wake = _wake;
                connection = _connection;
                closed = _connectionClosed;
                version = _connectionVersion;
            }

            if (connection is null || closed)
            {
                _logger.LogInformation("Realtime connection closed; reconnecting");
                break;
            }
            if (version != _routing.ConnectionVersion)
            {
                _logger.LogInformation("Server address or connection settings changed; reconnecting");
                break;
            }

            await WaitAsync(wake.Task, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }

        HubConnection? old;
        lock (_lock)
        {
            old = _connection;
            _connection = null;
        }
        _transportName = null;
        if (old is not null) await DisposeConnectionAsync(old).ConfigureAwait(false);
    }

    private HubConnection Build(HttpTransportType transport)
    {
        var url = _endpoints.Resolve(HubRoutes.Realtime.TrimStart('/'));
        var connectionSettings = _settings.Current.Connection;
        var proxy = _handlers.SupportsProxy ? _handlers.CreateProxy(connectionSettings) : null;
        var connector = _handlers.CreateWebSocketConnector(connectionSettings);
        var nativeStack = !OperatingSystem.IsBrowser();

        var connection = new HubConnectionBuilder()
            .WithUrl(url, transport, options =>
            {
                options.AccessTokenProvider = ProvideTokenAsync;
                options.UseStatefulReconnect = true;
                options.CloseTimeout = TimeSpan.FromSeconds(5);
                if (proxy is not null && !OperatingSystem.IsBrowser()) options.Proxy = proxy;
                if (connector is not null)
                    options.WebSocketFactory = (context, token) => ConnectWebSocketAsync(connector, context, token);
                if (nativeStack)
                    options.HttpMessageHandlerFactory = _ => _handlers.CreateHandler(connectionSettings);
            })
            .AddJsonProtocol(o => SkyJson.Configure(o.PayloadSerializerOptions))
            .Build();

        connection.ServerTimeout = TimeSpan.FromSeconds(40);
        connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
        Subscribe(connection);
        connection.Closed += error => OnClosed(connection, error);
        return connection;
    }

    private async Task<string?> ProvideTokenAsync()
    {
        var token = await _tokens.GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false);
        if (token is not null)
            await _routing.EnsureTrustedAsync(CancellationToken.None).ConfigureAwait(false);
        return token;
    }

    private async ValueTask<WebSocket> ConnectWebSocketAsync(
        Func<Uri, Action<ClientWebSocketOptions>?, CancellationToken, ValueTask<WebSocket>> connector,
        WebSocketConnectionContext context,
        CancellationToken cancellationToken)
    {
        // A custom factory replaces SignalR's own, which would have added the bearer header itself.
        var token = await ProvideTokenAsync().ConfigureAwait(false);
        return await connector(context.Uri, options =>
        {
            // Hosts only hand out a connector on native stacks; browsers cannot set WebSocket headers.
            if (!string.IsNullOrEmpty(token) && !OperatingSystem.IsBrowser())
                options.SetRequestHeader("Authorization", "Bearer " + token);
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Subscribe(HubConnection c)
    {
        c.On<MessageDto>(nameof(IRealtimeClient.MessageCreated), e => EventRaiser.Raise(MessageCreated, e, _logger));
        c.On<MessageDto>(nameof(IRealtimeClient.MessageUpdated), e => EventRaiser.Raise(MessageUpdated, e, _logger));
        c.On<MessagesDeletedEvent>(nameof(IRealtimeClient.MessagesDeleted), e => EventRaiser.Raise(MessagesDeleted, e, _logger));
        c.On<MessagesReadEvent>(nameof(IRealtimeClient.MessagesRead), e => EventRaiser.Raise(MessagesRead, e, _logger));
        c.On<TypingEvent>(nameof(IRealtimeClient.Typing), e => EventRaiser.Raise(Typing, e, _logger));
        c.On<ChatRefEvent>(nameof(IRealtimeClient.ChatAdded), e => EventRaiser.Raise(ChatAdded, e, _logger));
        c.On<ChatInfoDto>(nameof(IRealtimeClient.ChatUpdated), e => EventRaiser.Raise(ChatUpdated, e, _logger));
        c.On<ChatRefEvent>(nameof(IRealtimeClient.ChatRemoved), e => EventRaiser.Raise(ChatRemoved, e, _logger));
        c.On<ChatRefEvent>(nameof(IRealtimeClient.ChatMembersChanged), e => EventRaiser.Raise(ChatMembersChanged, e, _logger));
        c.On<ChatSettingsChangedEvent>(nameof(IRealtimeClient.ChatSettingsChanged), e => EventRaiser.Raise(ChatSettingsChanged, e, _logger));
        c.On<HistoryClearedEvent>(nameof(IRealtimeClient.HistoryCleared), e => EventRaiser.Raise(HistoryCleared, e, _logger));
        c.On<ChatRefEvent>(nameof(IRealtimeClient.PinnedMessagesChanged), e => EventRaiser.Raise(PinnedMessagesChanged, e, _logger));
        c.On<PresenceChangedEvent>(nameof(IRealtimeClient.PresenceChanged), e => EventRaiser.Raise(PresenceChanged, e, _logger));
        c.On<UserRefEvent>(nameof(IRealtimeClient.UserChanged), e => EventRaiser.Raise(UserChanged, e, _logger));
        c.On<ContactsChangedEvent>(nameof(IRealtimeClient.ContactsChanged), e => EventRaiser.Raise(ContactsChanged, e, _logger));
        c.On<MediaDto>(nameof(IRealtimeClient.MediaProcessed), e => EventRaiser.Raise(MediaProcessed, e, _logger));
        c.On<CallDto>(nameof(IRealtimeClient.IncomingCall), e => EventRaiser.Raise(IncomingCall, e, _logger));
        c.On<CallDto>(nameof(IRealtimeClient.CallUpdated), e => EventRaiser.Raise(CallUpdated, e, _logger));
        c.On<SessionRevokedEvent>(nameof(IRealtimeClient.SessionRevoked), e => EventRaiser.Raise(SessionRevoked, e, _logger));
    }

    private Task OnClosed(HubConnection connection, Exception? error)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(connection, _connection)) return Task.CompletedTask;
            _connectionClosed = true;
            _wake.TrySetResult();
        }
        if (error is not null) _logger.LogInformation("Realtime connection lost: {Reason}", error.GetType().Name);
        return Task.CompletedTask;
    }

    private void OnEndpointChanged()
    {
        lock (_lock)
        {
            if (_loopCts is null) return;
            if (_connection is not null && _connectionVersion == _routing.ConnectionVersion) return;
            _wake.TrySetResult();
        }
    }

    private void OnForegroundChanged(bool foreground)
    {
        SetActiveAsync(foreground).Forget(_logger);
        if (foreground) ReconnectNow();
    }

    private void OnNetworkChanged()
    {
        if (!_network.IsConnected) return;
        // A different network may let WebSockets through again.
        lock (_lock) _preferredTransport = HttpTransportType.WebSockets;
        ReconnectNow();
    }

    private async Task WaitAsync(Task wake, TimeSpan delay, CancellationToken ct)
    {
        if (delay == TimeSpan.Zero || wake.IsCompleted || ct.IsCancellationRequested) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = delay == Timeout.InfiniteTimeSpan
            ? Task.Delay(Timeout.Infinite, cts.Token)
            : Task.Delay(delay, _time, cts.Token);
        await Task.WhenAny(wake, delayTask).ConfigureAwait(false);
        cts.Cancel();
        try
        {
            await delayTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DisposeConnectionAsync(HubConnection connection)
    {
        try
        {
            using var timeout = new CancellationTokenSource(StopTimeout);
            await connection.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Stopping a realtime connection failed ({Error})", ex.GetType().Name);
        }
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Disposing a realtime connection failed ({Error})", ex.GetType().Name);
        }
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state) return;
        _state = state;
        EventRaiser.Raise(StateChanged, _logger);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SyncHandle(RealtimeService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndSync();
        }
    }
}
