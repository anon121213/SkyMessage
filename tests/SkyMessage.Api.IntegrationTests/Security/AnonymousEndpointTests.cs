using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>What the anonymous endpoints (SPEC §3 allow-list) reveal to someone without an account.</summary>
[Collection(ApiCollection.Name)]
public sealed class AnonymousEndpointTests(ApiFactory factory)
{
    [Fact]
    public async Task Server_info_is_public_facts_only()
    {
        using var response = await factory.NewClient().GetAsync(ApiRoutes.ServerInfo, SecurityKit.Ct);
        var json = await response.Content.ReadAsStringAsync(SecurityKit.Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json);

        var info = JsonSerializer.Deserialize<ServerInfoDto>(json, SkyJson.Options)!;
        info.InstanceId.ShouldNotBe(Guid.Empty);
        info.ApiVersion.ShouldBe(1);

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.ShouldNotContain(n => n.Contains("secret", StringComparison.OrdinalIgnoreCase)
                                    || n.Contains("password", StringComparison.OrdinalIgnoreCase)
                                    || n.Contains("key", StringComparison.OrdinalIgnoreCase)
                                    || n.Equals("registrationCode", StringComparison.OrdinalIgnoreCase)
                                    || n.Contains("connection", StringComparison.OrdinalIgnoreCase));

        var services = factory.Services;
        foreach (var secret in new[]
                 {
                     services.GetRequiredService<IOptions<JwtOptions>>().Value.SigningKey,
                     services.GetRequiredService<IOptions<LiveKitOptions>>().Value.ApiSecret,
                     services.GetRequiredService<IOptions<StorageOptions>>().Value.MasterKey,
                 })
        {
            if (!string.IsNullOrEmpty(secret)) json.ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task Probe_returns_bounded_random_bytes_that_are_never_cached()
    {
        var client = factory.NewClient();
        using var big = await client.GetAsync(ApiRoutes.ServerProbePath(10 * 1024 * 1024), SecurityKit.Ct);
        await big.ShouldHaveStatusAsync(HttpStatusCode.OK);
        (await big.Content.ReadAsByteArrayAsync(SecurityKit.Ct)).Length.ShouldBe(256 * 1024);
        big.Headers.CacheControl!.NoStore.ShouldBeTrue();
        big.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");

        using var first = await client.GetAsync(ApiRoutes.ServerProbePath(64), SecurityKit.Ct);
        using var second = await client.GetAsync(ApiRoutes.ServerProbePath(64), SecurityKit.Ct);
        var a = await first.Content.ReadAsByteArrayAsync(SecurityKit.Ct);
        var b = await second.Content.ReadAsByteArrayAsync(SecurityKit.Ct);
        a.Length.ShouldBe(64);
        a.ShouldNotBe(b);
    }

    [Fact]
    public async Task Server_proof_only_for_live_sessions_and_never_reveals_the_media_key()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var client = factory.NewClient();
        const string nonce = "nonce-0123456789";

        var proof = await client.GetJsonAsync<ServerProofDto>(ApiRoutes.ServerProofPath(alice.SessionId, nonce));
        MediaSignature.VerifyProof(MediaSignature.DecodeKey(alice.Auth.MediaKey), nonce, "localhost", proof.Proof).ShouldBeTrue();
        // A proof is bound to its nonce and host.
        MediaSignature.VerifyProof(MediaSignature.DecodeKey(alice.Auth.MediaKey), "other-nonce-000", "localhost", proof.Proof).ShouldBeFalse();
        MediaSignature.VerifyProof(MediaSignature.DecodeKey(alice.Auth.MediaKey), nonce, "mirror.example", proof.Proof).ShouldBeFalse();
        proof.Proof.ShouldNotContain(alice.Auth.MediaKey);

        await client.ProblemAsync(HttpMethod.Get, ApiRoutes.ServerProofPath(Guid.NewGuid(), nonce), null, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await client.ProblemAsync(HttpMethod.Get, ApiRoutes.ServerProofPath(alice.SessionId, "short"), null, HttpStatusCode.BadRequest, ErrorCodes.Validation);

        await alice.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Auth.Logout);
        await client.ProblemAsync(HttpMethod.Get, ApiRoutes.ServerProofPath(alice.SessionId, nonce), null, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    /// <summary>
    /// The proof exists so a client never sends tokens to an impostor address (SPEC §6.2). An impostor that relays the
    /// client's proof request to the real server — keeping its own host name in the Host header — must not get a proof
    /// that verifies for the impostor's host.
    /// </summary>
    [Fact]
    public async Task Server_proof_is_refused_for_hosts_this_deployment_does_not_serve()
    {
        var alice = await factory.RegisterUserAsync("alice");
        const string nonce = "client-nonce-42424242";

        // What an impostor at impostor.example does with the request it received from alice's app.
        var relay = factory.NewClient();
        relay.BaseAddress = new Uri("http://impostor.example/");
        using var response = await relay.GetAsync(ApiRoutes.ServerProofPath(alice.SessionId, nonce), SecurityKit.Ct);

        if (response.IsSuccessStatusCode)
        {
            var proof = (await response.Content.ReadFromJsonAsync<ServerProofDto>(SkyJson.Options, SecurityKit.Ct))!;
            MediaSignature.VerifyProof(MediaSignature.DecodeKey(alice.Auth.MediaKey), nonce, "impostor.example", proof.Proof)
                .ShouldBeFalse("the real server signed a proof for a host it does not serve: alice's app would trust impostor.example");
        }
        else
        {
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Username_check_reveals_only_availability()
    {
        var alice = await factory.RegisterUserAsync("alice", "Wonderland Secret");
        using var response = await factory.NewClient().GetAsync(ApiRoutes.Auth.UsernameAvailablePath(alice.Username.ToUpperInvariant()), SecurityKit.Ct);
        var json = await response.Content.ReadAsStringAsync(SecurityKit.Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json);
        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["available", "errorCode", "username"]);
        document.RootElement.GetProperty("available").GetBoolean().ShouldBeFalse();
        json.ShouldNotContain(alice.Id.ToString());
        json.ShouldNotContain(alice.DisplayName);
    }

    [Fact]
    public async Task Login_failures_look_the_same_for_unknown_users_and_wrong_passwords()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var client = factory.NewClient();

        var unknown = await client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
            new LoginRequest { Login = SecurityKit.NewUsername("nobody"), Password = "whatever-password", Device = SecurityKit.Device("x") },
            HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        var wrong = await client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
            new LoginRequest { Login = alice.Username, Password = "whatever-password", Device = SecurityKit.Device("x") },
            HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        var unknownPhone = await client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
            new LoginRequest { Login = "+7999" + Random.Shared.Next(1_000_000, 9_999_999), Password = "whatever-password", Device = SecurityKit.Device("x") },
            HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);

        wrong.Title.ShouldBe(unknown.Title);
        unknownPhone.Title.ShouldBe(unknown.Title);
        Strip(wrong.Root).ShouldBe(Strip(unknown.Root));
    }

    [Fact]
    public async Task Media_endpoints_need_a_bearer_or_a_signature_for_that_exact_media()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var photo = await MediaKit.UploadPhotoAsync(alice);
        var other = await MediaKit.UploadPhotoAsync(alice, "other.jpg");

        // The signed URL works without any header (this is how <img> loads it) ...
        using (var signed = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, alice.Sign(photo.Id))))
            await signed.ShouldHaveStatusAsync(HttpStatusCode.OK);

        var forOther = alice.Sign(other.Id);
        var cases = new (string Name, string Path)[]
        {
            ("no credentials", ApiRoutes.Media.ContentPath(photo.Id, null)),
            ("no credentials (thumbnail)", ApiRoutes.Media.ThumbnailPath(photo.Id, null)),
            ("garbage signature", ApiRoutes.Media.ContentPath(photo.Id, "garbage")),
            ("signature of another media", ApiRoutes.Media.ContentPath(photo.Id, forOther)),
            ("signature of another media (thumbnail)", ApiRoutes.Media.ThumbnailPath(photo.Id, forOther)),
            ("truncated signature", ApiRoutes.Media.ContentPath(photo.Id, alice.Sign(photo.Id)[..^1])),
            ("unknown session", ApiRoutes.Media.ContentPath(photo.Id, MediaSignature.Create(MediaSignature.NewKey(), Guid.NewGuid(), photo.Id))),
            ("right session, guessed key", ApiRoutes.Media.ContentPath(photo.Id, MediaSignature.Create(MediaSignature.NewKey(), alice.SessionId, photo.Id))),
        };
        foreach (var (name, path) in cases)
        {
            using var response = await MediaKit.GetSignedAsync(factory, path);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, name);
            await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        }

        // A signature dies with its session.
        var beforeLogout = alice.Sign(other.Id);
        await alice.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Auth.Logout);
        using var dead = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(other.Id, beforeLogout));
        await dead.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task LiveKit_webhook_accepts_only_LiveKit_signed_bodies()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var options = factory.Services.GetRequiredService<IOptions<LiveKitOptions>>().Value;
        var body = Encoding.UTF8.GetBytes("""{"event":"room_finished","id":"EV_security","room":{"sid":"RM_x","name":"call-00000000000000000000000000000000"}}""");
        var hash = Convert.ToBase64String(SHA256.HashData(body));

        async Task<HttpResponseMessage> PostAsync(string? authorization, byte[] payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Calls.LiveKitWebhook)
            {
                Content = new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/webhook+json") } },
            };
            if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
            return await factory.NewClient().SendAsync(request, SecurityKit.Ct);
        }

        string Sign(string secret, string issuer, string? sha256) =>
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Expires = DateTime.UtcNow.AddMinutes(5),
                Claims = sha256 is null ? new Dictionary<string, object>() : new Dictionary<string, object> { ["sha256"] = sha256 },
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256),
            });

        var rejected = new (string Name, string? Authorization)[]
        {
            ("no signature", null),
            ("a user's access token", alice.AccessToken),
            ("a user's access token as bearer", "Bearer " + alice.AccessToken),
            ("wrong secret", Sign("not-the-livekit-secret-0123456789abcdef", options.ApiKey, hash)),
            ("wrong issuer", Sign(options.ApiSecret, "someone", hash)),
            ("signature over another body", Sign(options.ApiSecret, options.ApiKey, Convert.ToBase64String(SHA256.HashData("{}"u8.ToArray())))),
            // A LiveKit *join* token is signed with the same secret but carries no body hash.
            ("a LiveKit join token", Sign(options.ApiSecret, options.ApiKey, null)),
        };
        foreach (var (name, authorization) in rejected)
        {
            using var response = await PostAsync(authorization, body);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, name);
            await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        }

        using var accepted = await PostAsync(Sign(options.ApiSecret, options.ApiKey, hash), body);
        await accepted.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_api_routes_and_LiveKit_admin_api_are_404_problems_not_the_spa()
    {
        var client = factory.NewClient();
        using (var unknown = await client.GetAsync("api/does-not-exist/" + Guid.NewGuid(), SecurityKit.Ct))
            await unknown.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);

        // LiveKit's server API (room admin) must never be reachable through the /livekit proxy.
        foreach (var path in new[] { "livekit/twirp/livekit.RoomService/ListRooms", "livekit/twirp/livekit.RoomService/DeleteRoom", "livekit/metrics" })
        {
            using var response = await client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"), SecurityKit.Ct);
            await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        using var health = await client.GetAsync(ApiRoutes.Health, SecurityKit.Ct);
        await health.ShouldHaveStatusAsync(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync(SecurityKit.Ct)).ShouldBe("Healthy");
    }

    /// <summary>The problem body without the per-request trace id.</summary>
    private static string Strip(JsonElement root) =>
        JsonSerializer.Serialize(root.EnumerateObject().Where(p => p.Name != "traceId").ToDictionary(p => p.Name, p => p.Value));
}
