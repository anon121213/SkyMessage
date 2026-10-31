using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// My order of pinned chats on this device: the same arrangement the server makes for
/// <see cref="ApiRoutes.Chats.PinnedOrder"/>, so a drag shows its result before the answer comes back.
/// </summary>
public static class PinnedOrderRules
{
    /// <summary>
    /// The top-first order after a reorder request: the requested chats that are pinned take the places the requested
    /// chats held, in the requested order; pinned chats left out (the other list — main vs archive — or pinned meanwhile
    /// on another device) keep their places. Unknown and repeated ids are ignored.
    /// </summary>
    public static IReadOnlyList<Guid> Arrange(IReadOnlyList<Guid> currentTopFirst, IReadOnlyList<Guid> requestedTopFirst)
    {
        var pinned = currentTopFirst.ToHashSet();
        var requested = requestedTopFirst.Where(pinned.Contains).Distinct().ToList();
        var moved = requested.ToHashSet();
        var next = new Queue<Guid>(requested);
        return currentTopFirst.Select(id => moved.Contains(id) ? next.Dequeue() : id).ToList();
    }

    /// <summary>
    /// Every pinned chat's PinOrder after the request (count … 1, top first), like the server numbers them. The current
    /// order is the chat list's: PinOrder, then pin time, then activity.
    /// </summary>
    public static IReadOnlyList<ChatPinOrderDto> Renumber(IEnumerable<ChatSummaryDto> chats, IReadOnlyList<Guid> requestedTopFirst)
    {
        var current = chats.Where(c => c.IsPinned)
            .OrderByDescending(c => c.PinOrder)
            .ThenByDescending(c => c.PinnedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(c => c.LastActivityAt)
            .ThenByDescending(c => c.Id)
            .Select(c => c.Id)
            .ToList();
        var arranged = Arrange(current, requestedTopFirst);
        return arranged.Select((id, i) => new ChatPinOrderDto { ChatId = id, PinOrder = arranged.Count - i }).ToList();
    }

    /// <summary>A new pin goes on top of my other pins.</summary>
    public static long NextPin(IEnumerable<ChatSummaryDto> chats) =>
        Math.Max(chats.Where(c => c.IsPinned).Select(c => c.PinOrder).DefaultIfEmpty(0).Max(), 0) + 1;

    /// <summary>
    /// The pinned chats of a list (top first) after the one at <paramref name="from"/> was dragged to <paramref name="to"/>.
    /// </summary>
    public static IReadOnlyList<Guid> Move(IReadOnlyList<Guid> pinnedTopFirst, int from, int to)
    {
        var list = pinnedTopFirst.ToList();
        if (from < 0 || from >= list.Count) return list;
        var id = list[from];
        list.RemoveAt(from);
        list.Insert(Math.Clamp(to, 0, list.Count), id);
        return list;
    }
}

/// <summary>
/// The "add to contacts / block" bar of a private chat: shown to me while the other person is not in my contacts and
/// I have not blocked them, until I close it (remembered on the server per chat, <see cref="ChatSummaryDto.ContactBarDismissed"/>).
/// </summary>
public static class ContactBarRules
{
    /// <param name="chat">The chat on screen.</param>
    /// <param name="peer">The other person, as the user store knows them now (contact names, deleted accounts).</param>
    /// <param name="me">The signed-in user.</param>
    /// <param name="contactsKnown">The contact and block lists have been loaded (from the cache or the server).</param>
    /// <param name="isContact">The peer is in my contacts.</param>
    /// <param name="blockedByMe">I blocked the peer.</param>
    public static bool ShouldShow(ChatSummaryDto? chat, UserSummaryDto? peer, Guid? me, bool contactsKnown, bool isContact, bool blockedByMe)
    {
        if (chat is not { Type: ChatType.Private } || chat.ContactBarDismissed) return false;
        if (peer is null || peer.IsDeleted || me is not { } myId || peer.Id == myId) return false;
        // Not before the lists are known: the bar must not flash up for a contact.
        if (!contactsKnown) return false;
        return !isContact && !peer.IsContact && !blockedByMe;
    }
}

/// <summary>How content protection looks to one member and what they may change about it.</summary>
/// <param name="IsProtected">No forwarding, copying, saving or screenshots of this chat's messages.</param>
/// <param name="CanChange">The switch is enabled: a group admin, or a participant of a private chat who can turn their part on or off.</param>
/// <param name="ByMe">Private chats: my part is on.</param>
/// <param name="ByOthers">Private chats: the other participant has their part on (I cannot turn it off for them).</param>
public sealed record ContentProtectionState(bool IsProtected, bool CanChange, bool ByMe, bool ByOthers);

/// <summary>
/// Content protection on the client (the server enforces it: forwards out of a protected chat fail with
/// <see cref="ErrorCodes.ForwardRestricted"/>). Groups: admins switch it for everyone. Private chats: either side turns
/// their own part on or off and the chat stays protected while anyone has theirs on.
/// </summary>
public static class ContentProtectionRules
{
    public static bool IsProtected(ChatSummaryDto? chat) =>
        chat is not null && chat.Type != ChatType.Saved && (chat.ProtectContent || chat.ProtectContentBy.Count > 0);

    public static ContentProtectionState Describe(ChatSummaryDto chat, Guid me)
    {
        switch (chat.Type)
        {
            case ChatType.Group:
                return new ContentProtectionState(chat.ProtectContent, chat.MyRole is ChatRole.Admin or ChatRole.Owner, false, false);
            case ChatType.Private:
                var byMe = chat.ProtectContentBy.Contains(me);
                var byOthers = chat.ProtectContentBy.Any(id => id != me);
                // Turning it on is possible unless either side blocked the other (the server refuses: its notice would
                // reach the other side); turning it off only takes my own part back.
                var canChange = byMe || (!chat.IsBlocked && !(chat.ProtectContent || byOthers));
                return new ContentProtectionState(IsProtected(chat), canChange, byMe, byOthers);
            default:
                return new ContentProtectionState(false, false, false, false);
        }
    }

    /// <summary>The chat as it will be once <paramref name="me"/> switched protection (the optimistic copy until the server answers).</summary>
    public static ChatSummaryDto Apply(ChatSummaryDto chat, Guid me, bool enabled)
    {
        if (chat.Type != ChatType.Private) return chat with { ProtectContent = enabled };
        var by = chat.ProtectContentBy.Where(id => id != me).ToList();
        if (enabled) by.Add(me);
        return chat with { ProtectContent = by.Count > 0, ProtectContentBy = by };
    }
}
