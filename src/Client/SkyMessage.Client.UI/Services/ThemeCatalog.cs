using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.UI.Services;

/// <summary>An accent: a two-stop gradient (<see cref="Hex"/> → <see cref="Hex2"/>). <see cref="NameKey"/> is a localization key.</summary>
public sealed record AccentInfo(string Key, string NameKey, string Hex, string Hex2)
{
    /// <summary>CSS gradient of the accent (swatches, previews).</summary>
    public string Gradient => $"linear-gradient(135deg, {Hex} 0%, {Hex2} 100%)";
}

/// <summary>One theme's sky: base gradient, solid base color, three orb colors (CSS colors) and star visibility 0..1.</summary>
public sealed record SkyColors(string Gradient, string Base, string Orb1, string Orb2, string Orb3, double Stars)
{
    /// <summary>A still picture of this sky as one CSS background (settings tiles, crossfades).</summary>
    public string PreviewCss =>
        $"radial-gradient(70% 55% at 12% 8%, {Orb1}, transparent), radial-gradient(60% 50% at 92% 38%, {Orb2}, transparent), " +
        $"radial-gradient(75% 55% at 30% 104%, {Orb3}, transparent), {Gradient}";
}

/// <summary>
/// A sky ("wallpaper" in settings): the atmosphere behind every screen and chat — our own gradients with drifting light
/// orbs (and stars at night), drawn by <c>SkyBackdrop</c>. No tiled patterns.
/// </summary>
public sealed record WallpaperInfo(string Key, string NameKey, SkyColors Light, SkyColors Dark)
{
    public string LightCss => Light.PreviewCss;
    public string DarkCss => Dark.PreviewCss;
    public string LightBase => Light.Base;
    public string DarkBase => Dark.Base;
}

/// <summary>Everything the page needs to theme itself; persisted by JS so boot.js can apply it before Blazor starts.</summary>
public sealed record ThemeState(string Mode, string Css, bool ReduceMotion, bool Compact);

/// <summary>Accents, skies and the CSS variable builder (docs/DESIGN.md). Singleton, no JS.</summary>
public sealed partial class ThemeCatalog
{
    public const string DefaultAccentKey = "violet";
    public const string DefaultWallpaperKey = "aurora";
    public const string NoWallpaperKey = "none";

    // Light/dark neutrals; keep in sync with app.css.
    private const string LightBg = "#F6F3FF";
    private const string LightText = "#1C1634";
    private const string DarkBg = "#0D0B1E";
    private const string None = "rgba(0,0,0,0)";

    /// <summary>Deep gradient pairs: white text stays readable on both ends (outgoing bubbles, buttons).</summary>
    public IReadOnlyList<AccentInfo> Accents { get; } =
    [
        new("violet", "theme.accent.violet", "#6A4BFF", "#9A48F0"),
        new("blue", "theme.accent.blue", "#3157E8", "#1E7BC8"),
        new("teal", "theme.accent.teal", "#0B7F80", "#0B8A5F"),
        new("green", "theme.accent.green", "#1C8048", "#4F7F18"),
        new("orange", "theme.accent.orange", "#D9480F", "#C2255C"),
        new("pink", "theme.accent.pink", "#C2185B", "#9C27B0"),
        new("red", "theme.accent.red", "#C62828", "#D84315"),
        new("graphite", "theme.accent.graphite", "#3F4A5C", "#5B4F7A"),
    ];

    public IReadOnlyList<WallpaperInfo> Wallpapers { get; } =
    [
        new("aurora", "theme.wallpaper.aurora",
            new("linear-gradient(172deg, #ECE6FF 0%, #F5ECFF 40%, #FFEDF3 72%, #FFF2E8 100%)", "#EFEAFD",
                "rgba(138,116,255,0.31)", "rgba(255,145,196,0.26)", "rgba(255,190,140,0.31)", 0),
            new("linear-gradient(175deg, #191143 0%, #120D30 45%, #0A0819 100%)", "#120D30",
                "rgba(104,72,255,0.25)", "rgba(190,60,210,0.16)", "rgba(40,180,200,0.11)", 1)),
        new("dawn", "theme.wallpaper.dawn",
            new("linear-gradient(170deg, #FFF0E4 0%, #FFE6EC 48%, #F0E6FF 100%)", "#FFE9EC",
                "rgba(255,170,120,0.34)", "rgba(255,130,170,0.25)", "rgba(170,140,255,0.25)", 0),
            new("linear-gradient(172deg, #2C1626 0%, #23142E 50%, #160F2C 100%)", "#23142E",
                "rgba(255,120,90,0.19)", "rgba(200,60,130,0.19)", "rgba(110,80,255,0.19)", 0.7)),
        new("sea", "theme.wallpaper.sea",
            new("linear-gradient(175deg, #E0F2FF 0%, #E3EEFF 45%, #DDF7F0 100%)", "#E2F1FB",
                "rgba(110,190,255,0.31)", "rgba(90,220,200,0.25)", "rgba(150,160,255,0.24)", 0),
            new("linear-gradient(175deg, #0A1C36 0%, #0B2234 50%, #08221F 100%)", "#0B2034",
                "rgba(30,136,229,0.22)", "rgba(20,184,166,0.15)", "rgba(90,90,214,0.15)", 1)),
        new("mint", "theme.wallpaper.mint",
            new("linear-gradient(170deg, #E6FAF0 0%, #EAF8EE 50%, #F4F8E2 100%)", "#E8F7EE",
                "rgba(110,220,170,0.31)", "rgba(180,230,120,0.25)", "rgba(120,200,230,0.22)", 0),
            new("linear-gradient(172deg, #0C231C 0%, #0D211E 50%, #101F12 100%)", "#0D221D",
                "rgba(16,185,129,0.19)", "rgba(132,204,22,0.12)", "rgba(14,165,164,0.15)", 0.8)),
        new("dusk", "theme.wallpaper.dusk",
            new("linear-gradient(168deg, #EFE4FF 0%, #FBE4F0 52%, #FFEBD9 100%)", "#F6E6F3",
                "rgba(170,130,255,0.31)", "rgba(255,140,190,0.28)", "rgba(255,180,110,0.31)", 0),
            new("linear-gradient(170deg, #1F1238 0%, #2C1435 52%, #2E1A1A 100%)", "#27142F",
                "rgba(139,92,246,0.25)", "rgba(236,72,153,0.19)", "rgba(245,158,11,0.12)", 1)),
        new("plain", "theme.wallpaper.plain",
            new("linear-gradient(180deg, #F1EEF8 0%, #F1EEF8 100%)", "#F1EEF8", None, None, None, 0),
            new("linear-gradient(180deg, #100E1E 0%, #100E1E 100%)", "#100E1E", None, None, None, 0.5)),
        new(NoWallpaperKey, "theme.wallpaper.none",
            new($"linear-gradient(180deg, {LightBg} 0%, {LightBg} 100%)", LightBg, None, None, None, 0),
            new($"linear-gradient(180deg, {DarkBg} 0%, {DarkBg} 100%)", DarkBg, None, None, None, 0)),
    ];

    public AccentInfo DefaultAccent => Accents[0];

    public AccentInfo? FindAccent(string? key) => Accents.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));

    public WallpaperInfo FindWallpaper(string? key) =>
        Wallpapers.FirstOrDefault(w => string.Equals(w.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Wallpapers[0];

    /// <summary>Accent hex in effect: valid custom hex, else the palette key, else violet.</summary>
    public string AccentHexOf(AppearanceSettings appearance) =>
        NormalizeHex(appearance.CustomAccentHex) ?? FindAccent(appearance.Accent)?.Hex ?? DefaultAccent.Hex;

    /// <summary>Second gradient stop for an accent hex: the palette's partner color, else the hue turned a little.</summary>
    public string SecondHexOf(string accentHex)
    {
        var known = Accents.FirstOrDefault(a => string.Equals(a.Hex, accentHex, StringComparison.OrdinalIgnoreCase));
        return known?.Hex2 ?? Color.Parse(accentHex).Shift(hue: 32, saturation: 0.04, lightness: -0.02).Hex;
    }

    /// <summary>"#RRGGBB" in upper case, or null when the value is not a 6-digit hex color.</summary>
    public static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (!v.StartsWith('#')) v = "#" + v;
        return HexRegex().IsMatch(v) ? v.ToUpperInvariant() : null;
    }

    /// <summary>The theme state for <paramref name="appearance"/> (CSS variables for both themes + flags).</summary>
    public ThemeState BuildState(AppearanceSettings appearance)
    {
        var hex = AccentHexOf(appearance);
        var accent = Color.Parse(hex);
        var accent2 = Color.Parse(SecondHexOf(hex));
        var wallpaper = FindWallpaper(appearance.Wallpaper);
        var fontPx = 16.0 * Math.Clamp(appearance.FontScalePercent, 85, 140) / 100.0;
        // The roundness setting (4..24, default 16) maps onto the softer shapes of this design (5..30 px, default 20).
        var radius = Math.Round(Math.Clamp(appearance.BubbleRadius, 4, 24) * 1.25);

        var css = new StringBuilder(4096);
        css.Append("html:root{");
        Var(css, "--sm-font-size", $"{fontPx.ToString("0.##", CultureInfo.InvariantCulture)}px");
        Var(css, "--sm-bubble-radius", $"{radius.ToString(CultureInfo.InvariantCulture)}px");
        css.Append('}');

        css.Append("html:root[data-theme=\"light\"]{");
        AppendAccent(css, accent, accent2, dark: false);
        AppendSky(css, wallpaper.Light);
        css.Append('}');

        css.Append("html:root[data-theme=\"dark\"]{");
        AppendAccent(css, accent, accent2, dark: true);
        AppendSky(css, wallpaper.Dark);
        css.Append('}');

        var mode = appearance.Theme switch
        {
            ThemeMode.Light => "light",
            ThemeMode.Dark => "dark",
            _ => "system",
        };
        return new ThemeState(mode, css.ToString(), !appearance.Animations, appearance.CompactChatList);
    }

    /// <summary>Still picture of a sky for previews (settings tiles).</summary>
    public string WallpaperCss(string key, bool dark)
    {
        var w = FindWallpaper(key);
        return dark ? w.DarkCss : w.LightCss;
    }

    /// <summary>Outgoing bubble color for an accent (a solid midpoint of its gradient).</summary>
    public string BubbleOutColor(string accentHex, bool dark)
    {
        var hex = NormalizeHex(accentHex) ?? DefaultAccent.Hex;
        var mid = Color.Mix(Color.Parse(hex), Color.Parse(SecondHexOf(hex)), 0.5);
        return (dark ? Color.Mix(mid, Color.Parse(DarkBg), 0.12) : mid).Hex;
    }

    /// <summary>Outgoing bubble background (the accent gradient) for an accent, as used in chats.</summary>
    public string BubbleOutCss(string accentHex, bool dark)
    {
        var hex = NormalizeHex(accentHex) ?? DefaultAccent.Hex;
        var (from, to) = BubbleStops(Color.Parse(hex), Color.Parse(SecondHexOf(hex)), dark);
        return $"linear-gradient(135deg, {from.Hex} 0%, {to.Hex} 100%)";
    }

    /// <summary>Hex background behind the system bars for the given theme (the top of the default sky).</summary>
    public static string SystemBarColor(bool dark) => dark ? "#150F38" : "#ECE6FF";

    private static (Color From, Color To) BubbleStops(Color accent, Color accent2, bool dark) =>
        dark ? (Color.Mix(accent, Color.Parse(DarkBg), 0.1), Color.Mix(accent2, Color.Parse(DarkBg), 0.14)) : (accent, accent2);

    private static void AppendAccent(StringBuilder css, Color accent, Color accent2, bool dark)
    {
        var (from, to) = BubbleStops(accent, accent2, dark);
        var gradient = $"linear-gradient(135deg, {from.Hex} 0%, {to.Hex} 100%)";
        // Text on the gradient: white unless the custom accent is very light.
        var onAccent = Color.Mix(from, to, 0.5).Luminance > 0.42 ? Color.Parse(LightText) : Color.White;
        var onAccentIsWhite = onAccent == Color.White;
        Var(css, "--sm-grad", gradient);
        Var(css, "--sm-bubble-out-bg", gradient);
        Var(css, "--sm-bubble-out", Color.Mix(from, to, 0.5).Hex);
        Var(css, "--sm-on-accent", onAccent.Hex);
        Var(css, "--sm-bubble-out-text", onAccent.Hex);
        Var(css, "--sm-bubble-out-meta", onAccentIsWhite ? "rgba(255,255,255,0.72)" : "rgba(28,22,52,0.62)");
        if (!dark)
        {
            Var(css, "--sm-accent", accent.Hex);
            Var(css, "--sm-accent-2", accent2.Hex);
            Var(css, "--sm-accent-hover", Color.Mix(accent, Color.Black, 0.1).Hex);
            Var(css, "--sm-accent-soft", accent.Rgba(0.11));
            Var(css, "--sm-link", (accent.Luminance > 0.3 ? Color.Mix(accent, Color.Black, 0.25) : accent).Hex);
            Var(css, "--sm-selection", accent.Rgba(0.22));
            Var(css, "--sm-grad-soft", $"linear-gradient(135deg, {accent.Rgba(0.14)} 0%, {accent2.Rgba(0.1)} 100%)");
            Var(css, "--sm-glow", $"0 6px 16px -8px {accent.Rgba(0.45)}");
            Var(css, "--sm-glow-s", $"0 4px 10px -6px {accent.Rgba(0.4)}");
        }
        else
        {
            var light = Color.Mix(accent, Color.White, accent.Luminance < 0.12 ? 0.34 : 0.22);
            var light2 = Color.Mix(accent2, Color.White, 0.24);
            Var(css, "--sm-accent", light.Hex);
            Var(css, "--sm-accent-2", light2.Hex);
            Var(css, "--sm-accent-hover", Color.Mix(light, Color.White, 0.12).Hex);
            Var(css, "--sm-accent-soft", light.Rgba(0.16));
            Var(css, "--sm-link", Color.Mix(light, Color.White, 0.25).Hex);
            Var(css, "--sm-selection", light.Rgba(0.32));
            Var(css, "--sm-grad-soft", $"linear-gradient(135deg, {light.Rgba(0.2)} 0%, {light2.Rgba(0.14)} 100%)");
            Var(css, "--sm-glow", "0 6px 16px -8px rgba(0,0,0,0.55)");
            Var(css, "--sm-glow-s", "0 4px 10px -6px rgba(0,0,0,0.5)");
        }
    }

    private static void AppendSky(StringBuilder css, SkyColors sky)
    {
        Var(css, "--sm-sky", sky.Gradient);
        Var(css, "--sm-orb-1", sky.Orb1);
        Var(css, "--sm-orb-2", sky.Orb2);
        Var(css, "--sm-orb-3", sky.Orb3);
        Var(css, "--sm-stars", sky.Stars.ToString("0.##", CultureInfo.InvariantCulture));
        Var(css, "--sm-chat-bg", sky.Base);
        Var(css, "--sm-chat-wallpaper", sky.Gradient);
    }

    private static void Var(StringBuilder css, string name, string value) => css.Append(name).Append(':').Append(value).Append(';');

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexRegex();

    /// <summary>Small sRGB helper (mixing, luminance) — enough for deriving theme tokens.</summary>
    public readonly record struct Color(double R, double G, double B)
    {
        public static Color White => new(255, 255, 255);
        public static Color Black => new(0, 0, 0);

        public static Color Parse(string hex)
        {
            var v = hex.TrimStart('#');
            if (v.Length != 6) return new Color(106, 75, 255);
            return new Color(
                int.Parse(v.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(v.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(v.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        /// <summary>Linear blend; <paramref name="amount"/> is the weight of <paramref name="b"/>.</summary>
        public static Color Mix(Color a, Color b, double amount) =>
            new(a.R + (b.R - a.R) * amount, a.G + (b.G - a.G) * amount, a.B + (b.B - a.B) * amount);

        public string Hex => $"#{Clamp(R):X2}{Clamp(G):X2}{Clamp(B):X2}";

        public string Rgba(double alpha) =>
            $"rgba({Clamp(R)},{Clamp(G)},{Clamp(B)},{alpha.ToString("0.###", CultureInfo.InvariantCulture)})";

        /// <summary>The same color with its hue turned by <paramref name="hue"/> degrees and saturation/lightness nudged (0..1 units).</summary>
        public Color Shift(double hue, double saturation = 0, double lightness = 0)
        {
            double r = R / 255.0, g = G / 255.0, b = B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double h = 0, s, l = (max + min) / 2, d = max - min;
            if (d == 0) s = 0;
            else
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                h *= 60;
            }
            h = ((h + hue) % 360 + 360) % 360;
            s = Math.Clamp(s + saturation, 0, 1);
            l = Math.Clamp(l + lightness, 0, 1);
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            return new Color(Hue(p, q, h / 360 + 1.0 / 3) * 255, Hue(p, q, h / 360) * 255, Hue(p, q, h / 360 - 1.0 / 3) * 255);

            static double Hue(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }
        }

        /// <summary>WCAG relative luminance 0..1.</summary>
        public double Luminance => 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);

        private static double Channel(double c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
    }
}
