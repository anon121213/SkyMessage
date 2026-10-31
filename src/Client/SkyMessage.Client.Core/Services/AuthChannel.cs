using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Raw auth endpoints with explicit tokens. The auth service uses this instead of <see cref="ISkyApi"/> so the
/// refresh path never re-enters the 401 → refresh pipeline (and DI has no AuthService ↔ SkyApi cycle).
/// Token-bearing calls first make sure the active address is the client's own deployment.
/// </summary>
internal interface IAuthChannel
{
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task LogoutAsync(string accessToken, CancellationToken cancellationToken);
    /// <param name="accessToken">When signed in, the server reports the caller's own username as available.</param>
    Task<UsernameAvailabilityDto> CheckUsernameAsync(string username, string? accessToken, CancellationToken cancellationToken);
    Task<MeDto> GetMeAsync(string accessToken, CancellationToken cancellationToken);
}

internal sealed class AuthChannel(ApiTransport transport) : IAuthChannel
{
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(5);

    public Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken) =>
        transport.SendJsonAsync<AuthResponse>(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Auth.Login, request, idempotent: false), null, cancellationToken);

    public Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken) =>
        transport.SendJsonAsync<AuthResponse>(ApiRequest.Json(HttpMethod.Post, ApiRoutes.Auth.Register, request, idempotent: false), null, cancellationToken);

    // The server answers a repeated refresh within 2 minutes of rotation normally (lost-response retry), so it is retry-safe.
    public async Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        await transport.Endpoints.EnsureTrustedAsync(cancellationToken).ConfigureAwait(false);
        return await transport.SendJsonAsync<AuthResponse>(
                ApiRequest.Json(HttpMethod.Post, ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = refreshToken }, idempotent: true,
                    carriesCredentials: true),
                null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task LogoutAsync(string accessToken, CancellationToken cancellationToken)
    {
        await transport.Endpoints.EnsureTrustedAsync(cancellationToken).ConfigureAwait(false);
        var request = new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = ApiRoutes.Auth.Logout,
            Idempotent = true,
            Timeout = LogoutTimeout,
            AllowFailover = false,
        };
        using var _ = await transport.SendAsync(request, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsernameAvailabilityDto> CheckUsernameAsync(string username, string? accessToken, CancellationToken cancellationToken)
    {
        if (accessToken is not null)
            await transport.Endpoints.EnsureTrustedAsync(cancellationToken).ConfigureAwait(false);
        return await transport.SendJsonAsync<UsernameAvailabilityDto>(ApiRequest.Get(ApiRoutes.Auth.UsernameAvailablePath(username)), accessToken, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MeDto> GetMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        await transport.Endpoints.EnsureTrustedAsync(cancellationToken).ConfigureAwait(false);
        return await transport.SendJsonAsync<MeDto>(ApiRequest.Get(ApiRoutes.Users.Me), accessToken, cancellationToken).ConfigureAwait(false);
    }
}
