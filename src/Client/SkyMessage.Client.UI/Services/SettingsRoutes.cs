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
    public const string Sessions = "sessions";
    public const string Notifications = "notifications";
    public const string Appearance = "appearance";
    public const string Chats = "chats";
    public const string Devices = "devices";
    public const string Data = "data";
    public const string Language = "language";
    public const string Connection = "connection";
    public const string About = "about";

    public static string PathOf(string section) => $"/settings/{section}";
}
