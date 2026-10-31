using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// <see cref="ISkyApi"/> over <see cref="ApiTransport"/> (anonymous endpoints) and <see cref="AuthorizedSender"/>
/// (bearer, identity check, one refresh + retry on 401). Network errors fail over once; idempotent requests are
/// retried (GET, PUT, DELETE, PATCH, and POSTs that are idempotent on the server: ClientMessageId sends, reads,
/// batch lookups, complete-upload...). Chunk uploads are watched for stalls.
/// </summary>
internal sealed class SkyApi(ApiTransport transport, AuthorizedSender sender, IEndpointRouting endpoints, TimeProvider time) : ISkyApi
{
    private static readonly HttpMethod Patch = HttpMethod.Patch;

    // Server

    public Task<ServerInfoDto> GetServerInfoAsync(CancellationToken ct = default) =>
        transport.SendJsonAsync<ServerInfoDto>(ApiRequest.Get(ApiRoutes.ServerInfo), null, ct);

    // Auth

    public Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        transport.SendJsonAsync<AuthResponse>(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Auth.Register, request, idempotent: false), null, ct);

    public Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
        transport.SendJsonAsync<AuthResponse>(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Auth.Login, request, idempotent: false), null, ct);

    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken ct = default)
    {
        await endpoints.EnsureTrustedAsync(ct).ConfigureAwait(false);
        return await transport.SendJsonAsync<AuthResponse>(
                ApiRequest.Json(HttpMethod.Post, ApiRoutes.Auth.Refresh, request, idempotent: true, carriesCredentials: true), null, ct)
            .ConfigureAwait(false);
    }

    public Task LogoutAsync(CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Post, ApiRoutes.Auth.Logout, idempotent: true), ct);

    public Task<UsernameAvailabilityDto> CheckUsernameAsync(string username, CancellationToken ct = default) =>
        sender.SendJsonAsync<UsernameAvailabilityDto>(ApiRequest.Get(ApiRoutes.Auth.UsernameAvailablePath(username)), ct, optionalAuth: true);

    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) =>
        NoContent(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Auth.ChangePassword, request, idempotent: false), ct);

    public Task DeleteAccountAsync(string password, CancellationToken ct = default) =>
        NoContent(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = password }, idempotent: false), ct);

    // Sessions

    public Task<IReadOnlyList<SessionDto>> GetSessionsAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<SessionDto>>(ApiRoutes.Sessions.Root, ct);

    public Task TerminateSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Sessions.ByIdPath(sessionId), idempotent: true), ct);

    public Task TerminateOtherSessionsAsync(CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Sessions.Others, idempotent: true), ct);

    public Task RegisterPushAsync(RegisterPushRequest request, CancellationToken ct = default) =>
        NoContent(ApiRequest.Json(HttpMethod.Put, ApiRoutes.Sessions.CurrentPush, request, idempotent: true), ct);

    // Users / profile

    public Task<MeDto> GetMeAsync(CancellationToken ct = default) => Get<MeDto>(ApiRoutes.Users.Me, ct);

    public Task<MeDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default) =>
        Json<MeDto>(Patch, ApiRoutes.Users.Me, request, idempotent: true, ct);

    public Task<MeDto> SetAvatarAsync(Guid mediaId, CancellationToken ct = default) =>
        Json<MeDto>(HttpMethod.Put, ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = mediaId }, idempotent: true, ct);

    public Task<MeDto> RemoveAvatarAsync(CancellationToken ct = default) =>
        sender.SendJsonAsync<MeDto>(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Users.MeAvatar, idempotent: true), ct);

    public Task<MeDto> SetPhoneAsync(SetPhoneRequest request, CancellationToken ct = default) =>
        Json<MeDto>(HttpMethod.Put, ApiRoutes.Users.MePhone, request, idempotent: true, ct);

    public Task<MeDto> RemovePhoneAsync(CancellationToken ct = default) =>
        sender.SendJsonAsync<MeDto>(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Users.MePhone, idempotent: true), ct);

    public Task<PrivacySettingsDto> GetPrivacyAsync(CancellationToken ct = default) =>
        Get<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy, ct);

    public Task<PrivacySettingsDto> SetPrivacyAsync(PrivacySettingsDto settings, CancellationToken ct = default) =>
        Json<PrivacySettingsDto>(HttpMethod.Put, ApiRoutes.Users.MePrivacy, settings, idempotent: true, ct);

    public Task<UserProfileDto> GetUserAsync(Guid userId, CancellationToken ct = default) =>
        Get<UserProfileDto>(ApiRoutes.Users.ByIdPath(userId), ct);

    public Task<UserProfileDto> GetUserByUsernameAsync(string username, CancellationToken ct = default) =>
        Get<UserProfileDto>(ApiRoutes.Users.ByUsernamePath(username.Trim().TrimStart('@')), ct);

    public async Task<IReadOnlyList<UserSummaryDto>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return [];
        if (ids.Count <= Limits.MaxBatchUsers) return await BatchAsync(ids, ct).ConfigureAwait(false);

        var result = new List<UserSummaryDto>(ids.Count);
        foreach (var chunk in ids.Chunk(Limits.MaxBatchUsers))
            result.AddRange(await BatchAsync(chunk, ct).ConfigureAwait(false));
        return result;
    }

    private async Task<IReadOnlyList<UserSummaryDto>> BatchAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        await Json<IReadOnlyList<UserSummaryDto>>(HttpMethod.Post, ApiRoutes.Users.Batch, new GetUsersRequest { UserIds = ids }, idempotent: true, ct)
            .ConfigureAwait(false) ?? [];

    public Task<IReadOnlyList<UserSummaryDto>> SearchUsersAsync(string query, int limit = 30, CancellationToken ct = default) =>
        Get<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Users.SearchPath(query, limit), ct);

    public Task<IReadOnlyList<PhoneMatchDto>> FindByPhonesAsync(IReadOnlyList<string> phoneNumbers, string? defaultRegion, CancellationToken ct = default) =>
        FindByPhonesAsync(new FindByPhonesRequest { PhoneNumbers = phoneNumbers, DefaultRegion = defaultRegion }, ct);

    public async Task<IReadOnlyList<PhoneMatchDto>> FindByPhonesAsync(FindByPhonesRequest request, CancellationToken ct = default)
    {
        if (request.PhoneNumbers.Count == 0) return [];
        if (request.PhoneNumbers.Count <= Limits.FindByPhonesMax)
            return await Json<IReadOnlyList<PhoneMatchDto>>(HttpMethod.Post, ApiRoutes.Users.FindByPhones, request, idempotent: true, ct).ConfigureAwait(false) ?? [];

        var result = new List<PhoneMatchDto>();
        foreach (var chunk in request.PhoneNumbers.Chunk(Limits.FindByPhonesMax))
        {
            var part = await Json<IReadOnlyList<PhoneMatchDto>>(HttpMethod.Post, ApiRoutes.Users.FindByPhones, request with { PhoneNumbers = chunk }, idempotent: true, ct)
                .ConfigureAwait(false);
            if (part is not null) result.AddRange(part);
        }
        return result;
    }

    // Contacts / blocks

    public Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root, ct);

    public Task<ContactDto> AddContactAsync(AddContactRequest request, CancellationToken ct = default) =>
        Json<ContactDto>(HttpMethod.Post, ApiRoutes.Contacts.Root, request, idempotent: true, ct);

    public Task<IReadOnlyList<ContactDto>> AddContactsBulkAsync(AddContactsBulkRequest request, CancellationToken ct = default) =>
        Json<IReadOnlyList<ContactDto>>(HttpMethod.Post, ApiRoutes.Contacts.Bulk, request, idempotent: true, ct);

    public Task<ContactDto> UpdateContactAsync(Guid userId, UpdateContactRequest request, CancellationToken ct = default) =>
        Json<ContactDto>(Patch, ApiRoutes.Contacts.ByUserIdPath(userId), request, idempotent: true, ct);

    public Task RemoveContactAsync(Guid userId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Contacts.ByUserIdPath(userId), idempotent: true), ct);

    public Task<IReadOnlyList<UserSummaryDto>> GetBlockedAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Blocks.Root, ct);

    public Task BlockAsync(Guid userId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Put, ApiRoutes.Blocks.ByUserIdPath(userId), idempotent: true), ct);

    public Task UnblockAsync(Guid userId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Blocks.ByUserIdPath(userId), idempotent: true), ct);

    // Chats

    public Task<IReadOnlyList<ChatSummaryDto>> GetChatsAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<ChatSummaryDto>>(ApiRoutes.Chats.Root, ct);

    public Task<ChatSummaryDto> GetChatAsync(Guid chatId, CancellationToken ct = default) =>
        Get<ChatSummaryDto>(ApiRoutes.Chats.ByIdPath(chatId), ct);

    public Task<ChatDetailsDto> GetChatDetailsAsync(Guid chatId, CancellationToken ct = default) =>
        Get<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(chatId), ct);

    // Returns the existing chat when there is one: safe to repeat.
    public Task<ChatSummaryDto> CreatePrivateChatAsync(Guid userId, CancellationToken ct = default) =>
        Json<ChatSummaryDto>(HttpMethod.Post, ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = userId }, idempotent: true, ct);

    public Task<CreateGroupResultDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default) =>
        Json<CreateGroupResultDto>(HttpMethod.Post, ApiRoutes.Chats.Groups, request, idempotent: false, ct);

    public Task<ChatInfoDto> UpdateChatAsync(Guid chatId, UpdateChatRequest request, CancellationToken ct = default) =>
        Json<ChatInfoDto>(Patch, ApiRoutes.Chats.ByIdPath(chatId), request, idempotent: true, ct);

    public Task<ChatInfoDto> SetChatAvatarAsync(Guid chatId, Guid mediaId, CancellationToken ct = default) =>
        Json<ChatInfoDto>(HttpMethod.Put, ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = mediaId }, idempotent: true, ct);

    public Task<ChatInfoDto> RemoveChatAvatarAsync(Guid chatId, CancellationToken ct = default) =>
        sender.SendJsonAsync<ChatInfoDto>(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Chats.AvatarPath(chatId), idempotent: true), ct);

    public Task<AddMembersResultDto> AddMembersAsync(Guid chatId, AddMembersRequest request, CancellationToken ct = default) =>
        Json<AddMembersResultDto>(HttpMethod.Post, ApiRoutes.Chats.MembersPath(chatId), request, idempotent: false, ct);

    public Task RemoveMemberAsync(Guid chatId, Guid userId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Chats.MemberPath(chatId, userId), idempotent: true), ct);

    public Task SetMemberRoleAsync(Guid chatId, Guid userId, ChatRole role, CancellationToken ct = default) =>
        NoContent(ApiRequest.Json(HttpMethod.Put, ApiRoutes.Chats.MemberRolePath(chatId, userId), new SetMemberRoleRequest { Role = role }, idempotent: true), ct);

    public Task LeaveChatAsync(Guid chatId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Post, ApiRoutes.Chats.LeavePath(chatId), idempotent: false), ct);

    // Returns the current link when one exists: safe to repeat.
    public Task<InviteLinkDto> CreateInviteLinkAsync(Guid chatId, CancellationToken ct = default) =>
        sender.SendJsonAsync<InviteLinkDto>(ApiRequest.Empty(HttpMethod.Post, ApiRoutes.Chats.InviteLinkPath(chatId), idempotent: true), ct);

    public Task RevokeInviteLinkAsync(Guid chatId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Chats.InviteLinkPath(chatId), idempotent: true), ct);

    public Task<ChatInvitePreviewDto> GetInvitePreviewAsync(string token, CancellationToken ct = default) =>
        Get<ChatInvitePreviewDto>(ApiRoutes.Chats.JoinPath(token), ct);

    public Task<ChatSummaryDto> JoinByInviteAsync(string token, CancellationToken ct = default) =>
        sender.SendJsonAsync<ChatSummaryDto>(ApiRequest.Empty(HttpMethod.Post, ApiRoutes.Chats.JoinPath(token), idempotent: false), ct);

    public Task<ChatUserSettingsDto> UpdateChatSettingsAsync(Guid chatId, UpdateChatSettingsRequest request, CancellationToken ct = default) =>
        Json<ChatUserSettingsDto>(HttpMethod.Put, ApiRoutes.Chats.SettingsPath(chatId), request, idempotent: true, ct);

    // The same order again changes nothing: safe to repeat.
    public Task<PinnedChatsOrderDto> ReorderPinnedChatsAsync(IReadOnlyList<Guid> chatIdsTopFirst, CancellationToken ct = default) =>
        Json<PinnedChatsOrderDto>(HttpMethod.Put, ApiRoutes.Chats.PinnedOrder, new ReorderPinnedChatsRequest { ChatIds = chatIdsTopFirst }, idempotent: true, ct);

    // A state, not a toggle: safe to repeat.
    public Task<ChatInfoDto> SetContentProtectionAsync(Guid chatId, bool enabled, CancellationToken ct = default) =>
        Json<ChatInfoDto>(HttpMethod.Put, ApiRoutes.Chats.ContentProtectionPath(chatId), new SetContentProtectionRequest { Enabled = enabled }, idempotent: true, ct);

    // Read marks only move forward on the server: safe to repeat.
    public Task MarkReadAsync(Guid chatId, long upToSeq, CancellationToken ct = default) =>
        NoContent(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Chats.ReadPath(chatId), new MarkReadRequest { UpToSeq = upToSeq }, idempotent: true), ct);

    public Task ClearHistoryAsync(Guid chatId, bool forEveryone, CancellationToken ct = default) =>
        NoContent(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Chats.ClearHistoryPath(chatId), new ClearHistoryRequest { ForEveryone = forEveryone }, idempotent: true), ct);

    public Task DeleteChatAsync(Guid chatId, bool forEveryone, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Chats.DeletePath(chatId, forEveryone), idempotent: true), ct);

    public Task<IReadOnlyList<MessageDto>> GetPinnedMessagesAsync(Guid chatId, CancellationToken ct = default) =>
        Get<IReadOnlyList<MessageDto>>(ApiRoutes.Chats.PinnedPath(chatId), ct);

    // Messages

    public Task<MessagesPageDto> GetMessagesAsync(Guid chatId, long? beforeSeq = null, long? afterSeq = null, long? aroundSeq = null,
        int limit = Limits.MessagesPageDefault, CancellationToken ct = default) =>
        Get<MessagesPageDto>(ApiRoutes.Messages.PagePath(chatId, beforeSeq, afterSeq, aroundSeq, Math.Clamp(limit, 1, Limits.MessagesPageMax)), ct);

    public Task<MessageChangesDto> GetMessageChangesAsync(Guid chatId, string cursor, CancellationToken ct = default) =>
        Get<MessageChangesDto>(ApiRoutes.Messages.ChangesPath(chatId, cursor), ct);

    public Task<IReadOnlyList<long>> GetUnreadMentionsAsync(Guid chatId, CancellationToken ct = default) =>
        Get<IReadOnlyList<long>>(ApiRoutes.Messages.MentionsPath(chatId), ct);

    // Idempotent on (sender, ClientMessageId).
    public Task<MessageDto> SendMessageAsync(Guid chatId, SendMessageRequest request, CancellationToken ct = default) =>
        Json<MessageDto>(HttpMethod.Post, ApiRoutes.Messages.RootPath(chatId), request, idempotent: true, ct);

    public Task<IReadOnlyList<MessageDto>> ForwardMessagesAsync(Guid toChatId, ForwardMessagesRequest request, CancellationToken ct = default) =>
        Json<IReadOnlyList<MessageDto>>(HttpMethod.Post, ApiRoutes.Messages.ForwardPath(toChatId), request, idempotent: true, ct);

    public Task<MessageDto> EditMessageAsync(Guid chatId, Guid messageId, EditMessageRequest request, CancellationToken ct = default) =>
        Json<MessageDto>(Patch, ApiRoutes.Messages.ByIdPath(chatId, messageId), request, idempotent: true, ct);

    public Task DeleteMessagesAsync(Guid chatId, DeleteMessagesRequest request, CancellationToken ct = default) =>
        NoContent(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Messages.DeletePath(chatId), request, idempotent: true), ct);

    public Task<MessageDto> SetReactionAsync(Guid chatId, Guid messageId, string emoji, CancellationToken ct = default) =>
        Json<MessageDto>(HttpMethod.Put, ApiRoutes.Messages.ReactionPath(chatId, messageId), new SetReactionRequest { Emoji = emoji }, idempotent: true, ct);

    public Task<MessageDto> RemoveReactionAsync(Guid chatId, Guid messageId, CancellationToken ct = default) =>
        sender.SendJsonAsync<MessageDto>(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Messages.ReactionPath(chatId, messageId), idempotent: true), ct);

    public Task PinMessageAsync(Guid chatId, Guid messageId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Put, ApiRoutes.Messages.PinPath(chatId, messageId), idempotent: true), ct);

    public Task UnpinMessageAsync(Guid chatId, Guid messageId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Delete, ApiRoutes.Messages.PinPath(chatId, messageId), idempotent: true), ct);

    public Task<MessagesPageDto> SearchInChatAsync(Guid chatId, string query, long? beforeSeq = null, CancellationToken ct = default) =>
        Get<MessagesPageDto>(ApiRoutes.Messages.SearchPath(chatId, query, beforeSeq), ct);

    public Task<MessagesPageDto> GetSharedMediaAsync(Guid chatId, SharedMediaFilter filter, long? beforeSeq = null, int limit = Limits.MessagesPageDefault,
        CancellationToken ct = default) =>
        Get<MessagesPageDto>(ApiRoutes.Messages.SharedMediaPath(chatId, filter, beforeSeq, Math.Clamp(limit, 1, Limits.MessagesPageMax)), ct);

    public Task<GlobalSearchResultDto> GlobalSearchAsync(string query, CancellationToken ct = default) =>
        Get<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(query), ct);

    public Task<IReadOnlyList<MessageSearchHitDto>> SearchMessagesAsync(string query, CancellationToken ct = default) =>
        Get<IReadOnlyList<MessageSearchHitDto>>(ApiRoutes.Search.MessagesPath(query), ct);

    // Media

    // A repeat would only leave an orphan upload behind (cleaned up server-side), but the uploader retries itself.
    public Task<UploadSessionDto> CreateUploadAsync(CreateUploadRequest request, CancellationToken ct = default) =>
        Json<UploadSessionDto>(HttpMethod.Post, ApiRoutes.Media.Uploads, request, idempotent: false, ct);

    public Task<UploadSessionDto> GetUploadAsync(Guid mediaId, CancellationToken ct = default) =>
        Get<UploadSessionDto>(ApiRoutes.Media.UploadPath(mediaId), ct);

    public async Task UploadChunkAsync(Guid mediaId, int index, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        // Browsers buffer the request body (fetch), so slice progress is invisible there: allow for the whole transfer.
        // Elsewhere, once the last slice was written the kernel may still hold a large part of the chunk: the wait for
        // the response gets the same allowance, so a slow uplink is not mistaken for a stall.
        var bufferedTimeout = TransferDefaults.BufferedUploadTimeout(data.Length);
        var stallTimeout = OperatingSystem.IsBrowser() ? bufferedTimeout : TransferDefaults.StallTimeout;
        using var watchdog = new StallWatchdog(time, stallTimeout, ct);
        var request = new ApiRequest
        {
            Method = HttpMethod.Put,
            Path = ApiRoutes.Media.UploadChunkPath(mediaId, index),
            Content = () =>
            {
                // Each attempt (a retry after failover) starts with the slice allowance again.
                watchdog.Touch(stallTimeout);
                return new ProgressContent(data, null, watchdog.Touch, () => watchdog.Touch(bufferedTimeout));
            },
            Idempotent = true,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        try
        {
            using var _ = await sender.SendAsync(request, watchdog.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (watchdog.Stalled && !ct.IsCancellationRequested && ex is OperationCanceledException or ApiException)
        {
            endpoints.ReportStall();
            throw HttpErrors.Stalled();
        }
    }

    public Task<MediaDto> CompleteUploadAsync(Guid mediaId, CancellationToken ct = default) =>
        sender.SendJsonAsync<MediaDto>(ApiRequest.Empty(HttpMethod.Post, ApiRoutes.Media.UploadCompletePath(mediaId), idempotent: true), ct);

    public Task<MediaDto> GetMediaAsync(Guid mediaId, CancellationToken ct = default) =>
        Get<MediaDto>(ApiRoutes.Media.ByIdPath(mediaId), ct);

    // Calls

    // The server returns my unfinished call in the chat instead of creating a second one.
    public Task<CallJoinInfoDto> StartCallAsync(StartCallRequest request, CancellationToken ct = default) =>
        Json<CallJoinInfoDto>(HttpMethod.Post, ApiRoutes.Calls.Root, request, idempotent: true, ct);

    public Task<CallDto> GetCallAsync(Guid callId, CancellationToken ct = default) =>
        Get<CallDto>(ApiRoutes.Calls.ByIdPath(callId), ct);

    // Accepting an answered call re-joins it: safe to repeat.
    public Task<CallJoinInfoDto> AcceptCallAsync(Guid callId, CancellationToken ct = default) =>
        sender.SendJsonAsync<CallJoinInfoDto>(ApiRequest.Empty(HttpMethod.Post, ApiRoutes.Calls.AcceptPath(callId), idempotent: true), ct);

    public Task DeclineCallAsync(Guid callId, bool busy = false, CancellationToken ct = default) =>
        NoContent(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Calls.DeclinePath(callId), new DeclineCallRequest { Busy = busy }, idempotent: true), ct);

    public Task<CallJoinInfoDto> JoinCallAsync(Guid callId, CancellationToken ct = default) =>
        sender.SendJsonAsync<CallJoinInfoDto>(ApiRequest.Empty(HttpMethod.Post, ApiRoutes.Calls.JoinPath(callId), idempotent: true), ct);

    public Task HangupAsync(Guid callId, CancellationToken ct = default) =>
        NoContent(ApiRequest.Empty(HttpMethod.Post, ApiRoutes.Calls.HangupPath(callId), idempotent: true), ct);

    public Task<CallDto?> GetActiveCallAsync(Guid chatId, CancellationToken ct = default) =>
        Get<CallDto?>(ApiRoutes.Calls.ActivePath(chatId), ct);

    public async Task<IReadOnlyList<CallHistoryItemDto>> GetCallHistoryAsync(DateTimeOffset? before = null, int limit = 50, CancellationToken ct = default) =>
        await Get<IReadOnlyList<CallHistoryItemDto>>(ApiRoutes.Calls.HistoryPath(before, Math.Clamp(limit, 1, 100)), ct).ConfigureAwait(false) ?? [];

    // Helpers

    private Task<T> Get<T>(string path, CancellationToken ct) => sender.SendJsonAsync<T>(ApiRequest.Get(path), ct);

    private Task<T> Json<T>(HttpMethod method, string path, object body, bool idempotent, CancellationToken ct) =>
        sender.SendJsonAsync<T>(ApiRequest.Json(method, path, body, idempotent), ct);

    private Task NoContent(ApiRequest request, CancellationToken ct) => sender.SendNoContentAsync(request, ct);
}
