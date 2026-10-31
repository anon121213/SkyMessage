using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>
/// Typed JSON access to <see cref="ILocalCacheStore"/>. Everything per account lives under
/// <c>acct/{userId}/</c> and is wiped on sign-out. The cache is best effort: unreadable entries are
/// treated as missing and write failures are logged, never thrown.
/// </summary>
internal sealed class LocalCache(ILocalCacheStore store, ILogger<LocalCache> logger)
{
    public static string AccountPrefix(Guid userId) => $"acct/{userId}/";

    public static string AccountKey(Guid userId, string name) => $"acct/{userId}/{name}";

    public async Task<T?> ReadAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, SkyJson.Options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache entry {Key} is unreadable; ignoring it", key);
            return default;
        }
    }

    public async Task WriteAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, SkyJson.Options);
            await store.WriteAsync(key, json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write cache entry {Key}", key);
        }
    }

    public async Task DeleteAsync(string key)
    {
        try
        {
            await store.DeleteAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete cache entry {Key}", key);
        }
    }

    public async Task ClearAsync(string prefix)
    {
        try
        {
            await store.ClearAsync(prefix).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear cache prefix {Prefix}", prefix);
        }
    }

    public async Task<long> GetSizeBytesAsync()
    {
        try
        {
            return await store.GetSizeBytesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the cache size");
            return 0;
        }
    }
}
