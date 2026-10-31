using System.Text;
using SkyMessage.Contracts;

namespace SkyMessage.Application.MediaFiles;

/// <summary>
/// File names are untrusted display text that also ends up in Content-Disposition headers and in files saved
/// on users' devices, so they are reduced to a single safe path segment.
/// </summary>
public static class MediaFileNames
{
    private const int MaxExtensionLength = 16;
    private const string ReservedCharacters = "<>:\"/\\|?*";

    /// <summary>
    /// Last path segment only, without control and bidirectional-override characters (no "gpj.exe" spoofing),
    /// characters reserved on common file systems replaced by '_', NFC-normalized, trimmed, at most
    /// <see cref="Limits.FileNameMaxLength"/> characters with the extension preserved. Falls back to a name based on
    /// the media kind (e.g. "photo.jpg") when nothing usable remains.
    /// </summary>
    public static string Sanitize(string? fileName, MediaKind kind, string? contentType)
    {
        var name = fileName ?? "";
        var separator = name.LastIndexOfAny(['/', '\\']);
        if (separator >= 0) name = name[(separator + 1)..];

        var builder = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || IsInvisibleFormatting(rune.Value)) continue;
            if (rune.IsAscii && ReservedCharacters.Contains((char)rune.Value))
            {
                builder.Append('_');
                continue;
            }
            builder.Append(rune.ToString());
        }

        name = builder.ToString().Normalize(NormalizationForm.FormC).Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.All(c => c == '.'))
            name = DefaultBaseName(kind) + (MediaContentTypes.ExtensionFor(contentType) ?? "");
        return Truncate(name, Limits.FileNameMaxLength);
    }

    /// <summary>
    /// Replaces (or adds) the extension, e.g. after a photo was re-encoded as JPEG. Dots that do not start an
    /// extension are part of the name ("Recording 10.15.33" + ".m4a" → "Recording 10.15.33.m4a").
    /// </summary>
    public static string WithExtension(string fileName, string extension)
    {
        var dot = ExtensionDot(fileName);
        var stem = dot > 0 ? fileName[..dot] : fileName;
        if (stem.Length == 0) stem = "file";
        return Truncate(stem + extension, Limits.FileNameMaxLength);
    }

    /// <summary>
    /// The name with an extension that fits <paramref name="contentType"/>: unchanged when its extension already
    /// does (any letter case, aliases such as ".jpeg" included) or the type has no conventional extension; otherwise
    /// the extension is replaced (or added). Processing keeps photo bytes as uploaded but stores the detected format,
    /// so a JPEG named "IMG_0001.HEIC" must not be downloaded under that name.
    /// </summary>
    public static string WithExtensionFor(string fileName, string contentType)
    {
        var expected = MediaContentTypes.ExtensionFor(contentType);
        if (expected is null) return fileName;
        var dot = ExtensionDot(fileName);
        // A name that is nothing but an extension (".png") is still judged by it.
        var current = dot > 0 ? fileName[dot..] : fileName.StartsWith('.') ? fileName : "";
        return MediaContentTypes.IsExtensionOf(contentType, current) ? fileName : WithExtension(fileName, expected);
    }

    /// <summary>
    /// Index of the dot that starts the extension, or -1: a final "." followed by 1–15 ASCII letters/digits with at
    /// least one letter, not at the start of the name (".bashrc" is a name; "12.05.2026" and "v1.2 final" have none).
    /// </summary>
    private static int ExtensionDot(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0) return -1;
        var extension = fileName.AsSpan(dot + 1);
        if (extension.Length is 0 or >= MaxExtensionLength) return -1;
        var hasLetter = false;
        foreach (var c in extension)
        {
            if (!char.IsAsciiLetterOrDigit(c)) return -1;
            hasLetter |= char.IsAsciiLetter(c);
        }
        return hasLetter ? dot : -1;
    }

    private static string DefaultBaseName(MediaKind kind) => kind switch
    {
        MediaKind.Photo => "photo",
        MediaKind.Video => "video",
        MediaKind.Voice => "voice",
        MediaKind.VideoNote => "video_note",
        MediaKind.Audio => "audio",
        _ => "file",
    };

    private static bool IsInvisibleFormatting(int codePoint) => codePoint is
        0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) // bidi controls
        or 0x2028 or 0x2029 or 0xFEFF or 0xFFF9 or 0xFFFA or 0xFFFB;

    private static string Truncate(string name, int maxLength)
    {
        if (name.Length <= maxLength) return name;
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= MaxExtensionLength ? name[dot..] : "";
        var stemLength = maxLength - extension.Length;
        // Never cut a surrogate pair in half.
        if (stemLength > 0 && char.IsHighSurrogate(name[stemLength - 1])) stemLength--;
        return name[..stemLength].TrimEnd('.', ' ') + extension;
    }
}
