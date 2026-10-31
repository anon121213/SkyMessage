using SkyMessage.Client.UI.Kit;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Media;

/// <summary>A photo or video shown in the media viewer, with the message it belongs to (caption, sender, date, forward).</summary>
public sealed record MediaViewerItem(MediaDto Media, MessageDto Message)
{
    public bool IsVideo => Media.Kind is MediaKind.Video or MediaKind.VideoNote
                           || Media.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Small media facts shared by the chat and the shared-media page.</summary>
public static class MediaFacts
{
    /// <summary>Photos and videos open in the viewer; everything else is a file.</summary>
    public static bool IsVisual(MediaDto media) =>
        media.Kind is MediaKind.Photo or MediaKind.Video
        || (media.Kind == MediaKind.File && IsViewableImage(media.ContentType));

    public static bool IsViewableImage(string? contentType) =>
        contentType is not null && contentType.ToLowerInvariant() is "image/jpeg" or "image/png" or "image/webp" or "image/gif";

    /// <summary>Lucide icon for a file card.</summary>
    public static string FileIcon(string? fileName, string? contentType)
    {
        var type = (contentType ?? "").ToLowerInvariant();
        var extension = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        if (type.StartsWith("image/", StringComparison.Ordinal)) return IconNames.FileImage;
        if (type.StartsWith("video/", StringComparison.Ordinal)) return IconNames.FileVideo;
        if (type.StartsWith("audio/", StringComparison.Ordinal)) return IconNames.FileAudio;
        return extension switch
        {
            "zip" or "rar" or "7z" or "tar" or "gz" or "tgz" or "bz2" or "xz" => IconNames.FileArchive,
            "pdf" or "doc" or "docx" or "odt" or "rtf" or "txt" or "md" or "xls" or "xlsx" or "ods" or "csv" or "ppt" or "pptx" or "odp" or "epub" => IconNames.FileText,
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "heic" or "heif" or "bmp" or "tiff" => IconNames.FileImage,
            "mp4" or "mov" or "mkv" or "avi" or "webm" or "m4v" or "3gp" => IconNames.FileVideo,
            "mp3" or "m4a" or "aac" or "ogg" or "opus" or "wav" or "flac" => IconNames.FileAudio,
            _ => IconNames.File,
        };
    }

    /// <summary>Short upper-case extension for the file badge ("PDF"), empty when unknown.</summary>
    public static string Extension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? "").TrimStart('.');
        return extension.Length is > 0 and <= 5 ? extension.ToUpperInvariant() : "";
    }

    /// <summary>Waveform reduced to <paramref name="bars"/> peaks in 0..1 (flat when unknown).</summary>
    public static double[] Bars(byte[]? waveform, int bars)
    {
        var result = new double[bars];
        if (waveform is not { Length: > 0 })
        {
            Array.Fill(result, 0.12);
            return result;
        }
        var max = Math.Max((byte)1, waveform.Max());
        for (var i = 0; i < bars; i++)
        {
            var from = i * waveform.Length / bars;
            var to = Math.Max(from + 1, (i + 1) * waveform.Length / bars);
            byte peak = 0;
            for (var j = from; j < to && j < waveform.Length; j++) peak = Math.Max(peak, waveform[j]);
            result[i] = Math.Clamp(peak / (double)max, 0.08, 1);
        }
        return result;
    }

    /// <summary>"photo.jpg" for unnamed media (saving, file cards).</summary>
    public static string DisplayName(MediaDto media) =>
        string.IsNullOrWhiteSpace(media.FileName) ? media.Kind.ToString().ToLowerInvariant() : media.FileName;
}
