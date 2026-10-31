using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging;

// Use cases behind ChatsController / MessagesController / SearchController. Every method acts for the
// current user (ICurrentUser); chats the caller is not a member of are reported as chat_not_found.

/// <summary>Chat list, chat lifecycle and per-user chat state.</summary>
public interface IChatService
{
    /// <summary>Visible (not hidden) chats, pinned first (in my pin order), then by last activity.</summary>
    Task<IReadOnlyList<ChatSummaryDto>> GetChatsAsync(CancellationToken cancellationToken = default);
    Task<ChatSummaryDto> GetChatAsync(Guid chatId, CancellationToken cancellationToken = default);
    Task<ChatDetailsDto> GetDetailsAsync(Guid chatId, CancellationToken cancellationToken = default);
    /// <summary>Pinned messages visible to the caller, newest pin first.</summary>
    Task<IReadOnlyList<MessageDto>> GetPinnedAsync(Guid chatId, CancellationToken cancellationToken = default);

    /// <summary>Self → Saved Messages; existing chat → unhidden for the caller; else created (peer row hidden until the first message).</summary>
    Task<ChatSummaryDto> OpenPrivateChatAsync(CreatePrivateChatRequest request, CancellationToken cancellationToken = default);
    Task<CreateGroupResultDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default);
    Task<ChatInfoDto> UpdateChatAsync(Guid chatId, UpdateChatRequest request, CancellationToken cancellationToken = default);
    Task<ChatInfoDto> SetAvatarAsync(Guid chatId, SetChatAvatarRequest request, CancellationToken cancellationToken = default);
    Task<ChatInfoDto> RemoveAvatarAsync(Guid chatId, CancellationToken cancellationToken = default);

    Task<ChatUserSettingsDto> UpdateSettingsAsync(Guid chatId, UpdateChatSettingsRequest request, CancellationToken cancellationToken = default);
    /// <summary>Rearranges my pinned chats (top first); returns every pinned chat's new order and tells all my devices.</summary>
    Task<PinnedChatsOrderDto> ReorderPinnedAsync(ReorderPinnedChatsRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Content protection on/off: groups for everyone (admins), private chats the caller's own part (either side).
    /// ChatUpdated to the members, plus a service message when the chat's protection actually flips.
    /// </summary>
    Task<ChatInfoDto> SetContentProtectionAsync(Guid chatId, SetContentProtectionRequest request, CancellationToken cancellationToken = default);
    Task MarkReadAsync(Guid chatId, MarkReadRequest request, CancellationToken cancellationToken = default);
    Task ClearHistoryAsync(Guid chatId, bool forEveryone, CancellationToken cancellationToken = default);
    /// <summary>
    /// Private/Saved: for me = hide + clear history, for everyone = delete the chat for both sides.
    /// Groups: for everyone = owner deletes the group, for me = leave.
    /// </summary>
    Task DeleteChatAsync(Guid chatId, bool forEveryone, CancellationToken cancellationToken = default);
}

/// <summary>Group membership, roles and invite links.</summary>
public interface IChatMemberService
{
    Task<AddMembersResultDto> AddMembersAsync(Guid chatId, AddMembersRequest request, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(Guid chatId, Guid userId, CancellationToken cancellationToken = default);
    Task SetRoleAsync(Guid chatId, Guid userId, SetMemberRoleRequest request, CancellationToken cancellationToken = default);
    Task LeaveAsync(Guid chatId, CancellationToken cancellationToken = default);

    /// <summary>Returns the current link, creating one when the group has none.</summary>
    Task<InviteLinkDto> CreateInviteLinkAsync(Guid chatId, CancellationToken cancellationToken = default);
    Task RevokeInviteLinkAsync(Guid chatId, CancellationToken cancellationToken = default);
    Task<ChatInvitePreviewDto> GetInvitePreviewAsync(string token, CancellationToken cancellationToken = default);
    Task<ChatSummaryDto> JoinByInviteAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>Message history, sync and message actions.</summary>
public interface IMessageService
{
    Task<MessagesPageDto> GetPageAsync(Guid chatId, long? beforeSeq, long? afterSeq, long? aroundSeq, int? limit, CancellationToken cancellationToken = default);
    Task<MessageChangesDto> GetChangesAsync(Guid chatId, string? cursor, CancellationToken cancellationToken = default);
    /// <summary>Seqs (ascending) of unread messages that mention the caller or reply to the caller's messages.</summary>
    Task<IReadOnlyList<long>> GetUnreadMentionsAsync(Guid chatId, CancellationToken cancellationToken = default);
    /// <summary>One message visible to the caller (tombstones included).</summary>
    Task<MessageDto> GetMessageAsync(Guid chatId, Guid messageId, CancellationToken cancellationToken = default);

    Task<MessageDto> SendAsync(Guid chatId, SendMessageRequest request, CancellationToken cancellationToken = default);
    /// <summary>Forwards into <paramref name="chatId"/>; returns the forwarded copies ordered by seq.</summary>
    Task<IReadOnlyList<MessageDto>> ForwardAsync(Guid chatId, ForwardMessagesRequest request, CancellationToken cancellationToken = default);
    Task<MessageDto> EditAsync(Guid chatId, Guid messageId, EditMessageRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid chatId, DeleteMessagesRequest request, CancellationToken cancellationToken = default);
    Task<MessageDto> SetReactionAsync(Guid chatId, Guid messageId, SetReactionRequest request, CancellationToken cancellationToken = default);
    Task<MessageDto> RemoveReactionAsync(Guid chatId, Guid messageId, CancellationToken cancellationToken = default);
    Task PinAsync(Guid chatId, Guid messageId, CancellationToken cancellationToken = default);
    Task UnpinAsync(Guid chatId, Guid messageId, CancellationToken cancellationToken = default);
}

/// <summary>In-chat search, shared media and global search.</summary>
public interface IMessageSearchService
{
    /// <summary>Text matches in one chat, newest first (<see cref="MessagesPageDto.HasMoreBefore"/> = more older hits).</summary>
    Task<MessagesPageDto> SearchInChatAsync(Guid chatId, string? query, long? beforeSeq, int? limit, CancellationToken cancellationToken = default);
    /// <summary>Shared media gallery, newest first.</summary>
    Task<MessagesPageDto> GetSharedMediaAsync(Guid chatId, SharedMediaFilter filter, long? beforeSeq, int? limit, CancellationToken cancellationToken = default);
    /// <summary>Text matches across all of the caller's chats, newest first.</summary>
    Task<IReadOnlyList<MessageSearchHitDto>> SearchMessagesAsync(string? query, int? limit, CancellationToken cancellationToken = default);
    Task<GlobalSearchResultDto> SearchAsync(string? query, CancellationToken cancellationToken = default);
}
