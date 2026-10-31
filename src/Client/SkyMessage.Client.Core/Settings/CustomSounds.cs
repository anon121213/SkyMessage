using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SkyMessage.Client.Core.Settings;

/// <summary>Where the user's own sound replaces the pack's.</summary>
public enum CustomSoundSlot
{
    Message,
    Ringtone,
}

/// <summary>
/// The user's own sound for one slot. The file lives on this device (ICustomSoundStore); the settings keep this
/// description of it.
/// </summary>
public sealed class CustomSound
{
    /// <summary>Random and file-name safe (<see cref="CustomSoundRules.NewId"/>); a new file gets a new id, which OS notification channels key on.</summary>
    public string Id { get; set; } = "";
    /// <summary>The picked file's name without its extension, shown in settings.</summary>
    public string Name { get; set; } = "";
    /// <summary>One of <see cref="CustomSoundRules.Extensions"/>, e.g. ".mp3".</summary>
    public string Extension { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public int DurationMs { get; set; }

    /// <summary>The stored file's name.</summary>
    [JsonIgnore]
    public string FileName => Id + Extension;
}

public enum CustomSoundProblem
{
    None,
    /// <summary>Not an audio file we accept (mp3, m4a/aac, ogg/opus, wav).</summary>
    Format,
    Empty,
    TooLarge,
    /// <summary>The player could not decode it, or its headers do not say how long it is.</summary>
    Unreadable,
    TooShort,
    TooLong,
    /// <summary>The app plays it, but this device's system player, which rings notifications, cannot (Android).</summary>
    SystemCannotPlay,
}

/// <summary>
/// What a custom sound may be. Checked before the file is read (type, size), on its headers before it is decoded
/// (container, length), by the system player where it rings notifications, and after it is decoded (exact length).
/// </summary>
public static class CustomSoundRules
{
    /// <summary>4 MB: a minute of a typical MP3; keeps decoding quick on budget phones.</summary>
    public const long MaxBytes = 4L * 1024 * 1024;
    public const int MinDurationMs = 100;
    /// <summary>A message tone plays in full for every notification; longer would be a song, not a tone.</summary>
    public const int MaxMessageDurationMs = 10_000;
    /// <summary>A ringtone loops while it rings; a minute covers any ringtone cut.</summary>
    public const int MaxRingtoneDurationMs = 60_000;
    public const int MaxNameLength = 64;

    /// <summary>Accepted extensions and the content type each is stored with.</summary>
    public static IReadOnlyDictionary<string, string> Extensions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".wav"] = "audio/wav",
    };

    /// <summary>Content types pickers report for those files (some report none or application/octet-stream: the extension decides then).</summary>
    private static readonly Dictionary<string, string> TypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/mpeg3"] = ".mp3",
        ["audio/x-mpeg-3"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/m4a"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/x-aac"] = ".aac",
        ["audio/aacp"] = ".aac",
        ["audio/ogg"] = ".ogg",
        ["application/ogg"] = ".ogg",
        ["audio/vorbis"] = ".ogg",
        ["audio/opus"] = ".opus",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/wave"] = ".wav",
        ["audio/vnd.wave"] = ".wav",
    };

    /// <summary>The picker filter (browser input accept): audio types plus the extensions for pickers that ignore types.</summary>
    public const string AcceptAttribute = "audio/*,.mp3,.m4a,.aac,.ogg,.oga,.opus,.wav";

    /// <summary>Content types for native pickers.</summary>
    public static IReadOnlyList<string> PickerContentTypes { get; } = ["audio/*", "application/ogg"];

    public static int MaxDurationMs(CustomSoundSlot slot) => slot == CustomSoundSlot.Message ? MaxMessageDurationMs : MaxRingtoneDurationMs;

    /// <summary>The extension to store the file under: its own when we accept it, else the one its content type implies; null when neither is an accepted audio file.</summary>
    public static string? ExtensionOf(string? fileName, string? contentType)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (Extensions.ContainsKey(extension)) return extension;
        var type = contentType?.Split(';', 2)[0].Trim();
        return type is not null && TypeExtensions.TryGetValue(type, out var implied) ? implied : null;
    }

    /// <summary>What can be checked before reading the file. <paramref name="size"/> -1 means unknown (checked again on the bytes).</summary>
    public static CustomSoundProblem CheckFile(string? fileName, string? contentType, long size)
    {
        if (ExtensionOf(fileName, contentType) is null) return CustomSoundProblem.Format;
        if (size == 0) return CustomSoundProblem.Empty;
        return size > MaxBytes ? CustomSoundProblem.TooLarge : CustomSoundProblem.None;
    }

    /// <summary>
    /// Before decoding, on what the headers say (<see cref="AudioFileProbe"/>). The player decodes a whole file into raw
    /// samples at once (about 12 MB a minute per channel), so a file must be one of the containers we accept, say how long it is,
    /// and be at most a quarter (plus a second) over the limit (an MP3's constant-bitrate estimate can be a little off);
    /// the exact length is checked after decoding.
    /// </summary>
    public static CustomSoundProblem CheckHeaders(CustomSoundSlot slot, AudioFileInfo info)
    {
        if (info.Container == AudioContainer.Unknown) return CustomSoundProblem.Format;
        if (info.Seconds is not { } s || double.IsNaN(s) || double.IsInfinity(s) || s < 0) return CustomSoundProblem.Unreadable;
        return s * 1000 > MaxDecodeMs(slot) ? CustomSoundProblem.TooLong : CustomSoundProblem.None;
    }

    /// <summary>The longest file, by its headers, that is decoded to measure it.</summary>
    public static double MaxDecodeMs(CustomSoundSlot slot) => MaxDurationMs(slot) * 1.25 + 1000;

    /// <summary>After decoding: <paramref name="seconds"/> null means the player could not decode it.</summary>
    public static CustomSoundProblem CheckDuration(CustomSoundSlot slot, double? seconds)
    {
        if (seconds is not { } s || double.IsNaN(s) || double.IsInfinity(s) || s <= 0) return CustomSoundProblem.Unreadable;
        var ms = s * 1000;
        if (ms < MinDurationMs) return CustomSoundProblem.TooShort;
        return ms > MaxDurationMs(slot) ? CustomSoundProblem.TooLong : CustomSoundProblem.None;
    }

    /// <summary>12 lowercase hex characters from a CSPRNG.</summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    /// <summary>The name shown in settings: the file name without its extension, trimmed and shortened.</summary>
    public static string DisplayName(string? fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? "").Trim();
        if (name.Length == 0) return "";
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength].TrimEnd();
    }

    /// <summary>A description that can come from saved settings: an id and extension safe to use as a file name, sane size and length.</summary>
    public static bool IsWellFormed(CustomSound sound, CustomSoundSlot slot) =>
        sound.Id.Length is >= 8 and <= 32
        && sound.Id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z')
        && Extensions.ContainsKey(sound.Extension)
        && sound.Size is > 0 and <= MaxBytes
        && sound.DurationMs >= MinDurationMs && sound.DurationMs <= MaxDurationMs(slot);

    /// <summary>The description of a file that passed every check.</summary>
    public static CustomSound Describe(string? fileName, string extension, long size, double seconds) => new()
    {
        Id = NewId(),
        Name = DisplayName(fileName),
        Extension = extension,
        ContentType = Extensions[extension],
        Size = size,
        DurationMs = (int)Math.Round(seconds * 1000),
    };
}
