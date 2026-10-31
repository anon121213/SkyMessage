using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Api.Hubs;
using SkyMessage.Contracts;

namespace SkyMessage.Api.IntegrationTests.Identity;

/// <summary>
/// One realtime connection that records the identity events it receives. Positive checks await an event with a
/// timeout; negative checks first <see cref="FlushAsync"/> (a marker sent through the same connection, so
/// everything the server sent earlier has arrived) and then look at what was recorded.
/// </summary>
internal sealed class RealtimeProbe : IAsyncDisposable
{
    private readonly ApiFactory _factory;
    private readonly Lock _gate = new();
    private readonly List<object> _events = [];
    private readonly List<(Func<object, bool> Match, TaskCompletionSource<object> Done)> _waiters = [];
    private readonly HashSet<Guid> _markers = [];
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RealtimeProbe(ApiFactory factory, string accessToken)
    {
        _factory = factory;
        Hub = factory.CreateHubConnection(accessToken);
        Record<TypingEvent>(nameof(IRealtimeClient.Typing));
        Record<PresenceChangedEvent>(nameof(IRealtimeClient.PresenceChanged));
        Record<UserRefEvent>(nameof(IRealtimeClient.UserChanged));
        Record<ContactsChangedEvent>(nameof(IRealtimeClient.ContactsChanged));
        Record<SessionRevokedEvent>(nameof(IRealtimeClient.SessionRevoked));
        Hub.Closed += error =>
        {
            _closed.TrySetResult(error);
            return Task.CompletedTask;
        };
    }

    public HubConnection Hub { get; }

    /// <summary>Completes when the connection closed (the server aborted it or it was stopped).</summary>
    public Task<Exception?> Closed => _closed.Task;

    public static Task<RealtimeProbe> ConnectAsync(TestUser user) => ConnectAsync(user.Factory, user.AccessToken);

    public static async Task<RealtimeProbe> ConnectAsync(ApiFactory factory, string accessToken)
    {
        var probe = new RealtimeProbe(factory, accessToken);
        try
        {
            await probe.Hub.StartAsync(IdentityKit.Ct);
            // The handshake completes before the hub's OnConnectedAsync runs; hub methods are dispatched only
            // after it finished. So after this call the server has registered the connection (session group,
            // connection registry, presence).
            await probe.Hub.InvokeAsync(nameof(IRealtimeServer.SetActive), true, IdentityKit.Ct);
            return probe;
        }
        catch
        {
            await probe.DisposeAsync();
            throw;
        }
    }

    public Task SendTypingAsync(Guid chatId, TypingAction action = TypingAction.Typing) =>
        Hub.InvokeAsync(nameof(IRealtimeServer.SendTyping), chatId, action, IdentityKit.Ct);

    public Task SetActiveAsync(bool isActive) =>
        Hub.InvokeAsync(nameof(IRealtimeServer.SetActive), isActive, IdentityKit.Ct);

    public IReadOnlyList<T> Received<T>(Func<T, bool>? match = null) where T : class
    {
        lock (_gate)
            return _events.OfType<T>().Where(e => !IsMarker(e) && (match?.Invoke(e) ?? true)).ToList();
    }

    public async Task<T> WaitForAsync<T>(Func<T, bool>? match = null, TimeSpan? timeout = null) where T : class
    {
        Task<object> pending;
        lock (_gate)
        {
            var existing = _events.OfType<T>().FirstOrDefault(e => !IsMarker(e) && (match?.Invoke(e) ?? true));
            if (existing is not null) return existing;
            var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((o => o is T t && !IsMarker(t) && (match?.Invoke(t) ?? true), done));
            pending = done.Task;
        }

        var limit = timeout ?? IdentityKit.EventTimeout;
        try
        {
            return (T)await pending.WaitAsync(limit, IdentityKit.Ct);
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException($"No matching {typeof(T).Name} within {limit.TotalSeconds:0.#} s. Received: {Describe()}");
        }
    }

    /// <summary>
    /// Sends a marker to this very connection and waits for it. SignalR keeps the order per connection, so
    /// afterwards every event the server sent before the marker has been recorded.
    /// </summary>
    public async Task FlushAsync()
    {
        var marker = Guid.NewGuid();
        Task<object> pending;
        lock (_gate)
        {
            _markers.Add(marker);
            var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((o => o is UserRefEvent u && u.UserId == marker, done));
            pending = done.Task;
        }

        var hub = _factory.Services.GetRequiredService<IHubContext<RealtimeHub, IRealtimeClient>>();
        await hub.Clients.Client(Hub.ConnectionId!).UserChanged(new UserRefEvent { UserId = marker });
        await pending.WaitAsync(IdentityKit.EventTimeout, IdentityKit.Ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Hub.DisposeAsync();
        }
        catch
        {
            // Already closed by the server.
        }
    }

    private void Record<T>(string method) where T : class => Hub.On<T>(method, payload => Add(payload));

    private void Add(object payload)
    {
        lock (_gate)
        {
            _events.Add(payload);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Match(payload)) continue;
                _waiters[i].Done.TrySetResult(payload);
                _waiters.RemoveAt(i);
            }
        }
    }

    private bool IsMarker(object e) => e is UserRefEvent u && _markers.Contains(u.UserId);

    private string Describe()
    {
        lock (_gate)
        {
            var real = _events.Where(e => !IsMarker(e)).ToList();
            return real.Count == 0 ? "nothing" : string.Join("; ", real.Select(e => e.ToString()));
        }
    }
}
