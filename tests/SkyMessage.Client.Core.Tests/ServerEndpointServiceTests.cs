using System.Net;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class ServerEndpointServiceTests
{
    private static readonly Uri Primary = new("https://primary.test/");
    private static readonly Uri Mirror = new("https://mirror.test/");

    private sealed class Harness
    {
        public Guid InstanceId { get; } = Guid.CreateVersion7();
        public byte[] MediaKey { get; } = MediaSignature.NewKey();
        public Guid SessionId { get; } = Guid.CreateVersion7();
        public AuthSessionStore Sessions { get; }
        public ServerEndpointService Service { get; }
        public List<string> ProofRequests { get; } = [];
        public InMemorySecureStore Preferences { get; } = new();
        public ISettingsService Settings { get; } = Fakes.Settings();

        /// <param name="mirrorInstance">InstanceId the mirror reports.</param>
        /// <param name="proofKey">Key the mirror signs proofs with (null = the session's own key).</param>
        public Harness(bool signedIn, Guid? mirrorInstance = null, byte[]? proofKey = null)
        {
            var preferences = Preferences;
            preferences.Values[ServerEndpointService.StateKey] = JsonSerializer.Serialize(new EndpointState { InstanceId = InstanceId }, SkyJson.Options);
            Sessions = new AuthSessionStore(new InMemorySecureStore(), Log.For<AuthSessionStore>());
            if (signedIn)
            {
                Sessions.Set(new StoredSession
                {
                    UserId = Guid.CreateVersion7(),
                    SessionId = SessionId,
                    AccessToken = "access",
                    RefreshToken = "refresh",
                    MediaKey = MediaSignature.EncodeKey(MediaKey),
                });
            }

            var reported = mirrorInstance ?? InstanceId;
            var handler = new StubHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == Primary.Host) throw new HttpRequestException("unreachable");
                var path = uri.AbsolutePath.TrimStart('/');
                if (path == ApiRoutes.ServerInfo)
                    return Task.FromResult(StubHandler.Json(new ServerInfoDto { InstanceId = reported }));
                if (path == ApiRoutes.ServerProbe)
                    return Task.FromResult(StubHandler.Bytes(64 * 1024));
                if (path == ApiRoutes.ServerProof)
                {
                    var nonce = Query(uri, "nonce");
                    lock (ProofRequests) ProofRequests.Add(nonce);
                    var proof = MediaSignature.CreateProof(proofKey ?? MediaKey, nonce, uri.Host);
                    return Task.FromResult(StubHandler.Json(new ServerProofDto { InstanceId = reported, Proof = proof }));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            var factory = Substitute.For<IHttpHandlerFactory>();
            factory.CreateHandler(Arg.Any<ConnectionSettings>()).Returns(_ => handler);
            var settings = Settings;
            var clients = new HttpClientProvider(factory, settings, TimeProvider.System, Log.For<HttpClientProvider>());
            var options = new ClientCoreOptions { DefaultServerUrl = Primary.ToString(), FallbackServerUrls = [Mirror.ToString()] };
            Service = new ServerEndpointService(options, settings, clients, preferences, Sessions, Substitute.For<IServiceProvider>(),
                TimeProvider.System, Log.For<ServerEndpointService>());
        }

        private static string Query(Uri uri, string name) =>
            uri.Query.TrimStart('?').Split('&')
                .Select(p => p.Split('=', 2))
                .Where(p => p[0] == name)
                .Select(p => Uri.UnescapeDataString(p[1]))
                .First();
    }

    [Fact]
    public async Task Fails_over_to_a_mirror_that_proves_it_holds_the_session_key()
    {
        var h = new Harness(signedIn: true);
        await h.Service.InitializeAsync();
        h.Service.BaseUri.ShouldBe(Primary);
        h.Service.Candidates.ShouldBe([Primary, Mirror]);

        (await h.Service.FailoverAsync()).ShouldBeTrue();

        h.Service.BaseUri.ShouldBe(Mirror);
        h.ProofRequests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_impostor_with_the_same_InstanceId_but_without_the_key_is_rejected()
    {
        var h = new Harness(signedIn: true, proofKey: MediaSignature.NewKey());
        await h.Service.InitializeAsync();
        var changes = 0;
        h.Service.Changed += () => changes++;

        (await h.Service.FailoverAsync()).ShouldBeFalse();

        h.Service.BaseUri.ShouldBe(Primary, "no token may be sent to an unverified address");
        h.ProofRequests.Count.ShouldBe(1);
        changes.ShouldBe(0);
    }

    [Fact]
    public async Task A_mirror_of_another_deployment_is_never_used()
    {
        var h = new Harness(signedIn: true, mirrorInstance: Guid.CreateVersion7());
        await h.Service.InitializeAsync();

        (await h.Service.FailoverAsync()).ShouldBeFalse();

        h.Service.BaseUri.ShouldBe(Primary);
        h.ProofRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_tokens_the_same_deployment_is_enough()
    {
        var h = new Harness(signedIn: false);
        await h.Service.InitializeAsync();

        (await h.Service.FailoverAsync()).ShouldBeTrue();

        h.Service.BaseUri.ShouldBe(Mirror);
        h.ProofRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Custom_connection_settings_loaded_at_start_keep_the_remembered_deployment()
    {
        var h = new Harness(signedIn: true);
        var backup = new Uri("https://backup.test/");
        // App start: the settings load (and raise Changed) before the endpoint memory is restored.
        var loaded = new AppSettings();
        loaded.Connection.ExtraServerUrls = [backup.ToString()];
        h.Settings.Current.Returns(loaded);
        h.Settings.Changed += Raise.Event<Action>();

        await h.Service.InitializeAsync();

        h.Service.KnownInstanceId.ShouldBe(h.InstanceId);
        h.Preferences.Values[ServerEndpointService.StateKey].ShouldContain(h.InstanceId.ToString());
        h.Service.Candidates.ShouldBe([Primary, backup, Mirror]);
        (await h.Service.FailoverAsync()).ShouldBeTrue("the backup address proves it serves the same deployment");
        h.Service.BaseUri.ShouldBe(backup);
    }

    [Fact]
    public void LiveKit_and_hub_urls_resolve_relative_to_the_base_address()
    {
        var h = new Harness(signedIn: false);

        h.Service.ResolveLiveKitUrl("/livekit").ShouldBe("wss://primary.test/livekit");
        h.Service.ResolveLiveKitUrl("wss://calls.example/rtc").ShouldBe("wss://calls.example/rtc");
        h.Service.Resolve(HubRoutes.Realtime).ShouldBe(new Uri("https://primary.test/hubs/realtime"));
    }

    [Theory]
    [InlineData("sky.example.org", "https://sky.example.org/")]
    [InlineData("http://10.0.2.2:8080", "http://10.0.2.2:8080/")]
    [InlineData("https://host/prefix?x=1#y", "https://host/prefix/")]
    public void Addresses_are_normalized(string input, string expected)
    {
        ServerEndpointService.TryNormalize(input, out var uri).ShouldBeTrue();
        uri.ToString().ShouldBe(expected);
    }
}
