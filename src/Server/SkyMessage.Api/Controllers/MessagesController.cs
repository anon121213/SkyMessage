using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SkyMessage.Application.Messaging;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

/// <summary>Message history, sync, sending and message actions inside one chat (SPEC §6.3).</summary>
[ApiController]
public sealed class MessagesController(IMessageService messages, IMessageSearchService search) : ControllerBase
{
    /// <summary>One of beforeSeq / afterSeq / aroundSeq (priority aroundSeq → afterSeq → beforeSeq); none = latest page.</summary>
    [HttpGet(ApiRoutes.Messages.Root)]
    public Task<MessagesPageDto> GetPage(
        Guid chatId,
        [FromQuery] long? beforeSeq,
        [FromQuery] long? afterSeq,
        [FromQuery] long? aroundSeq,
        [FromQuery] int? limit,
        CancellationToken ct) =>
        messages.GetPageAsync(chatId, beforeSeq, afterSeq, aroundSeq, limit, ct);

    [HttpPost(ApiRoutes.Messages.Root)]
    public Task<MessageDto> Send(Guid chatId, [FromBody] SendMessageRequest request, CancellationToken ct) =>
        messages.SendAsync(chatId, request, ct);

    [HttpGet(ApiRoutes.Messages.Changes)]
    public Task<MessageChangesDto> GetChanges(Guid chatId, [FromQuery] string? cursor, CancellationToken ct) =>
        messages.GetChangesAsync(chatId, cursor, ct);

    [HttpGet(ApiRoutes.Messages.Mentions)]
    public Task<IReadOnlyList<long>> GetUnreadMentions(Guid chatId, CancellationToken ct) =>
        messages.GetUnreadMentionsAsync(chatId, ct);

    [HttpPost(ApiRoutes.Messages.Forward)]
    public Task<IReadOnlyList<MessageDto>> Forward(Guid chatId, [FromBody] ForwardMessagesRequest request, CancellationToken ct) =>
        messages.ForwardAsync(chatId, request, ct);

    [HttpPost(ApiRoutes.Messages.Delete)]
    public async Task<IActionResult> Delete(Guid chatId, [FromBody] DeleteMessagesRequest request, CancellationToken ct)
    {
        await messages.DeleteAsync(chatId, request, ct);
        return NoContent();
    }

    [HttpGet(ApiRoutes.Messages.ById)]
    public Task<MessageDto> Get(Guid chatId, Guid messageId, CancellationToken ct) =>
        messages.GetMessageAsync(chatId, messageId, ct);

    [HttpPatch(ApiRoutes.Messages.ById)]
    [HttpPut(ApiRoutes.Messages.ById)]
    public Task<MessageDto> Edit(Guid chatId, Guid messageId, [FromBody] EditMessageRequest request, CancellationToken ct) =>
        messages.EditAsync(chatId, messageId, request, ct);

    [HttpPut(ApiRoutes.Messages.Reaction)]
    public Task<MessageDto> SetReaction(Guid chatId, Guid messageId, [FromBody] SetReactionRequest request, CancellationToken ct) =>
        messages.SetReactionAsync(chatId, messageId, request, ct);

    [HttpDelete(ApiRoutes.Messages.Reaction)]
    public Task<MessageDto> RemoveReaction(Guid chatId, Guid messageId, CancellationToken ct) =>
        messages.RemoveReactionAsync(chatId, messageId, ct);

    [HttpPut(ApiRoutes.Messages.Pin)]
    public async Task<IActionResult> Pin(Guid chatId, Guid messageId, CancellationToken ct)
    {
        await messages.PinAsync(chatId, messageId, ct);
        return NoContent();
    }

    [HttpDelete(ApiRoutes.Messages.Pin)]
    public async Task<IActionResult> Unpin(Guid chatId, Guid messageId, CancellationToken ct)
    {
        await messages.UnpinAsync(chatId, messageId, ct);
        return NoContent();
    }

    /// <summary>Newest first; page further with <c>beforeSeq</c> = the smallest seq received.</summary>
    [HttpGet(ApiRoutes.Messages.Search)]
    [EnableRateLimiting(RateLimitPolicies.Search)]
    public Task<MessagesPageDto> Search(Guid chatId, [FromQuery] string? q, [FromQuery] long? beforeSeq, [FromQuery] int? limit, CancellationToken ct) =>
        search.SearchInChatAsync(chatId, q, beforeSeq, limit, ct);

    /// <summary>Newest first; page further with <c>beforeSeq</c> = the smallest seq received.</summary>
    [HttpGet(ApiRoutes.Messages.SharedMedia)]
    public Task<MessagesPageDto> SharedMedia(
        Guid chatId,
        [FromQuery] SharedMediaFilter kind = SharedMediaFilter.Media,
        [FromQuery] long? beforeSeq = null,
        [FromQuery] int? limit = null,
        CancellationToken ct = default) =>
        search.GetSharedMediaAsync(chatId, kind, beforeSeq, limit, ct);
}
