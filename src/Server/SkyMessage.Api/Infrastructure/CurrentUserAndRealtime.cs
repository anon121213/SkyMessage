using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using SkyMessage.Api.Hubs;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Security;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Infrastructure;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId => ReadGuid(SkyClaims.UserId);

    public Guid SessionId => ReadGuid(SkyClaims.SessionId);

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    private Guid ReadGuid(string claim) =>
        Guid.TryParse(Principal?.FindFirst(claim)?.Value, out var id) ? id : throw new UnauthorizedException();

    /// <summary>Same claims, read from a hub connection.</summary>
    public static (Guid UserId, Guid SessionId) FromPrincipal(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirst(SkyClaims.UserId)?.Value, out var uid)
        && Guid.TryParse(principal?.FindFirst(SkyClaims.SessionId)?.Value, out var sid)
            ? (uid, sid)
            : throw new HubException(ErrorCodes.Unauthorized);
}

/// <summary>SignalR user id = the JWT "sub" claim (user Guid in "D" format).</summary>
internal sealed class SubClaimUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst(SkyClaims.UserId)?.Value;
}

internal sealed class SignalRRealtimeNotifier(IHubContext<RealtimeHub, IRealtimeClient> hub) : IRealtimeNotifier
{
    public IRealtimeClient User(Guid userId) => hub.Clients.User(userId.ToString());

    public IRealtimeClient Users(IReadOnlyCollection<Guid> userIds) =>
        hub.Clients.Users(userIds.Select(id => id.ToString()).ToList());

    public IRealtimeClient Session(Guid sessionId) => hub.Clients.Group(RealtimeHub.SessionGroup(sessionId));
}
