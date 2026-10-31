using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Options;

namespace SkyMessage.Infrastructure.MediaFiles.Storage;

/// <summary>
/// Holds the master key from <c>Storage:MasterKey</c> (base64, exactly 32 bytes; empty = no encryption at rest).
/// Whether a media is encrypted is decided once, at upload creation, by the presence of its wrapped key.
/// </summary>
internal sealed class MediaEncryption
{
    private readonly byte[]? _masterKey;

    public MediaEncryption(IOptions<StorageOptions> options) => _masterKey = ParseMasterKey(options.Value.MasterKey);

    public bool IsEnabled => _masterKey is not null;

    /// <exception cref="FormatException">The value is set but is not base64 of exactly 32 bytes.</exception>
    public static byte[]? ParseMasterKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        byte[] key;
        try
        {
            key = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new FormatException("Storage:MasterKey must be base64 (generate one with `openssl rand -base64 32`).");
        }
        if (key.Length != MediaKeyWrap.KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new FormatException($"Storage:MasterKey must decode to exactly {MediaKeyWrap.KeySize} bytes.");
        }
        return key;
    }

    public static bool IsValidMasterKey(string? value)
    {
        try
        {
            var key = ParseMasterKey(value);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A new wrapped data key for <paramref name="mediaId"/>, or null when encryption at rest is off.</summary>
    public byte[]? CreateWrappedKey(Guid mediaId)
    {
        if (_masterKey is null) return null;
        var dataKey = MediaKeyWrap.GenerateDataKey();
        try
        {
            return MediaKeyWrap.Wrap(_masterKey, mediaId, dataKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>The chunk cipher of an encrypted media; null for media stored in plain text.</summary>
    /// <exception cref="InvalidOperationException">The media is encrypted but no master key is configured.</exception>
    public MediaChunkCipher? CreateCipher(Guid mediaId, byte[]? wrappedKey)
    {
        if (wrappedKey is null) return null;
        if (_masterKey is null)
            throw new InvalidOperationException($"Media {mediaId} is encrypted at rest but Storage:MasterKey is not configured.");

        var dataKey = MediaKeyWrap.Unwrap(_masterKey, mediaId, wrappedKey);
        try
        {
            return new MediaChunkCipher(dataKey, mediaId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }
}
