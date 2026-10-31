using SkyMessage.Contracts;

namespace SkyMessage.Domain.Entities;

public sealed class Chat
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public ChatType Type { get; set; }
    /// <summary>Groups only.</summary>
    public string? Title { get; set; }
    public string? Description { get; set; }
    public Guid? AvatarMediaId { get; set; }
    /// <summary>
    /// Uniqueness key: "p:{lowerUserId:N}:{higherUserId:N}" for private chats, "s:{userId:N}" for
    /// Saved messages, null for groups. Unique index prevents duplicate private chats.
    /// </summary>
    public string? PrivateKey { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Seq of the newest message. New messages get Seq = ++LastMessageSeq, allocated with an atomic
    /// UPDATE ... RETURNING so seqs are gap-free and ordered per chat.
    /// </summary>
    public long LastMessageSeq { get; set; }
    public Guid? LastMessageId { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int MemberCount { get; set; }

    public GroupPermissions Permissions { get; set; } = new();
    /// <summary>Active group invite link token (unique); null when there is no link.</summary>
    public string? InviteToken { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>
    /// No forwarding, copying or saving of this chat's messages. Groups: switched by admins. Private chats: kept equal
    /// to "<see cref="ProtectContentBy"/> is not empty", so readers never need the list.
    /// </summary>
    public bool ProtectContent { get; set; }
    /// <summary>Private chats: the participants who turned protection on; each can only take back their own.</summary>
    public List<Guid> ProtectContentBy { get; set; } = [];

    public List<ChatMember> Members { get; set; } = [];

    public static string PrivateKeyFor(Guid a, Guid b)
    {
        var (lo, hi) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        return $"p:{lo:N}:{hi:N}";
    }

    public static string SavedKeyFor(Guid userId) => $"s:{userId:N}";
}

/// <summary>What ordinary members of a group may do (admins and the owner may always do everything).</summary>
public sealed class GroupPermissions
{
    public bool SendMessages { get; set; } = true;
    public bool SendMedia { get; set; } = true;
    public bool AddMembers { get; set; } = true;
    public bool PinMessages { get; set; }
    public bool ChangeInfo { get; set; }
}

/// <summary>
/// A user removed from a group by an admin. Blocks re-joining through invite links and re-adding by
/// ordinary members; an admin adding the user explicitly lifts the ban.
/// </summary>
public sealed class ChatBan
{
    public Guid ChatId { get; set; }
    public Guid UserId { get; set; }
    public Guid BannedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ChatMember
{
    public Guid ChatId { get; set; }
    public Chat? Chat { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public ChatRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public Guid? InvitedById { get; set; }

    /// <summary>The member has read messages up to this seq.</summary>
    public long LastReadSeq { get; set; }
    /// <summary>"Clear history for me": messages with Seq &lt;= this are hidden for this member.</summary>
    public long ClearedUpToSeq { get; set; }

    public DateTimeOffset? MutedUntil { get; set; }
    public bool IsPinned { get; set; }
    public DateTimeOffset? PinnedAt { get; set; }
    /// <summary>Place among the member's pinned chats: higher comes first, a new pin gets the highest; 0 when not pinned.</summary>
    public long PinOrder { get; set; }
    public bool IsArchived { get; set; }
    /// <summary>Private chat deleted "for me"; becomes visible again when a new message arrives.</summary>
    public bool IsHidden { get; set; }
    /// <summary>Private chats: the member closed the "add to contacts / block" bar.</summary>
    public bool ContactBarDismissed { get; set; }
}
