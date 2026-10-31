using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Api.Hubs;
using SkyMessage.Contracts;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>
/// One realtime connection of a test device that records the call-related events it receives. Positive checks
/// await a matching event with a timeout; negative checks call <see cref="FlushAsync"/> first (a marker sent
/// through the same connection: SignalR keeps the order per connection, so everything sent earlier has arrived).
/// </summary>
internal sealed class CallHub : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _host;
    private readonly Lock _gate = new();
    private readonly List<(string Name, object Payload)> _events = [];
    private readonly List<(Func<string, object, bool> Match, TaskCompletionSource<object> Done)> _waiters = [];
    private readonly HashSet<Guid> _markers = [];

    private CallHub(CallUser user)
    {
        _host = user.Host;
        User = user;
        Connection = new HubConnectionBuilder()
            .WithUrl(new Uri(user.Host.Server.BaseAddress, HubRoutes.Realtime.TrimStart('/')), o =>
            {
                o.HttpMessageHandlerFactory = _ => user.Host.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(user.AccessToken);
            })
            .AddJsonProtocol(o => SkyJson.Configure(o.PayloadSerializerOptions))
            .Build();

        Record<CallDto>(nameof(IRealtimeClient.IncomingCall));
        Record<CallDto>(nameof(IRealtimeClient.CallUpdated));
        Record<ChatInfoDto>(nameof(IRealtimeClient.ChatUpdated));
        Record<MessageDto>(nameof(IRealtimeClient.MessageCreated));
        Record<ChatRefEvent>(nameof(IRealtimeClient.ChatRemoved));
        Record<UserRefEvent>(nameof(IRealtimeClient.UserChanged));
    }

    public CallUser User { get; }

    public HubConnection Connection { get; }

    public static async Task<CallHub> ConnectAsync(CallUser user)
    {
        var hub = new CallHub(user);
        await hub.Connection.StartAsync(CallKit.Ct);
        // The handshake completes before the server registered the connection; hub methods are dispatched only
        // after OnConnectedAsync finished, so after this call user-targeted events reach this connection.
        await hub.Connection.InvokeAsync(nameof(IRealtimeServer.SetActive), true, CallKit.Ct);
        return hub;
    }

    // ---- waiting for events ----

    /// <summary>A position in the event stream: waits given it only look at events received after it.</summary>
    public int Checkpoint()
    {
        lock (_gate) return _events.Count;
    }

    public async Task<T> WaitAsync<T>(string eventName, Func<T, bool> match, int after = 0, TimeSpan? timeout = null) where T : class
    {
        Task<object> pending;
        lock (_gate)
        {
            for (var i = after; i < _events.Count; i++)
            {
                var (name, payload) = _events[i];
                if (name == eventName && payload is T t && !IsMarker(payload) && match(t)) return t;
            }
            var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(((name, payload) => name == eventName && payload is T t && !IsMarker(payload) && match(t), done));
            pending = done.Task;
        }

        var limit = timeout ?? CallKit.EventTimeout;
        try
        {
            return (T)await pending.WaitAsync(limit, CallKit.Ct);
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException(
                $"{User.DisplayName}: no matching {eventName} within {limit.TotalSeconds:0.#} s. Received: {Describe()}");
        }
    }

    public Task<CallDto> IncomingCallAsync(Guid callId) =>
        WaitAsync<CallDto>(nameof(IRealtimeClient.IncomingCall), c => c.Id == callId);

    /// <param name="after">A <see cref="Checkpoint"/>: ignore what arrived before it (earlier states may match too).</param>
    public Task<CallDto> CallUpdatedAsync(Guid callId, Func<CallDto, bool> match, int after = 0) =>
        WaitAsync<CallDto>(nameof(IRealtimeClient.CallUpdated), c => c.Id == callId && match(c), after);

    public Task<CallDto> CallStatusAsync(Guid callId, CallStatus status) =>
        CallUpdatedAsync(callId, c => c.Status == status);

    public Task<ChatInfoDto> ChatUpdatedAsync(Guid chatId, Func<ChatInfoDto, bool> match, int after = 0) =>
        WaitAsync<ChatInfoDto>(nameof(IRealtimeClient.ChatUpdated), c => c.Id == chatId && match(c), after);

    public Task<MessageDto> MessageCreatedAsync(Guid chatId, Func<MessageDto, bool> match) =>
        WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.ChatId == chatId && match(m));

    /// <summary>The call-log message of a finished private call.</summary>
    public Task<MessageDto> CallLogAsync(Guid chatId, Guid callId) =>
        MessageCreatedAsync(chatId, m => m.Type == MessageType.Call && m.Call?.CallId == callId);

    /// <summary>A group call service message (GroupCallStarted / GroupCallEnded).</summary>
    public Task<MessageDto> SystemEventAsync(Guid chatId, SystemEventType type, Guid callId) =>
        MessageCreatedAsync(chatId, m => m.Type == MessageType.System && m.SystemEvent?.Type == type && m.SystemEvent.CallId == callId);

    // ---- what arrived ----

    public IReadOnlyList<T> Received<T>(string eventName, Func<T, bool>? match = null) where T : class
    {
        lock (_gate)
        {
            return _events
                .Where(e => e.Name == eventName && !IsMarker(e.Payload))
                .Select(e => e.Payload)
                .OfType<T>()
                .Where(t => match?.Invoke(t) ?? true)
                .ToList();
        }
    }

    public IReadOnlyList<CallDto> CallEvents(string eventName, Guid callId) =>
        Received<CallDto>(eventName, c => c.Id == callId);

    /// <summary>
    /// Sends a marker to this very connection and waits for it, so every event the server sent before has been
    /// recorded. Use before asserting that something did not arrive.
    /// </summary>
    public async Task FlushAsync()
    {
        var marker = Guid.NewGuid();
        Task<object> pending;
        lock (_gate)
        {
            _markers.Add(marker);
            var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(((_, payload) => payload is UserRefEvent u && u.UserId == marker, done));
            pending = done.Task;
        }

        var hub = _host.Services.GetRequiredService<IHubContext<RealtimeHub, IRealtimeClient>>();
        await hub.Clients.Client(Connection.ConnectionId!).UserChanged(new UserRefEvent { UserId = marker });
        await pending.WaitAsync(CallKit.EventTimeout, CallKit.Ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Connection.DisposeAsync();
        }
        catch
        {
            // Already closed by the server or the host is gone.
        }
    }

    private void Record<T>(string method) where T : class => Connection.On<T>(method, payload => Add(method, payload));

    private void Add(string name, object payload)
    {
        lock (_gate)
        {
            _events.Add((name, payload));
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Match(name, payload)) continue;
                _waiters[i].Done.TrySetResult(payload);
                _waiters.RemoveAt(i);
            }
        }
    }

    private bool IsMarker(object payload) => payload is UserRefEvent u && _markers.Contains(u.UserId);

    private string Describe()
    {
        lock (_gate)
        {
            var real = _events.Where(e => !IsMarker(e.Payload)).ToList();
            return real.Count == 0 ? "nothing" : string.Join("; ", real.Select(e => $"{e.Name} {Summarize(e.Payload)}"));
        }
    }

    private static string Summarize(object payload) => payload switch
    {
        CallDto c => $"{{call {c.Id:N} {c.Status} [{string.Join(", ", c.Participants.Select(p => $"{p.UserId:N}={p.Status}"))}]}}",
        ChatInfoDto c => $"{{chat {c.Id:N} activeCall={c.ActiveCallId}}}",
        MessageDto m => $"{{message {m.Type} seq={m.Seq} call={m.Call?.CallId}/{m.Call?.Status} system={m.SystemEvent?.Type}}}",
        _ => payload.ToString() ?? "",
    };
}
