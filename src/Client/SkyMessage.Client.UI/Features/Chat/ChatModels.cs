using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Kit;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Chat;

/// <summary>Delivery mark of one of my messages.</summary>
public enum DeliveryStatus
{
    None,
    /// <summary>Preparing, uploading or sending right now.</summary>
    Sending,
    /// <summary>Waiting for the network (resent automatically).</summary>
    Queued,
    /// <summary>Refused permanently; retry or delete.</summary>
    Failed,
    Sent,
    /// <summary>Read by the other side (or by someone in a group).</summary>
    Read,
}

/// <summary>
/// Chat-level facts every bubble needs. A new instance is made only when one of them changes, so bubbles can skip
/// re-rendering by comparing it (value equality).
/// </summary>
public sealed record ChatView(
    Guid ChatId,
    ChatType Type,
    Guid Me,
    bool CanSend,
    bool SelectionMode,
    bool AutoplayNotes,
    bool AutoplayVideos,
    int MediaVersion);

/// <summary>Grouping and status of one message row in the list.</summary>
public sealed record MessageRow(
    bool Outgoing,
    bool FirstInGroup,
    bool LastInGroup,
    bool ShowSender,
    bool ShowAvatar,
    bool AvatarColumn,
    DeliveryStatus Status);

/// <summary>
/// What a bubble may ask the chat screen to do. Cascaded once (IsFixed) by ChatPage; every callback is safe to call
/// from the renderer's dispatcher.
/// </summary>
public sealed class ChatController
{
    public Guid ChatId { get; set; }

    /// <summary>Context menu for a message at a viewport point.</summary>
    public Func<MessageItem, MenuPosition, Task> ShowMenuAsync { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>Scrolls to (loading if needed) and highlights a message by seq.</summary>
    public Func<long, Task> JumpToSeqAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Scrolls to a message known only by id (pinned-message system events).</summary>
    public Func<Guid, Task> JumpToMessageAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Opens the media viewer at an attachment of a message.</summary>
    public Func<MessageItem, int, Task> OpenMediaAsync { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>Selection mode: toggles a message.</summary>
    public Func<MessageItem, Task> ToggleSelectAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Retry / delete for a failed or queued message.</summary>
    public Func<MessageItem, Task> PendingActionsAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Stops an upload in progress (removes the pending message).</summary>
    public Func<MessageItem, Task> CancelSendAsync { get; set; } = _ => Task.CompletedTask;

    public Func<Guid, Task> OpenUserAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Call log bubble: call back with the same kind of call.</summary>
    public Func<bool, Task> CallAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>"Join" on a group-call system message.</summary>
    public Func<Guid, Task> JoinCallAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Tap on a reaction chip: add mine or take it back.</summary>
    public Func<MessageItem, string, Task> ToggleReactionAsync { get; set; } = (_, _) => Task.CompletedTask;
}

/// <summary>A file waiting in the composer's tray (picked, pasted or dropped) until the user sends it.</summary>
public sealed class StagedAttachment
{
    public Guid Id { get; } = Guid.NewGuid();
    public required PickedFile File { get; init; }
    public required MediaKind Kind { get; init; }
    public string? PreviewUrl { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationMs { get; set; }
    public bool Probing { get; set; }

    public bool IsVisual => Kind is MediaKind.Photo or MediaKind.Video;

    public static MediaKind KindOf(PickedFile file)
    {
        if (file.IsImage && !file.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase)) return MediaKind.Photo;
        if (file.IsVideo) return MediaKind.Video;
        if (file.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return MediaKind.Audio;
        return MediaKind.File;
    }
}

/// <summary>Rules for what can be done with a message in the chat UI (the server enforces them again).</summary>
public static class MessageRules
{
    public static bool IsService(MessageDto message) => message.Type is MessageType.System;

    public static bool CanReply(MessageItem item) =>
        !item.IsPending && item.Message.Seq > 0 && !item.Message.IsDeleted && item.Message.Type != MessageType.System;

    /// <summary>Reactions go on confirmed messages only (the chat must also allow sending).</summary>
    public static bool CanReact(MessageItem item) =>
        !item.IsPending && item.Message.Seq > 0 && !item.Message.IsDeleted && item.Message.Type != MessageType.System;

    public static bool CanForward(MessageItem item) =>
        !item.IsPending && item.Message.Seq > 0 && !item.Message.IsDeleted && item.Message.Type is not (MessageType.System or MessageType.Call);

    public static bool CanCopy(MessageItem item) => !string.IsNullOrWhiteSpace(item.Message.Text);

    /// <summary>
    /// Own messages within the edit window (pending ones always), except system, call and note messages and forwarded
    /// copies (the server refuses to edit those: message_not_editable).
    /// </summary>
    public static bool CanEdit(MessageItem item, Guid me, DateTimeOffset now)
    {
        var m = item.Message;
        if (m.SenderId != me || m.IsDeleted || m.Forward is not null) return false;
        if (m.Type is MessageType.System or MessageType.Call or MessageType.Voice or MessageType.VideoNote) return false;
        if (item.IsPending) return item.SendState is not MessageSendState.Sending;
        return now - m.CreatedAt < TimeSpan.FromHours(Limits.EditWindowHours);
    }

    public static bool CanPin(MessageItem item, ChatSummaryDto chat) =>
        !item.IsPending && item.Message.Seq > 0 && item.Message.Type != MessageType.System && chat.CanPinMessages();

    public static bool HasSavableMedia(MessageDto message) =>
        message.Attachments.Any(a => a.Status == MediaStatus.Ready);
}
