using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Typed REST client for every endpoint in <see cref="ApiRoutes"/>. Adds the bearer token, refreshes
/// it on 401 once, fails over to another server on network errors, and throws <see cref="ApiException"/>.
/// </summary>
public interface ISkyApi
{
    // Server
    Task<ServerInfoDto> GetServerInfoAsync(CancellationToken ct = default);

    // Auth (normally used through IAuthService)
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<UsernameAvailabilityDto> CheckUsernameAsync(string username, CancellationToken ct = default);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);
    Task DeleteAccountAsync(string password, CancellationToken ct = default);

    // Sessions
    Task<IReadOnlyList<SessionDto>> GetSessionsAsync(CancellationToken ct = default);
    Task TerminateSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task TerminateOtherSessionsAsync(CancellationToken ct = default);
    Task RegisterPushAsync(RegisterPushRequest request, CancellationToken ct = default);

    // Users / profile
    Task<MeDto> GetMeAsync(CancellationToken ct = default);
    Task<MeDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default);
    Task<MeDto> SetAvatarAsync(Guid mediaId, CancellationToken ct = default);
    Task<MeDto> RemoveAvatarAsync(CancellationToken ct = default);
    Task<MeDto> SetPhoneAsync(SetPhoneRequest request, CancellationToken ct = default);
    Task<MeDto> RemovePhoneAsync(CancellationToken ct = default);
    Task<PrivacySettingsDto> GetPrivacyAsync(CancellationToken ct = default);
    Task<PrivacySettingsDto> SetPrivacyAsync(PrivacySettingsDto settings, CancellationToken ct = default);
    Task<UserProfileDto> GetUserAsync(Guid userId, CancellationToken ct = default);
    Task<UserProfileDto> GetUserByUsernameAsync(string username, CancellationToken ct = default);
    /// <summary>POST api/users/batch (≤ Limits.MaxBatchUsers ids per call; larger sets are split).</summary>
    Task<IReadOnlyList<UserSummaryDto>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task<IReadOnlyList<UserSummaryDto>> SearchUsersAsync(string query, int limit = 30, CancellationToken ct = default);
    Task<IReadOnlyList<PhoneMatchDto>> FindByPhonesAsync(IReadOnlyList<string> phoneNumbers, string? defaultRegion, CancellationToken ct = default);
    /// <summary>Full request (several <see cref="FindByPhonesRequest.DefaultRegions"/>, e.g. both SIM countries); ≤ Limits.FindByPhonesMax numbers.</summary>
    Task<IReadOnlyList<PhoneMatchDto>> FindByPhonesAsync(FindByPhonesRequest request, CancellationToken ct = default);

    // Contacts / blocks
    Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default);
    Task<ContactDto> AddContactAsync(AddContactRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ContactDto>> AddContactsBulkAsync(AddContactsBulkRequest request, CancellationToken ct = default);
    Task<ContactDto> UpdateContactAsync(Guid userId, UpdateContactRequest request, CancellationToken ct = default);
    Task RemoveContactAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserSummaryDto>> GetBlockedAsync(CancellationToken ct = default);
    Task BlockAsync(Guid userId, CancellationToken ct = default);
    Task UnblockAsync(Guid userId, CancellationToken ct = default);

    // Chats
    Task<IReadOnlyList<ChatSummaryDto>> GetChatsAsync(CancellationToken ct = default);
    Task<ChatSummaryDto> GetChatAsync(Guid chatId, CancellationToken ct = default);
    Task<ChatDetailsDto> GetChatDetailsAsync(Guid chatId, CancellationToken ct = default);
    Task<ChatSummaryDto> CreatePrivateChatAsync(Guid userId, CancellationToken ct = default);
    Task<CreateGroupResultDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default);
    Task<ChatInfoDto> UpdateChatAsync(Guid chatId, UpdateChatRequest request, CancellationToken ct = default);
    Task<ChatInfoDto> SetChatAvatarAsync(Guid chatId, Guid mediaId, CancellationToken ct = default);
    Task<ChatInfoDto> RemoveChatAvatarAsync(Guid chatId, CancellationToken ct = default);
    Task<AddMembersResultDto> AddMembersAsync(Guid chatId, AddMembersRequest request, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid chatId, Guid userId, CancellationToken ct = default);
    Task SetMemberRoleAsync(Guid chatId, Guid userId, ChatRole role, CancellationToken ct = default);
    Task LeaveChatAsync(Guid chatId, CancellationToken ct = default);
    Task<InviteLinkDto> CreateInviteLinkAsync(Guid chatId, CancellationToken ct = default);
    Task RevokeInviteLinkAsync(Guid chatId, CancellationToken ct = default);
    Task<ChatInvitePreviewDto> GetInvitePreviewAsync(string token, CancellationToken ct = default);
    Task<ChatSummaryDto> JoinByInviteAsync(string token, CancellationToken ct = default);
    Task<ChatUserSettingsDto> UpdateChatSettingsAsync(Guid chatId, UpdateChatSettingsRequest request, CancellationToken ct = default);
    /// <summary>My pinned chats in the order I want them (top first); returns every pinned chat's new order.</summary>
    Task<PinnedChatsOrderDto> ReorderPinnedChatsAsync(IReadOnlyList<Guid> chatIdsTopFirst, CancellationToken ct = default);
    /// <summary>No forwarding/copying/saving: groups for everyone (admins), private chats my own part.</summary>
    Task<ChatInfoDto> SetContentProtectionAsync(Guid chatId, bool enabled, CancellationToken ct = default);
    Task MarkReadAsync(Guid chatId, long upToSeq, CancellationToken ct = default);
    Task ClearHistoryAsync(Guid chatId, bool forEveryone, CancellationToken ct = default);
    Task DeleteChatAsync(Guid chatId, bool forEveryone, CancellationToken ct = default);
    Task<IReadOnlyList<MessageDto>> GetPinnedMessagesAsync(Guid chatId, CancellationToken ct = default);

    // Messages
    Task<MessagesPageDto> GetMessagesAsync(Guid chatId, long? beforeSeq = null, long? afterSeq = null, long? aroundSeq = null, int limit = Limits.MessagesPageDefault, CancellationToken ct = default);
    Task<MessageChangesDto> GetMessageChangesAsync(Guid chatId, string cursor, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetUnreadMentionsAsync(Guid chatId, CancellationToken ct = default);
    Task<MessageDto> SendMessageAsync(Guid chatId, SendMessageRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MessageDto>> ForwardMessagesAsync(Guid toChatId, ForwardMessagesRequest request, CancellationToken ct = default);
    Task<MessageDto> EditMessageAsync(Guid chatId, Guid messageId, EditMessageRequest request, CancellationToken ct = default);
    Task DeleteMessagesAsync(Guid chatId, DeleteMessagesRequest request, CancellationToken ct = default);
    Task<MessageDto> SetReactionAsync(Guid chatId, Guid messageId, string emoji, CancellationToken ct = default);
    Task<MessageDto> RemoveReactionAsync(Guid chatId, Guid messageId, CancellationToken ct = default);
    Task PinMessageAsync(Guid chatId, Guid messageId, CancellationToken ct = default);
    Task UnpinMessageAsync(Guid chatId, Guid messageId, CancellationToken ct = default);
    Task<MessagesPageDto> SearchInChatAsync(Guid chatId, string query, long? beforeSeq = null, CancellationToken ct = default);
    Task<MessagesPageDto> GetSharedMediaAsync(Guid chatId, SharedMediaFilter filter, long? beforeSeq = null, int limit = Limits.MessagesPageDefault, CancellationToken ct = default);
    Task<GlobalSearchResultDto> GlobalSearchAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<MessageSearchHitDto>> SearchMessagesAsync(string query, CancellationToken ct = default);

    // Media
    Task<UploadSessionDto> CreateUploadAsync(CreateUploadRequest request, CancellationToken ct = default);
    Task<UploadSessionDto> GetUploadAsync(Guid mediaId, CancellationToken ct = default);
    Task UploadChunkAsync(Guid mediaId, int index, ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task<MediaDto> CompleteUploadAsync(Guid mediaId, CancellationToken ct = default);
    Task<MediaDto> GetMediaAsync(Guid mediaId, CancellationToken ct = default);

    // Calls
    Task<CallJoinInfoDto> StartCallAsync(StartCallRequest request, CancellationToken ct = default);
    Task<CallDto> GetCallAsync(Guid callId, CancellationToken ct = default);
    Task<CallJoinInfoDto> AcceptCallAsync(Guid callId, CancellationToken ct = default);
    Task DeclineCallAsync(Guid callId, bool busy = false, CancellationToken ct = default);
    Task<CallJoinInfoDto> JoinCallAsync(Guid callId, CancellationToken ct = default);
    Task HangupAsync(Guid callId, CancellationToken ct = default);
    Task<CallDto?> GetActiveCallAsync(Guid chatId, CancellationToken ct = default);
    Task<IReadOnlyList<CallHistoryItemDto>> GetCallHistoryAsync(DateTimeOffset? before = null, int limit = 50, CancellationToken ct = default);
}
