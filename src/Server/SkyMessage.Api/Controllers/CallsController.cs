using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SkyMessage.Application.Calls;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

[ApiController]
public sealed class CallsController(ICallService calls, ICurrentUser currentUser) : ControllerBase
{
    [HttpPost(ApiRoutes.Calls.Root)]
    public Task<CallJoinInfoDto> Start(StartCallRequest request, CancellationToken cancellationToken) =>
        calls.StartAsync(currentUser.UserId, currentUser.SessionId, request, cancellationToken);

    [HttpGet(ApiRoutes.Calls.ById)]
    public Task<CallDto> Get(Guid callId, CancellationToken cancellationToken) =>
        calls.GetAsync(currentUser.UserId, callId, cancellationToken);

    [HttpPost(ApiRoutes.Calls.Accept)]
    public Task<CallJoinInfoDto> Accept(Guid callId, CancellationToken cancellationToken) =>
        calls.AcceptAsync(currentUser.UserId, currentUser.SessionId, callId, cancellationToken);

    [HttpPost(ApiRoutes.Calls.Decline)]
    public async Task<IActionResult> Decline(
        Guid callId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeclineCallRequest? request,
        CancellationToken cancellationToken)
    {
        await calls.DeclineAsync(currentUser.UserId, callId, request?.Busy ?? false, cancellationToken);
        return NoContent();
    }

    [HttpPost(ApiRoutes.Calls.Join)]
    public Task<CallJoinInfoDto> Join(Guid callId, CancellationToken cancellationToken) =>
        calls.JoinAsync(currentUser.UserId, currentUser.SessionId, callId, cancellationToken);

    [HttpPost(ApiRoutes.Calls.Hangup)]
    public async Task<IActionResult> Hangup(Guid callId, CancellationToken cancellationToken)
    {
        await calls.HangupAsync(currentUser.UserId, currentUser.SessionId, callId, cancellationToken);
        return NoContent();
    }

    /// <summary>204 when the chat has no unfinished call.</summary>
    [HttpGet(ApiRoutes.Calls.Active)]
    public async Task<ActionResult<CallDto>> Active([FromQuery] Guid chatId, CancellationToken cancellationToken)
    {
        var call = await calls.GetActiveAsync(currentUser.UserId, chatId, cancellationToken);
        if (call is null) return NoContent();
        return call;
    }

    [HttpGet(ApiRoutes.Calls.History)]
    public Task<IReadOnlyList<CallHistoryItemDto>> History(
        [FromQuery] DateTimeOffset? before,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        calls.GetHistoryAsync(currentUser.UserId, before, limit ?? CallHistoryLimits.Default, cancellationToken);
}
