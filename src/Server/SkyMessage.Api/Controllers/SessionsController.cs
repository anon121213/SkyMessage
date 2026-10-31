using Microsoft.AspNetCore.Mvc;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

[ApiController]
public sealed class SessionsController(SessionService sessions, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet(ApiRoutes.Sessions.Root)]
    public Task<IReadOnlyList<SessionDto>> List(CancellationToken ct) =>
        sessions.ListAsync(currentUser.UserId, currentUser.SessionId, ct);

    [HttpDelete(ApiRoutes.Sessions.ById)]
    public async Task<IActionResult> Terminate(Guid sessionId, CancellationToken ct)
    {
        await sessions.TerminateAsync(currentUser.UserId, sessionId, ct);
        return NoContent();
    }

    [HttpDelete(ApiRoutes.Sessions.Others)]
    public async Task<IActionResult> TerminateOthers(CancellationToken ct)
    {
        await sessions.TerminateOthersAsync(currentUser.UserId, currentUser.SessionId, ct);
        return NoContent();
    }

    [HttpPut(ApiRoutes.Sessions.CurrentPush)]
    public async Task<IActionResult> RegisterPush(RegisterPushRequest request, CancellationToken ct)
    {
        await sessions.RegisterPushAsync(currentUser.UserId, currentUser.SessionId, request, ct);
        return NoContent();
    }
}
