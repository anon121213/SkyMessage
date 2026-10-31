namespace SkyMessage.Contracts;

/// <summary>
/// Formatting / semantic span inside message text. Offsets and lengths are in UTF-16 code units.
/// <see cref="Url"/> (TextLink) must be an absolute http/https/mailto/tel URL of at most
/// <see cref="Limits.LinkUrlMaxLength"/> chars — the server rejects anything else.
/// Mentions: the composer sends <see cref="MessageEntityType.Mention"/> with <see cref="UserId"/>; plain
/// "@username" text without a UserId is resolved by the server.
/// </summary>
public sealed record MessageEntityDto
{
    public MessageEntityType Type { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; }
    public string? Url { get; init; }
    public Guid? UserId { get; init; }
    public string? Language { get; init; }
}

public sealed record MediaDto
{
    public Guid Id { get; init; }
    public MediaKind Kind { get; init; }
    public MediaStatus Status { get; init; }
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
    public long SizeBytes { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationMs { get; init; }
    /// <summary>Voice messages: <see cref="Limits.WaveformSamples"/> amplitude values 0..255.</summary>
    public byte[]? Waveform { get; init; }
    /// <summary>Tiny (≈24px) JPEG used as a blurred placeholder while the real image loads.</summary>
    public byte[]? MiniThumbnail { get; init; }
    public bool HasThumbnail { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record ReplyPreviewDto
{
    public Guid MessageId { get; init; }
    public long Seq { get; init; }
    public Guid? SenderId { get; init; }
    public MessageType Type { get; init; }
    /// <summary>Truncated to <see cref="Limits.ReplyPreviewTextLength"/>.</summary>
    public string? Text { get; init; }
    /// <summary>First attachment (for a thumbnail), if any.</summary>
    public MediaDto? Media { get; init; }
    public bool IsDeleted { get; init; }
}

public sealed record ForwardInfoDto
{
    public Guid? FromUserId { get; init; }
    public Guid? FromChatId { get; init; }
    public string SenderName { get; init; } = "";
    public DateTimeOffset OriginalDate { get; init; }
}

/// <summary>Viewer-independent: the client checks whether its own id is in <see cref="UserIds"/>.</summary>
public sealed record ReactionDto
{
    public string Emoji { get; init; } = "";
    public int Count { get; init; }
    /// <summary>Most recent reactors, capped at <see cref="Limits.MaxReactionUserIds"/>.</summary>
    public IReadOnlyList<Guid> UserIds { get; init; } = [];
}

public sealed record SystemEventDto
{
    public SystemEventType Type { get; init; }
    public Guid? ActorId { get; init; }
    public IReadOnlyList<Guid> TargetUserIds { get; init; } = [];
    /// <summary>New title / description etc.</summary>
    public string? Text { get; init; }
    /// <summary>For <see cref="SystemEventType.MessagePinned"/>.</summary>
    public Guid? MessageId { get; init; }
    public Guid? CallId { get; init; }
}

public sealed record CallInfoDto
{
    public Guid CallId { get; init; }
    public bool IsVideo { get; init; }
    public CallStatus Status { get; init; }
    public int DurationSeconds { get; init; }
    public Guid InitiatorId { get; init; }
}

/// <summary>
/// A message. Viewer-independent, so the same instance is broadcast to every chat member.
/// Deleted-for-everyone messages are tombstones: <see cref="IsDeleted"/> = true, no text/attachments.
/// </summary>
public sealed record MessageDto
{
    public Guid Id { get; init; }
    public Guid ChatId { get; init; }
    /// <summary>Per-chat, strictly increasing, gap-free sequence number.</summary>
    public long Seq { get; init; }
    /// <summary>Null for system messages.</summary>
    public Guid? SenderId { get; init; }
    public MessageType Type { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<MessageEntityDto>? Entities { get; init; }
    public IReadOnlyList<MediaDto> Attachments { get; init; } = [];
    public ReplyPreviewDto? ReplyTo { get; init; }
    public ForwardInfoDto? Forward { get; init; }
    public IReadOnlyList<ReactionDto> Reactions { get; init; } = [];
    public SystemEventDto? SystemEvent { get; init; }
    public CallInfoDto? Call { get; init; }
    public bool IsPinned { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? EditedAt { get; init; }
    /// <summary>Bumped on every change (edit, delete, reaction, pin); used for incremental sync.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Echo of <see cref="SendMessageRequest.ClientMessageId"/> so the sender can match its pending copy.</summary>
    public Guid? ClientMessageId { get; init; }
}

public sealed record MessagesPageDto
{
    /// <summary>Ascending by seq.</summary>
    public IReadOnlyList<MessageDto> Messages { get; init; } = [];
    /// <summary>Senders and users referenced by replies, forwards and system events on this page.</summary>
    public IReadOnlyList<UserSummaryDto> Users { get; init; } = [];
    public bool HasMoreBefore { get; init; }
    public bool HasMoreAfter { get; init; }
    /// <summary>
    /// Opaque cursor for <c>GET .../messages/changes?cursor=</c>, taken when this page was read. Store it
    /// with the cached window and catch up from it later.
    /// </summary>
    public string SyncCursor { get; init; } = "";
    /// <summary>My reaction per message on this page (reaction user lists are capped).</summary>
    public IReadOnlyDictionary<Guid, string> MyReactions { get; init; } = new Dictionary<Guid, string>();
}

/// <summary>
/// Incremental sync of one chat. Items are ordered by (UpdatedAt, Id) strictly after the cursor; cursors
/// lag a few seconds behind "now", so the same change may arrive twice — dedupe by (Id, UpdatedAt).
/// </summary>
public sealed record MessageChangesDto
{
    /// <summary>Changed messages including tombstones (IsDeleted = true → remove locally).</summary>
    public IReadOnlyList<MessageDto> Messages { get; init; } = [];
    /// <summary>Messages I deleted "for me" (on any device) since the cursor.</summary>
    public IReadOnlyList<Guid> HiddenMessageIds { get; init; } = [];
    /// <summary>My current "clear history" watermark: drop everything with Seq &lt;= this.</summary>
    public long ClearedUpToSeq { get; init; }
    public string NextCursor { get; init; } = "";
    /// <summary>More changes are available: call again with <see cref="NextCursor"/> right away.</summary>
    public bool HasMore { get; init; }
}

public sealed record SendMessageRequest
{
    /// <summary>Generated by the client (never Guid.Empty); makes sending idempotent (retries return the same message).</summary>
    public Guid ClientMessageId { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<MessageEntityDto>? Entities { get; init; }
    public Guid? ReplyToMessageId { get; init; }
    /// <summary>Uploaded, Ready media owned by the sender.</summary>
    public IReadOnlyList<Guid> MediaIds { get; init; } = [];
}

public sealed record EditMessageRequest
{
    public string? Text { get; init; }
    public IReadOnlyList<MessageEntityDto>? Entities { get; init; }
}

public sealed record ForwardMessagesRequest
{
    public Guid FromChatId { get; init; }
    public IReadOnlyList<Guid> MessageIds { get; init; } = [];
    /// <summary>Idempotency keys, parallel to <see cref="MessageIds"/> (same length).</summary>
    public IReadOnlyList<Guid> ClientMessageIds { get; init; } = [];
}

public sealed record DeleteMessagesRequest
{
    public IReadOnlyList<Guid> MessageIds { get; init; } = [];
    public bool ForEveryone { get; init; }
}

public sealed record SetReactionRequest
{
    public string Emoji { get; init; } = "";
}
