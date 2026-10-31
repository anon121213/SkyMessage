using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using SkyMessage.Contracts;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// One realtime connection that records every server event. <see cref="WaitAsync{T}"/> also sees events that
/// arrived before it was called, so there are no subscribe-after-trigger races and no sleeps.
/// </summary>
internal sealed class HubProbe : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<(string Name, object Payload)> _events = [];
    private readonly List<Waiter> _waiters = [];
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HubProbe(HubConnection connection, string label)
    {
        Connection = connection;
        Label = label;

        connection.On<MessageDto>(nameof(IRealtimeClient.MessageCreated), e => Add(nameof(IRealtimeClient.MessageCreated), e));
        connection.On<MessageDto>(nameof(IRealtimeClient.MessageUpdated), e => Add(nameof(IRealtimeClient.MessageUpdated), e));
        connection.On<MessagesDeletedEvent>(nameof(IRealtimeClient.MessagesDeleted), e => Add(nameof(IRealtimeClient.MessagesDeleted), e));
        connection.On<MessagesReadEvent>(nameof(IRealtimeClient.MessagesRead), e => Add(nameof(IRealtimeClient.MessagesRead), e));
        connection.On<TypingEvent>(nameof(IRealtimeClient.Typing), e => Add(nameof(IRealtimeClient.Typing), e));
        connection.On<ChatRefEvent>(nameof(IRealtimeClient.ChatAdded), e => Add(nameof(IRealtimeClient.ChatAdded), e));
        connection.On<ChatInfoDto>(nameof(IRealtimeClient.ChatUpdated), e => Add(nameof(IRealtimeClient.ChatUpdated), e));
        connection.On<ChatRefEvent>(nameof(IRealtimeClient.ChatRemoved), e => Add(nameof(IRealtimeClient.ChatRemoved), e));
        connection.On<ChatRefEvent>(nameof(IRealtimeClient.ChatMembersChanged), e => Add(nameof(IRealtimeClient.ChatMembersChanged), e));
        connection.On<ChatSettingsChangedEvent>(nameof(IRealtimeClient.ChatSettingsChanged), e => Add(nameof(IRealtimeClient.ChatSettingsChanged), e));
        connection.On<HistoryClearedEvent>(nameof(IRealtimeClient.HistoryCleared), e => Add(nameof(IRealtimeClient.HistoryCleared), e));
        connection.On<ChatRefEvent>(nameof(IRealtimeClient.PinnedMessagesChanged), e => Add(nameof(IRealtimeClient.PinnedMessagesChanged), e));
        connection.On<PresenceChangedEvent>(nameof(IRealtimeClient.PresenceChanged), e => Add(nameof(IRealtimeClient.PresenceChanged), e));
        connection.On<UserRefEvent>(nameof(IRealtimeClient.UserChanged), e => Add(nameof(IRealtimeClient.UserChanged), e));
        connection.On<ContactsChangedEvent>(nameof(IRealtimeClient.ContactsChanged), e => Add(nameof(IRealtimeClient.ContactsChanged), e));
        connection.On<MediaDto>(nameof(IRealtimeClient.MediaProcessed), e => Add(nameof(IRealtimeClient.MediaProcessed), e));
        connection.On<CallDto>(nameof(IRealtimeClient.IncomingCall), e => Add(nameof(IRealtimeClient.IncomingCall), e));
        connection.On<CallDto>(nameof(IRealtimeClient.CallUpdated), e => Add(nameof(IRealtimeClient.CallUpdated), e));
        connection.On<SessionRevokedEvent>(nameof(IRealtimeClient.SessionRevoked), e => Add(nameof(IRealtimeClient.SessionRevoked), e));

        connection.Closed += error =>
        {
            _closed.TrySetResult(error);
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }

    public string Label { get; }

    /// <summary>Completes when the connection closed (e.g. the server aborted a revoked session).</summary>
    public Task<Exception?> Closed => _closed.Task;

    /// <summary>
    /// Connects and returns only once the server finished <c>OnConnectedAsync</c> (groups, presence): hub invocations
    /// are dispatched after it, so a completed <c>SetActive</c> call is the barrier.
    /// </summary>
    public static async Task<HubProbe> ConnectAsync(ApiFactory factory, TestUser user, string? label = null)
    {
        var probe = new HubProbe(factory.CreateHubConnection(user.AccessToken), label ?? user.Username);
        await probe.Connection.StartAsync(SecurityKit.Ct);
        await probe.Connection.InvokeAsync(nameof(IRealtimeServer.SetActive), true, SecurityKit.Ct);
        return probe;
    }

    /// <summary>Position in the event log; pass it as <c>since</c> to wait only for events that arrive after this point.</summary>
    public int Mark()
    {
        lock (_gate) return _events.Count;
    }

    public async Task<T> WaitAsync<T>(string eventName, Func<T, bool>? match = null, TimeSpan? timeout = null, int since = 0)
    {
        Waiter waiter;
        lock (_gate)
        {
            for (var i = since; i < _events.Count; i++)
            {
                var (name, payload) = _events[i];
                if (name == eventName && payload is T typed && (match?.Invoke(typed) ?? true))
                    return typed;
            }
            waiter = new Waiter(eventName, payload => payload is T typed && (match?.Invoke(typed) ?? true));
            _waiters.Add(waiter);
        }

        try
        {
            return (T)await waiter.Completion.Task.WaitAsync(timeout ?? SecurityKit.EventTimeout, SecurityKit.Ct);
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException($"{Label}: no matching {eventName} within {(timeout ?? SecurityKit.EventTimeout).TotalSeconds}s. Received: {Describe()}");
        }
        finally
        {
            lock (_gate) _waiters.Remove(waiter);
        }
    }

    /// <summary>Snapshot of the events of one kind received so far.</summary>
    public IReadOnlyList<T> Received<T>(string eventName)
    {
        lock (_gate)
            return _events.Where(e => e.Name == eventName).Select(e => e.Payload).OfType<T>().ToList();
    }

    /// <summary>Every event received so far, for "nothing leaked" checks.</summary>
    public IReadOnlyList<(string Name, object Payload)> All()
    {
        lock (_gate) return _events.ToList();
    }

    /// <summary>
    /// Barrier for "nothing arrived" assertions: <paramref name="owner"/> (this connection's user) writes to their
    /// own Saved Messages and the echo is awaited. The server writes events to a connection in order, so anything it
    /// sent before has been received by the time the echo arrives.
    /// </summary>
    public async Task FlushAsync(TestUser owner)
    {
        var marker = "flush " + Guid.NewGuid().ToString("N");
        await owner.SendTextAsync(owner.SavedChatId, marker);
        await WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Text == marker);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Connection.DisposeAsync();
        }
        catch (Exception)
        {
            // Already closed by the server (revoked session tests).
        }
    }

    private void Add(string name, object payload)
    {
        List<Waiter> matched;
        lock (_gate)
        {
            _events.Add((name, payload));
            matched = _waiters.Where(w => w.EventName == name && w.Matches(payload)).ToList();
        }
        foreach (var waiter in matched)
            waiter.Completion.TrySetResult(payload);
    }

    private string Describe()
    {
        lock (_gate)
            return _events.Count == 0 ? "nothing" : string.Join(", ", _events.Select(e => e.Name));
    }

    private sealed record Waiter(string EventName, Func<object, bool> Matches)
    {
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
