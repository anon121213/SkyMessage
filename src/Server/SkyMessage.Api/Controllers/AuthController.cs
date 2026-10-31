using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

[ApiController]
public sealed class AuthController(AuthService auth, ICurrentUser currentUser) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost(ApiRoutes.Auth.Register)]
    public Task<AuthResponse> Register(RegisterRequest request, CancellationToken ct) =>
        auth.RegisterAsync(request, currentUser.IpAddress, ct);

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost(ApiRoutes.Auth.Login)]
    public Task<AuthResponse> Login(LoginRequest request, CancellationToken ct) =>
        auth.LoginAsync(request, currentUser.IpAddress, ct);

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost(ApiRoutes.Auth.Refresh)]
    public Task<AuthResponse> Refresh(RefreshRequest request, CancellationToken ct) =>
        auth.RefreshAsync(request, currentUser.IpAddress, ct);

    [HttpPost(ApiRoutes.Auth.Logout)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(currentUser.UserId, currentUser.SessionId, ct);
        return NoContent();
    }

    /// <summary>Anonymous; a signed-in caller's own username is reported as available.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.UsernameCheck)]
    [HttpGet(ApiRoutes.Auth.UsernameAvailable)]
    public Task<UsernameAvailabilityDto> UsernameAvailable([FromQuery] string? username, CancellationToken ct) =>
        auth.CheckUsernameAsync(username, currentUser.IsAuthenticated ? currentUser.UserId : null, ct);

    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost(ApiRoutes.Auth.ChangePassword)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        await auth.ChangePasswordAsync(currentUser.UserId, currentUser.SessionId, request, ct);
        return NoContent();
    }
}
