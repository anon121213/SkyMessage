using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>Chunk PUT, resume status and complete (SPEC §6.4).</summary>
[Collection(ApiCollection.Name)]
public sealed class UploadChunkTests(ApiFactory factory)
{
    [Fact]
    public async Task Chunks_must_have_exactly_their_expected_length()
    {
        var user = await factory.RegisterUserAsync();
        var chunkSize = factory.Storage().ChunkSize;
        var content = RandomBytes(chunkSize + 10, seed: 1);
        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", content.Length));

        byte[][] wrongFirst = [content[..(chunkSize - 1)], [.. content[..chunkSize], 0], []];
        foreach (var bytes in wrongFirst)
        {
            using var response = await user.Client.PutChunkAsync(session.MediaId, 0, bytes);
            await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ChunkInvalid);
        }
        // The last chunk is the remainder, not a full chunk.
        byte[][] wrongLast = [content[chunkSize..^1], [.. content[chunkSize..], 0], content[..chunkSize]];
        foreach (var bytes in wrongLast)
        {
            using var response = await user.Client.PutChunkAsync(session.MediaId, 1, bytes);
            await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ChunkInvalid);
        }
        (await user.GetUploadAsync(session.MediaId)).ReceivedChunks.ShouldBeEmpty("rejected chunks must not be stored");

        await user.PutChunkOkAsync(session.MediaId, 0, content[..chunkSize]);
        await user.PutChunkOkAsync(session.MediaId, 1, content[chunkSize..]);
        await user.CompleteAsync(session.MediaId);

        (await user.WaitProcessedAsync(session.MediaId)).Status.ShouldBe(MediaStatus.Ready);
        (await user.Client.DownloadAsync(session.MediaId)).ShouldBeSameBytes(content, "assembled file");
    }

    [Fact]
    public async Task Chunk_length_is_enforced_without_a_content_length_header()
    {
        var user = await factory.RegisterUserAsync();
        var content = RandomBytes(5000, seed: 2);
        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", content.Length));

        foreach (var bytes in new[] { content[..4999], [.. content, 1, 2, 3] })
        {
            using var response = await user.Client.PutAsync(ApiRoutes.Media.UploadChunkPath(session.MediaId, 0), new UnknownLengthContent(bytes), Ct);
            await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ChunkInvalid);
        }

        using (var exact = await user.Client.PutAsync(ApiRoutes.Media.UploadChunkPath(session.MediaId, 0), new UnknownLengthContent(content), Ct))
            await exact.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        await user.CompleteAsync(session.MediaId);
        await user.WaitProcessedAsync(session.MediaId);
        (await user.Client.DownloadAsync(session.MediaId)).ShouldBeSameBytes(content, "file sent with chunked transfer encoding");
    }

    [Fact]
    public async Task Chunk_index_must_be_inside_the_upload()
    {
        var user = await factory.RegisterUserAsync();
        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", 100));

        foreach (var index in new[] { -1, 1, 2, int.MaxValue })
        {
            using var response = await user.Client.PutChunkAsync(session.MediaId, index, new byte[100]);
            await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ChunkInvalid);
        }
        (await user.GetUploadAsync(session.MediaId)).ReceivedChunks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Retried_chunks_overwrite_the_previous_bytes()
    {
        var user = await factory.RegisterUserAsync();
        var chunkSize = factory.Storage().ChunkSize;
        var first = RandomBytes(chunkSize, seed: 3);
        var retry = RandomBytes(chunkSize, seed: 4);
        var last = RandomBytes(777, seed: 5);
        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", chunkSize + last.Length));

        await user.PutChunkOkAsync(session.MediaId, 0, first);
        await user.PutChunkOkAsync(session.MediaId, 1, last);
        await user.PutChunkOkAsync(session.MediaId, 0, retry);
        await user.PutChunkOkAsync(session.MediaId, 1, last);

        (await user.GetUploadAsync(session.MediaId)).ReceivedChunks.ShouldBe([0, 1]);
        (await user.CompleteAsync(session.MediaId)).SizeBytes.ShouldBe(chunkSize + last.Length);
        (await user.WaitProcessedAsync(session.MediaId)).Status.ShouldBe(MediaStatus.Ready);
        (await user.Client.DownloadAsync(session.MediaId)).ShouldBeSameBytes([.. retry, .. last], "the retried chunk wins");
    }

    [Fact]
    public async Task Other_users_cannot_see_or_touch_an_upload()
    {
        var owner = await factory.RegisterUserAsync();
        var stranger = await factory.RegisterUserAsync();
        var session = await owner.StartUploadAsync(UploadRequest(MediaKind.File, "secret.bin", "application/octet-stream", 10));
        await owner.PutChunkOkAsync(session.MediaId, 0, RandomBytes(10, seed: 6));

        foreach (var mediaId in new[] { session.MediaId, Guid.NewGuid() })
        {
            using (var put = await stranger.Client.PutChunkAsync(mediaId, 0, new byte[10]))
                await put.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
            using (var status = await stranger.Client.GetAsync(ApiRoutes.Media.UploadPath(mediaId), Ct))
                await status.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
            using (var complete = await stranger.Client.PostCompleteAsync(mediaId))
                await complete.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
            await stranger.ShouldNotSeeAsync(mediaId);
        }

        var state = await owner.GetUploadAsync(session.MediaId);
        state.Status.ShouldBe(MediaStatus.Uploading, "the stranger's complete must not have touched it");
        state.ReceivedChunks.ShouldBe([0]);
    }

    [Fact]
    public async Task Upload_status_lists_received_chunks_for_resuming()
    {
        var user = await factory.RegisterUserAsync();
        var chunkSize = factory.Storage().ChunkSize;
        var content = RandomBytes(2 * chunkSize + 5, seed: 8);
        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", content.Length));

        await user.PutChunkOkAsync(session.MediaId, 2, Slice(content, chunkSize, 2));
        await user.PutChunkOkAsync(session.MediaId, 0, Slice(content, chunkSize, 0));

        var resumed = await user.GetUploadAsync(session.MediaId);
        resumed.MediaId.ShouldBe(session.MediaId);
        resumed.Status.ShouldBe(MediaStatus.Uploading);
        resumed.ChunkSize.ShouldBe(chunkSize);
        resumed.ChunkCount.ShouldBe(3);
        resumed.ReceivedChunks.ShouldBe([0, 2]);

        foreach (var missing in Enumerable.Range(0, resumed.ChunkCount).Except(resumed.ReceivedChunks))
            await user.PutChunkOkAsync(session.MediaId, missing, Slice(content, chunkSize, missing));
        await user.CompleteAsync(session.MediaId);
        await user.WaitProcessedAsync(session.MediaId);

        var done = await user.GetUploadAsync(session.MediaId);
        done.Status.ShouldBe(MediaStatus.Ready);
        done.ChunkCount.ShouldBe(3);
        done.ReceivedChunks.ShouldBe([0, 1, 2]);
        (await user.Client.DownloadAsync(session.MediaId)).ShouldBeSameBytes(content, "resumed upload");
    }

    [Fact]
    public async Task Completing_with_missing_chunks_is_a_conflict_and_the_upload_can_continue()
    {
        var user = await factory.RegisterUserAsync();
        var chunkSize = factory.Storage().ChunkSize;
        var content = RandomBytes(2 * chunkSize + 5, seed: 9);
        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", content.Length));

        using (var none = await user.Client.PostCompleteAsync(session.MediaId))
            await none.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.UploadIncomplete);
        await user.PutChunkOkAsync(session.MediaId, 0, Slice(content, chunkSize, 0));
        await user.PutChunkOkAsync(session.MediaId, 2, Slice(content, chunkSize, 2));
        using (var partial = await user.Client.PostCompleteAsync(session.MediaId))
            await partial.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.UploadIncomplete);

        var state = await user.GetUploadAsync(session.MediaId);
        state.Status.ShouldBe(MediaStatus.Uploading);
        state.ReceivedChunks.ShouldBe([0, 2]);
        (await user.GetMediaAsync(session.MediaId)).Status.ShouldBe(MediaStatus.Uploading);

        await user.PutChunkOkAsync(session.MediaId, 1, Slice(content, chunkSize, 1));
        var completed = await user.CompleteAsync(session.MediaId);
        completed.Id.ShouldBe(session.MediaId);
        completed.Status.ShouldBeOneOf(MediaStatus.Processing, MediaStatus.Ready);
        (await user.WaitProcessedAsync(session.MediaId)).Status.ShouldBe(MediaStatus.Ready);
    }

    [Fact]
    public async Task Completing_again_returns_the_current_state()
    {
        var user = await factory.RegisterUserAsync();
        var content = RandomBytes(300, seed: 10);
        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", content.Length));
        await user.PutChunkOkAsync(session.MediaId, 0, content);

        var first = await user.CompleteAsync(session.MediaId);
        var second = await user.CompleteAsync(session.MediaId);
        var ready = await user.WaitProcessedAsync(session.MediaId);
        var third = await user.CompleteAsync(session.MediaId);

        first.Status.ShouldBeOneOf(MediaStatus.Processing, MediaStatus.Ready);
        second.Id.ShouldBe(session.MediaId);
        second.Status.ShouldBeOneOf(MediaStatus.Processing, MediaStatus.Ready);
        ready.Status.ShouldBe(MediaStatus.Ready);
        third.Id.ShouldBe(session.MediaId);
        third.Status.ShouldBe(MediaStatus.Ready);
        third.SizeBytes.ShouldBe(ready.SizeBytes);
        third.CreatedAt.ShouldBe(ready.CreatedAt);
        (await user.Client.DownloadAsync(session.MediaId)).ShouldBeSameBytes(content, "file completed three times");
    }

    [Fact]
    public async Task Chunks_cannot_be_written_once_the_upload_is_complete()
    {
        var user = await factory.RegisterUserAsync();
        var content = RandomBytes(300, seed: 11);
        var session = await user.StartUploadAsync(UploadRequest(MediaKind.File, "a.bin", "application/octet-stream", content.Length));
        await user.PutChunkOkAsync(session.MediaId, 0, content);
        await user.CompleteAsync(session.MediaId);

        using (var late = await user.Client.PutChunkAsync(session.MediaId, 0, RandomBytes(300, seed: 12)))
            await late.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ChunkInvalid);
        await user.WaitProcessedAsync(session.MediaId);
        using (var afterReady = await user.Client.PutChunkAsync(session.MediaId, 0, RandomBytes(300, seed: 13)))
            await afterReady.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ChunkInvalid);

        (await user.Client.DownloadAsync(session.MediaId)).ShouldBeSameBytes(content, "content after late chunk writes");
    }

    /// <summary>A body whose length is not known up front, so it is sent with chunked transfer encoding.</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            // Several writes, like a client streaming from disk.
            for (var offset = 0; offset < _bytes.Length; offset += 1000)
                await stream.WriteAsync(_bytes.AsMemory(offset, Math.Min(1000, _bytes.Length - offset)));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
