using Microsoft.AspNetCore.Mvc;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

[ApiController]
public sealed class BlocksController(BlockService blocks, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet(ApiRoutes.Blocks.Root)]
    public Task<IReadOnlyList<UserSummaryDto>> List(CancellationToken ct) => blocks.ListAsync(currentUser.UserId, ct);

    [HttpPut(ApiRoutes.Blocks.ByUserId)]
    public async Task<IActionResult> Block(Guid userId, CancellationToken ct)
    {
        await blocks.BlockAsync(currentUser.UserId, userId, ct);
        return NoContent();
    }

    [HttpDelete(ApiRoutes.Blocks.ByUserId)]
    public async Task<IActionResult> Unblock(Guid userId, CancellationToken ct)
    {
        await blocks.UnblockAsync(currentUser.UserId, userId, ct);
        return NoContent();
    }
}
