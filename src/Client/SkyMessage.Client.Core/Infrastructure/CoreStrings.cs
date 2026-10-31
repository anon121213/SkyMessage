using System.Globalization;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>
/// Minimal ru/en texts Core needs when the UI's <see cref="IMessagePreviewFormatter"/> is not registered
/// (notifications from a host without the UI, hidden previews). Russian when the OS language is Russian or
/// unknown, English otherwise, unless the user picked a language.
/// </summary>
internal static class CoreStrings
{
    public static bool IsRussian(ISettingsService settings)
    {
        var language = settings.Current.Language;
        if (string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) return false;
        var os = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.IsNullOrEmpty(os) || os is "ru" or "iv";
    }

    public static string SavedMessages(bool ru) => ru ? "Избранное" : "Saved Messages";

    public static string NewMessage(bool ru) => ru ? "Новое сообщение" : "New message";

    public static string UnknownUser => "…";

    public static string Preview(MessageDto message, bool ru)
    {
        if (message.IsDeleted) return ru ? "Сообщение удалено" : "Message deleted";
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            var text = message.Text.ReplaceLineEndings(" ").Trim();
            var prefix = message.Type switch
            {
                MessageType.Photo => "🖼 ",
                MessageType.Video => "🎬 ",
                MessageType.Album => "🖼 ",
                MessageType.File or MessageType.Audio => "📎 ",
                _ => "",
            };
            return prefix + (text.Length > 120 ? text[..120] + "…" : text);
        }

        return message.Type switch
        {
            MessageType.Photo => ru ? "🖼 Фото" : "🖼 Photo",
            MessageType.Video => ru ? "🎬 Видео" : "🎬 Video",
            MessageType.Album => ru ? "🖼 Альбом" : "🖼 Album",
            MessageType.File => "📎 " + (message.Attachments.FirstOrDefault()?.FileName ?? (ru ? "Файл" : "File")),
            MessageType.Audio => "🎵 " + (message.Attachments.FirstOrDefault()?.FileName ?? (ru ? "Аудио" : "Audio")),
            MessageType.Voice => ru ? "🎤 Голосовое сообщение" : "🎤 Voice message",
            MessageType.VideoNote => ru ? "📹 Видеосообщение" : "📹 Video message",
            MessageType.Call => CallPreview(message, ru),
            MessageType.System => ru ? "Событие в чате" : "Chat event",
            _ => ru ? "Сообщение" : "Message",
        };
    }

    private static string CallPreview(MessageDto message, bool ru)
    {
        var call = message.Call;
        if (call is null) return ru ? "📞 Звонок" : "📞 Call";
        return call.Status switch
        {
            CallStatus.Missed => call.IsVideo ? (ru ? "📹 Пропущенный видеозвонок" : "📹 Missed video call") : (ru ? "📞 Пропущенный звонок" : "📞 Missed call"),
            CallStatus.Declined or CallStatus.Busy or CallStatus.Cancelled => ru ? "📞 Отменённый звонок" : "📞 Cancelled call",
            _ => call.IsVideo ? (ru ? "📹 Видеозвонок" : "📹 Video call") : (ru ? "📞 Звонок" : "📞 Call"),
        };
    }
}
