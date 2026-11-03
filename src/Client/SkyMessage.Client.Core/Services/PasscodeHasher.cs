using System.Diagnostics;
using System.Security.Cryptography;

namespace SkyMessage.Client.Core.Services;

/// <summary>A stored passcode hash: PBKDF2-HMAC-SHA256 with its salt and round count (base64).</summary>
internal sealed record PasscodeHash(string Algorithm, int Iterations, string Salt, string Hash);

/// <summary>
/// Salted PBKDF2-HMAC-SHA256 for the app passcode; the passcode itself is never stored. The round count is picked when a
/// passcode is set so that one check takes about a quarter of a second on this device (from 10,000 up to 600,000 rounds:
/// a fast phone gets more, a budget phone or a browser fewer) and is kept with the hash, so checks never depend on the
/// device's speed later. A 4–6 digit code cannot resist an offline guesser whatever the round count: the hash lives in
/// the platform's secure storage and the app pauses after wrong attempts. Comparison runs in constant time.
/// </summary>
internal static class PasscodeHasher
{
    public const string Algorithm = "pbkdf2-sha256";
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const int MinIterations = 10_000;
    public const int MaxIterations = 600_000;
    private const int ProbeIterations = 4_000;
    private static readonly TimeSpan Target = TimeSpan.FromMilliseconds(250);

    public static PasscodeHash Create(string passcode, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passcode);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(passcode, salt, iterations);
        return new PasscodeHash(Algorithm, iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>False for a wrong passcode and for a hash this version cannot read.</summary>
    public static bool Verify(string passcode, PasscodeHash stored)
    {
        if (string.IsNullOrEmpty(passcode) || stored.Algorithm != Algorithm || stored.Iterations is < 1 or > 10_000_000) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(stored.Salt);
            expected = Convert.FromBase64String(stored.Hash);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length == 0 || expected.Length != HashBytes) return false;
        var actual = Derive(passcode, salt, stored.Iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Rounds for about <see cref="Target"/> per check on this device, measured with a short warm run.</summary>
    public static int ChooseIterations()
    {
        var salt = new byte[SaltBytes];
        // The first run pays for loading and JIT; the second one is measured.
        Derive("0000", salt, 64);
        var started = Stopwatch.GetTimestamp();
        Derive("0000", salt, ProbeIterations);
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed <= TimeSpan.Zero) return MaxIterations;
        var rounds = ProbeIterations * (Target.TotalMilliseconds / elapsed.TotalMilliseconds);
        return (int)Math.Clamp(Math.Round(rounds / 1000) * 1000, MinIterations, MaxIterations);
    }

    private static byte[] Derive(string passcode, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passcode, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
