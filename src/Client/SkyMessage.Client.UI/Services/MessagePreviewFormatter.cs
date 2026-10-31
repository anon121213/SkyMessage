using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// One-line localized previews for the chat list, notifications, reply bars and search hits, plus the
/// text of system and call-log messages. Registered as Core's <see cref="IMessagePreviewFormatter"/>.
/// Store dependencies are resolved lazily because Core resolves this formatter lazily as well.
/// </summary>
public sealed class MessagePreviewFormatter(ILocalizer localizer, FormatService format, IServiceProvider services) : IMessagePreviewFormatter
{
    private const int MaxPreviewLength = 160;

    private IUserStore? _users;
    private IAuthService? _auth;

    private IUserStore? Users => _users ??= services.GetService<IUserStore>();
    private IAuthService? Auth => _auth ??= services.GetService<IAuthService>();

    public string FormatPreview(MessageDto message)
    {
        if (message.IsDeleted) return localizer["message.preview.deleted"];
        var caption = OneLine(MaskSpoilers(message.Text, message.Entities));
        switch (message.Type)
        {
            case MessageType.Text:
                return caption.Length > 0 ? caption : localizer["message.preview.empty"];
            case MessageType.Photo:
                return WithCaption("🖼", localizer["message.preview.photo"], caption);
            case MessageType.Video:
                return WithCaption("🎬", localizer["message.preview.video"], caption);
            case MessageType.Album:
                return WithCaption("🖼", AlbumLabel(message.Attachments), caption);
            case MessageType.File:
                return WithCaption("📎", FilesLabel(message.Attachments), caption);
            case MessageType.Audio:
                return WithCaption("🎵", FilesLabel(message.Attachments, stripExtension: true), caption);
            case MessageType.Voice:
                return $"🎤 {localizer["message.preview.voice"]}{DurationSuffix(message)}";
            case MessageType.VideoNote:
                return $"📹 {localizer["message.preview.videoNote"]}{DurationSuffix(message)}";
            case MessageType.System:
                return FormatSystem(message);
            case MessageType.Call:
                return FormatCall(message);
            default:
                return caption.Length > 0 ? caption : localizer["message.preview.unsupported"];
        }
    }

    public string FormatChatTitle(ChatSummaryDto chat)
    {
        switch (chat.Type)
        {
            case ChatType.Saved:
                return localizer["common.savedMessages"];
            case ChatType.Private when chat.Peer is { } embedded:
                var peer = Users?.Get(embedded.Id) ?? embedded;
                if (peer.IsDeleted) return localizer["common.deletedAccount"];
                var name = peer.ContactName ?? peer.DisplayName;
                return string.IsNullOrWhiteSpace(name) ? chat.Title : name;
            default:
                return string.IsNullOrWhiteSpace(chat.Title) ? localizer["common.untitledChat"] : chat.Title;
        }
    }

    /// <summary>Text of a system message ("Анна теперь в группе").</summary>
    public string FormatSystem(MessageDto message)
    {
        var e = message.SystemEvent;
        if (e is null) return localizer["message.system.unknown"];
        var actor = e.ActorId is { } id ? NameOf(id) : "";
        var targets = format.JoinNames(e.TargetUserIds.Select(NameOf).ToList());
        var text = e.Text ?? "";
        var key = e.Type switch
        {
            SystemEventType.ChatCreated => "message.system.chatCreated",
            SystemEventType.MembersAdded => "message.system.membersAdded",
            SystemEventType.MemberLeft => "message.system.memberLeft",
            SystemEventType.MemberRemoved => "message.system.memberRemoved",
            SystemEventType.MemberJoinedByLink => "message.system.joinedByLink",
            SystemEventType.TitleChanged => "message.system.titleChanged",
            SystemEventType.PhotoChanged => "message.system.photoChanged",
            SystemEventType.PhotoRemoved => "message.system.photoRemoved",
            SystemEventType.DescriptionChanged => "message.system.descriptionChanged",
            SystemEventType.MessagePinned => "message.system.messagePinned",
            SystemEventType.GroupCallStarted => "message.system.groupCallStarted",
            SystemEventType.GroupCallEnded => "message.system.groupCallEnded",
            _ => "message.system.unknown",
        };
        // Left/joined events name the actor when the server sends no explicit targets.
        if (targets.Length == 0 && e.Type is SystemEventType.MemberLeft or SystemEventType.MemberJoinedByLink or SystemEventType.MembersAdded)
            targets = actor;
        // Russian verbs agree with "Вы" differently ("Вы закрепили"), so actions by me have their own templates.
        if (e.ActorId is { } actorId && actorId == Auth?.UserId && localizer.Has(key + ".you")) key += ".you";
        return localizer.Format(key, actor, targets, text);
    }

    /// <summary>Call log line: "Исходящий звонок · 2 мин", "Пропущенный видеозвонок".</summary>
    public string FormatCall(MessageDto message)
    {
        var call = message.Call;
        if (call is null) return $"📞 {localizer["message.call.generic"]}";
        var me = Auth?.UserId;
        var outgoing = me is { } id && call.InitiatorId == id;
        var icon = call.IsVideo ? "📹" : "📞";
        var kind = call.IsVideo ? "video" : "audio";
        var key = call.Status switch
        {
            CallStatus.Missed when outgoing => $"message.call.{kind}.noAnswer",
            CallStatus.Missed => $"message.call.{kind}.missed",
            CallStatus.Cancelled when outgoing => $"message.call.{kind}.cancelled",
            CallStatus.Cancelled => $"message.call.{kind}.missed",
            CallStatus.Declined when outgoing => $"message.call.{kind}.declinedByPeer",
            CallStatus.Declined => $"message.call.{kind}.declined",
            CallStatus.Busy => $"message.call.{kind}.busy",
            CallStatus.Failed => $"message.call.{kind}.failed",
            CallStatus.Ringing or CallStatus.Active => $"message.call.{kind}.ongoing",
            _ when outgoing => $"message.call.{kind}.outgoing",
            _ => $"message.call.{kind}.incoming",
        };
        var line = localizer[key];
        if (call.Status == CallStatus.Ended && call.DurationSeconds > 0)
            line = $"{line} · {format.DurationWords(call.DurationSeconds)}";
        return $"{icon} {line}";
    }

    private string NameOf(Guid userId)
    {
        if (Auth?.UserId is { } me && me == userId) return localizer["common.you"];
        return Users?.DisplayNameOf(userId) ?? localizer["common.unknownUser"];
    }

    private string AlbumLabel(IReadOnlyList<MediaDto> attachments)
    {
        var count = Math.Max(attachments.Count, 1);
        if (attachments.Count > 0 && attachments.All(a => a.Kind == MediaKind.Photo)) return localizer.Plural("plural.photos", count);
        if (attachments.Count > 0 && attachments.All(a => a.Kind == MediaKind.Video)) return localizer.Plural("plural.videos", count);
        return localizer.Plural("plural.mediaFiles", count);
    }

    private string FilesLabel(IReadOnlyList<MediaDto> attachments, bool stripExtension = false)
    {
        if (attachments.Count == 0) return localizer["message.preview.file"];
        var name = attachments[0].FileName;
        if (stripExtension)
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0) name = name[..dot];
        }
        name = OneLine(name);
        if (name.Length == 0) name = localizer["message.preview.file"];
        return attachments.Count > 1
            ? localizer.Plural("plural.andMoreFiles", attachments.Count - 1, name)
            : name;
    }

    private string DurationSuffix(MessageDto message)
    {
        var ms = message.Attachments.FirstOrDefault()?.DurationMs;
        return ms is > 0 ? $" {format.DurationMs(ms)}" : "";
    }

    private static string WithCaption(string icon, string label, string caption) =>
        caption.Length > 0 ? $"{icon} {caption}" : $"{icon} {label}";

    /// <summary>Hides spoiler ranges ("▒▒▒") so previews and notifications never reveal them.</summary>
    public static string? MaskSpoilers(string? text, IReadOnlyList<MessageEntityDto>? entities)
    {
        if (string.IsNullOrEmpty(text) || entities is null || entities.Count == 0) return text;
        char[]? chars = null;
        foreach (var entity in entities)
        {
            if (entity.Type != MessageEntityType.Spoiler) continue;
            var start = Math.Clamp(entity.Offset, 0, text.Length);
            var end = Math.Clamp(entity.Offset + entity.Length, start, text.Length);
            chars ??= text.ToCharArray();
            for (var i = start; i < end; i++)
            {
                if (!char.IsWhiteSpace(chars[i])) chars[i] = '▒';
            }
        }
        return chars is null ? text : new string(chars);
    }

    /// <summary>Collapses whitespace and line breaks and truncates (previews are single-line).</summary>
    public static string OneLine(string? text, int maxLength = MaxPreviewLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
            {
                builder.Append(' ');
                space = false;
            }
            builder.Append(ch);
            if (builder.Length >= maxLength) break;
        }
        var result = builder.ToString();
        if (result.Length >= maxLength)
        {
            // Never cut a surrogate pair in half.
            if (char.IsHighSurrogate(result[^1])) result = result[..^1];
            result += "…";
        }
        return result;
    }
}
