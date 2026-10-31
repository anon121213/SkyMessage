using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class ServerInfoTests(ApiFactory factory)
{
    [Fact]
    public async Task Server_info_is_anonymous_and_reports_a_stable_instance_id()
    {
        var client = factory.NewClient();

        var first = await client.GetAs<ServerInfoDto>(ApiRoutes.ServerInfo);
        var second = await client.GetAs<ServerInfoDto>(ApiRoutes.ServerInfo);

        first.InstanceId.ShouldNotBe(Guid.Empty);
        second.InstanceId.ShouldBe(first.InstanceId);
        var stored = await factory.WithDbAsync(db => db.ServerProperties.AsNoTracking().SingleAsync(p => p.Key == ServerProperty.InstanceIdKey, Ct));
        Guid.Parse(stored.Value).ShouldBe(first.InstanceId);

        first.Name.ShouldBe("SkyMessage");
        first.ApiVersion.ShouldBe(1);
        first.Version.ShouldNotBeNullOrWhiteSpace();
        first.RegistrationMode.ShouldBe(RegistrationMode.Open);
        first.MaxUploadBytes.ShouldBe(Limits.DefaultMaxUploadBytes);
        first.ChunkSize.ShouldBe(Limits.DefaultChunkSize);
        first.MirrorUrls.ShouldBeEmpty();
        first.PublicBaseUrl.ShouldBeNull();
        (first.ServerTime - DateTimeOffset.UtcNow).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1));
        first.Features.ShouldContain("server-proof");
        first.Features.ShouldContain("media-signatures");
        first.Features.Contains("calls").ShouldBe(first.CallsEnabled);
    }

    [Fact]
    public async Task Server_info_reports_configured_mirrors_and_names()
    {
        await using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Server:Name", "Family Sky");
            b.UseSetting("Server:PublicBaseUrl", "https://sky.example.org");
            b.UseSetting("Server:MinClientVersion", "1.2.0");
            b.UseSetting("Server:MirrorUrls:0", "https://mirror-a.example.org");
            b.UseSetting("Server:MirrorUrls:1", " https://mirror-b.example.org ");
            b.UseSetting("Server:MirrorUrls:2", "https://mirror-a.example.org");
        });
        var client = configured.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", NewIp());

        var info = await client.GetAs<ServerInfoDto>(ApiRoutes.ServerInfo);

        info.Name.ShouldBe("Family Sky");
        info.PublicBaseUrl.ShouldBe("https://sky.example.org");
        info.MinClientVersion.ShouldBe("1.2.0");
        info.MirrorUrls.ShouldBe(["https://mirror-a.example.org", "https://mirror-b.example.org"]);
        info.InstanceId.ShouldBe((await factory.NewClient().GetAs<ServerInfoDto>(ApiRoutes.ServerInfo)).InstanceId);
    }

    [Fact]
    public async Task Proof_is_bound_to_the_session_key_the_nonce_and_the_host()
    {
        var user = await factory.NewUserAsync();
        var anonymous = factory.NewClient();
        var nonce = "nonce-" + Guid.NewGuid().ToString("N");

        var proof = await anonymous.GetAs<ServerProofDto>(ApiRoutes.ServerProofPath(user.SessionId, nonce));

        var key = MediaSignature.DecodeKey(user.Auth.MediaKey);
        proof.InstanceId.ShouldBe((await anonymous.GetAs<ServerInfoDto>(ApiRoutes.ServerInfo)).InstanceId);
        MediaSignature.VerifyProof(key, nonce, "localhost", proof.Proof).ShouldBeTrue();
        MediaSignature.VerifyProof(key, nonce, "mirror.example.org", proof.Proof).ShouldBeFalse();
        MediaSignature.VerifyProof(key, nonce + "x", "localhost", proof.Proof).ShouldBeFalse();
        MediaSignature.VerifyProof(MediaSignature.NewKey(), nonce, "localhost", proof.Proof).ShouldBeFalse();

        // The proof follows the Host the client addressed (a LAN address is a served host in Development;
        // hosts the deployment does not serve get no proof at all — see the Security tests).
        var viaLan = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.ServerProofPath(user.SessionId, nonce));
        viaLan.Headers.Host = "192.168.1.5:8443";
        var lan = await (await anonymous.SendAsync(viaLan, Ct)).ReadAsync<ServerProofDto>();
        MediaSignature.VerifyProof(key, nonce, "192.168.1.5", lan.Proof).ShouldBeTrue();
        MediaSignature.VerifyProof(key, nonce, "localhost", lan.Proof).ShouldBeFalse();
    }

    [Fact]
    public async Task Proof_is_not_found_for_unknown_or_revoked_sessions_and_validates_the_nonce()
    {
        var user = await factory.NewUserAsync();
        var anonymous = factory.NewClient();

        await (await anonymous.Get(ApiRoutes.ServerProofPath(Guid.CreateVersion7(), "valid-nonce")))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await (await anonymous.Get(ApiRoutes.ServerProofPath(user.SessionId, "short")))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await anonymous.Get(ApiRoutes.ServerProofPath(user.SessionId, new string('n', 129))))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await anonymous.Get(ApiRoutes.ServerProof + "?sid=not-a-guid&nonce=valid-nonce"))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        await (await user.Client.Post(ApiRoutes.Auth.Logout)).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        await (await anonymous.Get(ApiRoutes.ServerProofPath(user.SessionId, "valid-nonce")))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10_000_000, 256 * 1024)]
    public async Task Probe_returns_the_requested_number_of_random_bytes_within_limits(int size, int expected)
    {
        var response = await factory.NewClient().Get(ApiRoutes.ServerProbePath(size));
        await response.ShouldBeStatusAsync(HttpStatusCode.OK);

        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        bytes.Length.ShouldBe(expected);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/octet-stream");
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        if (expected >= 1000)
            bytes.Distinct().Count().ShouldBeGreaterThan(200); // random, not a constant fill
    }

    [Fact]
    public async Task Probe_without_a_size_returns_64_KB()
    {
        var bytes = await (await factory.NewClient().Get(ApiRoutes.ServerProbe)).Content.ReadAsByteArrayAsync(Ct);
        bytes.Length.ShouldBe(64 * 1024);
    }

    [Fact]
    public async Task Proof_and_probe_are_rate_limited_per_address()
    {
        var client = factory.NewClient();
        for (var i = 0; i < 60; i++)
            await (await client.Get(ApiRoutes.ServerProbePath(1))).ShouldBeStatusAsync(HttpStatusCode.OK);

        await (await client.Get(ApiRoutes.ServerProbePath(1))).ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        await (await client.Get(ApiRoutes.ServerProofPath(Guid.CreateVersion7(), "valid-nonce"))).ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        (await client.GetAs<ServerInfoDto>(ApiRoutes.ServerInfo)).InstanceId.ShouldNotBe(Guid.Empty);
        await (await factory.NewClient().Get(ApiRoutes.ServerProbePath(1))).ShouldBeStatusAsync(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("GET", ApiRoutes.Users.Me)]
    [InlineData("GET", ApiRoutes.Sessions.Root)]
    [InlineData("GET", ApiRoutes.Contacts.Root)]
    [InlineData("GET", ApiRoutes.Blocks.Root)]
    [InlineData("POST", ApiRoutes.Auth.Logout)]
    [InlineData("POST", ApiRoutes.Users.Batch)]
    [InlineData("GET", ApiRoutes.Users.Search + "?q=abc")]
    public async Task Identity_endpoints_other_than_the_anonymous_ones_require_a_session(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var response = await factory.NewClient().SendAsync(request, Ct);

        await response.ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
    }
}
