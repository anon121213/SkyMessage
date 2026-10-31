using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using SkyMessage.Contracts;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>The development LiveKit key pair (appsettings.Development.json, docker/livekit/livekit.yaml).</summary>
internal static class LiveKitDev
{
    public const string ApiKey = "devkey";
    public const string ApiSecret = "devsecret-change-me-0123456789abcdef0123456789";

    public static SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(ApiSecret));
}

/// <summary>Decodes and verifies LiveKit JWTs issued by the API (join tokens, Twirp server tokens).</summary>
internal static class Jwt
{
    public static JsonElement Header(string jwt) => Decode(jwt.Split('.')[0]);

    public static JsonElement Payload(string jwt) => Decode(jwt.Split('.')[1]);

    /// <summary>True when the token is an HS256 JWT signed with the dev secret and issued by the dev API key.</summary>
    public static async Task<bool> IsSignedByDevKeyAsync(string jwt)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            ValidIssuer = LiveKitDev.ApiKey,
            ValidateAudience = false,
            IssuerSigningKey = LiveKitDev.SigningKey,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        });
        return result.IsValid;
    }

    private static JsonElement Decode(string part)
    {
        using var json = JsonDocument.Parse(Base64Url.DecodeFromChars(part));
        return json.RootElement.Clone();
    }
}

/// <summary>LiveKit webhook requests: protojson bodies signed like LiveKit does (raw JWT in Authorization, claim sha256).</summary>
internal static class LiveKitWebhook
{
    /// <param name="disconnectReason">Enum DisconnectReason: a name (protojson) or a number (other encoders).</param>
    public static byte[] Participant(string eventName, string roomName, string identity, string? participantSid = null,
        JsonNode? disconnectReason = null, string? eventId = null)
    {
        var participant = new JsonObject
        {
            ["sid"] = participantSid ?? "PA_" + Guid.NewGuid().ToString("N")[..12],
            ["identity"] = identity,
            ["state"] = eventName == "participant_joined" ? "ACTIVE" : "DISCONNECTED",
            ["joinedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        };
        if (disconnectReason is not null) participant["disconnectReason"] = disconnectReason;
        return Event(eventName, roomName, participant, eventId);
    }

    public static byte[] RoomFinished(string roomName, string? eventId = null) => Event("room_finished", roomName, null, eventId);

    public static byte[] Event(string eventName, string? roomName, JsonObject? participant, string? eventId = null)
    {
        var body = new JsonObject
        {
            ["event"] = eventName,
            ["id"] = eventId ?? "EV_" + Guid.NewGuid().ToString("N"),
            ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        };
        if (roomName is not null)
        {
            body["room"] = new JsonObject
            {
                ["sid"] = "RM_" + Guid.NewGuid().ToString("N")[..12],
                ["name"] = roomName,
                ["emptyTimeout"] = 300,
                ["creationTime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            };
        }
        if (participant is not null) body["participant"] = participant;
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    /// <summary>The Authorization header value LiveKit sends: a raw JWT (no scheme) binding the body by its SHA-256.</summary>
    public static string Sign(byte[] body, string apiKey = LiveKitDev.ApiKey, string apiSecret = LiveKitDev.ApiSecret,
        DateTime? notBefore = null, TimeSpan? lifetime = null, bool includeHash = true)
    {
        var from = notBefore ?? DateTime.UtcNow;
        var claims = new Dictionary<string, object>();
        if (includeHash) claims["sha256"] = Convert.ToBase64String(SHA256.HashData(body));
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = apiKey,
            IssuedAt = from,
            NotBefore = from,
            Expires = from + (lifetime ?? TimeSpan.FromMinutes(5)),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(apiSecret)), SecurityAlgorithms.HmacSha256),
        });
    }

    public static async Task<HttpResponseMessage> PostAsync(WebApplicationFactory<Program> host, byte[] body, string? authorization)
    {
        using var client = host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Calls.LiveKitWebhook) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/webhook+json");
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return await client.SendAsync(request, CallKit.Ct);
    }

    /// <summary>Posts a correctly signed webhook and expects 200.</summary>
    public static async Task SendAsync(WebApplicationFactory<Program> host, byte[] body)
    {
        var response = await PostAsync(host, body, Sign(body));
        if (!response.IsSuccessStatusCode)
            throw new ShouldAssertException($"Webhook answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CallKit.Ct)}");
    }
}
