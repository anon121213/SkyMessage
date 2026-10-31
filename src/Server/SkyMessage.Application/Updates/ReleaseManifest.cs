using System.Text.Json;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Updates;

/// <summary>
/// The manifest (<c>latest.json</c>) CI writes into the updates directory next to the APK it names
/// (<c>.github/workflows/release.yml</c>). camelCase JSON; unknown fields are ignored so newer pipelines can add some:
/// <code>
/// { "android": { "versionCode": 57, "versionName": "0.2.57", "file": "SkyMessage-0.2.57.apk",
///                "sha256": "…64 hex…", "size": 61234567, "notes": "…", "minVersionCode": 0,
///                "publishedAt": "2026-10-31T12:00:00Z" } }
/// </code>
/// </summary>
public sealed record ReleaseManifest
{
    public AndroidReleaseEntry? Android { get; init; }
}

/// <summary>The Android build of a <see cref="ReleaseManifest"/>; <see cref="File"/> is a file name in the updates directory.</summary>
public sealed record AndroidReleaseEntry
{
    public int VersionCode { get; init; }
    public string? VersionName { get; init; }
    public string? File { get; init; }
    public string? Sha256 { get; init; }
    public long Size { get; init; }
    public string? Notes { get; init; }
    public int MinVersionCode { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// Turns the manifest into what clients may install. Anything incomplete or unsafe is not offered at all: clients
/// would only fail the download or the checksum, and a wrong offer cannot be taken back once phones downloaded it.
/// </summary>
public static class ReleaseManifestRules
{
    public const int FileNameMaxLength = 128;
    public const int VersionNameMaxLength = 64;
    public const int NotesMaxLength = 2000;

    /// <summary>The manifest, or null when the text is not a JSON object of the expected shape.</summary>
    public static ReleaseManifest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReleaseManifest>(json, SkyJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The release clients may install, or null when the entry is missing or invalid, or its APK is absent or of
    /// another size than announced (a manifest that names a file still uploading, or a stale one).
    /// </summary>
    /// <param name="packageLength">Length of a file in the updates directory, null when it does not exist.</param>
    public static AndroidReleaseDto? ToDto(AndroidReleaseEntry? entry, Func<string, long?> packageLength)
    {
        if (entry is null || entry.VersionCode <= 0 || entry.Size <= 0) return null;
        if (!IsSafeFileName(entry.File)) return null;
        var sha256 = NormalizeSha256(entry.Sha256);
        if (sha256 is null) return null;
        if (packageLength(entry.File!) != entry.Size) return null;

        var name = entry.VersionName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > VersionNameMaxLength) name = entry.VersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var notes = entry.Notes?.Trim();
        if (string.IsNullOrEmpty(notes)) notes = null;
        else if (notes.Length > NotesMaxLength) notes = notes[..NotesMaxLength].TrimEnd() + "…";

        return new AndroidReleaseDto
        {
            VersionCode = entry.VersionCode,
            VersionName = name,
            ApkUrl = $"{ApiRoutes.Updates}/{Uri.EscapeDataString(entry.File!)}",
            Sha256 = sha256,
            Size = entry.Size,
            Notes = notes,
            // A minimum above the offered build would lock every phone out for good.
            MinVersionCode = Math.Clamp(entry.MinVersionCode, 0, entry.VersionCode),
            PublishedAt = entry.PublishedAt,
        };
    }

    /// <summary>A plain APK file name: letters, digits, '.', '-', '_' only, no leading dot, ".apk" at the end.</summary>
    public static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > FileNameMaxLength || name[0] == '.') return false;
        if (!name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || name.Length == ".apk".Length) return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return false;
        }
        return !name.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>64 hex characters in lowercase, or null when the text is not a SHA-256.</summary>
    public static string? NormalizeSha256(string? hex)
    {
        var value = hex?.Trim();
        if (value is not { Length: 64 } || !value.All(char.IsAsciiHexDigit)) return null;
        return value.ToLowerInvariant();
    }
}
