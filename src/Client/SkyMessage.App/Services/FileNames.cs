using System.Text;

namespace SkyMessage.App.Services;

internal static class FileNames
{
    private static readonly char[] Invalid = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>A safe single-segment file name (keeps the extension, never empty, ≤ 120 chars).</summary>
    public static string Sanitize(string? fileName, string fallback = "file")
    {
        var name = Path.GetFileName(fileName ?? "");
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsControl(c) || Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
        name = sb.ToString().Trim().Trim('.');
        if (name.Length == 0) name = fallback;
        if (name.Length > 120)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 16) ext = "";
            name = name[..(120 - ext.Length)] + ext;
        }
        return name;
    }

    public static string WithExtension(string fileName, string extension)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "file";
        return baseName + extension;
    }

    public static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/heic" => ".heic",
        "image/heif" => ".heif",
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        "video/webm" => ".webm",
        "video/3gpp" => ".3gp",
        "audio/mp4" or "audio/aac" or "audio/x-m4a" => ".m4a",
        "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/webm" => ".webm",
        "audio/wav" or "audio/x-wav" => ".wav",
        "application/pdf" => ".pdf",
        "text/plain" => ".txt",
        _ => "",
    };

    public static string ContentTypeFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".3gp" => "video/3gpp",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".mp3" => "audio/mpeg",
            ".ogg" or ".oga" or ".opus" => "audio/ogg",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }

    /// <summary>"name.ext" → "name (2).ext" until <paramref name="exists"/> is false.</summary>
    public static string Unique(string fileName, Func<string, bool> exists)
    {
        if (!exists(fileName)) return fileName;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{baseName} ({i}){ext}";
            if (!exists(candidate)) return candidate;
        }
        return $"{baseName}-{Guid.NewGuid():N}{ext}";
    }
}
