using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary><c>MediaProcessed</c> goes to the uploader's devices only (SPEC §7), for Ready and Failed alike.</summary>
[Collection(ApiCollection.Name)]
public sealed class MediaProcessedEventTests(ApiFactory factory)
{
    [Fact]
    public async Task Every_device_of_the_uploader_is_told_when_processing_finished()
    {
        var owner = await factory.RegisterUserAsync();
        var ownerPhone = await factory.SignInAgainAsync(owner);
        var outsider = await factory.RegisterUserAsync();
        await using var laptop = await RealtimeProbe.ConnectAsync(factory, owner);
        await using var phone = await RealtimeProbe.ConnectAsync(factory, ownerPhone);
        await using var other = await RealtimeProbe.ConnectAsync(factory, outsider);

        var completed = await owner.UploadAsync(MediaKind.Photo, TestImages.Png(320, 200), "image/png", "event.png");

        var onLaptop = await laptop.MediaProcessed.WaitAsync(m => m.Id == completed.Id);
        var onPhone = await phone.MediaProcessed.WaitAsync(m => m.Id == completed.Id);
        foreach (var e in new[] { onLaptop, onPhone })
        {
            e.Status.ShouldBe(MediaStatus.Ready);
            e.Kind.ShouldBe(MediaKind.Photo);
            e.ContentType.ShouldBe("image/png");
            (e.Width, e.Height).ShouldBe((320, 200));
            e.HasThumbnail.ShouldBeTrue();
            e.MiniThumbnail.ShouldNotBeNull().ShouldNotBeEmpty();
        }
        // The event carries the same state the REST API reports.
        var current = await owner.GetMediaAsync(completed.Id);
        (current.Status, current.SizeBytes, current.FileName).ShouldBe((onLaptop.Status, onLaptop.SizeBytes, onLaptop.FileName));

        // Ordering sentinel: the outsider's own event is sent after the owner's one was, so a misrouted copy of
        // the owner's event would already have arrived on the outsider's connection.
        var own = await outsider.UploadAsync(MediaKind.File, RandomBytes(100, seed: 51), "application/octet-stream", "own.bin");
        await other.MediaProcessed.WaitAsync(m => m.Id == own.Id);
        other.MediaProcessed.Items.Select(m => m.Id).ShouldBe([own.Id]);
        laptop.MediaProcessed.Items.ShouldNotContain(m => m.Id == own.Id);
    }

    [Fact]
    public async Task Parallel_chunks_and_repeated_completes_process_the_upload_exactly_once()
    {
        var owner = await factory.RegisterUserAsync();
        await using var probe = await RealtimeProbe.ConnectAsync(factory, owner);
        var chunkSize = factory.Storage().ChunkSize;
        var content = RandomBytes(4 * chunkSize + 321, seed: 53);
        var session = await owner.StartUploadAsync(UploadRequest(MediaKind.File, "parallel.bin", "application/octet-stream", content.Length));

        // Clients upload several chunks at once, and retry "complete" when a response is lost.
        await Task.WhenAll(Enumerable.Range(0, session.ChunkCount)
            .Select(i => owner.PutChunkOkAsync(session.MediaId, i, Slice(content, chunkSize, i))));
        var completes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => owner.CompleteAsync(session.MediaId)));

        completes.ShouldAllBe(m => m.Id == session.MediaId && (m.Status == MediaStatus.Processing || m.Status == MediaStatus.Ready));
        var processed = await probe.MediaProcessed.WaitAsync(m => m.Id == session.MediaId);
        processed.Status.ShouldBe(MediaStatus.Ready);
        (await owner.Client.DownloadAsync(session.MediaId)).ShouldBeSameBytes(content, "file uploaded in parallel");

        // Sentinel: a later upload's event arrives after any duplicate of the first one would have.
        var sentinel = await owner.UploadAsync(MediaKind.File, RandomBytes(10, seed: 54), "application/octet-stream", "sentinel.bin");
        await probe.MediaProcessed.WaitAsync(m => m.Id == sentinel.Id);
        probe.MediaProcessed.Items.Count(m => m.Id == session.MediaId).ShouldBe(1);
        (await owner.CompleteAsync(session.MediaId)).Status.ShouldBe(MediaStatus.Ready);
    }

    [Fact]
    public async Task Failed_processing_is_reported_as_well()
    {
        var owner = await factory.RegisterUserAsync();
        await using var probe = await RealtimeProbe.ConnectAsync(factory, owner);

        var completed = await owner.UploadAsync(MediaKind.Photo, RandomBytes(2000, seed: 52), "image/jpeg", "corrupt.jpg");

        var e = await probe.MediaProcessed.WaitAsync(m => m.Id == completed.Id);
        e.Status.ShouldBe(MediaStatus.Failed);
        e.HasThumbnail.ShouldBeFalse();
        (await owner.GetMediaAsync(completed.Id)).Status.ShouldBe(MediaStatus.Failed);
    }

    [Fact]
    public async Task Event_reports_the_processed_metadata_of_a_voice_message()
    {
        Ffmpeg.Require();
        var owner = await factory.RegisterUserAsync();
        await using var probe = await RealtimeProbe.ConnectAsync(factory, owner);

        var completed = await owner.UploadAsync(MediaKind.Voice, TestAudio.Wav(1.0), "audio/wav", "hello.wav");

        var e = await probe.MediaProcessed.WaitAsync(m => m.Id == completed.Id);
        e.Status.ShouldBe(MediaStatus.Ready);
        e.ContentType.ShouldBe("audio/mp4");
        e.FileName.ShouldBe("hello.m4a");
        e.DurationMs.ShouldNotBeNull().ShouldBeInRange(900, 1200);
        e.Waveform.ShouldNotBeNull().Length.ShouldBe(Limits.WaveformSamples);
        e.SizeBytes.ShouldBe((await owner.Client.DownloadAsync(completed.Id)).Length);
    }
}
