using Microsoft.Extensions.Caching.Memory;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Password-guessing lockout: after <see cref="MaxFailures"/> failures for one login within
/// <see cref="Window"/> (counted from the first failure) further attempts get 429 until the window ends.
/// In-memory (single API instance); IP-based rate limiting applies on top.
/// </summary>
public sealed class LoginThrottle(IMemoryCache cache)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public static string UsernameKey(string normalizedUsername) => "u:" + normalizedUsername;
    public static string PhoneKey(string phone) => "p:" + phone;
    /// <summary>Current-password checks of a signed-in user (change password, delete account).</summary>
    public static string PasswordKey(Guid userId) => $"pw:{userId:N}";

    public bool IsLockedOut(string key) =>
        cache.TryGetValue(IdentityCacheKeys.LoginFailures(key), out Counter? counter)
        && Volatile.Read(ref counter!.Count) >= MaxFailures;

    public void RegisterFailure(string key)
    {
        var counter = cache.GetOrCreate(IdentityCacheKeys.LoginFailures(key), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Window;
            entry.Size = 1;
            return new Counter();
        })!;
        Interlocked.Increment(ref counter.Count);
    }

    public void Reset(string key) => cache.Remove(IdentityCacheKeys.LoginFailures(key));

    private sealed class Counter
    {
        public int Count;
    }
}
