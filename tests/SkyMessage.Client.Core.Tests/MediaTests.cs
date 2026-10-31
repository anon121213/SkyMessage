using System.Net;
using System.Net.Http.Headers;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class MediaUrlBuilderTests
{
    private static readonly Uri BaseUri = new("https://sky.test/");

    private static (MediaUrlBuilder Builder, AuthSessionStore Sessions, IServerEndpointService Endpoints, byte[] Key, Guid SessionId) Create(ClientCoreOptions options)
    {
        var endpoints = Substitute.For<IServerEndpointService>();
        endpoints.BaseUri.Returns(BaseUri);
        var sessions = new AuthSessionStore(new InMemorySecureStore(), Log.For<AuthSessionStore>());
        var key = MediaSignature.NewKey();
        var sessionId = Guid.CreateVersion7();
        sessions.Set(new StoredSession { UserId = Guid.CreateVersion7(), SessionId = sessionId, RefreshToken = "r", MediaKey = MediaSignature.EncodeKey(key) });
        return (new MediaUrlBuilder(options, endpoints, sessions, Log.For<MediaUrlBuilder>()), sessions, endpoints, key, sessionId);
    }

    private static MediaDto Media(MediaKind kind = MediaKind.Photo, string contentType = "image/jpeg", long size = 1234) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = kind,
        Status = MediaStatus.Ready,
        ContentType = contentType,
        SizeBytes = size,
        FileName = "file",
    };

    [Fact]
    public void Direct_urls_carry_a_verifiable_per_file_signature()
    {
        var (builder, _, _, key, sessionId) = Create(new ClientCoreOptions { MediaUrlMode = MediaUrlMode.Direct });
        var media = Media();

        var url = new Uri(builder.ContentUrl(media));

        url.GetLeftPart(UriPartial.Path).ShouldBe($"https://sky.test/api/media/{media.Id}/content");
        var token = Uri.UnescapeDataString(url.Query["?t=".Length..]);
        MediaSignature.TryParse(token, out var parsedSession, out var signature).ShouldBeTrue();
        parsedSession.ShouldBe(sessionId);
        MediaSignature.Verify(key, media.Id, signature).ShouldBeTrue();
        builder.ContentUrl(media, download: true).ShouldEndWith("&download=1");
        builder.ThumbnailUrl(media.Id).ShouldStartWith($"https://sky.test/api/media/{media.Id}/thumbnail?t=");
        builder.AvatarUrl(null).ShouldBeNull();
        builder.ContentUrl(media).ShouldBe(builder.ContentUrl(media.Id), "signatures are cached per media id");
    }

    [Fact]
    public void App_proxy_urls_carry_size_and_type_and_honor_the_proxied_kinds()
    {
        var (builder, _, _, _, _) = Create(new ClientCoreOptions
        {
            MediaUrlMode = MediaUrlMode.AppProxy,
            AppProxyKinds = [MediaKind.Photo, MediaKind.Voice],
        });
        var photo = Media(MediaKind.Photo, "image/jpeg", 2048);
        var video = Media(MediaKind.Video, "video/mp4", 99);

        builder.ContentUrl(photo).ShouldBe($"/_sm/media/{photo.Id}/content?s=2048&ct=image%2Fjpeg");
        builder.ThumbnailUrl(video.Id).ShouldBe($"/_sm/media/{video.Id}/thumbnail");
        builder.ContentUrl(video).ShouldStartWith($"https://sky.test/api/media/{video.Id}/content?t=", Case.Sensitive,
            "kinds outside AppProxyKinds stay on direct signed URLs");
        builder.MiniThumbnailDataUrl(photo with { MiniThumbnail = [1, 2, 3] }).ShouldBe("data:image/jpeg;base64,AQID");
    }

    [Fact]
    public void Changed_fires_when_the_session_or_the_server_address_changes()
    {
        var (builder, sessions, endpoints, _, _) = Create(new ClientCoreOptions());
        var changes = 0;
        builder.Changed += () => changes++;
        var before = builder.ContentUrl(Guid.Empty);

        sessions.Set(null);
        changes.ShouldBe(1);
        endpoints.BaseUri.Returns(new Uri("https://mirror.test/"));
        endpoints.Changed += Raise.Event<Action>();

        changes.ShouldBe(2);
        builder.ContentUrl(Guid.Empty).ShouldNotBe(before);
    }
}

public sealed class MediaContentSourceTests
{
    private static readonly Guid MediaId = Guid.CreateVersion7();
    private static readonly byte[] Body = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();

    internal static MediaContentSource Create(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, IMediaFileStore? files = null,
        MediaCacheTrimmer? trimmer = null)
    {
        var handler = new StubHandler(request => Task.FromResult(respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound)));
        var factory = Substitute.For<IHttpHandlerFactory>();
        factory.CreateHandler(Arg.Any<ConnectionSettings>()).Returns(handler);
        var routing = Substitute.For<IEndpointRouting>();
        routing.BaseUri.Returns(new Uri("https://sky.test/"));
        routing.EnsureTrustedAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        routing.FailoverFromAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(false);
        var tokens = Substitute.For<ITokenSource>();
        tokens.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("token");
        var transport = new ApiTransport(new HttpClientProvider(factory, Fakes.Settings(), TimeProvider.System, Log.For<HttpClientProvider>()), routing, TimeProvider.System);
        var fetcher = new MediaFetcher(new AuthorizedSender(transport, tokens), routing, TimeProvider.System);
        files ??= Fakes.NoMediaFiles();
        return new MediaContentSource(fetcher, files, trimmer ?? Fakes.Trimmer(files), Log.For<MediaContentSource>());
    }

    private static string Path(long size = 1000, string contentType = "video%2Fmp4") => $"/_sm/media/{MediaId}/content?s={size}&ct={contentType}";

    [Theory]
    [InlineData("bytes=100-199", 206, "bytes 100-199/1000", "100")]
    [InlineData("bytes=900-", 206, "bytes 900-999/1000", "100")]
    [InlineData("bytes=-100", 206, "bytes 900-999/1000", "100")]
    [InlineData("bytes=0-5000", 206, "bytes 0-999/1000", "1000")]
    [InlineData("bytes=2000-", 416, "bytes */1000", "0")]
    [InlineData(null, 200, null, "1000")]
    [InlineData("bytes=1-2,5-6", 200, null, "1000")]
    public void Head_is_computed_from_the_url_without_io(string? range, int status, string? contentRange, string contentLength)
    {
        var source = Create();

        var head = source.GetHead(Path(), range);

        head.StatusCode.ShouldBe(status);
        head.ReasonPhrase.ShouldNotBeNullOrWhiteSpace();
        head.Headers.GetValueOrDefault("Content-Range").ShouldBe(contentRange);
        head.Headers["Content-Length"].ShouldBe(contentLength);
        head.Headers["Accept-Ranges"].ShouldBe("bytes");
        head.Headers["Content-Type"].ShouldBe("video/mp4");
        if (status == 206) head.ReasonPhrase.ShouldBe("Partial Content");
    }

    [Fact]
    public void Unknown_paths_are_404_and_active_content_is_never_served_inline()
    {
        var source = Create();

        source.CanHandle("/_sm/media/not-a-guid/content").ShouldBeFalse();
        var missing = source.GetHead("/somewhere/else", null);
        missing.StatusCode.ShouldBe(404);
        missing.ReasonPhrase.ShouldBe("Not Found");

        var html = source.GetHead(Path(contentType: "text%2Fhtml"), null);
        html.Headers["Content-Type"].ShouldBe("application/octet-stream");
        html.Headers["Content-Disposition"].ShouldBe("attachment");
        html.Headers["Content-Security-Policy"].ShouldBe("sandbox");

        var thumbnail = source.GetHead($"https://0.0.0.1/_sm/media/{MediaId}/thumbnail", null);
        thumbnail.StatusCode.ShouldBe(200);
        thumbnail.Headers["Content-Type"].ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task Ranged_bodies_are_exact_even_when_the_server_ignores_the_range()
    {
        string? sentRange = null;
        var source = Create(request =>
        {
            sentRange = request.Headers.TryGetValues("Range", out var values) ? values.Single() : null;
            request.Headers.Authorization!.Parameter.ShouldBe("token");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) };
        });

        await using var body = await source.OpenBodyAsync(Path(size: 5_000_000), "bytes=100-199");
        using var copy = new MemoryStream();
        await body.CopyToAsync(copy);

        sentRange.ShouldBe("bytes=100-199");
        copy.ToArray().ShouldBe(Body[100..200]);
    }

    [Fact]
    public async Task Partial_responses_are_passed_through()
    {
        var source = Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Body[100..200]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(100, 199, 5_000_000);
            return response;
        });

        await using var body = await source.OpenBodyAsync(Path(size: 5_000_000), "bytes=100-199");
        using var copy = new MemoryStream();
        await body.CopyToAsync(copy);

        copy.ToArray().ShouldBe(Body[100..200]);
    }

    [Fact]
    public async Task Failures_simply_end_the_body()
    {
        var source = Create(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        await using var body = await source.OpenBodyAsync(Path(size: 5_000_000), null);

        (await body.ReadAsync(new byte[16])).ShouldBe(0);
    }
}
