namespace SkyMessage.Contracts;

/// <summary>
/// Server → client events on <see cref="HubRoutes.Realtime"/>. Method names are the SignalR
/// method names (clients subscribe with <c>nameof(IRealtimeClient.X)</c>). Every method takes a
/// single payload argument so payloads can grow without breaking clients.
/// </summary>
public interface IRealtimeClient
{
    /// <summary>New message in a chat the user belongs to (also echoed to the sender's devices).</summary>
    Task MessageCreated(MessageDto message);

    /// <summary>Edit, reaction change, pin change or tombstone of an existing message.</summary>
    Task MessageUpdated(MessageDto message);

    Task MessagesDeleted(MessagesDeletedEvent e);

    /// <summary>Someone (possibly the user on another device) read a chat up to a seq.</summary>
    Task MessagesRead(MessagesReadEvent e);

    Task Typing(TypingEvent e);

    /// <summary>The user got a new chat (created, added to a group, first private message).</summary>
    Task ChatAdded(ChatRefEvent e);

    Task ChatUpdated(ChatInfoDto chat);

    /// <summary>The user left / was removed / chat deleted.</summary>
    Task ChatRemoved(ChatRefEvent e);

    Task ChatMembersChanged(ChatRefEvent e);

    /// <summary>Pin / archive / mute changed on another device of the same user.</summary>
    Task ChatSettingsChanged(ChatSettingsChangedEvent e);

    /// <summary>The user rearranged their pinned chats (sent to all of the user's devices, the acting one included).</summary>
    Task PinnedChatsReordered(PinnedChatsOrderDto order);

    Task HistoryCleared(HistoryClearedEvent e);

    Task PinnedMessagesChanged(ChatRefEvent e);

    Task PresenceChanged(PresenceChangedEvent e);

    /// <summary>
    /// Profile/privacy/phone/avatar of a related user changed; refetch it. Also sent to the user's own
    /// devices (UserId == me → refetch api/users/me).
    /// </summary>
    Task UserChanged(UserRefEvent e);

    /// <summary>The user's contacts or blocks changed on another device; refetch.</summary>
    Task ContactsChanged(ContactsChangedEvent e);

    /// <summary>Sent to the uploader when server-side processing finished (Ready or Failed).</summary>
    Task MediaProcessed(MediaDto media);

    Task IncomingCall(CallDto call);

    /// <summary>Any call state change (accepted, declined, participant joined/left, ended).</summary>
    Task CallUpdated(CallDto call);

    /// <summary>This session was terminated; the client must sign out.</summary>
    Task SessionRevoked(SessionRevokedEvent e);
}

/// <summary>Client → server hub methods.</summary>
public interface IRealtimeServer
{
    Task SendTyping(Guid chatId, TypingAction action);

    /// <summary>App went to foreground (true) or background (false); drives online presence.</summary>
    Task SetActive(bool isActive);
}

public sealed record ChatRefEvent
{
    public Guid ChatId { get; init; }
}

public sealed record UserRefEvent
{
    public Guid UserId { get; init; }
}

public sealed record ContactsChangedEvent
{
    public bool BlocksChanged { get; init; }
}

public sealed record MessagesDeletedEvent
{
    public Guid ChatId { get; init; }
    public IReadOnlyList<Guid> MessageIds { get; init; } = [];
    /// <summary>False = deleted only for this user (sent to the user's own devices).</summary>
    public bool ForEveryone { get; init; }
}

public sealed record MessagesReadEvent
{
    public Guid ChatId { get; init; }
    public Guid UserId { get; init; }
    public long UpToSeq { get; init; }
}

public sealed record TypingEvent
{
    public Guid ChatId { get; init; }
    public Guid UserId { get; init; }
    public TypingAction Action { get; init; }
}

public sealed record ChatSettingsChangedEvent
{
    public Guid ChatId { get; init; }
    public ChatUserSettingsDto Settings { get; init; } = new();
}

public sealed record HistoryClearedEvent
{
    public Guid ChatId { get; init; }
    public long UpToSeq { get; init; }
    public bool ForEveryone { get; init; }
}

public sealed record PresenceChangedEvent
{
    public Guid UserId { get; init; }
    public PresenceDto Presence { get; init; } = new();
}

public sealed record SessionRevokedEvent
{
    public Guid SessionId { get; init; }
}
