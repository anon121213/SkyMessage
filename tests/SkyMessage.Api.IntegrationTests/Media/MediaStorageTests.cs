using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.MediaFiles;
using SkyMessage.Infrastructure.MediaFiles.Storage;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>Bytes in PostgreSQL: AES-256-GCM at rest (SPEC §1.6, §6.4) and the cleanup of stale media.</summary>
[Collection(ApiCollection.Name)]
public sealed class MediaStorageTests(ApiFactory factory)
{
    private const int GcmOverhead = 12 + 16;

    [Fact]
    public async Task Chunks_are_encrypted_at_rest_and_bound_to_their_media_and_index()
    {
        var encryption = factory.Services.GetRequiredService<MediaEncryption>();
        encryption.IsEnabled.ShouldBeTrue("the test host runs with the development Storage:MasterKey");
        var owner = await factory.RegisterUserAsync();
        var chunk = factory.Storage().ChunkSize;
        var marker = "SkyMessage plaintext marker | Привет из Бангкока | "u8.ToArray();
        var plaintext = Enumerable.Range(0, chunk * 3 / 2 / marker.Length + 1).SelectMany(_ => marker).Take(chunk * 3 / 2).ToArray();
        var media = await owner.UploadReadyAsync(MediaKind.File, plaintext, "text/plain", "notes.txt");

        var (wrappedKey, rows) = await factory.QueryDbAsync(async db =>
        {
            var key = await db.Media.Where(m => m.Id == media.Id).Select(m => m.WrappedKey).SingleAsync(Ct);
            var chunks = await db.MediaChunks.AsNoTracking().Where(c => c.MediaId == media.Id).OrderBy(c => c.Index).ToListAsync(Ct);
            return (key, chunks);
        });

        wrappedKey.ShouldNotBeNull().Length.ShouldBe(12 + 32 + 16);
        rows.Select(r => r.Index).ShouldBe([0, 1]);
        rows.Select(r => r.Size).ShouldBe([chunk, plaintext.Length - chunk]);
        var spec = new SpecDecryptor(MasterKey(), media.Id, wrappedKey!);
        foreach (var row in rows)
        {
            var expected = Slice(plaintext, chunk, row.Index);
            row.Data.Length.ShouldBe(row.Size + GcmOverhead, "nonce | ciphertext | tag");
            row.Data.AsSpan().IndexOf(marker.AsSpan(0, 16)).ShouldBe(-1, $"chunk {row.Index} must not contain plaintext");
            row.Data.AsSpan(12, 64).SequenceEqual(expected.AsSpan(0, 64)).ShouldBeFalse();
            spec.Decrypt(row.Index, row.Data).ShouldBeSameBytes(expected, $"chunk {row.Index} decrypted as SPEC §6.4 describes");
        }
        // AAD = media id ‖ index: chunks can be neither reordered nor moved to another media.
        Should.Throw<Exception>(() => spec.Decrypt(1, rows[0].Data)).ShouldBeAssignableTo<CryptographicException>();
        var otherMedia = await owner.UploadReadyAsync(MediaKind.File, plaintext[..100], "text/plain", "other.txt");
        var otherKey = await factory.QueryDbAsync(db => db.Media.Where(m => m.Id == otherMedia.Id).Select(m => m.WrappedKey).SingleAsync(Ct));
        Should.Throw<Exception>(() => new SpecDecryptor(MasterKey(), media.Id, otherKey!)).ShouldBeAssignableTo<CryptographicException>();
        Should.Throw<Exception>(() => factory.Services.GetRequiredService<MediaEncryption>().CreateCipher(media.Id, otherKey))
            .ShouldBeAssignableTo<CryptographicException>();

        (await owner.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(plaintext, "served plaintext");
    }

    [Fact]
    public async Task Chunk_bytes_are_stored_out_of_line_without_compression()
    {
        // Media is already compressed (or encrypted): TOAST compression would only burn CPU.
        var storage = await factory.QueryDbAsync(db => db.Database.SqlQueryRaw<string>(
                "SELECT attstorage::text AS \"Value\" FROM pg_attribute WHERE attrelid = 'media_chunks'::regclass AND attname = 'data'")
            .SingleAsync(Ct));

        storage.ShouldBe("e");
    }

    [Fact]
    public async Task Identical_uploads_get_different_keys_and_ciphertexts()
    {
        var owner = await factory.RegisterUserAsync();
        var bytes = Encoding.UTF8.GetBytes(new string('z', 5000));
        var first = await owner.UploadReadyAsync(MediaKind.File, bytes, "text/plain", "a.txt");
        var second = await owner.UploadReadyAsync(MediaKind.File, bytes, "text/plain", "b.txt");

        var stored = await factory.QueryDbAsync(async db => await db.MediaChunks.AsNoTracking()
            .Where(c => c.MediaId == first.Id || c.MediaId == second.Id)
            .Select(c => new { c.MediaId, c.Data })
            .ToListAsync(Ct));
        var keys = await factory.QueryDbAsync(async db => await db.Media
            .Where(m => m.Id == first.Id || m.Id == second.Id)
            .Select(m => m.WrappedKey!)
            .ToListAsync(Ct));

        stored.Count.ShouldBe(2);
        Convert.ToBase64String(stored[0].Data).ShouldNotBe(Convert.ToBase64String(stored[1].Data));
        keys.Select(Convert.ToBase64String).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Thumbnails_are_encrypted_at_rest_too()
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadReadyAsync(MediaKind.Photo, TestImages.Png(640, 480), "image/png", "photo.png");
        var served = await owner.Client.DownloadThumbnailAsync(media.Id);

        var (stored, wrappedKey) = await factory.QueryDbAsync(async db => (
            await db.MediaThumbnails.AsNoTracking().Where(t => t.MediaId == media.Id).Select(t => t.Data).SingleAsync(Ct),
            await db.Media.Where(m => m.Id == media.Id).Select(m => m.WrappedKey).SingleAsync(Ct)));

        stored.Length.ShouldBe(served.Length + GcmOverhead);
        stored.AsSpan().IndexOf(served.AsSpan(0, 64)).ShouldBe(-1, "the stored preview is not a plain JPEG");
        new SpecDecryptor(MasterKey(), media.Id, wrappedKey.ShouldNotBeNull()).Decrypt(MediaChunkCipher.ThumbnailIndex, stored)
            .ShouldBeSameBytes(served, "preview decrypted with the chunk format and index -1");
    }

    [Fact]
    public async Task Processed_replacement_content_is_stored_encrypted()
    {
        var owner = await factory.RegisterUserAsync();
        var max = factory.Processing().PhotoMaxDimension;
        var media = await owner.UploadReadyAsync(MediaKind.Photo, TestImages.Png(max + 200, 40), "image/png", "wide.png");
        var served = await owner.Client.DownloadAsync(media.Id);

        var (rows, wrappedKey) = await factory.QueryDbAsync(async db => (
            await db.MediaChunks.AsNoTracking().Where(c => c.MediaId == media.Id).OrderBy(c => c.Index).ToListAsync(Ct),
            await db.Media.Where(m => m.Id == media.Id).Select(m => m.WrappedKey).SingleAsync(Ct)));

        rows.Sum(r => (long)r.Size).ShouldBe(served.Length);
        var spec = new SpecDecryptor(MasterKey(), media.Id, wrappedKey.ShouldNotBeNull());
        rows.SelectMany(r => spec.Decrypt(r.Index, r.Data)).ToArray().ShouldBeSameBytes(served, "re-encoded JPEG");
        rows[0].Data.AsSpan(0, 2).SequenceEqual(new byte[] { 0xFF, 0xD8 }).ShouldBeFalse("no plain JPEG header at rest");
    }

    [Fact]
    public async Task Stale_uploads_are_deleted_with_their_chunks_and_fresh_ones_are_kept()
    {
        var owner = await factory.RegisterUserAsync();
        var ttl = TimeSpan.FromHours(factory.Storage().IncompleteUploadTtlHours);
        var stale = await owner.StartUploadAsync(UploadRequest(MediaKind.File, "abandoned.bin", "application/octet-stream", 2000));
        await owner.PutChunkOkAsync(stale.MediaId, 0, RandomBytes(2000, seed: 81));
        var fresh = await owner.StartUploadAsync(UploadRequest(MediaKind.File, "in-progress.bin", "application/octet-stream", 2000));
        await owner.PutChunkOkAsync(fresh.MediaId, 0, RandomBytes(2000, seed: 82));
        var almostStale = await owner.StartUploadAsync(UploadRequest(MediaKind.File, "slow.bin", "application/octet-stream", 2000));
        await BackdateAsync(stale.MediaId, created: ttl + TimeSpan.FromMinutes(5));
        await BackdateAsync(almostStale.MediaId, created: ttl - TimeSpan.FromMinutes(5));

        await RunCleanupAsync();

        (await ExistsAsync(stale.MediaId)).ShouldBe((false, 0));
        (await ExistsAsync(fresh.MediaId)).ShouldBe((true, 1));
        (await ExistsAsync(almostStale.MediaId)).ShouldBe((true, 0));
        using (var status = await owner.Client.GetAsync(ApiRoutes.Media.UploadPath(stale.MediaId), Ct))
            await status.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var late = await owner.Client.PutChunkAsync(stale.MediaId, 0, RandomBytes(2000, seed: 83)))
            await late.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // The fresh upload is unaffected and can still be finished.
        await owner.CompleteAsync(fresh.MediaId);
        (await owner.WaitProcessedAsync(fresh.MediaId)).Status.ShouldBe(MediaStatus.Ready);
    }

    [Fact]
    public async Task Failed_media_is_deleted_once_it_is_older_than_the_ttl()
    {
        var owner = await factory.RegisterUserAsync();
        var ttl = TimeSpan.FromHours(factory.Storage().IncompleteUploadTtlHours);
        var oldFailure = await owner.UploadProcessedAsync(MediaKind.Photo, RandomBytes(500, seed: 84), "image/jpeg", "old.jpg");
        var newFailure = await owner.UploadProcessedAsync(MediaKind.Photo, RandomBytes(500, seed: 85), "image/jpeg", "new.jpg");
        oldFailure.Status.ShouldBe(MediaStatus.Failed);
        newFailure.Status.ShouldBe(MediaStatus.Failed);
        await BackdateAsync(oldFailure.Id, created: ttl * 2, updated: ttl + TimeSpan.FromMinutes(5));

        await RunCleanupAsync();

        (await ExistsAsync(oldFailure.Id)).Exists.ShouldBeFalse();
        (await ExistsAsync(newFailure.Id)).Exists.ShouldBeTrue();
    }

    [Fact]
    public async Task Ready_media_nobody_references_is_deleted_after_a_week_and_referenced_media_is_kept()
    {
        var owner = await factory.RegisterUserAsync();
        var friend = await factory.RegisterUserAsync();
        var week = TimeSpan.FromDays(7) + TimeSpan.FromHours(1);
        var orphan = await owner.UploadPhotoAsync("orphan.png");
        var recentOrphan = await owner.UploadPhotoAsync("recent.png");
        var attached = await owner.UploadPhotoAsync("attached.png");
        var avatar = await owner.UploadPhotoAsync("avatar.png");
        var groupAvatar = await owner.UploadPhotoAsync("group.png");
        var chatId = await owner.OpenPrivateChatAsync(friend.Id);
        await owner.SendMediaAsync(chatId, attached.Id);
        await owner.SetAvatarAsync(avatar.Id);
        await owner.CreateGroupAsync([friend], groupAvatar.Id);
        foreach (var id in new[] { orphan.Id, attached.Id, avatar.Id, groupAvatar.Id })
            await BackdateAsync(id, created: week, updated: week);
        await BackdateAsync(recentOrphan.Id, created: TimeSpan.FromDays(6), updated: TimeSpan.FromDays(6));

        await RunCleanupAsync();

        (await ExistsAsync(orphan.Id)).ShouldBe((false, 0));
        (await ExistsAsync(recentOrphan.Id)).Exists.ShouldBeTrue();
        foreach (var kept in new[] { attached, avatar, groupAvatar })
        {
            (await ExistsAsync(kept.Id)).Exists.ShouldBeTrue(kept.FileName);
            await friend.ShouldSeeAsync(kept);
        }
        (await factory.QueryDbAsync(db => db.MediaThumbnails.AnyAsync(t => t.MediaId == orphan.Id, Ct))).ShouldBeFalse("previews go with the media");
    }

    private byte[] MasterKey() => Convert.FromBase64String(factory.Storage().MasterKey.ShouldNotBeNull());

    /// <summary>Decrypts exactly as SPEC §6.4 describes, independently of the server's implementation.</summary>
    private sealed class SpecDecryptor
    {
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private readonly byte[] _dataKey = new byte[32];
        private readonly byte[] _mediaId = new byte[16];

        /// <summary>Data key wrapped with the master key: nonce | ciphertext(32) | tag, AAD = media id (16 bytes, big-endian Guid).</summary>
        public SpecDecryptor(byte[] masterKey, Guid mediaId, byte[] wrappedKey)
        {
            mediaId.TryWriteBytes(_mediaId, bigEndian: true, out _).ShouldBeTrue();
            using var aes = new AesGcm(masterKey, TagSize);
            aes.Decrypt(wrappedKey.AsSpan(0, NonceSize), wrappedKey.AsSpan(NonceSize, 32), wrappedKey.AsSpan(NonceSize + 32, TagSize), _dataKey, _mediaId);
        }

        /// <summary>Chunk = nonce(12) | ciphertext | tag(16), AAD = media id ‖ chunk index (int32 big-endian).</summary>
        public byte[] Decrypt(int index, byte[] stored)
        {
            var plaintext = new byte[stored.Length - NonceSize - TagSize];
            var aad = new byte[20];
            _mediaId.CopyTo(aad, 0);
            BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(16), index);
            using var aes = new AesGcm(_dataKey, TagSize);
            aes.Decrypt(stored.AsSpan(0, NonceSize), stored.AsSpan(NonceSize, plaintext.Length), stored.AsSpan(NonceSize + plaintext.Length), plaintext, aad);
            return plaintext;
        }
    }

    private Task<int> BackdateAsync(Guid mediaId, TimeSpan created, TimeSpan? updated = null)
    {
        var now = DateTimeOffset.UtcNow;
        var createdAt = now - created;
        var updatedAt = now - (updated ?? TimeSpan.Zero);
        return factory.QueryDbAsync(db => db.Media.Where(m => m.Id == mediaId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.CreatedAt, createdAt)
            .SetProperty(m => m.UpdatedAt, updated is null ? now : updatedAt), Ct));
    }

    private Task<(bool Exists, int Chunks)> ExistsAsync(Guid mediaId) =>
        factory.QueryDbAsync(async db => (
            await db.Media.AnyAsync(m => m.Id == mediaId, Ct),
            await db.MediaChunks.CountAsync(c => c.MediaId == mediaId, Ct)));

    /// <summary>One pass of the hourly cleanup, run directly on the registered hosted service.</summary>
    private async Task RunCleanupAsync()
    {
        var cleanup = factory.Services.GetServices<IHostedService>().OfType<MediaCleanupService>().Single();
        await cleanup.RunOnceAsync(Ct);
    }
}
