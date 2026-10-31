using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

public enum PasswordCheckResult
{
    Failed = 0,
    Success = 1,
    /// <summary>Correct, but hashed with outdated parameters: store a fresh hash.</summary>
    SuccessRehashNeeded = 2,
}

/// <summary>Password hashing (Infrastructure: ASP.NET Core Identity's PBKDF2 <c>PasswordHasher</c>).</summary>
public interface IUserPasswordHasher
{
    string Hash(User user, string password);

    PasswordCheckResult Verify(User user, string passwordHash, string password);

    /// <summary>
    /// Verifies against a fixed throw-away hash. Used for unknown logins so that "no such user" costs the
    /// same time as "wrong password".
    /// </summary>
    void VerifyDummy(string password);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Issues access JWTs (HS256, claims sub/sid/name/jti). Infrastructure.</summary>
public interface IAccessTokenIssuer
{
    /// <param name="refreshBinding">Claim <c>rtb</c>: <see cref="RefreshTokens.Binding"/> of the refresh token issued alongside.</param>
    AccessToken Issue(Guid userId, string username, Guid sessionId, DateTimeOffset now, string? refreshBinding = null);
}

/// <summary>Live realtime connections per signed-in device, filled by the hub. Infrastructure.</summary>
public interface ISessionConnections
{
    /// <summary>
    /// Aborts every realtime connection of the session. Call after <c>SessionRevoked</c> was sent; the abort
    /// is slightly delayed so that event can still be flushed to the client.
    /// </summary>
    void AbortSession(Guid sessionId);
}
