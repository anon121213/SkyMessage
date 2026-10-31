using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Rules;

/// <summary>Which attachment combinations form a message and what type that message gets (SPEC §6.3).</summary>
internal static class MessageTypeRules
{
    /// <summary>
    /// No media → Text; one photo/video → Photo/Video; several photos/videos → Album; voice and video notes
    /// only alone; files and audio together → File (Audio when all are audio). Returns null for any other
    /// combination (e.g. photos mixed with files, two voice messages, more than the attachment limit).
    /// </summary>
    public static MessageType? Infer(IReadOnlyList<MediaKind> kinds)
    {
        if (kinds.Count == 0) return MessageType.Text;
        if (kinds.Count > Limits.MaxAttachmentsPerMessage) return null;

        if (kinds.Any(k => k is MediaKind.Voice or MediaKind.VideoNote))
        {
            if (kinds.Count != 1) return null;
            return kinds[0] == MediaKind.Voice ? MessageType.Voice : MessageType.VideoNote;
        }

        if (kinds.All(k => k is MediaKind.Photo or MediaKind.Video))
        {
            if (kinds.Count > 1) return MessageType.Album;
            return kinds[0] == MediaKind.Photo ? MessageType.Photo : MessageType.Video;
        }

        if (kinds.All(k => k is MediaKind.File or MediaKind.Audio))
            return kinds.All(k => k == MediaKind.Audio) ? MessageType.Audio : MessageType.File;

        return null;
    }

    /// <summary>Kinds a sender may combine, for error messages.</summary>
    public const string CombinationHint =
        "Voice and video notes must be sent alone; photos and videos can be combined into an album; files and audio can be combined with each other.";

    public static bool IsVisualMedia(MessageType type) => type is MessageType.Photo or MessageType.Video or MessageType.Album;

    /// <summary>Message types that can be edited, forwarded, reacted to and pinned like user content.</summary>
    public static bool IsUserContent(MessageType type) => type is not (MessageType.System or MessageType.Call);
}
