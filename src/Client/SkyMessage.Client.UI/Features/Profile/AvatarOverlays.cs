using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Profile;

/// <summary>Whose photos the avatar viewer shows: a person's (<see cref="UserId"/>) or a group's (<see cref="ChatId"/>).</summary>
/// <param name="CurrentMediaId">The photo on screen now: shown at once, while the history loads.</param>
/// <param name="CanManage">My own photos, or a group whose info I may change: "set as main" and delete.</param>
public sealed record AvatarSubject(Guid? UserId, Guid? ChatId, string Title, Guid? CurrentMediaId, bool CanManage);

/// <summary>An open crop editor and who waits for its result (JPEG bytes, null when cancelled).</summary>
/// <param name="Sticker">A sticker's crop: a plain square and a transparent PNG instead of the avatar's squircle and JPEG.</param>
internal sealed record AvatarEditorRequest(PickedFile Source, TaskCompletionSource<byte[]?> Result, bool Sticker = false);

/// <summary>
/// The full-screen avatar viewer (history, zoom, swipes) and the crop editor, rendered once by the shell
/// (<see cref="AvatarOverlayHost"/> in MainLayout) above every page. Scoped (one per WebView / tab).
/// </summary>
public sealed class AvatarOverlays(IAuthService auth, IChatListStore chats, IUserStore users, ILocalizer l)
{
    internal AvatarSubject? Viewer { get; private set; }
    /// <summary>A new viewer each time one opens (the host keys the component by it).</summary>
    internal int ViewerVersion { get; private set; }
    internal AvatarEditorRequest? Editor { get; private set; }

    public event Action? Changed;

    public bool IsViewerOpen => Viewer is not null;

    /// <summary>A person's photos, opening on the current one (nothing when no photo is visible).</summary>
    public void ViewUser(UserSummaryDto user)
    {
        var shown = users.Get(user.Id) ?? user;
        if (shown.IsDeleted || shown.AvatarMediaId is not { } current) return;
        Open(new AvatarSubject(shown.Id, null, shown.NameOf(l), current, shown.Id == auth.UserId));
    }

    /// <summary>My own photos: also from Settings while I have no current photo but earlier ones are kept (<paramref name="current"/> null).</summary>
    public void ViewMine(Guid? current)
    {
        if (auth.Me is not { } me) return;
        Open(new AvatarSubject(me.Id, null, me.DisplayName, current, true));
    }

    /// <summary>
    /// A chat's photos: the peer's for a private chat, the group's for a group (nothing for Saved Messages). A group
    /// without a photo opens on its earlier ones for who may change it (<paramref name="earlierPhotos"/>, from
    /// <see cref="ChatDetailsDto.EarlierAvatarCount"/>): they can set one again or delete them.
    /// </summary>
    public void ViewChat(ChatSummaryDto chat, int earlierPhotos = 0)
    {
        switch (chat.Type)
        {
            case ChatType.Private when chat.Peer is { } peer:
                ViewUser(peer);
                break;
            case ChatType.Group when chats.AvatarOf(chat) is { } current:
                Open(new AvatarSubject(null, chat.Id, chats.TitleOf(chat), current, chat.CanChangeInfo()));
                break;
            case ChatType.Group when earlierPhotos > 0 && chat.CanChangeInfo():
                Open(new AvatarSubject(null, chat.Id, chats.TitleOf(chat), null, CanManage: true));
                break;
        }
    }

    /// <summary>Can <see cref="ViewChat"/> show something for this chat?</summary>
    public bool HasPhoto(ChatSummaryDto chat, int earlierPhotos = 0) => chat.Type switch
    {
        ChatType.Private when chat.Peer is { } peer => (users.Get(peer.Id) ?? peer) is { IsDeleted: false, AvatarMediaId: not null },
        ChatType.Group => chats.AvatarOf(chat) is not null || (earlierPhotos > 0 && chat.CanChangeInfo()),
        _ => false,
    };

    /// <summary>Opens the crop editor over everything; completes with the framed 1024 px JPEG, or null when cancelled.</summary>
    public Task<byte[]?> EditAsync(PickedFile source)
    {
        Editor?.Result.TrySetResult(null);
        var request = new AvatarEditorRequest(source, new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously));
        Editor = request;
        Changed?.Invoke();
        return request.Result.Task;
    }

    /// <summary>
    /// The same crop editor for a sticker (Features/Stickers): a square window, transparency kept; completes with a
    /// <see cref="Limits.StickerMaxSide"/> px PNG drawn on the device, or null when cancelled.
    /// </summary>
    public Task<byte[]?> EditStickerAsync(PickedFile source)
    {
        Editor?.Result.TrySetResult(null);
        var request = new AvatarEditorRequest(source, new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously), Sticker: true);
        Editor = request;
        Changed?.Invoke();
        return request.Result.Task;
    }

    internal void CloseViewer()
    {
        if (Viewer is null) return;
        Viewer = null;
        Changed?.Invoke();
    }

    internal void CompleteEditor(AvatarEditorRequest request, byte[]? result)
    {
        if (ReferenceEquals(Editor, request))
        {
            Editor = null;
            Changed?.Invoke();
        }
        request.Result.TrySetResult(result);
    }

    private void Open(AvatarSubject subject)
    {
        Viewer = subject;
        ViewerVersion++;
        Changed?.Invoke();
    }
}
