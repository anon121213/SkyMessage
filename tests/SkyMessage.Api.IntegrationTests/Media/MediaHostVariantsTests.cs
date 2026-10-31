using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.MediaFiles.Storage;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>
/// Behaviour that depends on how a server process is started: a restart after a crash, a server without FFmpeg and
/// a server without a master key. Each test starts a second host of the same deployment (same database, same JWT key).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MediaHostVariantsTests(ApiFactory factory)
{
    [Fact]
    public async Task Uploads_left_in_processing_by_a_crash_are_processed_after_a_restart()
    {
        var owner = await factory.RegisterUserAsync();
        var photo = TestImages.Png(200, 100);
        var session = await owner.StartUploadAsync(UploadRequest(MediaKind.Photo, "crash.png", "image/png", photo.Length));
        await owner.PutAllChunksAsync(session, photo);
        // "complete" committed, but the process died before its in-memory queue handed the job to a worker.
        await factory.QueryDbAsync(db => db.Media.Where(m => m.Id == session.MediaId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, MediaStatus.Processing)
            .SetProperty(m => m.CompletedAt, DateTimeOffset.UtcNow), Ct));
        (await owner.GetMediaAsync(session.MediaId)).Status.ShouldBe(MediaStatus.Processing);

        await using var restarted = factory.WithWebHostBuilder(_ => { });
        _ = restarted.Server; // starting the host re-queues every Processing row

        var media = await owner.WaitProcessedAsync(session.MediaId);
        media.Status.ShouldBe(MediaStatus.Ready);
        (media.Width, media.Height).ShouldBe((200, 100));
        media.HasThumbnail.ShouldBeTrue();
        (await owner.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(photo, "photo processed after the restart");
    }

    [Fact]
    public async Task Without_ffmpeg_audio_and_video_are_stored_as_uploaded_with_the_client_hints()
    {
        await using var noTools = factory.WithWebHostBuilder(b => b
            .UseSetting("MediaProcessing:FfmpegPath", "/nonexistent/skymessage/ffmpeg")
            .UseSetting("MediaProcessing:FfprobePath", "/nonexistent/skymessage/ffprobe"));
        var owner = (await factory.RegisterUserAsync()).On(noTools);
        var waveform = Enumerable.Range(0, Limits.WaveformSamples).Select(i => (byte)(i * 2)).ToArray();
        var video = RandomBytes(30_000, seed: 91);
        var voice = RandomBytes(8_000, seed: 92);

        var storedVideo = await owner.UploadReadyAsync(MediaKind.Video, video, "video/webm", "clip.webm",
            r => r with { Width = 1280, Height = 720, DurationMs = 4200 });
        var storedVoice = await owner.UploadReadyAsync(MediaKind.Voice, voice, "audio/ogg", "voice.ogg",
            r => r with { DurationMs = 3100, Waveform = waveform });
        var photo = await owner.UploadReadyAsync(MediaKind.Photo, TestImages.Png(300, 200), "image/png", "still.png");

        storedVideo.ContentType.ShouldBe("video/webm");
        storedVideo.FileName.ShouldBe("clip.webm");
        (storedVideo.Width, storedVideo.Height, storedVideo.DurationMs).ShouldBe((1280, 720, 4200));
        storedVideo.HasThumbnail.ShouldBeFalse();
        (await owner.Client.DownloadAsync(storedVideo.Id)).ShouldBeSameBytes(video, "video stored as uploaded");

        storedVoice.ContentType.ShouldBe("audio/ogg");
        storedVoice.DurationMs.ShouldBe(3100);
        storedVoice.Waveform.ShouldBe(waveform);
        (await owner.Client.DownloadAsync(storedVoice.Id)).ShouldBeSameBytes(voice, "voice stored as uploaded");

        // Photos do not need FFmpeg.
        photo.HasThumbnail.ShouldBeTrue();
        (photo.Width, photo.Height).ShouldBe((300, 200));
    }

    [Fact]
    public async Task Without_a_master_key_media_is_stored_in_plain_text_and_encrypted_media_fails_closed()
    {
        await using var plainHost = factory.WithWebHostBuilder(b => b.UseSetting("Storage:MasterKey", ""));
        plainHost.Services.GetRequiredService<MediaEncryption>().IsEnabled.ShouldBeFalse();
        var owner = await factory.RegisterUserAsync();
        var onPlainHost = owner.On(plainHost);
        var content = RandomBytes(3000, seed: 93);

        var plain = await onPlainHost.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "plain.bin");

        var (wrappedKey, stored) = await factory.QueryDbAsync(async db => (
            await db.Media.Where(m => m.Id == plain.Id).Select(m => m.WrappedKey).SingleAsync(Ct),
            await db.MediaChunks.Where(c => c.MediaId == plain.Id).Select(c => c.Data).SingleAsync(Ct)));
        wrappedKey.ShouldBeNull();
        stored.ShouldBeSameBytes(content, "chunk stored without encryption");
        // Encryption is decided per media when the upload starts, so a server with a key still serves it.
        (await owner.Client.DownloadAsync(plain.Id)).ShouldBeSameBytes(content, "plain media read by an encrypting server");

        // Encrypted media cannot be served without the key: a clean error, never ciphertext.
        var encrypted = await owner.UploadReadyAsync(MediaKind.File, content, "application/octet-stream", "secret.bin");
        using var response = await onPlainHost.Client.GetContentAsync(encrypted.Id);
        await response.ShouldBeProblemAsync(HttpStatusCode.InternalServerError, ErrorCodes.Internal);
    }
}
