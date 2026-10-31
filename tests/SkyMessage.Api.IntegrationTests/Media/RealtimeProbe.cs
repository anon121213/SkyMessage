using Microsoft.AspNetCore.SignalR.Client;
using SkyMessage.Contracts;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>Collects events of one kind and lets a test await the first one matching a predicate.</summary>
internal sealed class EventRecorder<T>
{
    private readonly Lock _gate = new();
    private readonly List<T> _items = [];
    private readonly List<(Func<T, bool> Match, TaskCompletionSource<T> Result)> _waiters = [];

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    public void Add(T item)
    {
        lock (_gate)
        {
            _items.Add(item);
            foreach (var waiter in _waiters.Where(w => w.Match(item)).ToList())
            {
                _waiters.Remove(waiter);
                waiter.Result.TrySetResult(item);
            }
        }
    }

    public async Task<T> WaitAsync(Func<T, bool> match, TimeSpan? timeout = null)
    {
        TaskCompletionSource<T> result;
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (match(item)) return item;
            }
            result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, result));
        }

        try
        {
            return await result.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60), MediaTestKit.Ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No matching {typeof(T).Name} arrived; received: {string.Join(", ", Items)}");
        }
    }
}

/// <summary>A realtime connection of one session (long polling over the test server) recording media events.</summary>
internal sealed class RealtimeProbe : IAsyncDisposable
{
    private RealtimeProbe(HubConnection connection) => Connection = connection;

    public HubConnection Connection { get; }

    public EventRecorder<MediaDto> MediaProcessed { get; } = new();

    public static async Task<RealtimeProbe> ConnectAsync(ApiFactory factory, TestUser user)
    {
        var probe = new RealtimeProbe(factory.CreateHubConnection(user.Auth.AccessToken));
        probe.Connection.On<MediaDto>(nameof(IRealtimeClient.MediaProcessed), probe.MediaProcessed.Add);
        await probe.Connection.StartAsync(MediaTestKit.Ct);
        // Hub invocations run only after the server finished OnConnectedAsync, so once this returns the connection
        // is registered and reachable through the user's id.
        await probe.Connection.InvokeAsync(nameof(IRealtimeServer.SetActive), true, MediaTestKit.Ct);
        return probe;
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
