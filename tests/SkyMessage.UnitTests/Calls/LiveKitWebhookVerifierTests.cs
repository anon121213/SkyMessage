using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using SkyMessage.Application.Common.Options;
using SkyMessage.Infrastructure.Calls;
using Xunit;

namespace SkyMessage.UnitTests.Calls;

public sealed class LiveKitWebhookVerifierTests
{
    // Captured from livekit-server v1.13.10 (docker/livekit with the dev key pair): the raw Authorization header and
    // body of a room_started webhook. nbf = 1793302344, exp = nbf + 300 s, sha256 = base64(SHA-256(body)).
    private const string CapturedAuthorization =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJkZXZrZXkiLCJleHAiOjE3OTMzMDI2NDQsIm5iZiI6MTc5MzMwMjM0NCwiaWF0IjoxNzkzMzAyMzQ0LCJzaGEyNTYiOiIzUHBtT2dYN0xpZHR6R2NBa2pJNUJ2dk0vQVU4RTN4YVQ3ZGszcHdVOXlvPSJ9.c3oMTokjo_RJi87hVhUj3jCMCYFnh98RLBNDhWzOTN8";

    private const string CapturedBody =
        """{"event":"room_started","room":{"sid":"RM_nG8EuoKTERBg","name":"call-hooktest","emptyTimeout":30,"departureTimeout":20,"creationTime":"1793302344","creationTimeMs":"1793302344381","turnPassword":"ezr6XTpLkfmCJfCNO6J2ZtSsDgJfV3bt5GnLoe79UWKB","enabledCodecs":[{"mime":"audio/PCMU"},{"mime":"audio/PCMA"},{"mime":"audio/opus"},{"mime":"audio/red"},{"mime":"video/VP8"},{"mime":"video/H264"},{"mime":"video/VP9"},{"mime":"video/AV1"},{"mime":"video/H265"},{"mime":"video/rtx"}]},"id":"EV_eQzhrWhYGSRc","createdAt":"1793302344"}""";

    private static readonly DateTimeOffset CapturedAt = DateTimeOffset.FromUnixTimeSeconds(1793302344);

    private static LiveKitWebhookVerifier Create(DateTimeOffset now, string apiKey = LiveKitDev.ApiKey, string apiSecret = LiveKitDev.ApiSecret) =>
        new(Options.Create(new LiveKitOptions { ApiKey = apiKey, ApiSecret = apiSecret }),
            new FakeClock(now), NullLogger<LiveKitWebhookVerifier>.Instance);

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public async Task Real_livekit_webhook_verifies()
    {
        var verified = await Create(CapturedAt.AddSeconds(10)).VerifyAsync(Body(CapturedBody), CapturedAuthorization);

        verified.ShouldBeTrue();
    }

    [Fact]
    public async Task Tampered_body_is_rejected()
    {
        var tampered = CapturedBody.Replace("room_started", "room_finished");

        var verified = await Create(CapturedAt.AddSeconds(10)).VerifyAsync(Body(tampered), CapturedAuthorization);

        verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Body_with_an_extra_byte_is_rejected()
    {
        var verified = await Create(CapturedAt.AddSeconds(10)).VerifyAsync(Body(CapturedBody + "\n"), CapturedAuthorization);

        verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Signature_from_another_secret_is_rejected()
    {
        var verified = await Create(CapturedAt.AddSeconds(10), apiSecret: LiveKitDev.ApiSecret.Replace('0', '1'))
            .VerifyAsync(Body(CapturedBody), CapturedAuthorization);

        verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Token_issued_for_another_api_key_is_rejected()
    {
        var verified = await Create(CapturedAt.AddSeconds(10), apiKey: "otherkey").VerifyAsync(Body(CapturedBody), CapturedAuthorization);

        verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var verified = await Create(CapturedAt.AddMinutes(7)).VerifyAsync(Body(CapturedBody), CapturedAuthorization);

        verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Token_from_the_future_is_rejected()
    {
        var verified = await Create(CapturedAt.AddMinutes(-2)).VerifyAsync(Body(CapturedBody), CapturedAuthorization);

        verified.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("not-a-jwt")]
    public async Task Missing_or_malformed_authorization_is_rejected(string? authorization)
    {
        var verified = await Create(CapturedAt.AddSeconds(10)).VerifyAsync(Body(CapturedBody), authorization);

        verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Bearer_prefix_is_tolerated()
    {
        var verified = await Create(CapturedAt.AddSeconds(10)).VerifyAsync(Body(CapturedBody), "Bearer " + CapturedAuthorization);

        verified.ShouldBeTrue();
    }

    [Fact]
    public async Task Livekit_style_token_for_any_body_verifies_and_binds_that_body()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var body = Body("""{"event":"participant_left","room":{"name":"call-x"},"participant":{"identity":"a.b"},"id":"EV_1"}""");
        var authorization = Sign(now, Convert.ToBase64String(SHA256.HashData(body)));
        var verifier = Create(now.AddSeconds(1));

        (await verifier.VerifyAsync(body, authorization)).ShouldBeTrue();
        (await verifier.VerifyAsync(Body("{}"), authorization)).ShouldBeFalse();
    }

    [Fact]
    public async Task Token_without_a_body_hash_is_rejected()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        var verified = await Create(now).VerifyAsync(Body(CapturedBody), Sign(now, sha256: null));

        verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Unsigned_token_is_rejected()
    {
        var parts = CapturedAuthorization.Split('.');
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");

        var verified = await Create(CapturedAt.AddSeconds(10)).VerifyAsync(Body(CapturedBody), $"{header}.{parts[1]}.");

        verified.ShouldBeFalse();
    }

    /// <summary>Signs like LiveKit's webhook notifier: HS256, iss = API key, 5 minutes, sha256 claim.</summary>
    private static string Sign(DateTimeOffset now, string? sha256)
    {
        var claims = new Dictionary<string, object>();
        if (sha256 is not null) claims["sha256"] = sha256;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LiveKitDev.ApiKey,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(LiveKitDev.ApiSecret)), SecurityAlgorithms.HmacSha256),
        });
    }
}
