using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SkyMessage.Application.Messaging;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

/// <summary>Search across all of the caller's chats (SPEC §6.3).</summary>
[ApiController]
[EnableRateLimiting(RateLimitPolicies.Search)]
public sealed class SearchController(IMessageSearchService search) : ControllerBase
{
    /// <summary>Chats, people and messages matching <c>q</c> (each section capped for search-as-you-type).</summary>
    [HttpGet(ApiRoutes.Search.Global)]
    public Task<GlobalSearchResultDto> Search([FromQuery] string? q, CancellationToken ct) => search.SearchAsync(q, ct);

    /// <summary>Message hits across all chats, newest first.</summary>
    [HttpGet(ApiRoutes.Search.Messages)]
    public Task<IReadOnlyList<MessageSearchHitDto>> SearchMessages([FromQuery] string? q, [FromQuery] int? limit, CancellationToken ct) =>
        search.SearchMessagesAsync(q, limit, ct);
}
