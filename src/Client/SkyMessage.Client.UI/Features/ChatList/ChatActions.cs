using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Kit;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.ChatList;

/// <summary>
/// Chat-level actions with their confirmations and feedback (pin, mute, archive, read, clear, leave, delete).
/// Shared by the chat list, chat info and the chat screen menu. Scoped.
/// </summary>
public sealed class ChatActions(
    IChatListStore chats,
    IAuthService auth,
    DialogService dialogs,
    ToastService toasts,
    NavigationService navigation,
    ILocalizer localizer,
    ILogger<ChatActions> logger)
{
    public const int MuteForever = -1;

    /// <summary>Mute durations offered to the user (seconds; -1 = until turned back on).</summary>
    public IReadOnlyList<ChoiceOption<int>> MuteOptions =>
    [
        new(3600, localizer["chatlist.mute.hour"]) { Icon = IconNames.Clock },
        new(8 * 3600, localizer["chatlist.mute.hours8"]) { Icon = IconNames.Clock },
        new(24 * 3600, localizer["chatlist.mute.day"]) { Icon = IconNames.Calendar },
        new(7 * 24 * 3600, localizer["chatlist.mute.week"]) { Icon = IconNames.Calendar },
        new(MuteForever, localizer["chatlist.mute.forever"]) { Icon = IconNames.BellOff },
    ];

    public string TitleOf(ChatSummaryDto chat) =>
        chat.Type == ChatType.Saved ? localizer["common.savedMessages"] : chats.TitleOf(chat);

    public async Task TogglePinAsync(ChatSummaryDto chat)
    {
        await RunAsync(() => chats.SetPinnedAsync(chat.Id, !chat.IsPinned),
            chat.IsPinned ? localizer["chatlist.toast.unpinned"] : localizer["chatlist.toast.pinned"]);
    }

    /// <summary>Unmutes a muted chat; otherwise asks for how long to mute it.</summary>
    public async Task ToggleMuteAsync(ChatSummaryDto chat)
    {
        if (chat.IsMuted(DateTimeOffset.UtcNow))
        {
            await RunAsync(() => chats.SetMutedAsync(chat.Id, 0), localizer["chatlist.toast.unmuted"]);
            return;
        }
        var choice = await dialogs.ChooseAsync(localizer["chatlist.mute.title"], MuteOptions);
        if (choice is null) return;
        await RunAsync(() => chats.SetMutedAsync(chat.Id, choice.Value), localizer["chatlist.toast.muted"]);
    }

    public async Task ToggleArchiveAsync(ChatSummaryDto chat)
    {
        var archive = !chat.IsArchived;
        try
        {
            await chats.SetArchivedAsync(chat.Id, archive);
            toasts.Show(archive ? localizer["chatlist.toast.archived"] : localizer["chatlist.toast.unarchived"], ToastKind.Success,
                localizer["common.undo"], () => chats.SetArchivedAsync(chat.Id, !archive));
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public async Task MarkReadAsync(ChatSummaryDto chat)
    {
        var seq = Math.Max(chat.LastMessageSeq, chat.LastMessage?.Seq ?? 0);
        if (seq <= 0) return;
        await RunAsync(() => chats.MarkReadAsync(chat.Id, seq), null);
    }

    /// <summary>"Clear history" with the private-chat "also for the other person" option. True when cleared.</summary>
    public async Task<bool> ClearHistoryAsync(ChatSummaryDto chat)
    {
        var title = TitleOf(chat);
        var result = await dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = localizer["chatlist.clear.title"],
            Message = chat.Type == ChatType.Saved ? localizer["chatlist.clear.savedText"] : localizer.Format("chatlist.clear.text", title),
            ConfirmText = localizer["chatlist.clear.confirm"],
            Danger = true,
            CheckboxLabel = chat.Type == ChatType.Private && chat.Peer is { IsDeleted: false } ? localizer.Format("chatlist.alsoFor", FirstName(title)) : null,
        });
        if (!result.Confirmed) return false;
        return await RunAsync(() => chats.ClearHistoryAsync(chat.Id, result.Checked && chat.Type == ChatType.Private), localizer["chatlist.toast.cleared"]);
    }

    /// <summary>Leaves a group (the owner is warned that ownership passes on). True when left.</summary>
    public async Task<bool> LeaveAsync(ChatSummaryDto chat)
    {
        if (chat.Type != ChatType.Group) return false;
        var ok = await dialogs.ConfirmAsync(localizer.Format("chatlist.leave.title", TitleOf(chat)),
            chat.MyRole == ChatRole.Owner ? localizer["chatlist.leave.ownerText"] : localizer["chatlist.leave.text"],
            localizer["chatlist.leave.confirm"], danger: true);
        if (!ok) return false;
        var done = await RunAsync(() => chats.LeaveAsync(chat.Id), localizer["chatlist.toast.left"]);
        if (done) await navigation.LeaveChatAsync(chat.Id);
        return done;
    }

    /// <summary>
    /// Deletes a chat the way that fits its type: private (optionally for both), Saved (clears it), group
    /// (members leave; the owner may delete it for everyone). True when something was removed.
    /// </summary>
    public async Task<bool> DeleteAsync(ChatSummaryDto chat)
    {
        switch (chat.Type)
        {
            case ChatType.Saved:
                return await ClearHistoryAsync(chat);
            case ChatType.Group when chat.MyRole == ChatRole.Owner:
            {
                var choice = await dialogs.ChooseAsync(TitleOf(chat),
                [
                    new ChoiceOption<bool>(false, localizer["chatlist.leave.confirm"]) { Icon = IconNames.DoorOpen },
                    new ChoiceOption<bool>(true, localizer["chatlist.deleteGroup.confirm"]) { Icon = IconNames.Trash2, Danger = true },
                ]);
                if (choice is null) return false;
                if (!choice.Value) return await LeaveAsync(chat);
                return await DeleteGroupForEveryoneAsync(chat);
            }
            case ChatType.Group:
                return await LeaveAsync(chat);
            default:
            {
                var title = TitleOf(chat);
                var result = await dialogs.ConfirmAsync(new ConfirmOptions
                {
                    Title = localizer.Format("chatlist.delete.title", title),
                    Message = localizer["chatlist.delete.text"],
                    ConfirmText = localizer["chatlist.delete.confirm"],
                    Danger = true,
                    CheckboxLabel = chat.Peer is { IsDeleted: false } ? localizer.Format("chatlist.alsoFor", FirstName(title)) : null,
                });
                if (!result.Confirmed) return false;
                var done = await RunAsync(() => chats.DeleteAsync(chat.Id, result.Checked), localizer["chatlist.toast.deleted"]);
                if (done) await navigation.LeaveChatAsync(chat.Id);
                return done;
            }
        }
    }

    public async Task<bool> DeleteGroupForEveryoneAsync(ChatSummaryDto chat)
    {
        var ok = await dialogs.ConfirmAsync(localizer.Format("chatlist.deleteGroup.title", TitleOf(chat)), localizer["chatlist.deleteGroup.text"],
            localizer["chatlist.deleteGroup.confirm"], danger: true);
        if (!ok) return false;
        var done = await RunAsync(() => chats.DeleteAsync(chat.Id, forEveryone: true), localizer["chatlist.toast.deleted"]);
        if (done) await navigation.LeaveChatAsync(chat.Id);
        return done;
    }

    private async Task<bool> RunAsync(Func<Task> action, string? success)
    {
        try
        {
            await action();
            if (success is not null) toasts.Success(success);
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
    }

    private void Fail(Exception ex)
    {
        logger.LogInformation(ex, "Chat action failed");
        toasts.Error(ex);
    }

    private static string FirstName(string name) => FormatService.FirstName(name);

    /// <summary>The signed-in user (for "You:" prefixes and own-message checks).</summary>
    public Guid? Me => auth.UserId;
}
