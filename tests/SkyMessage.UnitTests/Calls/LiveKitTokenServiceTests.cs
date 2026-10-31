using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using SkyMessage.Application.Calls;
using SkyMessage.Application.Common.Options;
using SkyMessage.Infrastructure.Calls;
using Xunit;

namespace SkyMessage.UnitTests.Calls;

public sealed class LiveKitTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static LiveKitTokenService Create(Action<LiveKitOptions>? configure = null)
    {
        var options = new LiveKitOptions { ApiKey = LiveKitDev.ApiKey, ApiSecret = LiveKitDev.ApiSecret, TokenTtlMinutes = 15 };
        configure?.Invoke(options);
        return new LiveKitTokenService(Options.Create(options), new FakeClock(Now), NullLogger<LiveKitTokenService>.Instance);
    }

    private static (JsonElement Header, JsonElement Payload) Decode(string token)
    {
        var parts = token.Split('.');
        parts.Length.ShouldBe(3);
        var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0])).RootElement;
        var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1])).RootElement;
        return (header, payload);
    }

    [Fact]
    public void Join_token_has_the_livekit_claim_layout()
    {
        var userId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var avatarId = Guid.CreateVersion7();

        var token = Create().CreateJoinToken(new LiveKitJoinGrant("call-0123", userId, sessionId, "Анна Петрова", avatarId));

        var (header, payload) = Decode(token);
        header.GetProperty("alg").GetString().ShouldBe("HS256");
        payload.GetProperty("iss").GetString().ShouldBe(LiveKitDev.ApiKey);
        payload.GetProperty("sub").GetString().ShouldBe($"{userId:N}.{sessionId:N}");
        payload.GetProperty("name").GetString().ShouldBe("Анна Петрова");
        payload.GetProperty("nbf").GetInt64().ShouldBe(Now.ToUnixTimeSeconds());
        payload.GetProperty("exp").GetInt64().ShouldBe(Now.AddMinutes(15).ToUnixTimeSeconds());

        var video = payload.GetProperty("video");
        video.ValueKind.ShouldBe(JsonValueKind.Object);
        video.GetProperty("room").GetString().ShouldBe("call-0123");
        video.GetProperty("roomJoin").GetBoolean().ShouldBeTrue();
        video.GetProperty("canPublish").GetBoolean().ShouldBeTrue();
        video.GetProperty("canSubscribe").GetBoolean().ShouldBeTrue();
        video.GetProperty("canPublishData").GetBoolean().ShouldBeTrue();
        video.TryGetProperty("roomAdmin", out _).ShouldBeFalse();
        video.TryGetProperty("roomCreate", out _).ShouldBeFalse();

        // LiveKit expects metadata as a string; clients JSON-parse it to group devices by user.
        var metadata = payload.GetProperty("metadata");
        metadata.ValueKind.ShouldBe(JsonValueKind.String);
        var parsed = JsonDocument.Parse(metadata.GetString()!).RootElement;
        parsed.GetProperty("userId").GetGuid().ShouldBe(userId);
        parsed.GetProperty("avatarMediaId").GetGuid().ShouldBe(avatarId);
    }

    [Fact]
    public void Metadata_carries_a_null_avatar_when_there_is_none()
    {
        var token = Create().CreateJoinToken(new LiveKitJoinGrant("call-0123", Guid.CreateVersion7(), Guid.CreateVersion7(), "Ivan", null));

        var metadata = JsonDocument.Parse(Decode(token).Payload.GetProperty("metadata").GetString()!).RootElement;
        metadata.GetProperty("avatarMediaId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Join_token_is_signed_with_the_api_secret()
    {
        var token = Create().CreateJoinToken(new LiveKitJoinGrant("call-0123", Guid.CreateVersion7(), Guid.CreateVersion7(), "Ivan", null));

        var valid = await Validate(token, LiveKitDev.ApiSecret);
        var forged = await Validate(token, LiveKitDev.ApiSecret + "x");

        valid.IsValid.ShouldBeTrue();
        forged.IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(15, 15)]
    [InlineData(100_000, 24 * 60)]
    public void Join_token_lifetime_follows_the_ttl_option_within_bounds(int configuredMinutes, int expectedMinutes)
    {
        var token = Create(o => o.TokenTtlMinutes = configuredMinutes)
            .CreateJoinToken(new LiveKitJoinGrant("call-0123", Guid.CreateVersion7(), Guid.CreateVersion7(), "Ivan", null));

        var payload = Decode(token).Payload;
        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("nbf").GetInt64()).ShouldBe(expectedMinutes * 60L);
    }

    [Fact]
    public void Server_token_carries_only_the_requested_grant()
    {
        var token = Create().CreateServerToken(new Dictionary<string, object> { ["roomAdmin"] = true, ["room"] = "call-0123" });

        var payload = Decode(token).Payload;
        payload.GetProperty("iss").GetString().ShouldBe(LiveKitDev.ApiKey);
        payload.TryGetProperty("sub", out _).ShouldBeFalse();
        var video = payload.GetProperty("video");
        video.GetProperty("roomAdmin").GetBoolean().ShouldBeTrue();
        video.GetProperty("room").GetString().ShouldBe("call-0123");
        video.TryGetProperty("roomJoin", out _).ShouldBeFalse();
    }

    [Fact]
    public void Configured_with_key_and_long_secret()
    {
        Create().IsConfigured.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, LiveKitDev.ApiKey, LiveKitDev.ApiSecret)]
    [InlineData(true, "", LiveKitDev.ApiSecret)]
    [InlineData(true, LiveKitDev.ApiKey, "")]
    [InlineData(true, LiveKitDev.ApiKey, "too-short-secret")]
    public void Not_configured_when_disabled_or_keys_are_unusable(bool enabled, string apiKey, string apiSecret)
    {
        var service = Create(o =>
        {
            o.Enabled = enabled;
            o.ApiKey = apiKey;
            o.ApiSecret = apiSecret;
        });

        service.IsConfigured.ShouldBeFalse();
    }

    private static Task<TokenValidationResult> Validate(string token, string secret) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = LiveKitDev.ApiKey,
            ValidateAudience = false,
            ValidateLifetime = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        });
}
