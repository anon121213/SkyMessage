using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Client-side mirror of the server's chat permission rules, used only to show or hide actions (the server
/// enforces them). Owner ⊃ Admin ⊃ Member; admins and the owner bypass group permissions.
/// </summary>
public static class ChatRules
{
    public static bool IsAdmin(ChatRole role) => role is ChatRole.Admin or ChatRole.Owner;

    public static bool IsAdmin(this ChatSummaryDto chat) => chat.Type == ChatType.Group && IsAdmin(chat.MyRole);

    public static bool CanSendMessages(this ChatSummaryDto chat) =>
        !chat.IsBlocked && (chat.Type != ChatType.Group || IsAdmin(chat.MyRole) || chat.Permissions.SendMessages);

    public static bool CanSendMedia(this ChatSummaryDto chat) =>
        !chat.IsBlocked && (chat.Type != ChatType.Group || IsAdmin(chat.MyRole) || (chat.Permissions.SendMessages && chat.Permissions.SendMedia));

    /// <summary>Adding members and creating/revoking the invite link.</summary>
    public static bool CanAddMembers(this ChatSummaryDto chat) =>
        chat.Type == ChatType.Group && (IsAdmin(chat.MyRole) || chat.Permissions.AddMembers);

    public static bool CanPinMessages(this ChatSummaryDto chat) =>
        chat.Type != ChatType.Group || IsAdmin(chat.MyRole) || chat.Permissions.PinMessages;

    /// <summary>Title, description and photo.</summary>
    public static bool CanChangeInfo(this ChatSummaryDto chat) =>
        chat.Type == ChatType.Group && (IsAdmin(chat.MyRole) || chat.Permissions.ChangeInfo);

    public static bool CanChangePermissions(this ChatSummaryDto chat) => chat.Type == ChatType.Group && IsAdmin(chat.MyRole);

    /// <summary>Admins remove members; only the owner removes admins; nobody removes the owner.</summary>
    public static bool CanRemoveMember(ChatRole actor, ChatRole target) => actor switch
    {
        ChatRole.Owner => target != ChatRole.Owner,
        ChatRole.Admin => target == ChatRole.Member,
        _ => false,
    };

    public static bool CanSetRoles(ChatRole actor) => actor == ChatRole.Owner;

    /// <summary>"Delete for everyone": own messages always; private and Saved chats any message; groups others' only for admins.</summary>
    public static bool CanDeleteForEveryone(this ChatSummaryDto chat, Guid me, Guid? senderId) =>
        chat.Type != ChatType.Group || senderId == me || IsAdmin(chat.MyRole);

    /// <summary>Delete the whole chat for every member: either side of a private chat, the owner of a group.</summary>
    public static bool CanDeleteChatForEveryone(this ChatSummaryDto chat) =>
        chat.Type == ChatType.Private || (chat.Type == ChatType.Group && chat.MyRole == ChatRole.Owner);

    public static bool IsMuted(this ChatSummaryDto chat, DateTimeOffset now) => chat.MutedUntil is { } until && until > now;

    /// <summary>Muted "forever" (the server stores a far-future date).</summary>
    public static bool IsMutedForever(this ChatSummaryDto chat) => chat.MutedUntil is { } until && until.Year >= 2100;

    /// <summary>The last message was sent by me and everyone else has read it (double check).</summary>
    public static bool LastMessageReadByOthers(this ChatSummaryDto chat) =>
        chat.LastMessage is { } last && last.Seq > 0 && chat.OthersLastReadSeq >= last.Seq;
}
