using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>
/// Serving media: HTTP Range over the chunked, encrypted storage, validators and the protective headers
/// (SPEC §6.4: ETag, <c>Cache-Control: private, max-age=31536000, immutable</c>, nosniff, CSP sandbox, attachments).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MediaContentTests(ApiFactory factory)
{
    [Fact]
    public async Task Full_download_returns_the_exact_bytes_and_advertises_range_support()
    {
        var owner = await factory.RegisterUserAsync();
        var chunk = factory.Storage().ChunkSize;
        var content = RandomBytes(2 * chunk + chunk / 2, seed: 70);
        var media = await owner.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "big.bin");

        using var response = await owner.Client.GetContentAsync(media.Id);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        response.Headers.AcceptRanges.ShouldContain("bytes");
        response.Content.Headers.ContentLength.ShouldBe(content.Length);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(content, "multi-chunk file");
    }

    [Fact]
    public async Task Byte_ranges_are_served_as_partial_content_across_chunk_boundaries()
    {
        var owner = await factory.RegisterUserAsync();
        var chunk = factory.Storage().ChunkSize;
        var content = RandomBytes(2 * chunk + chunk / 2, seed: 71);
        long size = content.Length;
        var media = await owner.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "big.bin");

        (long? From, long? To, long ExpectedFrom, long ExpectedTo, string What)[] cases =
        [
            (0, 99, 0, 99, "the first bytes"),
            (chunk - 10, chunk + 9, chunk - 10, chunk + 9, "across the first chunk boundary"),
            (2L * chunk - 1, 2L * chunk, 2L * chunk - 1, 2L * chunk, "the last byte of chunk 1 and the first of chunk 2"),
            (chunk, chunk, chunk, chunk, "the first byte of a chunk"),
            (1000, size - 1000, 1000, size - 1000, "a range spanning every chunk"),
            (2L * chunk, null, 2L * chunk, size - 1, "the last chunk, open-ended"),
            (size - 1, size - 1, size - 1, size - 1, "the last byte"),
            (size - 100, size + 5000, size - 100, size - 1, "an end past the file is clipped"),
            (null, 500, size - 500, size - 1, "a suffix range"),
            (null, chunk + 1, size - chunk - 1, size - 1, "a suffix range crossing a chunk boundary"),
            (null, size + 10, 0, size - 1, "a suffix longer than the file"),
        ];

        foreach (var c in cases)
        {
            using var response = await owner.Client.GetContentAsync(media.Id, configure: r => r.Headers.Range = new RangeHeaderValue(c.From, c.To));
            await response.ShouldHaveStatusAsync(HttpStatusCode.PartialContent);
            var range = response.Content.Headers.ContentRange.ShouldNotBeNull(c.What);
            range.Unit.ShouldBe("bytes", c.What);
            range.From.ShouldBe(c.ExpectedFrom, c.What);
            range.To.ShouldBe(c.ExpectedTo, c.What);
            range.Length.ShouldBe(size, c.What);
            response.Content.Headers.ContentLength.ShouldBe(c.ExpectedTo - c.ExpectedFrom + 1, c.What);
            response.Headers.ETag.ShouldNotBeNull(c.What);
            (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(content[(int)c.ExpectedFrom..(int)(c.ExpectedTo + 1)], c.What);
        }
    }

    [Fact]
    public async Task Ranges_work_when_the_size_is_an_exact_multiple_of_the_chunk_size()
    {
        var owner = await factory.RegisterUserAsync();
        var chunk = factory.Storage().ChunkSize;
        var content = RandomBytes(2 * chunk, seed: 76);
        var media = await owner.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "exact.bin");

        (await owner.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(content, "full file");
        foreach (var (from, to, start, end) in new (long?, long?, int, int)[] { (null, 1, 2 * chunk - 1, 2 * chunk - 1), (chunk, null, chunk, 2 * chunk - 1), (chunk - 1, chunk, chunk - 1, chunk) })
        {
            using var response = await owner.Client.GetContentAsync(media.Id, configure: r => r.Headers.Range = new RangeHeaderValue(from, to));
            await response.ShouldHaveStatusAsync(HttpStatusCode.PartialContent);
            (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(content[start..(end + 1)], $"bytes {start}-{end}");
        }
    }

    [Fact]
    public async Task Ranges_beyond_the_end_are_not_satisfiable()
    {
        var owner = await factory.RegisterUserAsync();
        var content = RandomBytes(10_000, seed: 72);
        var media = await owner.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "a.bin");

        foreach (var (from, to) in new (long?, long?)[] { (content.Length, null), (content.Length + 10, content.Length + 20) })
        {
            using var response = await owner.Client.GetContentAsync(media.Id, configure: r => r.Headers.Range = new RangeHeaderValue(from, to));
            response.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
            var range = response.Content.Headers.ContentRange.ShouldNotBeNull();
            range.HasRange.ShouldBeFalse();
            range.Length.ShouldBe(content.Length);
        }
    }

    [Fact]
    public async Task Multiple_ranges_are_answered_with_the_whole_content()
    {
        var owner = await factory.RegisterUserAsync();
        var content = RandomBytes(10_000, seed: 73);
        var media = await owner.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "a.bin");
        var ranges = new RangeHeaderValue();
        ranges.Ranges.Add(new RangeItemHeaderValue(0, 9));
        ranges.Ranges.Add(new RangeItemHeaderValue(100, 109));

        using var response = await owner.Client.GetContentAsync(media.Id, configure: r => r.Headers.Range = ranges);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(content, "multi-range request");
    }

    [Fact]
    public async Task Players_can_seek_with_a_signed_url()
    {
        var owner = await factory.RegisterUserAsync();
        var chunk = factory.Storage().ChunkSize;
        var content = RandomBytes(chunk + 4096, seed: 74);
        var media = await owner.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "a.bin");

        using var response = await factory.CreateJsonClient().GetContentAsync(media.Id, owner.Sign(media.Id),
            configure: r => r.Headers.Range = new RangeHeaderValue(chunk - 2048, chunk + 2047));

        await response.ShouldHaveStatusAsync(HttpStatusCode.PartialContent);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(content[(chunk - 2048)..(chunk + 2048)], "signed range");
    }

    [Fact]
    public async Task Strong_etag_supports_revalidation_and_if_range()
    {
        var owner = await factory.RegisterUserAsync();
        var content = RandomBytes(10_000, seed: 75);
        var media = await owner.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "a.bin");

        using var first = await owner.Client.GetContentAsync(media.Id);
        var etag = first.Headers.ETag.ShouldNotBeNull();
        etag.IsWeak.ShouldBeFalse();
        etag.Tag.ShouldStartWith("\"");
        using (var second = await owner.Client.GetContentAsync(media.Id, owner.Sign(media.Id)))
            second.Headers.ETag.ShouldBe(etag, "the validator is stable and independent of how the request is authorized");

        using (var notModified = await owner.Client.GetContentAsync(media.Id, configure: r => r.Headers.IfNoneMatch.Add(etag)))
        {
            notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);
            (await notModified.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        }
        using (var changed = await owner.Client.GetContentAsync(media.Id, configure: r => r.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"other\""))))
            await changed.ShouldHaveStatusAsync(HttpStatusCode.OK);

        using (var current = await owner.Client.GetContentAsync(media.Id, configure: r =>
               {
                   r.Headers.Range = new RangeHeaderValue(10, 19);
                   r.Headers.IfRange = new RangeConditionHeaderValue(etag);
               }))
        {
            await current.ShouldHaveStatusAsync(HttpStatusCode.PartialContent);
            (await current.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(content[10..20], "If-Range with the current ETag");
        }
        using (var stale = await owner.Client.GetContentAsync(media.Id, configure: r =>
               {
                   r.Headers.Range = new RangeHeaderValue(10, 19);
                   r.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"stale\""));
               }))
        {
            await stale.ShouldHaveStatusAsync(HttpStatusCode.OK);
            (await stale.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(content, "If-Range with a stale ETag");
        }
    }

    [Fact]
    public async Task Photos_are_served_inline_with_caching_and_protective_headers()
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadReadyAsync(MediaKind.Photo, TestImages.Png(640, 480), "image/png", "holiday.png");

        using var content = await owner.Client.GetContentAsync(media.Id);
        await content.ShouldHaveStatusAsync(HttpStatusCode.OK);
        content.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
        ShouldBeDisposition(content, "inline", "holiday.png");
        ShouldHaveProtectiveHeaders(content);

        using var thumbnail = await owner.Client.GetThumbnailAsync(media.Id);
        await thumbnail.ShouldHaveStatusAsync(HttpStatusCode.OK);
        thumbnail.Content.Headers.ContentType?.MediaType.ShouldBe("image/jpeg");
        ShouldBeDisposition(thumbnail, "inline", "holiday.jpg");
        ShouldHaveProtectiveHeaders(thumbnail);
        thumbnail.Headers.ETag.ShouldNotBeNull().ShouldNotBe(content.Headers.ETag, "content and preview are different representations");
    }

    [Fact]
    public async Task Files_are_always_served_as_attachments()
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadReadyAsync(MediaKind.File, "%PDF-1.4 test"u8.ToArray(), "application/pdf", "report.pdf");

        foreach (var download in new[] { false, true })
        {
            using var response = await owner.Client.GetContentAsync(media.Id, download: download);
            await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/pdf");
            ShouldBeDisposition(response, "attachment", "report.pdf");
            ShouldHaveProtectiveHeaders(response);
        }
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xhtml+xml")]
    [InlineData("image/svg+xml")]
    [InlineData("text/xml")]
    [InlineData("application/xml")]
    [InlineData("application/rss+xml")]
    [InlineData("text/javascript")]
    [InlineData("application/javascript")]
    [InlineData("application/x-shockwave-flash")]
    [InlineData("multipart/related")]
    [InlineData("text/html; charset=utf-8")]
    public async Task Active_content_types_are_only_served_as_octet_stream_attachments(string contentType)
    {
        var owner = await factory.RegisterUserAsync();
        var page = "<html><body><script>alert(document.cookie)</script></body></html>"u8.ToArray();
        var media = await owner.UploadReadyAsync(MediaKind.File, page, contentType, "page.html");

        foreach (var signature in new[] { null, owner.Sign(media.Id) })
        {
            var client = signature is null ? owner.Client : factory.CreateJsonClient();
            using var response = await client.GetContentAsync(media.Id, signature);
            await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/octet-stream");
            ShouldBeDisposition(response, "attachment", "page.html");
            ShouldHaveProtectiveHeaders(response);
            (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeSameBytes(page, "the bytes themselves are untouched");
        }
    }

    [Fact]
    public async Task Audio_with_a_scriptable_subtype_is_an_octet_stream_attachment_too()
    {
        var owner = await factory.RegisterUserAsync();
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"u8.ToArray();

        var media = await owner.UploadReadyAsync(MediaKind.Audio, svg, "audio/x-evil+xml", "tune.svg");

        using var response = await owner.Client.GetContentAsync(media.Id);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/octet-stream");
        ShouldBeDisposition(response, "attachment", "tune.svg");
    }

    [Theory]
    [InlineData("1", "attachment")]
    [InlineData("true", "attachment")]
    [InlineData("0", "inline")]
    public async Task Download_flag_forces_an_attachment_without_changing_the_type(string flag, string disposition)
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync("cat.png");

        using var response = await owner.Client.GetAsync($"{ApiRoutes.Media.ContentPath(media.Id, null)}?download={flag}", Ct);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
        ShouldBeDisposition(response, disposition, "cat.png");
    }

    [Fact]
    public async Task Non_ascii_file_names_are_encoded_in_content_disposition()
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadReadyAsync(MediaKind.File, "x"u8.ToArray(), "application/pdf", "Отчёт за 2026.pdf");

        using var response = await owner.Client.GetContentAsync(media.Id);

        var disposition = response.Content.Headers.ContentDisposition.ShouldNotBeNull();
        disposition.DispositionType.ShouldBe("attachment");
        disposition.FileNameStar.ShouldBe("Отчёт за 2026.pdf");
        disposition.FileName.ShouldNotBeNull().ShouldAllBe(c => c < 128, "the plain filename= parameter is the ASCII fallback");
    }

    [Fact]
    public async Task File_names_cannot_inject_response_headers()
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadReadyAsync(MediaKind.File, "x"u8.ToArray(), "application/pdf", "evil\r\nSet-Cookie: session=1\r\n.pdf");

        using var response = await owner.Client.GetContentAsync(media.Id);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        ShouldBeDisposition(response, "attachment", "evilSet-Cookie_ session=1.pdf");
    }

    [Fact]
    public async Task Error_responses_are_never_cached_as_immutable()
    {
        var owner = await factory.RegisterUserAsync();
        var stranger = await factory.RegisterUserAsync();
        var session = await owner.StartUploadAsync(UploadRequest(MediaKind.Photo, "soon.png", "image/png", 100));
        var ready = await owner.UploadPhotoAsync();

        using var notReady = await owner.Client.GetContentAsync(session.MediaId);
        using var notFound = await stranger.Client.GetContentAsync(ready.Id);
        using var unauthorized = await factory.CreateJsonClient().GetContentAsync(ready.Id);

        notReady.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        notFound.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        foreach (var response in new[] { notReady, notFound, unauthorized })
        {
            var cacheControl = response.Headers.CacheControl;
            (cacheControl?.Extensions.Any(e => e.Name == "immutable") ?? false).ShouldBeFalse($"{response.StatusCode} must stay revalidatable");
            (cacheControl?.MaxAge ?? TimeSpan.Zero).ShouldBe(TimeSpan.Zero, $"{response.StatusCode} must not be cached");
        }
    }

    private static void ShouldBeDisposition(HttpResponseMessage response, string type, string fileName)
    {
        var disposition = response.Content.Headers.ContentDisposition.ShouldNotBeNull("Content-Disposition is always sent");
        disposition.DispositionType.ShouldBe(type);
        (disposition.FileNameStar ?? disposition.FileName?.Trim('"')).ShouldBe(fileName);
    }

    private static void ShouldHaveProtectiveHeaders(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl.ShouldNotBeNull();
        cacheControl.Private.ShouldBeTrue();
        cacheControl.Public.ShouldBeFalse();
        cacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(31_536_000));
        cacheControl.Extensions.ShouldContain(e => e.Name == "immutable");
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["sandbox"]);
        var etag = response.Headers.ETag.ShouldNotBeNull();
        etag.IsWeak.ShouldBeFalse();
    }
}
