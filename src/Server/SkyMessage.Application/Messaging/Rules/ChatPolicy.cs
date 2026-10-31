using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Rules;

/// <summary>
/// Permission matrix. Roles: Owner ⊃ Admin ⊃ Member. Admins and the owner bypass <see cref="GroupPermissions"/>;
/// only the owner manages admins. Private and Saved chats have no group permissions.
/// </summary>
internal static class ChatPolicy
{
    public static bool IsAdmin(ChatRole role) => role is ChatRole.Admin or ChatRole.Owner;

    public static bool CanSendMessages(ChatType type, ChatRole role, GroupPermissions permissions) =>
        type != ChatType.Group || IsAdmin(role) || permissions.SendMessages;

    public static bool CanSendMedia(ChatType type, ChatRole role, GroupPermissions permissions) =>
        type != ChatType.Group || IsAdmin(role) || (permissions.SendMessages && permissions.SendMedia);

    /// <summary>Adding members and creating/revoking the invite link.</summary>
    public static bool CanAddMembers(ChatType type, ChatRole role, GroupPermissions permissions) =>
        type == ChatType.Group && (IsAdmin(role) || permissions.AddMembers);

    public static bool CanPinMessages(ChatType type, ChatRole role, GroupPermissions permissions) =>
        type != ChatType.Group || IsAdmin(role) || permissions.PinMessages;

    /// <summary>Title, description and photo.</summary>
    public static bool CanChangeInfo(ChatType type, ChatRole role, GroupPermissions permissions) =>
        type == ChatType.Group && (IsAdmin(role) || permissions.ChangeInfo);

    public static bool CanChangePermissions(ChatType type, ChatRole role) => type == ChatType.Group && IsAdmin(role);

    /// <summary>Banned users can only be (re-)added by admins, which lifts the ban.</summary>
    public static bool CanAddBannedUser(ChatRole role) => IsAdmin(role);

    /// <summary>Admins remove members; only the owner removes admins; nobody removes the owner.</summary>
    public static bool CanRemoveMember(ChatRole actor, ChatRole target) => actor switch
    {
        ChatRole.Owner => target != ChatRole.Owner,
        ChatRole.Admin => target == ChatRole.Member,
        _ => false,
    };

    public static bool CanSetRoles(ChatRole actor) => actor == ChatRole.Owner;

    /// <summary>
    /// "Delete for everyone": own messages always; in private and Saved chats any message; in groups other
    /// people's (and service) messages only for admins.
    /// </summary>
    public static bool CanDeleteForEveryone(ChatType type, ChatRole role, Guid actorId, Guid? senderId) =>
        type != ChatType.Group || senderId == actorId || IsAdmin(role);

    /// <summary>Deleting the whole chat for every member: either side of a private chat, the owner of a group.</summary>
    public static bool CanDeleteChatForEveryone(ChatType type, ChatRole role) =>
        type != ChatType.Group || role == ChatRole.Owner;

    /// <summary>
    /// Content protection (no forwarding, copying, saving): admins switch it for a group; either side of a private chat
    /// turns their own part on or off; Saved Messages has nobody to protect from.
    /// </summary>
    public static bool CanSetContentProtection(ChatType type, ChatRole role) => type switch
    {
        ChatType.Group => IsAdmin(role),
        ChatType.Private => true,
        _ => false,
    };
}

/// <summary>How a private chat's protection follows its participants' choices (see <see cref="Chat.ProtectContentBy"/>).</summary>
internal static class ContentProtectionRules
{
    /// <summary>The participants who want protection after <paramref name="userId"/> turned their part on or off.</summary>
    public static List<Guid> Toggle(IReadOnlyCollection<Guid> protectedBy, Guid userId, bool enabled)
    {
        var next = protectedBy.Where(id => id != userId).ToList();
        if (enabled) next.Add(userId);
        return next;
    }

    /// <summary>A private chat is protected while anyone has their part on.</summary>
    public static bool IsProtected(IReadOnlyCollection<Guid> protectedBy) => protectedBy.Count > 0;
}

/// <summary>Who becomes the owner when the owner leaves.</summary>
internal static class OwnershipRules
{
    public readonly record struct Candidate(Guid UserId, ChatRole Role, DateTimeOffset JoinedAt);

    /// <summary>The oldest admin, otherwise the oldest member (ties by user id); null when nobody is left.</summary>
    public static Guid? PickSuccessor(IEnumerable<Candidate> remaining) =>
        remaining
            .OrderByDescending(c => c.Role)
            .ThenBy(c => c.JoinedAt)
            .ThenBy(c => c.UserId)
            .Select(c => (Guid?)c.UserId)
            .FirstOrDefault();
}
