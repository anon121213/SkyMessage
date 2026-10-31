using SkyMessage.Client.Core.Infrastructure;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// Small per-account values under <c>acct/{userId}/local/</c> in the local cache. The whole account prefix is wiped
/// on sign-out; while signed out reads return default and writes are dropped.
/// </summary>
internal sealed class AccountLocalStore(LocalCache cache, AccountLifecycle lifecycle) : IAccountLocalStore
{
    public async Task<T?> GetAsync<T>(string key)
    {
        if (lifecycle.ActiveUserId is not { } userId) return default;
        return await cache.ReadAsync<T>(Key(userId, key)).ConfigureAwait(false);
    }

    public async Task SetAsync<T>(string key, T value)
    {
        if (lifecycle.ActiveUserId is not { } userId) return;
        await cache.WriteAsync(Key(userId, key), value).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key)
    {
        if (lifecycle.ActiveUserId is not { } userId) return;
        await cache.DeleteAsync(Key(userId, key)).ConfigureAwait(false);
    }

    // Keys are free-form: escape them so they stay one flat name in file- and IndexedDB-backed stores.
    private static string Key(Guid userId, string key) => LocalCache.AccountKey(userId, "local/" + Uri.EscapeDataString(key));
}
