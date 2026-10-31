using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Services;

/// <summary>Small string values in the OS preferences store (SharedPreferences / NSUserDefaults).</summary>
public sealed class PreferencesStore(ILogger<PreferencesStore> logger) : IPreferencesStore
{
    internal const string SharedName = "skymessage.prefs";

    public Task<string?> GetAsync(string key)
    {
        try
        {
            return Task.FromResult(Preferences.Default.Get<string?>(key, null, SharedName));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Preference {Key} is unreadable", key);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string? value)
    {
        try
        {
            if (value is null) Preferences.Default.Remove(key, SharedName);
            else Preferences.Default.Set(key, value, SharedName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not store preference {Key}", key);
        }
        return Task.CompletedTask;
    }
}
