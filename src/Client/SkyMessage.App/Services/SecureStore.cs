using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Services;

/// <summary>
/// Tokens and the proxy password in the OS keystore (Android Keystore-backed encrypted preferences,
/// Apple Keychain). Keystore failures never crash the app:
/// <list type="bullet">
/// <item>Android: an invalidated keystore (restored device, reset lock screen) makes stored values
/// unrecoverable — they are wiped and the user signs in again.</item>
/// <item>Apple: Keychain errors (e.g. -34018 on a simulator without the keychain entitlement) switch to a
/// fallback store: preferences in DEBUG (development convenience), memory only in release (the session
/// lasts until the app is closed; nothing sensitive is written in plain text).</item>
/// </list>
/// </summary>
public sealed class SecureStore(ILogger<SecureStore> logger) : ISecureStore
{
    private const string DebugFallbackName = "skymessage.debug-securestore";

    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);
    private volatile bool _degraded;

    public async Task<string?> GetAsync(string key)
    {
        if (_degraded) return FallbackGet(key);
        try
        {
            var value = await SecureStorage.Default.GetAsync(key).ConfigureAwait(false);
#if DEBUG && (IOS || MACCATALYST)
            // MAUI reads return null (no exception) when the Keychain is unusable; a value in the DEBUG fallback
            // store means an earlier run degraded, so keep using that store for reads and writes.
            if (value is null && FallbackGet(key) is { } fallback)
            {
                Degrade();
                return fallback;
            }
#endif
            return value;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Secure storage read failed for {Key}", key);
            return OnFailure(key);
        }
    }

    public async Task SetAsync(string key, string value)
    {
        if (_degraded)
        {
            FallbackSet(key, value);
            return;
        }
        try
        {
            await SecureStorage.Default.SetAsync(key, value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Secure storage write failed for {Key}", key);
#if ANDROID
            // A broken master key: wipe and retry once with a fresh key.
            TryRemoveAll();
            try
            {
                await SecureStorage.Default.SetAsync(key, value).ConfigureAwait(false);
                return;
            }
            catch (Exception retry)
            {
                logger.LogWarning(retry, "Secure storage is unusable; keeping {Key} in memory", key);
            }
#endif
            Degrade();
            FallbackSet(key, value);
        }
    }

    public Task RemoveAsync(string key)
    {
        _memory.TryRemove(key, out _);
        RemoveDebugFallback(key);
        if (!_degraded)
        {
            try
            {
                SecureStorage.Default.Remove(key);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Secure storage remove failed for {Key}", key);
            }
        }
        return Task.CompletedTask;
    }

    private string? OnFailure(string key)
    {
#if ANDROID
        TryRemoveAll();
        return null;
#else
        Degrade();
        return FallbackGet(key);
#endif
    }

    private void Degrade()
    {
        if (_degraded) return;
        _degraded = true;
        logger.LogWarning("Secure storage unavailable; using the {Mode} fallback store",
#if DEBUG
            "preferences (DEBUG)"
#else
            "in-memory"
#endif
        );
    }

    private string? FallbackGet(string key)
    {
        if (_memory.TryGetValue(key, out var value)) return value;
#if DEBUG
        try
        {
            value = Preferences.Default.Get<string?>(key, null, DebugFallbackName);
            if (value is not null) _memory[key] = value;
            return value;
        }
        catch (Exception)
        {
            return null;
        }
#else
        return null;
#endif
    }

    private void FallbackSet(string key, string value)
    {
        _memory[key] = value;
#if DEBUG
        try
        {
            Preferences.Default.Set(key, value, DebugFallbackName);
        }
        catch (Exception)
        {
            // Memory copy is enough for this run.
        }
#endif
    }

    private static void RemoveDebugFallback(string key)
    {
#if DEBUG
        try
        {
            Preferences.Default.Remove(key, DebugFallbackName);
        }
        catch (Exception)
        {
        }
#endif
    }

    private void TryRemoveAll()
    {
        try
        {
            SecureStorage.Default.RemoveAll();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reset secure storage");
        }
    }
}
