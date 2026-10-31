using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.Common.Security;
using SkyMessage.Application.Identity;

namespace SkyMessage.Infrastructure.Identity;

/// <summary>
/// HS256 access tokens validated by the JwtBearer setup in the Api (same issuer, audience and key; inbound
/// claim mapping is off, so <c>sub</c>/<c>sid</c>/<c>name</c> arrive verbatim).
/// </summary>
internal sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();
    private volatile CachedCredentials? _cached;

    public AccessToken Issue(Guid userId, string username, Guid sessionId, DateTimeOffset now, string? refreshBinding = null)
    {
        var jwt = options.Value;
        var expiresAt = now.AddMinutes(Math.Max(1, jwt.AccessTokenMinutes));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [SkyClaims.UserId] = userId.ToString(),
                [SkyClaims.SessionId] = sessionId.ToString(),
                [SkyClaims.Username] = username,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = Credentials(jwt.SigningKey),
        };
        if (refreshBinding is not null)
            descriptor.Claims[SkyClaims.RefreshBinding] = refreshBinding;
        return new AccessToken(_handler.CreateToken(descriptor), expiresAt);
    }

    private SigningCredentials Credentials(string signingKey)
    {
        if (string.IsNullOrEmpty(signingKey))
            throw new InvalidOperationException("Jwt:SigningKey is not configured.");
        var cached = _cached;
        if (cached is not null && cached.Key == signingKey)
            return cached.Credentials;
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        _cached = new CachedCredentials(signingKey, credentials);
        return credentials;
    }

    private sealed record CachedCredentials(string Key, SigningCredentials Credentials);
}
