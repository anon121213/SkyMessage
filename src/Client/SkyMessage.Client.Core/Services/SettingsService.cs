using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// "settings.v1" JSON in <see cref="IPreferencesStore"/>; the proxy password lives in <see cref="ISecureStore"/>
/// ("proxy.password"). <see cref="Current"/> is a snapshot that is replaced, never mutated.
/// </summary>
internal sealed class SettingsService : ISettingsService
{
    public const string Key = "settings.v1";
    public const string ProxyPasswordKey = "proxy.password";

    private readonly IPreferencesStore _preferences;
    private readonly ISecureStore _secureStore;
    private readonly IPlatformInfo _platform;
    private readonly ILogger<SettingsService> _logger;
    private readonly SerialQueue _queue = new();
    private volatile AppSettings _current;
    private bool _loaded;

    public SettingsService(IPreferencesStore preferences, ISecureStore secureStore, IPlatformInfo platform, ILogger<SettingsService> logger)
    {
        _preferences = preferences;
        _secureStore = secureStore;
        _platform = platform;
        _logger = logger;
        _current = CreateDefaults();
    }

    public AppSettings Current => _current;

    public event Action? Changed;

    public async Task LoadAsync()
    {
        var changed = await _queue.RunAsync(async () =>
        {
            if (_loaded) return false;
            _loaded = true;

            AppSettings? stored = null;
            try
            {
                var json = await _preferences.GetAsync(Key).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json))
                    stored = JsonSerializer.Deserialize<AppSettings>(json, SkyJson.Options);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stored settings are unreadable; using defaults");
            }

            var settings = stored ?? CreateDefaults();
            settings.Appearance ??= new AppearanceSettings();
            settings.Chats ??= new ChatBehaviorSettings();
            settings.Notifications ??= new NotificationSettings();
            settings.Devices ??= new DeviceSettings();
            settings.Data ??= new DataSettings();
            settings.Connection ??= new ConnectionSettings();
            settings.Connection.Proxy ??= new ProxySettings();
            settings.Connection.ExtraServerUrls ??= [];

            try
            {
                settings.Connection.Proxy.Password = await _secureStore.GetAsync(ProxyPasswordKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the proxy password");
            }

            Normalize(settings);
            _current = settings;
            return true;
        }).ConfigureAwait(false);

        if (changed) EventRaiser.Raise(Changed, _logger);
    }

    public async Task UpdateAsync(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _queue.RunAsync(async () =>
        {
            var previous = _current;
            var next = previous.Clone();
            mutate(next);
            Normalize(next);
            await PersistAsync(next, previous.Connection.Proxy.Password).ConfigureAwait(false);
            _current = next;
        }).ConfigureAwait(false);
        EventRaiser.Raise(Changed, _logger);
    }

    /// <summary>Back to defaults, keeping the connection section (server address and proxy) so the session keeps working.</summary>
    public async Task ResetAsync()
    {
        await _queue.RunAsync(async () =>
        {
            var previous = _current;
            var next = CreateDefaults();
            next.Connection = previous.Clone().Connection;
            await PersistAsync(next, previous.Connection.Proxy.Password).ConfigureAwait(false);
            _current = next;
        }).ConfigureAwait(false);
        EventRaiser.Raise(Changed, _logger);
    }

    private AppSettings CreateDefaults()
    {
        var settings = new AppSettings();
        // Phones use the send button; Enter inserts a new line.
        if (_platform.IsMobile) settings.Chats.SendByEnter = false;
        return settings;
    }

    private async Task PersistAsync(AppSettings settings, string? previousPassword)
    {
        try
        {
            await _preferences.SetAsync(Key, JsonSerializer.Serialize(settings, SkyJson.Options)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist settings");
        }

        var password = settings.Connection.Proxy.Password;
        if (string.Equals(password, previousPassword, StringComparison.Ordinal)) return;
        try
        {
            if (string.IsNullOrEmpty(password))
                await _secureStore.RemoveAsync(ProxyPasswordKey).ConfigureAwait(false);
            else
                await _secureStore.SetAsync(ProxyPasswordKey, password).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist the proxy password");
        }
    }

    private static void Normalize(AppSettings s)
    {
        s.Appearance.FontScalePercent = Math.Clamp(s.Appearance.FontScalePercent, 85, 140);
        s.Appearance.BubbleRadius = Math.Clamp(s.Appearance.BubbleRadius, 4, 24);
        if (string.IsNullOrWhiteSpace(s.Appearance.Accent)) s.Appearance.Accent = "violet";
        if (string.IsNullOrWhiteSpace(s.Appearance.Wallpaper)) s.Appearance.Wallpaper = "aurora";
        s.Chats.VoicePlaybackRate = s.Chats.VoicePlaybackRate is >= 0.5 and <= 3 ? s.Chats.VoicePlaybackRate : 1.0;
        if (string.IsNullOrWhiteSpace(s.Chats.QuickReaction)) s.Chats.QuickReaction = "👍";
        s.Data.CacheLimitMb = Math.Clamp(s.Data.CacheLimitMb, 50, 64 * 1024);
        s.Data.AutoDownloadWifi ??= new AutoDownloadSettings();
        s.Data.AutoDownloadMobile ??= new AutoDownloadSettings();
        s.Data.AutoDownloadWifi.MaxFileMb = Math.Clamp(s.Data.AutoDownloadWifi.MaxFileMb, 0, 4096);
        s.Data.AutoDownloadMobile.MaxFileMb = Math.Clamp(s.Data.AutoDownloadMobile.MaxFileMb, 0, 4096);
        s.Connection.Proxy.Port = Math.Clamp(s.Connection.Proxy.Port, 0, 65535);
        s.Connection.Proxy.Host = s.Connection.Proxy.Host?.Trim() ?? "";
        s.Connection.CustomServerUrl = string.IsNullOrWhiteSpace(s.Connection.CustomServerUrl) ? null : s.Connection.CustomServerUrl.Trim();
        s.Connection.ExtraServerUrls = s.Connection.ExtraServerUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (s.Language is not ("auto" or "ru" or "en")) s.Language = "auto";
    }
}
