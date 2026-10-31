using SkyMessage.Contracts;

namespace SkyMessage.Domain.Entities;

public sealed class Message
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ChatId { get; set; }
    public Chat? Chat { get; set; }
    /// <summary>Unique per chat, gap-free, see <see cref="Chat.LastMessageSeq"/>.</summary>
    public long Seq { get; set; }
    /// <summary>Null for system messages.</summary>
    public Guid? SenderId { get; set; }
    public User? Sender { get; set; }

    public MessageType Type { get; set; }
    public string? Text { get; set; }
    /// <summary>Stored as jsonb.</summary>
    public List<MessageEntity> Entities { get; set; } = [];

    public Guid? ReplyToMessageId { get; set; }
    public Message? ReplyTo { get; set; }

    public Guid? ForwardFromUserId { get; set; }
    public Guid? ForwardFromChatId { get; set; }
    public Guid? ForwardFromMessageId { get; set; }
    public string? ForwardSenderName { get; set; }
    public DateTimeOffset? ForwardOriginalDate { get; set; }

    /// <summary>jsonb; set for <see cref="MessageType.System"/>.</summary>
    public SystemEvent? SystemEvent { get; set; }
    /// <summary>jsonb; set for <see cref="MessageType.Call"/>.</summary>
    public CallLog? CallLog { get; set; }

    /// <summary>Idempotency key from the sender; unique per (SenderId, ClientMessageId).</summary>
    public Guid? ClientMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    /// <summary>Deleted for everyone: text/entities/attachments are removed, the row stays as a tombstone.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>Bumped on every change; drives <c>GET .../messages/changes?since=</c>.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public List<MessageAttachment> Attachments { get; set; } = [];
    public List<MessageReaction> Reactions { get; set; } = [];

    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>Owned collection stored in the message's jsonb "entities" column.</summary>
public sealed class MessageEntity
{
    public MessageEntityType Type { get; set; }
    public int Offset { get; set; }
    public int Length { get; set; }
    public string? Url { get; set; }
    public Guid? UserId { get; set; }
    public string? Language { get; set; }
}

/// <summary>Owned, stored as jsonb.</summary>
public sealed class SystemEvent
{
    public SystemEventType Type { get; set; }
    public Guid? ActorId { get; set; }
    public List<Guid> TargetUserIds { get; set; } = [];
    public string? Text { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? CallId { get; set; }
}

/// <summary>Owned, stored as jsonb.</summary>
public sealed class CallLog
{
    public Guid CallId { get; set; }
    public bool IsVideo { get; set; }
    public CallStatus Status { get; set; }
    public int DurationSeconds { get; set; }
    public Guid InitiatorId { get; set; }
}

public sealed class MessageAttachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MessageId { get; set; }
    public Message? Message { get; set; }
    public Guid MediaId { get; set; }
    public Media? Media { get; set; }
    public int Order { get; set; }
}

/// <summary>One reaction per user per message (setting another emoji replaces it).</summary>
public sealed class MessageReaction
{
    public Guid MessageId { get; set; }
    public Message? Message { get; set; }
    public Guid UserId { get; set; }
    public string Emoji { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Index of "this message concerns user X": Mention entities (incl. server-resolved @username) and
/// replies to X's messages. Drives unread mention badges; rows are deleted with the message tombstone.
/// </summary>
public sealed class MessageMention
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public Guid ChatId { get; set; }
    public long Seq { get; set; }
}

/// <summary>"Delete for me".</summary>
public sealed class MessageHidden
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PinnedMessage
{
    public Guid ChatId { get; set; }
    public Guid MessageId { get; set; }
    public Message? Message { get; set; }
    public Guid PinnedById { get; set; }
    public DateTimeOffset PinnedAt { get; set; }
}
