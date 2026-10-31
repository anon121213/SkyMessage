using System.Buffers.Text;
using System.Security.Cryptography;

namespace SkyMessage.Contracts;

/// <summary>
/// Per-file media URL signatures shared by server and clients. Token format:
/// <c>{sessionId:N}.{base64url(HMAC-SHA256(sessionMediaKey, mediaId big-endian bytes))[..22]}</c>.
/// The server looks up the session's key, verifies in constant time, then applies normal access rules.
/// </summary>
public static class MediaSignature
{
    public const int KeyLength = 32;
    private const int SignatureChars = 22;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyLength);

    public static string EncodeKey(ReadOnlySpan<byte> key) => Base64Url.EncodeToString(key);

    public static byte[] DecodeKey(string encodedKey) => Base64Url.DecodeFromChars(encodedKey);

    public static string Create(string encodedKey, Guid sessionId, Guid mediaId) =>
        Create(DecodeKey(encodedKey), sessionId, mediaId);

    public static string Create(ReadOnlySpan<byte> key, Guid sessionId, Guid mediaId) =>
        $"{sessionId:N}.{Sign(key, mediaId)}";

    public static bool TryParse(string? token, out Guid sessionId, out string signature)
    {
        sessionId = Guid.Empty;
        signature = "";
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot != 32 || token.Length != 33 + SignatureChars) return false;
        if (!Guid.TryParseExact(token.AsSpan(0, 32), "N", out sessionId)) return false;
        signature = token[(dot + 1)..];
        return true;
    }

    /// <summary>Server identity proof for <see cref="ServerProofDto"/>: HMAC over "proof|{nonce}|{host}".</summary>
    public static string CreateProof(ReadOnlySpan<byte> key, string nonce, string host)
    {
        var data = System.Text.Encoding.UTF8.GetBytes($"proof|{nonce}|{host.ToLowerInvariant()}");
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, data, mac);
        return Base64Url.EncodeToString(mac);
    }

    public static bool VerifyProof(ReadOnlySpan<byte> key, string nonce, string host, string proof)
    {
        var expected = CreateProof(key, nonce, host);
        return expected.Length == proof.Length
               && CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.ASCII.GetBytes(expected),
                   System.Text.Encoding.ASCII.GetBytes(proof));
    }

    public static bool Verify(ReadOnlySpan<byte> key, Guid mediaId, string signature)
    {
        var expected = Sign(key, mediaId);
        return expected.Length == signature.Length
               && CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.ASCII.GetBytes(expected),
                   System.Text.Encoding.ASCII.GetBytes(signature));
    }

    private static string Sign(ReadOnlySpan<byte> key, Guid mediaId)
    {
        Span<byte> id = stackalloc byte[16];
        mediaId.TryWriteBytes(id, bigEndian: true, out _);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, id, mac);
        return Base64Url.EncodeToString(mac)[..SignatureChars];
    }
}
