namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Section keys of <c>/settings/{Section}</c>, shared by every screen that links into settings (profile page
/// "Edit profile", connection banners, notification hints). The settings feature renders these sections.
/// </summary>
public static class SettingsRoutes
{
    public const string Profile = "profile";
    public const string Phone = "phone";
    public const string Privacy = "privacy";
    public const string Blocked = "blocked";
    /// <summary>The app passcode (a sub-page of privacy).</summary>
    public const string Passcode = "passcode";
    public const string Sessions = "sessions";
    public const string Notifications = "notifications";
    public const string Appearance = "appearance";
    public const string Chats = "chats";
    public const string Folders = "folders";
    /// <summary>The folder editor (a sub-page of <see cref="Folders"/>): <c>?id=</c> an existing folder, none for a new one.</summary>
    public const string Folder = "folder";
    /// <summary>Stickers and GIFs: my packs, installed packs (their order), suggestions and GIF autoplay.</summary>
    public const string Stickers = "stickers";
    /// <summary>A pack's editor (a sub-page of <see cref="Stickers"/>): <c>?id=</c> the pack.</summary>
    public const string StickerPack = "stickerpack";
    public const string Devices = "devices";
    public const string Data = "data";
    public const string Language = "language";
    public const string Connection = "connection";
    public const string About = "about";

    public static string PathOf(string section) => $"/settings/{section}";
}
