using System.Text.Json;
using System.Text.Json.Serialization;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Settings;

/// <summary>
/// Per-device settings, persisted as JSON through <see cref="IPreferencesStore"/>. Instances handed out
/// by ISettingsService.Current are read-only snapshots: never mutate them, use UpdateAsync.
/// </summary>
public sealed class AppSettings
{
    public AppearanceSettings Appearance { get; set; } = new();
    public ChatBehaviorSettings Chats { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public DeviceSettings Devices { get; set; } = new();
    public DataSettings Data { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();
    /// <summary>"auto" (follow the OS), "ru" or "en".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Deep copy (JSON round trip; the proxy password is not serialized and is copied explicitly).</summary>
    public AppSettings Clone()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SkyJson.Options), SkyJson.Options)!;
        copy.Connection.Proxy.Password = Connection.Proxy.Password;
        return copy;
    }
}

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>Palette key defined by the UI theme service (e.g. "violet", "blue", "teal").</summary>
    public string Accent { get; set; } = "violet";
    /// <summary>Overrides <see cref="Accent"/> when set ("#RRGGBB").</summary>
    public string? CustomAccentHex { get; set; }
    /// <summary>85..140.</summary>
    public int FontScalePercent { get; set; } = 100;
    /// <summary>Built-in wallpaper key or "none".</summary>
    public string Wallpaper { get; set; } = "aurora";
    /// <summary>Message bubble corner radius in px, 4..24.</summary>
    public int BubbleRadius { get; set; } = 16;
    public bool Animations { get; set; } = true;
    public bool CompactChatList { get; set; }
}

public sealed class ChatBehaviorSettings
{
    /// <summary>Enter sends, Shift+Enter new line (desktop/web). On phones the send button is used.</summary>
    public bool SendByEnter { get; set; } = true;
    public string QuickReaction { get; set; } = "👍";
    public bool AutoplayVideos { get; set; } = true;
    public bool AutoplayVideoNotes { get; set; } = true;
    public double VoicePlaybackRate { get; set; } = 1.0;
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool ShowPreview { get; set; } = true;
    public bool Sound { get; set; } = true;
    public bool Vibrate { get; set; } = true;
    public bool GroupsEnabled { get; set; } = true;
    public bool CallsEnabled { get; set; } = true;
    public bool InAppSounds { get; set; } = true;
    public bool InAppVibrate { get; set; } = true;
    /// <summary>Android: keep the connection alive in background (persistent notification) so calls ring when the app is closed.</summary>
    public bool KeepConnectedInBackground { get; set; } = true;
    /// <summary>Mentions and replies to me notify even in muted chats.</summary>
    public bool MentionsBypassMute { get; set; } = true;
    /// <summary>
    /// Sound pack (<see cref="SoundCatalog.Packs"/>: wood, bells, piano — our own synthesized sounds). It drives the
    /// message tone, the ringtone and the call sounds. Settings saved before packs existed have none: they get "wood",
    /// whose sounds are the ones those versions played.
    /// </summary>
    public string SoundPack { get; set; } = SoundCatalog.DefaultPack;
    /// <summary>A key of the pack's message tones, "none", or "custom" (<see cref="CustomMessageTone"/>).</summary>
    public string MessageTone { get; set; } = SoundCatalog.DefaultMessageTone;
    /// <summary>A key of the pack's ringtones or "custom" (<see cref="CustomRingtone"/>).</summary>
    public string Ringtone { get; set; } = SoundCatalog.DefaultRingtone;
    /// <summary>The user's own message tone (kept while another tone is chosen, so it can be picked again).</summary>
    public CustomSound? CustomMessageTone { get; set; }
    /// <summary>The user's own ringtone.</summary>
    public CustomSound? CustomRingtone { get; set; }
}

public enum VideoQuality
{
    Auto,
    Low,
    Medium,
    High,
}

public sealed class DeviceSettings
{
    /// <summary>MediaDeviceInfo.deviceId values; null = system default.</summary>
    public string? MicrophoneId { get; set; }
    public string? CameraId { get; set; }
    public string? SpeakerId { get; set; }
    public bool EchoCancellation { get; set; } = true;
    public bool NoiseSuppression { get; set; } = true;
    public bool AutoGainControl { get; set; } = true;
    public bool MirrorFrontCamera { get; set; } = true;
    public VideoQuality CallVideoQuality { get; set; } = VideoQuality.Auto;
    /// <summary>Phones: default output for voice calls.</summary>
    public AudioRouteKind? PreferredAudioRoute { get; set; }
}

public enum MediaQuality
{
    Low,
    Standard,
    High,
}

public sealed class AutoDownloadSettings
{
    public bool Photos { get; set; } = true;
    public bool Videos { get; set; } = true;
    public bool Files { get; set; }
    public int MaxFileMb { get; set; } = 20;
}

/// <summary>Photos/videos/files are auto-downloaded (and cached) per network type via INetworkStatusService.</summary>
public sealed class DataSettings
{
    public AutoDownloadSettings AutoDownloadWifi { get; set; } = new() { Photos = true, Videos = true, Files = true, MaxFileMb = 50 };
    public AutoDownloadSettings AutoDownloadMobile { get; set; } = new() { Photos = true, Videos = false, Files = false, MaxFileMb = 10 };
    /// <summary>Photo: Low 1280px / Standard 2560px / High 4096px.</summary>
    public MediaQuality PhotoQuality { get; set; } = MediaQuality.Standard;
    /// <summary>Video: Low 480p / Standard 720p / High 1080p.</summary>
    public MediaQuality VideoQuality { get; set; } = MediaQuality.Standard;
    public bool SaveToGallery { get; set; }
    public int CacheLimitMb { get; set; } = 1024;
    /// <summary>Lower call bitrate and thumbnails on slow networks.</summary>
    public bool LowDataMode { get; set; }
}

public enum ProxyType
{
    None,
    Http,
    Socks5,
}

public sealed class ProxySettings
{
    public ProxyType Type { get; set; } = ProxyType.None;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? Username { get; set; }
    /// <summary>Kept in ISecureStore ("proxy.password") by the settings service, never in preferences JSON.</summary>
    [JsonIgnore]
    public string? Password { get; set; }
}

public sealed class ConnectionSettings
{
    /// <summary>User-entered server base URL; overrides the build default when set.</summary>
    public string? CustomServerUrl { get; set; }
    /// <summary>Extra fallback addresses tried when the main one is unreachable.</summary>
    public List<string> ExtraServerUrls { get; set; } = [];
    public ProxySettings Proxy { get; set; } = new();
    /// <summary>Allow SignalR to fall back to Server-Sent Events / long polling when WebSockets are blocked.</summary>
    public bool AllowTransportFallback { get; set; } = true;
}
