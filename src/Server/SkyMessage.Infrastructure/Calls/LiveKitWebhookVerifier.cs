using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkyMessage.Application.Calls;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;

namespace SkyMessage.Infrastructure.Calls;

/// <summary>
/// LiveKit signs every webhook with a JWT in the raw <c>Authorization</c> header (no "Bearer" prefix): HS256 with
/// the API secret, <c>iss</c> = API key, valid for 5 minutes, claim <c>sha256</c> = base64(SHA-256(body)).
/// </summary>
internal sealed class LiveKitWebhookVerifier(IOptions<LiveKitOptions> options, IClock clock, ILogger<LiveKitWebhookVerifier> logger)
    : ILiveKitWebhookVerifier
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);
    private const string BearerPrefix = "Bearer ";

    private readonly JsonWebTokenHandler _handler = new();

    public async Task<bool> VerifyAsync(ReadOnlyMemory<byte> body, string? authorization)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(authorization) || string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrEmpty(o.ApiSecret))
            return false;

        var token = authorization.Trim();
        if (token.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)) token = token[BearerPrefix.Length..].Trim();

        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = o.ApiKey,
            ValidateAudience = false,
            // Checked below against IClock (deterministic, same clock as the rest of the server).
            ValidateLifetime = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.ApiSecret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
        });
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            logger.LogDebug("LiveKit webhook token rejected: {Reason}", result.Exception?.GetType().Name);
            return false;
        }

        var now = clock.UtcNow.UtcDateTime;
        if (jwt.ValidTo == DateTime.MinValue || now > jwt.ValidTo + ClockSkew) return false;
        if (jwt.ValidFrom != DateTime.MinValue && now < jwt.ValidFrom - ClockSkew) return false;

        if (!jwt.TryGetPayloadValue<string>("sha256", out var claimed) || !TryDecodeBase64(claimed, out var claimedHash))
            return false;
        Span<byte> actualHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body.Span, actualHash);
        return CryptographicOperations.FixedTimeEquals(actualHash, claimedHash);
    }

    /// <summary>LiveKit uses standard base64 with padding; the URL-safe alphabet is accepted as well.</summary>
    private static bool TryDecodeBase64(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value)) return false;
        var buffer = new byte[(value.Length * 3 / 4) + 3];
        if (Convert.TryFromBase64String(value, buffer, out var written)
            || (Base64Url.IsValid(value) && Base64Url.TryDecodeFromChars(value, buffer, out written)))
        {
            bytes = buffer[..written];
            return true;
        }
        return false;
    }
}
