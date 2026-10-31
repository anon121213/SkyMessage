using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SkyMessage.Application.Messaging;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

/// <summary>Chats, groups, members, invite links and per-user chat state (SPEC §6.3).</summary>
[ApiController]
public sealed class ChatsController(IChatService chats, IChatMemberService members) : ControllerBase
{
    [HttpGet(ApiRoutes.Chats.Root)]
    public Task<IReadOnlyList<ChatSummaryDto>> GetChats(CancellationToken ct) => chats.GetChatsAsync(ct);

    [HttpGet(ApiRoutes.Chats.ById)]
    public Task<ChatSummaryDto> GetChat(Guid chatId, CancellationToken ct) => chats.GetChatAsync(chatId, ct);

    [HttpGet(ApiRoutes.Chats.Details)]
    public Task<ChatDetailsDto> GetDetails(Guid chatId, CancellationToken ct) => chats.GetDetailsAsync(chatId, ct);

    [HttpGet(ApiRoutes.Chats.Pinned)]
    public Task<IReadOnlyList<MessageDto>> GetPinned(Guid chatId, CancellationToken ct) => chats.GetPinnedAsync(chatId, ct);

    [HttpPost(ApiRoutes.Chats.Private)]
    public Task<ChatSummaryDto> OpenPrivate([FromBody] CreatePrivateChatRequest request, CancellationToken ct) =>
        chats.OpenPrivateChatAsync(request, ct);

    [HttpPost(ApiRoutes.Chats.Groups)]
    public Task<CreateGroupResultDto> CreateGroup([FromBody] CreateGroupRequest request, CancellationToken ct) =>
        chats.CreateGroupAsync(request, ct);

    [HttpPatch(ApiRoutes.Chats.ById)]
    public Task<ChatInfoDto> Update(Guid chatId, [FromBody] UpdateChatRequest request, CancellationToken ct) =>
        chats.UpdateChatAsync(chatId, request, ct);

    [HttpPut(ApiRoutes.Chats.Avatar)]
    public Task<ChatInfoDto> SetAvatar(Guid chatId, [FromBody] SetChatAvatarRequest request, CancellationToken ct) =>
        chats.SetAvatarAsync(chatId, request, ct);

    [HttpDelete(ApiRoutes.Chats.Avatar)]
    public Task<ChatInfoDto> RemoveAvatar(Guid chatId, CancellationToken ct) => chats.RemoveAvatarAsync(chatId, ct);

    [HttpPost(ApiRoutes.Chats.Members)]
    public Task<AddMembersResultDto> AddMembers(Guid chatId, [FromBody] AddMembersRequest request, CancellationToken ct) =>
        members.AddMembersAsync(chatId, request, ct);

    [HttpDelete(ApiRoutes.Chats.Member)]
    public async Task<IActionResult> RemoveMember(Guid chatId, Guid userId, CancellationToken ct)
    {
        await members.RemoveMemberAsync(chatId, userId, ct);
        return NoContent();
    }

    [HttpPut(ApiRoutes.Chats.MemberRole)]
    [HttpPatch(ApiRoutes.Chats.MemberRole)]
    [HttpPost(ApiRoutes.Chats.MemberRole)]
    public async Task<IActionResult> SetRole(Guid chatId, Guid userId, [FromBody] SetMemberRoleRequest request, CancellationToken ct)
    {
        await members.SetRoleAsync(chatId, userId, request, ct);
        return NoContent();
    }

    [HttpPost(ApiRoutes.Chats.Leave)]
    public async Task<IActionResult> Leave(Guid chatId, CancellationToken ct)
    {
        await members.LeaveAsync(chatId, ct);
        return NoContent();
    }

    [HttpPost(ApiRoutes.Chats.InviteLink)]
    public Task<InviteLinkDto> CreateInviteLink(Guid chatId, CancellationToken ct) => members.CreateInviteLinkAsync(chatId, ct);

    [HttpDelete(ApiRoutes.Chats.InviteLink)]
    public async Task<IActionResult> RevokeInviteLink(Guid chatId, CancellationToken ct)
    {
        await members.RevokeInviteLinkAsync(chatId, ct);
        return NoContent();
    }

    [HttpGet(ApiRoutes.Chats.Join)]
    public Task<ChatInvitePreviewDto> GetInvitePreview(string token, CancellationToken ct) => members.GetInvitePreviewAsync(token, ct);

    [HttpPost(ApiRoutes.Chats.Join)]
    public Task<ChatSummaryDto> Join(string token, CancellationToken ct) => members.JoinByInviteAsync(token, ct);

    [HttpPut(ApiRoutes.Chats.Settings)]
    [HttpPatch(ApiRoutes.Chats.Settings)]
    public Task<ChatUserSettingsDto> UpdateSettings(Guid chatId, [FromBody] UpdateChatSettingsRequest request, CancellationToken ct) =>
        chats.UpdateSettingsAsync(chatId, request, ct);

    [HttpPost(ApiRoutes.Chats.Read)]
    public async Task<IActionResult> MarkRead(Guid chatId, [FromBody] MarkReadRequest request, CancellationToken ct)
    {
        await chats.MarkReadAsync(chatId, request, ct);
        return NoContent();
    }

    /// <summary>Body <see cref="ClearHistoryRequest"/> is optional (<c>?forEveryone=</c> works too).</summary>
    [HttpPost(ApiRoutes.Chats.ClearHistory)]
    public async Task<IActionResult> ClearHistory(
        Guid chatId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ClearHistoryRequest? request,
        [FromQuery] bool? forEveryone,
        CancellationToken ct)
    {
        await chats.ClearHistoryAsync(chatId, request?.ForEveryone ?? forEveryone ?? false, ct);
        return NoContent();
    }

    [HttpDelete(ApiRoutes.Chats.ById)]
    public async Task<IActionResult> Delete(Guid chatId, [FromQuery] bool forEveryone, CancellationToken ct)
    {
        await chats.DeleteChatAsync(chatId, forEveryone, ct);
        return NoContent();
    }
}
