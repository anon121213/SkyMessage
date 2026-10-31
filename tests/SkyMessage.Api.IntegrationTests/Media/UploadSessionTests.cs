using System.Net;
using System.Net.Http.Json;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary><c>POST api/media/uploads</c>: validation, size limit, content types per kind, file names (SPEC §6.4).</summary>
[Collection(ApiCollection.Name)]
public sealed class UploadSessionTests(ApiFactory factory)
{
    [Fact]
    public async Task Upload_session_describes_the_chunk_geometry_and_the_owner_sees_the_pending_media()
    {
        var user = await factory.RegisterUserAsync();
        var chunkSize = factory.Storage().ChunkSize;
        var size = 2L * chunkSize + 123;

        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "report.pdf", "application/pdf", size));

        session.MediaId.ShouldNotBe(Guid.Empty);
        session.ChunkSize.ShouldBe(chunkSize);
        session.ChunkCount.ShouldBe(3);
        session.ReceivedChunks.ShouldBeEmpty();
        session.Status.ShouldBe(MediaStatus.Uploading);

        var media = await user.GetMediaAsync(session.MediaId);
        media.Status.ShouldBe(MediaStatus.Uploading);
        media.Kind.ShouldBe(MediaKind.File);
        media.FileName.ShouldBe("report.pdf");
        media.ContentType.ShouldBe("application/pdf");
        media.SizeBytes.ShouldBe(size);
        media.HasThumbnail.ShouldBeFalse();
    }

    [Theory]
    [InlineData(1L, 1)]
    [InlineData(1024L * 1024, 1)]
    [InlineData(1024L * 1024 + 1, 2)]
    [InlineData(5L * 1024 * 1024 - 1, 5)]
    public async Task Chunk_count_is_the_size_divided_by_the_chunk_size_rounded_up(long size, int expectedForOneMebibyte)
    {
        var user = await factory.RegisterUserAsync();
        var chunkSize = factory.Storage().ChunkSize;

        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", size));

        session.ChunkCount.ShouldBe((int)((size + chunkSize - 1) / chunkSize));
        if (chunkSize == 1024 * 1024) session.ChunkCount.ShouldBe(expectedForOneMebibyte);
        (await user.GetUploadAsync(session.MediaId)).ChunkCount.ShouldBe(session.ChunkCount);
    }

    [Fact]
    public async Task Server_info_advertises_the_limits_uploads_enforce()
    {
        var user = await factory.RegisterUserAsync();

        using var response = await factory.CreateJsonClient().GetAsync(ApiRoutes.ServerInfo, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var info = (await response.Content.ReadFromJsonAsync<ServerInfoDto>(SkyJson.Options, Ct))!;

        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", info.MaxUploadBytes));
        session.ChunkSize.ShouldBe(info.ChunkSize);
        using var tooLarge = await user.Client.PostUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", info.MaxUploadBytes + 1));
        await tooLarge.ShouldBeProblemAsync(HttpStatusCode.RequestEntityTooLarge, ErrorCodes.MediaTooLarge);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Empty_or_negative_size_is_rejected(long size)
    {
        var user = await factory.RegisterUserAsync();

        using var response = await user.Client.PostUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", size));

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Size_is_limited_by_the_server_maximum()
    {
        var user = await factory.RegisterUserAsync();
        var max = factory.Storage().MaxUploadBytes;

        using (var tooLarge = await user.Client.PostUploadAsync(UploadRequest(MediaKind.Video, "movie.mp4", "video/mp4", max + 1)))
            await tooLarge.ShouldBeProblemAsync(HttpStatusCode.RequestEntityTooLarge, ErrorCodes.MediaTooLarge);

        var atLimit = await user.StartUploadAsync(UploadRequest(MediaKind.Video, "movie.mp4", "video/mp4", max));
        atLimit.ChunkCount.ShouldBe((int)((max + atLimit.ChunkSize - 1) / atLimit.ChunkSize));
    }

    [Theory]
    [InlineData(MediaKind.Photo, "image/jpeg", "image/jpeg")]
    [InlineData(MediaKind.Photo, "image/jpg", "image/jpeg")]
    [InlineData(MediaKind.Photo, "IMAGE/PNG", "image/png")]
    [InlineData(MediaKind.Photo, "image/webp", "image/webp")]
    [InlineData(MediaKind.Photo, "image/gif", "image/gif")]
    [InlineData(MediaKind.Photo, "image/heic", "image/heic")]
    [InlineData(MediaKind.Photo, "image/heif", "image/heif")]
    [InlineData(MediaKind.Video, "video/mp4", "video/mp4")]
    [InlineData(MediaKind.Video, "video/quicktime", "video/quicktime")]
    [InlineData(MediaKind.Video, "video/webm;codecs=vp9", "video/webm")]
    [InlineData(MediaKind.VideoNote, "video/mp4", "video/mp4")]
    [InlineData(MediaKind.VideoNote, "video/webm", "video/webm")]
    [InlineData(MediaKind.Voice, "audio/ogg; codecs=opus", "audio/ogg")]
    [InlineData(MediaKind.Voice, "audio/webm", "audio/webm")]
    [InlineData(MediaKind.Voice, "audio/mp4", "audio/mp4")]
    [InlineData(MediaKind.Voice, "video/webm", "video/webm")]
    [InlineData(MediaKind.Audio, "audio/mpeg", "audio/mpeg")]
    [InlineData(MediaKind.Audio, "audio/flac", "audio/flac")]
    [InlineData(MediaKind.File, "application/pdf", "application/pdf")]
    [InlineData(MediaKind.File, "text/html", "text/html")]
    [InlineData(MediaKind.File, "video/mp4", "video/mp4")]
    [InlineData(MediaKind.File, "", "application/octet-stream")]
    [InlineData(MediaKind.File, "definitely not a type", "application/octet-stream")]
    public async Task Accepted_content_types_are_stored_normalized(MediaKind kind, string declared, string stored)
    {
        var user = await factory.RegisterUserAsync();

        var session = await user.StartUploadAsync(UploadRequest(kind, "upload", declared, 100));

        (await user.GetMediaAsync(session.MediaId)).ContentType.ShouldBe(stored);
    }

    [Theory]
    [InlineData(MediaKind.Photo, "image/svg+xml")]
    [InlineData(MediaKind.Photo, "image/bmp")]
    [InlineData(MediaKind.Photo, "image/tiff")]
    [InlineData(MediaKind.Photo, "text/html")]
    [InlineData(MediaKind.Photo, "video/mp4")]
    [InlineData(MediaKind.Photo, "")]
    [InlineData(MediaKind.Video, "audio/mp4")]
    [InlineData(MediaKind.Video, "image/gif")]
    [InlineData(MediaKind.Video, "application/octet-stream")]
    [InlineData(MediaKind.VideoNote, "audio/webm")]
    [InlineData(MediaKind.Voice, "video/mp4")]
    [InlineData(MediaKind.Voice, "text/plain")]
    [InlineData(MediaKind.Audio, "video/webm")]
    [InlineData(MediaKind.Audio, "application/ogg")]
    public async Task Content_types_the_kind_does_not_accept_are_unsupported(MediaKind kind, string contentType)
    {
        var user = await factory.RegisterUserAsync();

        using var response = await user.Client.PostUploadAsync(UploadRequest(kind, "upload", contentType, 100));

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.MediaUnsupported);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\me\\Desktop\\invoice.pdf", "invoice.pdf")]
    [InlineData("invoice\u202Efdp.exe", "invoicefdp.exe")]
    [InlineData("na\u0000me\u0007.txt", "name.txt")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("   spaced out.txt   ", "spaced out.txt")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("Отчёт за 2026.pdf", "Отчёт за 2026.pdf")]
    [InlineData("evil\r\nSet-Cookie: session=1.pdf", "evilSet-Cookie_ session=1.pdf")]
    [InlineData("étude.pdf", "étude.pdf")]
    public async Task File_names_are_reduced_to_one_safe_segment(string declared, string stored)
    {
        var user = await factory.RegisterUserAsync();

        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, declared, "application/pdf", 10));

        (await user.GetMediaAsync(session.MediaId)).FileName.ShouldBe(stored);
    }

    [Theory]
    [InlineData(MediaKind.Photo, "image/jpeg", "photo.jpg")]
    [InlineData(MediaKind.Voice, "audio/ogg", "voice.ogg")]
    [InlineData(MediaKind.VideoNote, "video/mp4", "video_note.mp4")]
    [InlineData(MediaKind.File, "application/zip", "file.zip")]
    public async Task Missing_file_names_get_a_default_per_kind(MediaKind kind, string contentType, string expected)
    {
        var user = await factory.RegisterUserAsync();

        var session = await user.StartUploadAsync(UploadRequest(kind, "../", contentType, 10));

        (await user.GetMediaAsync(session.MediaId)).FileName.ShouldBe(expected);
    }

    [Fact]
    public async Task Long_file_names_are_truncated_keeping_the_extension()
    {
        var user = await factory.RegisterUserAsync();

        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, new string('я', 400) + ".pdf", "application/pdf", 10));

        var name = (await user.GetMediaAsync(session.MediaId)).FileName;
        name.Length.ShouldBe(Limits.FileNameMaxLength);
        name.ShouldEndWith(".pdf");
    }

    [Fact]
    public async Task Client_hints_are_kept_only_for_kinds_they_describe()
    {
        var user = await factory.RegisterUserAsync();
        var waveform = Enumerable.Range(0, Limits.WaveformSamples).Select(i => (byte)i).ToArray();

        CreateUploadRequest WithHints(MediaKind kind, string contentType) =>
            UploadRequest(kind, "x", contentType, 10) with { Width = 640, Height = 480, DurationMs = 1500, Waveform = waveform };

        async Task<MediaDto> StartAsync(MediaKind kind, string contentType) =>
            await user.GetMediaAsync((await user.StartUploadAsync(WithHints(kind, contentType))).MediaId);

        var photo = await StartAsync(MediaKind.Photo, "image/jpeg");
        Describe(photo).ShouldBe("640x480 duration=- waveform=-");

        var video = await StartAsync(MediaKind.Video, "video/mp4");
        Describe(video).ShouldBe("640x480 duration=1500 waveform=-");

        var voice = await StartAsync(MediaKind.Voice, "audio/ogg");
        Describe(voice).ShouldBe("-x- duration=1500 waveform=100");
        voice.Waveform.ShouldBe(waveform);

        var audio = await StartAsync(MediaKind.Audio, "audio/mpeg");
        Describe(audio).ShouldBe("-x- duration=1500 waveform=-");

        var file = await StartAsync(MediaKind.File, "application/pdf");
        Describe(file).ShouldBe("-x- duration=- waveform=-");

        // A waveform that does not have exactly the protocol's sample count is ignored, not stored half-valid.
        var odd = await user.StartUploadAsync(UploadRequest(MediaKind.Voice, "x", "audio/ogg", 10) with { Waveform = [1, 2, 3] });
        (await user.GetMediaAsync(odd.MediaId)).Waveform.ShouldBeNull();
    }

    private static string Describe(MediaDto media) =>
        $"{media.Width?.ToString() ?? "-"}x{media.Height?.ToString() ?? "-"} duration={media.DurationMs?.ToString() ?? "-"} " +
        $"waveform={media.Waveform?.Length.ToString() ?? "-"}";

    [Fact]
    public async Task Malformed_upload_requests_fail_validation()
    {
        var user = await factory.RegisterUserAsync();
        var valid = UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", 10);
        CreateUploadRequest[] invalid =
        [
            valid with { Sha256 = "not-a-hash" },
            valid with { Sha256 = new string('a', 63) },
            valid with { Width = 0 },
            valid with { Height = -5 },
            valid with { DurationMs = -1 },
            valid with { Kind = (MediaKind)99 },
            valid with { FileName = new string('x', 2000) },
            valid with { ContentType = "application/" + new string('x', 300) },
            valid with { Waveform = new byte[2000] },
        ];

        foreach (var request in invalid)
        {
            using var response = await user.Client.PostUploadAsync(request);
            await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        }

        using var nullName = await user.Client.PostAsJsonAsync(ApiRoutes.Media.Uploads,
            new { kind = "File", fileName = (string?)null, contentType = "text/plain", sizeBytes = 10 }, Ct);
        await nullName.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        using var unknownKind = await user.Client.PostAsJsonAsync(ApiRoutes.Media.Uploads,
            new { kind = "Hologram", fileName = "a", contentType = "text/plain", sizeBytes = 10 }, Ct);
        await unknownKind.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Upload_sha256_is_accepted_in_any_letter_case()
    {
        var user = await factory.RegisterUserAsync();
        var content = RandomBytes(1000, seed: 7);

        var media = await user.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "a.bin",
            r => r with { Sha256 = Sha256Hex(content).ToUpperInvariant() });

        (await user.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(content, "stored file");
    }

    [Fact]
    public async Task Upload_endpoints_require_a_signed_in_user()
    {
        var owner = await factory.RegisterUserAsync();
        var session = await owner.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", 10));
        var anonymous = factory.CreateJsonClient();

        using (var create = await anonymous.PostUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", 10)))
            await create.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        using (var chunk = await anonymous.PutChunkAsync(session.MediaId, 0, new byte[10]))
            await chunk.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        using (var status = await anonymous.GetAsync(ApiRoutes.Media.UploadPath(session.MediaId), Ct))
            await status.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        using (var complete = await anonymous.PostCompleteAsync(session.MediaId))
            await complete.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        using (var metadata = await anonymous.GetMediaResponseAsync(session.MediaId))
            await metadata.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);

        (await owner.GetUploadAsync(session.MediaId)).ReceivedChunks.ShouldBeEmpty();
    }
}
