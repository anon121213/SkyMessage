namespace SkyMessage.Client.Core.Settings;

/// <summary>
/// The app's sounds: three packs of our own synthesized sounds (rendered by tools/tones, no samples), each with the
/// same keys, plus the user's own sounds (<see cref="CustomSound"/>). The selected pack drives the message tone, the
/// ringtone and the call-progress sounds; the settings pick a key within it. This class knows the valid values and
/// repairs anything else. Settings saved by older versions (one set of tones, no pack) keep their key in the wood
/// pack; those keys sound different now, since the earlier set is not one of the packs.
/// </summary>
public static class SoundCatalog
{
    public const string Wood = "wood";
    public const string Bells = "bells";
    public const string Piano = "piano";
    public const string DefaultPack = Wood;

    public const string None = "none";
    /// <summary>MessageTone / Ringtone value meaning "the user's own sound" (<see cref="NotificationSettings.CustomMessageTone"/>, <see cref="NotificationSettings.CustomRingtone"/>).</summary>
    public const string Custom = "custom";

    public const string DefaultMessageTone = "chime";
    public const string DefaultRingtone = "classic";
    public const string Ringback = "ringback";
    public const string Busy = "busy";
    public const string CallEnd = "end";

    /// <summary>In the order the settings show them.</summary>
    public static IReadOnlyList<string> Packs { get; } = [Wood, Bells, Piano];

    public static IReadOnlyList<string> MessageTones { get; } = [DefaultMessageTone, "pop", "glass"];

    public static IReadOnlyList<string> Ringtones { get; } = [DefaultRingtone, "soft", "bright"];

    public static IReadOnlyList<string> CallProgress { get; } = [Ringback, Busy, CallEnd];

    /// <summary>Every key a pack has a file for.</summary>
    public static IReadOnlyList<string> Keys { get; } = [.. MessageTones, .. Ringtones, .. CallProgress];

    public static bool IsPack(string? key) => key is not null && Packs.Contains(key);

    public static string PackOrDefault(string? key) => Lower(key) is { } k && IsPack(k) ? k : DefaultPack;

    /// <summary>A pack's message tone, "none", or "custom" when the user has one; anything else is the default.</summary>
    public static string MessageToneOrDefault(string? key, bool hasCustom) => Lower(key) switch
    {
        { } k when MessageTones.Contains(k) || k == None => k,
        Custom when hasCustom => Custom,
        _ => DefaultMessageTone,
    };

    /// <summary>A pack's ringtone or "custom" when the user has one; anything else is the default (calls always ring).</summary>
    public static string RingtoneOrDefault(string? key, bool hasCustom) => Lower(key) switch
    {
        { } k when Ringtones.Contains(k) => k,
        Custom when hasCustom => Custom,
        _ => DefaultRingtone,
    };

    /// <summary>Repairs the sound choices of a settings block in place (unknown pack or key, a custom choice without its sound).</summary>
    public static void Normalize(NotificationSettings settings)
    {
        settings.SoundPack = PackOrDefault(settings.SoundPack);
        if (settings.CustomMessageTone is { } message && !CustomSoundRules.IsWellFormed(message, CustomSoundSlot.Message))
            settings.CustomMessageTone = null;
        if (settings.CustomRingtone is { } ring && !CustomSoundRules.IsWellFormed(ring, CustomSoundSlot.Ringtone))
            settings.CustomRingtone = null;
        settings.MessageTone = MessageToneOrDefault(settings.MessageTone, settings.CustomMessageTone is not null);
        settings.Ringtone = RingtoneOrDefault(settings.Ringtone, settings.CustomRingtone is not null);
    }

    /// <summary>What a message sounds like under these settings; null when the tone is "none".</summary>
    public static SoundChoice? MessageSound(NotificationSettings settings)
    {
        var pack = PackOrDefault(settings.SoundPack);
        var key = MessageToneOrDefault(settings.MessageTone, settings.CustomMessageTone is not null);
        if (key == None) return null;
        return key == Custom
            ? new SoundChoice(pack, DefaultMessageTone, settings.CustomMessageTone)
            : new SoundChoice(pack, key, null);
    }

    /// <summary>What an incoming call rings with under these settings.</summary>
    public static SoundChoice RingtoneSound(NotificationSettings settings)
    {
        var pack = PackOrDefault(settings.SoundPack);
        var key = RingtoneOrDefault(settings.Ringtone, settings.CustomRingtone is not null);
        return key == Custom
            ? new SoundChoice(pack, DefaultRingtone, settings.CustomRingtone)
            : new SoundChoice(pack, key, null);
    }

    /// <summary>Android res/raw name of a pack sound (tools/tones writes tone_&lt;pack&gt;_&lt;key&gt;.ogg).</summary>
    public static string ResourceName(string pack, string key) => $"tone_{pack}_{key}";

    private static string? Lower(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

/// <summary>
/// A sound to play: the user's own (<see cref="Custom"/>), or the pack sound <see cref="Pack"/>/<see cref="Key"/>,
/// which is also what plays when the custom file cannot be (it is gone, or the player cannot decode it).
/// </summary>
public sealed record SoundChoice(string Pack, string Key, CustomSound? Custom)
{
    public bool IsCustom => Custom is not null;

    /// <summary>Identifies the sound for OS notification channels (whose sound is fixed): it changes whenever the sound does.</summary>
    public string Id => Custom is { } custom ? $"custom-{custom.Id}" : $"{Pack}-{Key}";

    /// <summary>The pack sound that stands in for a custom one.</summary>
    public SoundChoice Fallback => Custom is null ? this : this with { Custom = null };
}
