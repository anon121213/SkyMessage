using System.Collections.Frozen;
using System.Text.RegularExpressions;
using SkyMessage.Contracts;

namespace SkyMessage.Application.MediaFiles;

/// <summary>
/// Content-type rules: normalization, which types each <see cref="MediaKind"/> accepts, and which types must
/// never be rendered inline by a browser.
/// </summary>
public static partial class MediaContentTypes
{
    public const string OctetStream = "application/octet-stream";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";
    public const string Gif = "image/gif";
    public const string Heic = "image/heic";
    public const string Heif = "image/heif";
    public const string Mp4Video = "video/mp4";
    public const string Mp4Audio = "audio/mp4";

    private const int MaxStoredLength = 128;

    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["image/jpg"] = Jpeg,
        ["image/pjpeg"] = Jpeg,
        ["image/x-png"] = Png,
        ["image/x-webp"] = Webp,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> PhotoTypes = new[]
    {
        Jpeg, Png, Webp, Gif, Heic, Heif, "image/heic-sequence", "image/heif-sequence",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> HeifTypes = new[]
    {
        Heic, Heif, "image/heic-sequence", "image/heif-sequence",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Raster formats a browser can show in &lt;img&gt; (used when a photo has no separate thumbnail).</summary>
    private static readonly FrozenSet<string> InlineImageTypes = new[] { Jpeg, Png, Webp, Gif }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ActiveDocumentTypes = new[]
    {
        "text/html", "application/xhtml+xml", "image/svg+xml", "text/xml", "application/xml", "text/xsl",
        "application/xslt+xml", "text/javascript", "application/javascript", "application/x-javascript",
        "application/ecmascript", "text/ecmascript", "application/x-shockwave-flash", "application/vnd.wap.xhtml+xml",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> Extensions = new Dictionary<string, string>
    {
        [Jpeg] = ".jpg", [Png] = ".png", [Webp] = ".webp", [Gif] = ".gif", [Heic] = ".heic", [Heif] = ".heif",
        [Mp4Video] = ".mp4", ["video/webm"] = ".webm", ["video/quicktime"] = ".mov", ["video/x-matroska"] = ".mkv",
        [Mp4Audio] = ".m4a", ["audio/x-m4a"] = ".m4a", ["audio/mpeg"] = ".mp3", ["audio/ogg"] = ".ogg",
        ["audio/opus"] = ".opus", ["audio/webm"] = ".webm", ["audio/wav"] = ".wav", ["audio/x-wav"] = ".wav",
        ["audio/flac"] = ".flac", ["audio/aac"] = ".aac", ["application/pdf"] = ".pdf", ["text/plain"] = ".txt",
        ["application/zip"] = ".zip",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Other extensions in common use for a type (besides the conventional one in <see cref="Extensions"/>).</summary>
    private static readonly FrozenDictionary<string, string[]> ExtensionAliases = new Dictionary<string, string[]>
    {
        [Jpeg] = [".jpeg", ".jpe", ".jfif"],
        [Heic] = [".heics"],
        [Heif] = [".hif", ".heifs"],
        [Mp4Video] = [".m4v"],
        ["video/quicktime"] = [".qt"],
        [Mp4Audio] = [".mp4"],
        ["audio/mpeg"] = [".mpga"],
        ["audio/ogg"] = [".oga", ".opus"],
        ["text/plain"] = [".text", ".log"],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    [GeneratedRegex("^[a-z0-9][a-z0-9!#$&^_.+-]*/[a-z0-9][a-z0-9!#$&^_.+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MediaTypePattern();

    /// <summary>
    /// Lower-case <c>type/subtype</c> without parameters (<c>"audio/webm;codecs=opus"</c> → <c>"audio/webm"</c>),
    /// with common aliases folded. Null when empty, malformed or too long to store.
    /// </summary>
    public static string? Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var semicolon = contentType.IndexOf(';');
        var value = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();
        if (value.Length is 0 or > MaxStoredLength || !MediaTypePattern().IsMatch(value)) return null;
        return Aliases.GetValueOrDefault(value, value);
    }

    /// <summary>
    /// The content type to store for an upload of <paramref name="kind"/>, or null when that kind does not accept
    /// it (→ <see cref="ErrorCodes.MediaUnsupported"/>). Files accept anything (malformed → octet-stream).
    /// </summary>
    public static string? ResolveForUpload(MediaKind kind, string? contentType)
    {
        var normalized = Normalize(contentType);
        if (kind == MediaKind.File) return normalized ?? OctetStream;
        if (normalized is null) return null;
        return kind switch
        {
            MediaKind.Photo => PhotoTypes.Contains(normalized) ? normalized : null,
            MediaKind.Video or MediaKind.VideoNote => normalized.StartsWith("video/", StringComparison.Ordinal) ? normalized : null,
            MediaKind.Voice => normalized.StartsWith("audio/", StringComparison.Ordinal) || normalized == "video/webm" ? normalized : null,
            MediaKind.Audio => normalized.StartsWith("audio/", StringComparison.Ordinal) ? normalized : null,
            _ => null,
        };
    }

    public static bool IsHeif(string? contentType) => Normalize(contentType) is { } t && HeifTypes.Contains(t);

    public static bool IsInlineImage(string? contentType) => Normalize(contentType) is { } t && InlineImageTypes.Contains(t);

    /// <summary>
    /// Types a browser could execute or render as an active document (HTML, SVG, XML, scripts). They are only
    /// ever served as <c>application/octet-stream</c> attachments.
    /// </summary>
    public static bool IsDangerous(string? contentType)
    {
        var t = Normalize(contentType);
        if (t is null) return false;
        return ActiveDocumentTypes.Contains(t)
               || t.EndsWith("+xml", StringComparison.Ordinal)
               || t.Contains("html", StringComparison.Ordinal)
               || t.Contains("javascript", StringComparison.Ordinal)
               || t.Contains("ecmascript", StringComparison.Ordinal)
               || t.StartsWith("multipart/", StringComparison.Ordinal);
    }

    /// <summary>Conventional file extension (with the dot) for a content type, null when unknown.</summary>
    public static string? ExtensionFor(string? contentType) =>
        Normalize(contentType) is { } t ? Extensions.GetValueOrDefault(t) : null;

    /// <summary>
    /// Is <paramref name="extension"/> (with the dot, any letter case) a usual extension of <paramref name="contentType"/>:
    /// its conventional one or a common alias (".jpeg", ".m4v" …)?
    /// </summary>
    public static bool IsExtensionOf(string? contentType, string? extension)
    {
        if (string.IsNullOrEmpty(extension) || Normalize(contentType) is not { } t) return false;
        return string.Equals(Extensions.GetValueOrDefault(t), extension, StringComparison.OrdinalIgnoreCase)
               || (ExtensionAliases.TryGetValue(t, out var aliases) && aliases.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }
}
