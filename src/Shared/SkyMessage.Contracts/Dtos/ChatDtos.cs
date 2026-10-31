namespace SkyMessage.Contracts;

public sealed record ChatPermissionsDto
{
    public bool SendMessages { get; init; } = true;
    public bool SendMedia { get; init; } = true;
    public bool AddMembers { get; init; } = true;
    public bool PinMessages { get; init; }
    public bool ChangeInfo { get; init; }
}

/// <summary>
/// A chat as it appears in the viewer's chat list (viewer-dependent: title of a private chat is
/// the peer's name, unread counters and settings are the viewer's).
/// </summary>
public sealed record ChatSummaryDto
{
    public Guid Id { get; init; }
    public ChatType Type { get; init; }
    /// <summary>Group title, peer display/contact name for private chats, localized by the client for Saved.</summary>
    public string Title { get; init; } = "";
    public Guid? AvatarMediaId { get; init; }
    /// <summary>The other participant of a private chat.</summary>
    public UserSummaryDto? Peer { get; init; }
    public int MemberCount { get; init; }
    public ChatRole MyRole { get; init; }
    public ChatPermissionsDto Permissions { get; init; } = new();

    public MessageDto? LastMessage { get; init; }
    /// <summary>Sender of <see cref="LastMessage"/> (groups: "Alice: ..." without an extra round trip).</summary>
    public UserSummaryDto? LastMessageSender { get; init; }
    public long LastMessageSeq { get; init; }
    public DateTimeOffset LastActivityAt { get; init; }

    public int UnreadCount { get; init; }
    /// <summary>Unread messages that mention me or reply to one of my messages.</summary>
    public int UnreadMentionCount { get; init; }
    public long? FirstUnreadMentionSeq { get; init; }
    /// <summary>I have read messages up to this seq.</summary>
    public long MyLastReadSeq { get; init; }
    /// <summary>Highest seq read by anyone else (drives the "read" double check on my messages).</summary>
    public long OthersLastReadSeq { get; init; }
    /// <summary>"Clear history for me": messages with Seq &lt;= this are gone for me (drop them from caches).</summary>
    public long ClearedUpToSeq { get; init; }

    public bool IsPinned { get; init; }
    public DateTimeOffset? PinnedAt { get; init; }
    public bool IsArchived { get; init; }
    public DateTimeOffset? MutedUntil { get; init; }
    /// <summary>Private chat where either side blocked the other.</summary>
    public bool IsBlocked { get; init; }
    public Guid? ActiveCallId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Viewer-independent chat info, broadcast to all members on change.</summary>
public sealed record ChatInfoDto
{
    public Guid Id { get; init; }
    public ChatType Type { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public Guid? AvatarMediaId { get; init; }
    public int MemberCount { get; init; }
    public ChatPermissionsDto Permissions { get; init; } = new();
    public Guid? ActiveCallId { get; init; }
}

public sealed record ChatMemberDto
{
    public UserSummaryDto User { get; init; } = new();
    public ChatRole Role { get; init; }
    public DateTimeOffset JoinedAt { get; init; }
    /// <summary>For "seen by" in small groups.</summary>
    public long LastReadSeq { get; init; }
}

public sealed record ChatDetailsDto
{
    public ChatSummaryDto Summary { get; init; } = new();
    public string? Description { get; init; }
    public IReadOnlyList<ChatMemberDto> Members { get; init; } = [];
    /// <summary>Only for admins/owner of groups.</summary>
    public string? InviteToken { get; init; }
    /// <summary>Newest first.</summary>
    public IReadOnlyList<MessageDto> PinnedMessages { get; init; } = [];
    public Guid? CreatedById { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record CreatePrivateChatRequest
{
    public Guid UserId { get; init; }
}

public sealed record CreateGroupRequest
{
    public string Title { get; init; } = "";
    public IReadOnlyList<Guid> MemberIds { get; init; } = [];
    public string? Description { get; init; }
    public Guid? AvatarMediaId { get; init; }
}

/// <summary>PATCH semantics: null fields are left unchanged.</summary>
public sealed record UpdateChatRequest
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public ChatPermissionsDto? Permissions { get; init; }
}

public sealed record SetChatAvatarRequest
{
    public Guid MediaId { get; init; }
}

public sealed record AddMembersRequest
{
    public IReadOnlyList<Guid> UserIds { get; init; } = [];
}

public sealed record AddMemberFailureDto
{
    public Guid UserId { get; init; }
    public string Code { get; init; } = "";
}

public sealed record AddMembersResultDto
{
    public IReadOnlyList<Guid> Added { get; init; } = [];
    public IReadOnlyList<AddMemberFailureDto> Failed { get; init; } = [];
}

public sealed record CreateGroupResultDto
{
    public ChatSummaryDto Chat { get; init; } = new();
    /// <summary>Members that could not be added (privacy, blocks); invite them with a link instead.</summary>
    public IReadOnlyList<AddMemberFailureDto> Failed { get; init; } = [];
}

public sealed record SetMemberRoleRequest
{
    public ChatRole Role { get; init; }
}

public sealed record ChatUserSettingsDto
{
    public bool IsPinned { get; init; }
    public DateTimeOffset? PinnedAt { get; init; }
    public bool IsArchived { get; init; }
    public DateTimeOffset? MutedUntil { get; init; }
}

/// <summary>PATCH semantics: null fields are left unchanged.</summary>
public sealed record UpdateChatSettingsRequest
{
    public bool? IsPinned { get; init; }
    public bool? IsArchived { get; init; }
    /// <summary>null = no change, 0 = unmute, -1 = mute forever, &gt;0 = mute for N seconds.</summary>
    public int? MuteForSeconds { get; init; }
}

public sealed record MarkReadRequest
{
    public long UpToSeq { get; init; }
}

public sealed record ClearHistoryRequest
{
    /// <summary>Private chats only: delete for both sides.</summary>
    public bool ForEveryone { get; init; }
}

public sealed record InviteLinkDto
{
    public string Token { get; init; } = "";
}

public sealed record ChatInvitePreviewDto
{
    public string Token { get; init; } = "";
    public Guid ChatId { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public Guid? AvatarMediaId { get; init; }
    public int MemberCount { get; init; }
    public bool IsMember { get; init; }
}

public sealed record GlobalSearchResultDto
{
    /// <summary>My chats whose title / peer name matches.</summary>
    public IReadOnlyList<ChatSummaryDto> Chats { get; init; } = [];
    /// <summary>Users found by username / display name (not necessarily contacts).</summary>
    public IReadOnlyList<UserSummaryDto> Users { get; init; } = [];
    public IReadOnlyList<MessageSearchHitDto> Messages { get; init; } = [];
}

public sealed record MessageSearchHitDto
{
    public Guid ChatId { get; init; }
    public ChatType ChatType { get; init; }
    public string ChatTitle { get; init; } = "";
    public Guid? ChatAvatarMediaId { get; init; }
    public MessageDto Message { get; init; } = new();
}
