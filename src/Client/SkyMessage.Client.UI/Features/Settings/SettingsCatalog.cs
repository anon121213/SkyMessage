using SkyMessage.Client.UI.Kit;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Features.Settings;

/// <summary>A settings screen: route key (<see cref="SettingsRoutes"/>), title key, icon and the hue of its pastel tile (<see cref="SettingsTones"/>).</summary>
public sealed record SettingsSectionInfo(string Key, string TitleKey, string Icon, string Color);

/// <summary>
/// The settings menu, grouped. "blocked" and "passcode" are sub-pages of privacy, "folder" one of folders, "stickerpack"
/// one of stickers: none of them has a menu row of its own.
/// </summary>
public static class SettingsCatalog
{
    public static IReadOnlyList<SettingsSectionInfo> Account { get; } =
    [
        new(SettingsRoutes.Profile, "settings.profile.title", IconNames.UserPen, SettingsTones.Violet),
        new(SettingsRoutes.Phone, "settings.phone.title", IconNames.Smartphone, SettingsTones.Mint),
        new(SettingsRoutes.Privacy, "settings.privacy.title", IconNames.Lock, SettingsTones.Periwinkle),
        new(SettingsRoutes.Sessions, "settings.sessions.title", IconNames.MonitorSmartphone, SettingsTones.Lagoon),
    ];

    public static IReadOnlyList<SettingsSectionInfo> App { get; } =
    [
        new(SettingsRoutes.Notifications, "settings.notifications.title", IconNames.Bell, SettingsTones.Rose),
        new(SettingsRoutes.Appearance, "settings.appearance.title", IconNames.Palette, SettingsTones.Orchid),
        new(SettingsRoutes.Chats, "settings.chats.title", IconNames.MessageCircle, SettingsTones.Sky),
        new(SettingsRoutes.Folders, "folders.settings.title", IconNames.FolderOpen, SettingsTones.Mint),
        new(SettingsRoutes.Stickers, "stickers.settings.title", IconNames.Smile, SettingsTones.Periwinkle),
        new(SettingsRoutes.Devices, "settings.devices.title", IconNames.Headphones, SettingsTones.Peach),
        new(SettingsRoutes.Data, "settings.data.title", IconNames.HardDrive, SettingsTones.Gold),
        new(SettingsRoutes.Language, "settings.language.title", IconNames.Languages, SettingsTones.Lagoon),
        new(SettingsRoutes.Connection, "settings.connection.title", IconNames.Network, SettingsTones.Violet),
    ];

    public static IReadOnlyList<SettingsSectionInfo> Help { get; } =
    [
        new(SettingsRoutes.About, "settings.about.title", IconNames.Info, SettingsTones.Mauve),
    ];

    private static readonly SettingsSectionInfo BlockedInfo = new(SettingsRoutes.Blocked, "settings.blocked.title", IconNames.Ban, SettingsTones.Rose);
    private static readonly SettingsSectionInfo PasscodeInfo = new(SettingsRoutes.Passcode, "passcode.title", IconNames.Lock, SettingsTones.Violet);
    private static readonly SettingsSectionInfo FolderInfo = new(SettingsRoutes.Folder, "folders.edit.title", IconNames.FolderOpen, SettingsTones.Mint);
    private static readonly SettingsSectionInfo StickerPackInfo = new(SettingsRoutes.StickerPack, "stickers.edit.title", IconNames.Smile, SettingsTones.Periwinkle);

    public static IEnumerable<SettingsSectionInfo> All =>
        Account.Concat(App).Concat(Help).Append(BlockedInfo).Append(PasscodeInfo).Append(FolderInfo).Append(StickerPackInfo);

    /// <summary>Known section key (case-insensitive) in its canonical form, else null.</summary>
    public static string? Normalize(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(s => string.Equals(s.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))?.Key;

    /// <summary>The menu row that represents a section (sub-pages highlight their parent).</summary>
    public static string MenuKeyOf(string key) => key switch
    {
        SettingsRoutes.Blocked or SettingsRoutes.Passcode => SettingsRoutes.Privacy,
        SettingsRoutes.Folder => SettingsRoutes.Folders,
        SettingsRoutes.StickerPack => SettingsRoutes.Stickers,
        _ => key,
    };

    /// <summary>Where Back goes from a section opened by a deep link.</summary>
    public static string ParentPathOf(string key) => key switch
    {
        SettingsRoutes.Blocked or SettingsRoutes.Passcode => SettingsRoutes.PathOf(SettingsRoutes.Privacy),
        SettingsRoutes.Folder => SettingsRoutes.PathOf(SettingsRoutes.Folders),
        SettingsRoutes.StickerPack => SettingsRoutes.PathOf(SettingsRoutes.Stickers),
        _ => "/settings",
    };
}

/// <summary>Cascaded by SettingsPage: sections rendered next to the menu (wide screens) hide their back button.</summary>
public sealed record SettingsContext(string Key, bool InSplit);
