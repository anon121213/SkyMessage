using SkyMessage.Client.UI.Kit;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Features.Settings;

/// <summary>A settings screen: route key (<see cref="SettingsRoutes"/>), title key, icon and the hue of its pastel tile (<see cref="SettingsTones"/>).</summary>
public sealed record SettingsSectionInfo(string Key, string TitleKey, string Icon, string Color);

/// <summary>The settings menu, grouped. "blocked" is a sub-page of privacy and has no menu row of its own.</summary>
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

    public static IEnumerable<SettingsSectionInfo> All => Account.Concat(App).Concat(Help).Append(BlockedInfo);

    /// <summary>Known section key (case-insensitive) in its canonical form, else null.</summary>
    public static string? Normalize(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(s => string.Equals(s.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))?.Key;

    /// <summary>The menu row that represents a section (sub-pages highlight their parent).</summary>
    public static string MenuKeyOf(string key) => key == SettingsRoutes.Blocked ? SettingsRoutes.Privacy : key;

    /// <summary>Where Back goes from a section opened by a deep link.</summary>
    public static string ParentPathOf(string key) =>
        key == SettingsRoutes.Blocked ? SettingsRoutes.PathOf(SettingsRoutes.Privacy) : "/settings";
}

/// <summary>Cascaded by SettingsPage: sections rendered next to the menu (wide screens) hide their back button.</summary>
public sealed record SettingsContext(string Key, bool InSplit);
