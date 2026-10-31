using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Shouldly;
using SkyMessage.Application.Common.Options;
using SkyMessage.Infrastructure.MediaFiles.Storage;
using Xunit;

namespace SkyMessage.UnitTests.Media;

public sealed class MediaCryptoTests
{
    private static readonly byte[] MasterKey = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Wrapped_key_round_trips_and_has_the_documented_layout()
    {
        var mediaId = Guid.CreateVersion7();
        var dataKey = MediaKeyWrap.GenerateDataKey();

        var wrapped = MediaKeyWrap.Wrap(MasterKey, mediaId, dataKey);

        wrapped.Length.ShouldBe(12 + 32 + 16);
        MediaKeyWrap.Unwrap(MasterKey, mediaId, wrapped).ShouldBe(dataKey);
    }

    [Fact]
    public void Wrapped_key_uses_media_id_big_endian_as_aad()
    {
        var mediaId = Guid.CreateVersion7();
        var dataKey = MediaKeyWrap.GenerateDataKey();
        var wrapped = MediaKeyWrap.Wrap(MasterKey, mediaId, dataKey);

        // Independent decryption following the spec: nonce(12) | ciphertext(32) | tag(16), AAD = Guid big-endian.
        var aad = new byte[16];
        mediaId.TryWriteBytes(aad, bigEndian: true, out _).ShouldBeTrue();
        var plain = new byte[32];
        using var aes = new AesGcm(MasterKey, 16);
        aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(12, 32), wrapped.AsSpan(44, 16), plain, aad);
        plain.ShouldBe(dataKey);
    }

    [Fact]
    public void Wrapped_key_is_bound_to_media_and_master_key()
    {
        var mediaId = Guid.CreateVersion7();
        var wrapped = MediaKeyWrap.Wrap(MasterKey, mediaId, MediaKeyWrap.GenerateDataKey());

        Should.Throw<CryptographicException>(() => MediaKeyWrap.Unwrap(MasterKey, Guid.CreateVersion7(), wrapped));
        Should.Throw<CryptographicException>(() => MediaKeyWrap.Unwrap(RandomNumberGenerator.GetBytes(32), mediaId, wrapped));

        var tampered = (byte[])wrapped.Clone();
        tampered[20] ^= 0x01;
        Should.Throw<CryptographicException>(() => MediaKeyWrap.Unwrap(MasterKey, mediaId, tampered));
        Should.Throw<CryptographicException>(() => MediaKeyWrap.Unwrap(MasterKey, mediaId, wrapped.AsSpan(0, 59)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(1024 * 1024)]
    public void Chunk_round_trips(int length)
    {
        var mediaId = Guid.CreateVersion7();
        using var cipher = new MediaChunkCipher(MediaKeyWrap.GenerateDataKey(), mediaId);
        var plain = RandomNumberGenerator.GetBytes(length);

        var payload = cipher.Encrypt(7, plain);

        payload.Length.ShouldBe(length + 28);
        cipher.Decrypt(7, payload).ShouldBe(plain);
    }

    [Fact]
    public void Chunk_uses_media_id_and_big_endian_index_as_aad()
    {
        var mediaId = Guid.CreateVersion7();
        var dataKey = MediaKeyWrap.GenerateDataKey();
        using var cipher = new MediaChunkCipher(dataKey, mediaId);
        var plain = RandomNumberGenerator.GetBytes(333);
        var payload = cipher.Encrypt(0x01020304, plain);

        var aad = new byte[20];
        mediaId.TryWriteBytes(aad.AsSpan(0, 16), bigEndian: true, out _).ShouldBeTrue();
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(16), 0x01020304);
        aad[16..].ShouldBe(new byte[] { 1, 2, 3, 4 });

        var decrypted = new byte[plain.Length];
        using var aes = new AesGcm(dataKey, 16);
        aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(12, plain.Length), payload.AsSpan(12 + plain.Length, 16), decrypted, aad);
        decrypted.ShouldBe(plain);
    }

    [Fact]
    public void Tampered_chunk_is_rejected()
    {
        var mediaId = Guid.CreateVersion7();
        using var cipher = new MediaChunkCipher(MediaKeyWrap.GenerateDataKey(), mediaId);
        var payload = cipher.Encrypt(3, RandomNumberGenerator.GetBytes(100));

        foreach (var position in new[] { 0, 11, 12, 60, payload.Length - 16, payload.Length - 1 })
        {
            var tampered = (byte[])payload.Clone();
            tampered[position] ^= 0x80;
            Should.Throw<CryptographicException>(() => cipher.Decrypt(3, tampered), $"byte {position}");
        }

        Should.Throw<CryptographicException>(() => cipher.Decrypt(3, payload.AsSpan(0, payload.Length - 1).ToArray()));
        Should.Throw<CryptographicException>(() => cipher.Decrypt(3, new byte[27]));
    }

    [Fact]
    public void Swapped_chunks_are_rejected()
    {
        var mediaId = Guid.CreateVersion7();
        var dataKey = MediaKeyWrap.GenerateDataKey();
        using var cipher = new MediaChunkCipher(dataKey, mediaId);
        var chunk1 = cipher.Encrypt(1, RandomNumberGenerator.GetBytes(64));

        // Another position of the same media.
        Should.Throw<CryptographicException>(() => cipher.Decrypt(2, chunk1));
        // Same position of another media, even with the same data key.
        using var other = new MediaChunkCipher(dataKey, Guid.CreateVersion7());
        Should.Throw<CryptographicException>(() => other.Decrypt(1, chunk1));
        // The thumbnail slot is not chunk 0.
        var thumbnail = cipher.Encrypt(MediaChunkCipher.ThumbnailIndex, RandomNumberGenerator.GetBytes(64));
        Should.Throw<CryptographicException>(() => cipher.Decrypt(0, thumbnail));
        cipher.Decrypt(MediaChunkCipher.ThumbnailIndex, thumbnail).Length.ShouldBe(64);
    }

    [Fact]
    public void Every_encryption_uses_a_fresh_nonce()
    {
        using var cipher = new MediaChunkCipher(MediaKeyWrap.GenerateDataKey(), Guid.CreateVersion7());
        var plain = new byte[32];

        var first = cipher.Encrypt(0, plain);
        var second = cipher.Encrypt(0, plain);

        first.AsSpan(0, 12).SequenceEqual(second.AsSpan(0, 12)).ShouldBeFalse();
        first.SequenceEqual(second).ShouldBeFalse();
    }

    [Fact]
    public void Master_key_parsing()
    {
        MediaEncryption.ParseMasterKey(null).ShouldBeNull();
        MediaEncryption.ParseMasterKey("  ").ShouldBeNull();
        MediaEncryption.ParseMasterKey(Convert.ToBase64String(MasterKey)).ShouldBe(MasterKey);
        Should.Throw<FormatException>(() => MediaEncryption.ParseMasterKey("not base64!"));
        Should.Throw<FormatException>(() => MediaEncryption.ParseMasterKey(Convert.ToBase64String(new byte[16])));

        MediaEncryption.IsValidMasterKey("").ShouldBeTrue();
        MediaEncryption.IsValidMasterKey(Convert.ToBase64String(new byte[33])).ShouldBeFalse();
        // The committed development key is a valid 32-byte key.
        MediaEncryption.IsValidMasterKey(DevSecrets.StorageMasterKey).ShouldBeTrue();
    }

    [Fact]
    public void Encryption_is_off_without_a_master_key()
    {
        var encryption = new MediaEncryption(Options.Create(new StorageOptions { MasterKey = "" }));

        encryption.IsEnabled.ShouldBeFalse();
        encryption.CreateWrappedKey(Guid.CreateVersion7()).ShouldBeNull();
        encryption.CreateCipher(Guid.CreateVersion7(), null).ShouldBeNull();
        // Media encrypted earlier cannot be read once the key is removed.
        Should.Throw<InvalidOperationException>(() => encryption.CreateCipher(Guid.CreateVersion7(), new byte[60]));
    }

    [Fact]
    public void Encryption_service_creates_working_ciphers()
    {
        var encryption = new MediaEncryption(Options.Create(new StorageOptions { MasterKey = Convert.ToBase64String(MasterKey) }));
        var mediaId = Guid.CreateVersion7();

        var wrapped = encryption.CreateWrappedKey(mediaId);

        wrapped.ShouldNotBeNull();
        using var writer = encryption.CreateCipher(mediaId, wrapped)!;
        using var reader = encryption.CreateCipher(mediaId, wrapped)!;
        var plain = RandomNumberGenerator.GetBytes(500);
        reader.Decrypt(4, writer.Encrypt(4, plain)).ShouldBe(plain);
        Should.Throw<CryptographicException>(() => encryption.CreateCipher(Guid.CreateVersion7(), wrapped));
    }
}
