using Microsoft.Extensions.Caching.Memory;

namespace SkyMessage.Application.Identity;

/// <summary>IMemoryCache keys of the identity feature (one place, so evictions always hit the right entry).</summary>
internal static class IdentityCacheKeys
{
    public static string SessionActive(Guid sessionId) => $"identity:session-active:{sessionId:N}";
    public static string SessionMediaKey(Guid sessionId) => $"identity:session-media:{sessionId:N}";
    public static string SessionTouch(Guid sessionId) => $"identity:session-touch:{sessionId:N}";
    public static string RefreshConfirmed(Guid sessionId, string binding) => $"identity:refresh-confirmed:{sessionId:N}:{binding}";
    public static string SavedChat(Guid userId) => $"identity:saved-chat:{userId:N}";
    public static string TypingMembers(Guid chatId) => $"identity:typing-members:{chatId:N}";
    /// <summary>Users who blocked <paramref name="userId"/> or whom <paramref name="userId"/> blocked (typing fan-out).</summary>
    public static string BlockPartners(Guid userId) => $"identity:block-partners:{userId:N}";
    public static string LoginFailures(string key) => $"identity:login-failures:{key}";

    /// <summary>Entry options with a size, so entries stay valid even if a size-limited cache is configured later.</summary>
    public static MemoryCacheEntryOptions Absolute(TimeSpan ttl) =>
        new() { AbsoluteExpirationRelativeToNow = ttl, Size = 1 };

    public static MemoryCacheEntryOptions Sliding(TimeSpan ttl) =>
        new() { SlidingExpiration = ttl, Size = 1 };
}
