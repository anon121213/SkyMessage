using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Per-connection typing rate limit: at most one event per <see cref="MinInterval"/>, except that a single
/// <see cref="TypingAction.Cancel"/> may follow right away (so "stopped typing" is never lost after a send).
/// Not thread-safe: SignalR invokes one hub method per connection at a time.
/// </summary>
public sealed class TypingThrottle
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(300);

    private DateTimeOffset _lastAcceptedAt = DateTimeOffset.MinValue;
    private TypingAction? _lastAction;

    public bool TryAccept(TypingAction action, DateTimeOffset now)
    {
        var withinInterval = now - _lastAcceptedAt < MinInterval;
        if (withinInterval && (action != TypingAction.Cancel || _lastAction == TypingAction.Cancel))
            return false;

        _lastAcceptedAt = now;
        _lastAction = action;
        return true;
    }
}
