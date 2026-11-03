using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Kit;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.ChatList;

/// <summary>A folder icon the apps offer: its stored key (<see cref="ChatFolderDto.Icon"/>) and the Lucide glyph.</summary>
public sealed record FolderIconInfo(string Key, string Icon);

/// <summary>
/// The UI side of chat folders: the icons a folder can have, the names of the recommended folders, routes to the folder
/// settings, and the contact check the folder rules need. Folder icons are our own keys (stable in stored data), each
/// drawn with a Lucide glyph; anything else non-empty is the user's emoji.
/// </summary>
public static class FolderUi
{
    public const string DefaultIconKey = "folder";

    public static IReadOnlyList<FolderIconInfo> Icons { get; } =
    [
        new(DefaultIconKey, IconNames.FolderOpen),
        new("chats", IconNames.MessagesSquare),
        new("new", IconNames.UserRoundPlus),
        new("contacts", IconNames.ContactRound),
        new("users", IconNames.Users),
        new("channels", IconNames.Hash),
        new("unread", IconNames.MessageCircleMore),
        new("star", IconNames.Star),
        new("heart", IconNames.Heart),
        new("home", IconNames.House),
        new("work", IconNames.Laptop),
        new("bookmark", IconNames.Bookmark),
        new("book", IconNames.BookOpen),
        new("travel", IconNames.Plane),
        new("coffee", IconNames.Coffee),
        new("music", IconNames.Music),
        new("pets", IconNames.PawPrint),
        new("nature", IconNames.Leaf),
    ];

    /// <summary>The Lucide glyph of an icon key; null for an emoji. Unknown keys (from a newer app) get the plain folder.</summary>
    public static string? GlyphOf(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return IconNames.FolderOpen;
        if (!IsKey(icon)) return null;
        foreach (var info in Icons)
            if (info.Key == icon) return info.Icon;
        return IconNames.FolderOpen;
    }

    /// <summary>The user's emoji (null when the icon is one of ours).</summary>
    public static string? EmojiOf(string? icon) => string.IsNullOrEmpty(icon) || IsKey(icon) ? null : icon;

    private static bool IsKey(string icon) => icon[0] is >= 'a' and <= 'z';

    public static string NameKeyOf(ChatFolderPreset preset) => $"folders.preset.{preset.Key}";

    public static string HintKeyOf(ChatFolderPreset preset) => $"folders.preset.{preset.Key}Hint";

    /// <summary>Settings › Folders.</summary>
    public static string SettingsPath => SettingsRoutes.PathOf(SettingsRoutes.Folders);

    /// <summary>The folder editor: an existing folder, or a new one (optionally pre-filled from a recommended folder).</summary>
    public static string EditPath(Guid? folderId = null) =>
        folderId is { } id ? $"{SettingsRoutes.PathOf(SettingsRoutes.Folder)}?id={id}" : SettingsRoutes.PathOf(SettingsRoutes.Folder);

    /// <summary>
    /// Private chats: the other person is in my contacts now (the contact list once it is known, else what the chat list
    /// was told) — the type rules of folders depend on it.
    /// </summary>
    public static bool PeerIsContact(ChatSummaryDto chat, IContactStore contacts, IUserStore users)
    {
        if (chat.Type != ChatType.Private || chat.Peer is not { } peer) return false;
        if (contacts.IsLoaded) return contacts.IsContact(peer.Id);
        return (users.Get(peer.Id) ?? peer).IsContact;
    }
}

/// <summary>wwwroot/js/folders.js: folder tabs, the swipe between folders, reordering by a handle.</summary>
public static class FolderJs
{
    public const string Path = "./_content/SkyMessage.Client.UI/js/folders.js";
}
