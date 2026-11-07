using System.Globalization;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Profile;

/// <summary>
/// How the profile style keys (<see cref="ProfileStyle"/>) look: profile colors are the app's accent pairs
/// (<see cref="ThemeCatalog.Accents"/>), covers are calm pieces of sky in both themes (docs/DESIGN.md: no neon, no
/// rainbow). Keys this app does not know fall back to the default look.
/// </summary>
public static class ProfileLook
{
    private static readonly ThemeCatalog Catalog = new();

    private sealed record Cover(string Light, string Dark);

    // Soft, low-saturation skies: two or three stops, the lower edge a little lighter so the avatar sits well on it.
    private static readonly Dictionary<string, Cover> Covers = new(StringComparer.Ordinal)
    {
        ["dawn"] = new("linear-gradient(165deg, #FFD7C2 0%, #F7C6D9 48%, #D8CCFF 100%)",
            "linear-gradient(165deg, #5B3140 0%, #47284F 52%, #2B2452 100%)"),
        ["day"] = new("linear-gradient(175deg, #A9D7FF 0%, #CFE6FF 52%, #EEF2FF 100%)",
            "linear-gradient(175deg, #173C68 0%, #1C2F59 55%, #1B1F44 100%)"),
        ["sunset"] = new("linear-gradient(170deg, #FFBF95 0%, #FFAFA5 46%, #E6B4E4 100%)",
            "linear-gradient(170deg, #6B3528 0%, #5E2B40 52%, #3A2352 100%)"),
        ["dusk"] = new("linear-gradient(165deg, #C7B6FF 0%, #E8C2E8 55%, #FFD6C8 100%)",
            "linear-gradient(165deg, #3C2B70 0%, #4A2B60 55%, #4B2F35 100%)"),
        ["night"] = new("linear-gradient(175deg, #4B4C9C 0%, #36377C 55%, #282862 100%)",
            "linear-gradient(175deg, #221D52 0%, #16133A 58%, #0E0C26 100%)"),
        ["mint"] = new("linear-gradient(170deg, #B6EED6 0%, #D3F1E2 52%, #E9F3CF 100%)",
            "linear-gradient(170deg, #134639 0%, #143B36 55%, #203B23 100%)"),
    };

    /// <summary>The accent pair of a profile color key; null for none or an unknown key.</summary>
    public static AccentInfo? Accent(string? key) => ProfileStyle.IsColor(key) ? Catalog.FindAccent(key) : null;

    /// <summary>A person's name color in groups: their profile color when they picked one, else their avatar's.</summary>
    public static string NameColor(Guid userId, string? profileColor) => Accent(profileColor)?.Hex ?? AvatarPalette.Color(userId);

    /// <summary>
    /// CSS custom properties that tint a profile (<c>--pc</c>, <c>--pc2</c>, and <c>--pc-ink</c> for text on a fill of
    /// <c>--pc</c>: the main action's orb); null for the default look. The ink is the profile color's own (every profile
    /// color is a deep accent: white), never the viewer's <c>--sm-on-accent</c>, so a profile looks the same to everyone.
    /// </summary>
    public static string? TintStyle(string? profileColor) =>
        Accent(profileColor) is { } accent ? $"--pc:{accent.Hex};--pc2:{accent.Hex2};--pc-ink:{InkOn(accent.Hex)}" : null;

    /// <summary>Text on a fill of a profile color: <see cref="ThemeCatalog.OnAccentInk"/> of that color alone.</summary>
    public static string InkOn(string hex)
    {
        var color = ThemeCatalog.Color.Parse(hex);
        return ThemeCatalog.OnAccentInk(color, color).Hex;
    }

    public static bool IsCover(string? key) => key is not null && Covers.ContainsKey(key);

    /// <summary>CSS background of a cover gradient in the given theme; null for an unknown key.</summary>
    public static string? CoverCss(string? key, bool dark) =>
        key is not null && Covers.TryGetValue(key, out var cover) ? (dark ? cover.Dark : cover.Light) : null;

    public static string CoverNameKey(string key) => "customize.cover.gradient." + key;

    /// <summary>"9 мая" / "9 мая 1990" (birthday display; February 29 works without a year).</summary>
    public static string BirthdayText(FormatService format, BirthdayDto birthday)
    {
        var date = new DateTime(birthday.Year ?? 2000, birthday.Month, birthday.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return birthday.Year is null ? format.DayMonthLong(date) : format.DayMonthYear(date);
    }

    /// <summary>The month names for pickers ("Январь"…), from the app's own date strings.</summary>
    public static string MonthName(ILocalizer l, int month) => l[$"date.month.long.{month.ToString(CultureInfo.InvariantCulture)}"];
}
