using Microsoft.AspNetCore.Mvc;
using SkyMessage.Application.Messaging;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

/// <summary>My chat folders and chat list settings (SPEC §6.3 "Folders and the archive").</summary>
[ApiController]
public sealed class FoldersController(IChatFolderService folders) : ControllerBase
{
    [HttpGet(ApiRoutes.Folders.Root)]
    public Task<ChatFoldersDto> Get(CancellationToken ct) => folders.GetAsync(ct);

    [HttpPut(ApiRoutes.Folders.ById)]
    public Task<ChatFoldersDto> Save(Guid folderId, [FromBody] SaveChatFolderRequest request, CancellationToken ct) =>
        folders.SaveFolderAsync(folderId, request, ct);

    [HttpDelete(ApiRoutes.Folders.ById)]
    public Task<ChatFoldersDto> Delete(Guid folderId, CancellationToken ct) => folders.DeleteFolderAsync(folderId, ct);

    [HttpPut(ApiRoutes.Folders.Order)]
    public Task<ChatFoldersDto> Reorder([FromBody] ReorderChatFoldersRequest request, CancellationToken ct) =>
        folders.ReorderFoldersAsync(request, ct);

    [HttpPut(ApiRoutes.Folders.Settings)]
    public Task<ChatFoldersDto> UpdateSettings([FromBody] ChatListSettingsDto settings, CancellationToken ct) =>
        folders.UpdateSettingsAsync(settings, ct);

    [HttpPut(ApiRoutes.Folders.Pin)]
    public Task<ChatFoldersDto> Pin(Guid folderId, Guid chatId, CancellationToken ct) => folders.PinAsync(folderId, chatId, ct);

    [HttpDelete(ApiRoutes.Folders.Pin)]
    public Task<ChatFoldersDto> Unpin(Guid folderId, Guid chatId, CancellationToken ct) => folders.UnpinAsync(folderId, chatId, ct);

    [HttpPut(ApiRoutes.Folders.PinnedOrder)]
    public Task<ChatFoldersDto> ReorderPinned(Guid folderId, [FromBody] ReorderPinnedChatsRequest request, CancellationToken ct) =>
        folders.ReorderPinnedAsync(folderId, request, ct);

    [HttpPost(ApiRoutes.Folders.Read)]
    public async Task<IActionResult> MarkRead(Guid folderId, CancellationToken ct)
    {
        await folders.MarkReadAsync(folderId, ct);
        return NoContent();
    }

    [HttpPost(ApiRoutes.Folders.ReadAll)]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        await folders.MarkReadAsync(null, ct);
        return NoContent();
    }
}
