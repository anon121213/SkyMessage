using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SkyMessage.Infrastructure.MediaFiles.Storage;

/// <summary>
/// Per-media data keys (32 random bytes) wrapped with the server master key using AES-256-GCM:
/// <c>nonce(12) | ciphertext(32) | tag(16)</c>, AAD = media id (16 bytes, big-endian Guid). The AAD binds a wrapped
/// key to its media row, so copying <c>wrapped_key</c> between rows makes decryption fail.
/// </summary>
internal static class MediaKeyWrap
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int WrappedSize = NonceSize + KeySize + TagSize;

    public static byte[] GenerateDataKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] Wrap(ReadOnlySpan<byte> masterKey, Guid mediaId, ReadOnlySpan<byte> dataKey)
    {
        if (masterKey.Length != KeySize) throw new ArgumentException("The master key must be 32 bytes.", nameof(masterKey));
        if (dataKey.Length != KeySize) throw new ArgumentException("The data key must be 32 bytes.", nameof(dataKey));

        var wrapped = new byte[WrappedSize];
        var nonce = wrapped.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        Span<byte> aad = stackalloc byte[16];
        WriteMediaId(aad, mediaId);
        using var aes = new AesGcm(masterKey, TagSize);
        aes.Encrypt(nonce, dataKey, wrapped.AsSpan(NonceSize, KeySize), wrapped.AsSpan(NonceSize + KeySize, TagSize), aad);
        return wrapped;
    }

    /// <exception cref="CryptographicException">Wrong master key, wrong media id or tampered bytes.</exception>
    public static byte[] Unwrap(ReadOnlySpan<byte> masterKey, Guid mediaId, ReadOnlySpan<byte> wrapped)
    {
        if (masterKey.Length != KeySize) throw new ArgumentException("The master key must be 32 bytes.", nameof(masterKey));
        if (wrapped.Length != WrappedSize) throw new CryptographicException("The wrapped media key has an invalid length.");

        var dataKey = new byte[KeySize];
        Span<byte> aad = stackalloc byte[16];
        WriteMediaId(aad, mediaId);
        using var aes = new AesGcm(masterKey, TagSize);
        aes.Decrypt(wrapped[..NonceSize], wrapped.Slice(NonceSize, KeySize), wrapped.Slice(NonceSize + KeySize, TagSize), dataKey, aad);
        return dataKey;
    }

    internal static void WriteMediaId(Span<byte> destination, Guid mediaId)
    {
        if (!mediaId.TryWriteBytes(destination, bigEndian: true, out _))
            throw new ArgumentException("Destination is too small for a Guid.", nameof(destination));
    }
}

/// <summary>
/// Chunk encryption with one media's data key (AES-256-GCM): stored chunk = <c>nonce(12) | ciphertext | tag(16)</c>,
/// AAD = media id (16 bytes, big-endian Guid) ‖ chunk index (int32 big-endian), so chunks can neither be reordered
/// nor moved to another media. Preview thumbnails use the same format with <see cref="ThumbnailIndex"/>.
/// Not thread-safe (one instance per stream/operation).
/// </summary>
internal sealed class MediaChunkCipher : IDisposable
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Overhead = NonceSize + TagSize;
    public const int ThumbnailIndex = -1;

    private readonly AesGcm _aes;
    private readonly Guid _mediaId;

    public MediaChunkCipher(ReadOnlySpan<byte> dataKey, Guid mediaId)
    {
        if (dataKey.Length != MediaKeyWrap.KeySize) throw new ArgumentException("The data key must be 32 bytes.", nameof(dataKey));
        _aes = new AesGcm(dataKey, TagSize);
        _mediaId = mediaId;
    }

    public Guid MediaId => _mediaId;

    public static int EncryptedLength(int plaintextLength) => checked(plaintextLength + Overhead);

    public void Encrypt(int index, ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        if (destination.Length != EncryptedLength(plaintext.Length))
            throw new ArgumentException("Destination must be exactly plaintext length + 28 bytes.", nameof(destination));

        var nonce = destination[..NonceSize];
        RandomNumberGenerator.Fill(nonce);
        Span<byte> aad = stackalloc byte[20];
        WriteAad(aad, index);
        _aes.Encrypt(nonce, plaintext, destination.Slice(NonceSize, plaintext.Length), destination[^TagSize..], aad);
    }

    public byte[] Encrypt(int index, ReadOnlySpan<byte> plaintext)
    {
        var payload = new byte[EncryptedLength(plaintext.Length)];
        Encrypt(index, plaintext, payload);
        return payload;
    }

    /// <returns>The plaintext length (payload length − 28).</returns>
    /// <exception cref="CryptographicException">Tampered, truncated, or encrypted for another index/media/key.</exception>
    public int Decrypt(int index, ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        if (payload.Length < Overhead) throw new CryptographicException("The encrypted chunk is truncated.");
        var length = payload.Length - Overhead;
        if (destination.Length < length) throw new ArgumentException("Destination is too small for the chunk.", nameof(destination));

        Span<byte> aad = stackalloc byte[20];
        WriteAad(aad, index);
        _aes.Decrypt(payload[..NonceSize], payload.Slice(NonceSize, length), payload[^TagSize..], destination[..length], aad);
        return length;
    }

    public byte[] Decrypt(int index, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Overhead) throw new CryptographicException("The encrypted chunk is truncated.");
        var plaintext = new byte[payload.Length - Overhead];
        Decrypt(index, payload, plaintext);
        return plaintext;
    }

    public void Dispose() => _aes.Dispose();

    private void WriteAad(Span<byte> aad, int index)
    {
        MediaKeyWrap.WriteMediaId(aad[..16], _mediaId);
        BinaryPrimitives.WriteInt32BigEndian(aad[16..], index);
    }
}
