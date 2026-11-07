using System.Globalization;
using System.Text;
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

/// <summary>A calm tint for the other person's bubbles, by day and at night (the ink is derived from it).</summary>
public sealed record BubbleTintInfo(string Key, string NameKey, string Light, string Dark);

/// <summary>A ready-made gradient offered by the background editor; <see cref="NameKey"/> names it for screen readers.</summary>
public sealed record GradientPreset(string Key, string NameKey, IReadOnlyList<string> Colors, int Angle)
{
    public string Css => ThemeCatalog.GradientCss(Colors, Angle);
}

/// <summary>Text that sits right on a background (a "#RRGGBB" color) and the contrast it needs there.</summary>
public sealed record ScreenInk(string Color, double Min);

/// <summary>
/// The readability floor of a background the root screens' text sits on: the least veil of <see cref="Veil"/> (the
/// theme's background color) over which every one of <see cref="Inks"/> keeps its contrast (ThemeCatalog.ScreenVeil).
/// </summary>
public sealed record ScreenFloor(string Veil, IReadOnlyList<ScreenInk> Inks);

/// <summary>
/// A picture the theme shows: the app's ("sky") or the chats' default ("chat") background in the "light" or "dark"
/// theme. <see cref="Src"/> is where the page loads it from (IBackdropImageStore.PageSource), <see cref="Blur"/> 0..100.
/// <see cref="Floor"/> (the app's pictures): boot.js veils the picture at least as much as that floor needs, measured on
/// the picture itself (--sm-sky-dim-min).
/// </summary>
public sealed record ThemeImage(string Slot, string Theme, string Src, int Blur, ScreenFloor? Floor = null);

/// <summary>
/// What one theme shows beyond its CSS: <see cref="Own"/> lists the slots with a background of the user's own ("app",
/// "chat"; html[data-sky-own] hides the sky's lights over them) and <see cref="Bar"/> is the color under the system bars
/// (meta theme-color, the Android window).
/// </summary>
public sealed record ThemeScene(string Theme, string Own, string Bar);

/// <summary>
/// Everything the page needs to theme itself; persisted by JS so boot.js can apply it before Blazor starts (the pictures
/// in <see cref="Images"/> load right after).
/// </summary>
public sealed record ThemeState(string Mode, string Css, bool ReduceMotion, bool Compact, IReadOnlyList<ThemeImage> Images, IReadOnlyList<ThemeScene> Scenes)
{
    public ThemeState(string mode, string css, bool reduceMotion, bool compact) : this(mode, css, reduceMotion, compact, [], []) { }
}

/// <summary>
/// How a bubble is painted: its background (a color or a gradient), a solid color of it, the ink, the meta color (time,
/// ticks) and the color of a failed status, both solid and kept readable on every part of the fill.
/// </summary>
public sealed record BubblePaint(string Background, string Solid, string Ink, string Meta, string? Danger = null);

/// <summary>A ready-made single color of a picker; <see cref="NameKey"/> names it for screen readers.</summary>
public sealed record PaletteColor(string Hex, string NameKey);

/// <summary>
/// A still picture of a background for previews and tiles: <see cref="Css"/> over <see cref="Base"/>, star dots
/// (0..1), and for a photo the picture with its blur (0..100) and veil (0..1). <see cref="Veil"/>: the veil as a CSS
/// image instead (row by row, as the readability floor needs it behind the root screens; <see cref="Dim"/> is then its
/// strongest row).
/// </summary>
public sealed record BackdropPaint(string Css, string Base, double Stars, BackdropPhoto? Photo = null, int Blur = 0, double Dim = 0, string? Veil = null);

/// <summary>The few tokens a mock screen needs to look like one theme while the page shows the other (editor previews).</summary>
public sealed record PreviewPalette(string Text, string Text2, string Glass, string Card, string Veil, string Accent, BubblePaint Outgoing, BubblePaint Incoming);

/// <summary>Accents, skies, bubble paints, backgrounds and the CSS variable builder (docs/DESIGN.md). Singleton, no JS.</summary>
public sealed class ThemeCatalog
{
    public const string DefaultAccentKey = "violet";
    public const string DefaultWallpaperKey = "aurora";
    public const string NoWallpaperKey = "none";
    /// <summary>The app's own background (<see cref="AppearanceSettings.CustomBackground"/>) instead of a sky.</summary>
    public const string CustomWallpaperKey = BackdropRules.CustomWallpaper;

    /// <summary>WCAG AA for normal text: what the ink on every bubble keeps against every part of it.</summary>
    public const double MinTextContrast = 4.5;

    /// <summary>What the time, the ticks and a failed status on a bubble keep at least (WCAG for icons and large text).</summary>
    public const double MinMetaContrast = 3;

    /// <summary>Root screens' titles and names over the app's background (WCAG AAA for normal text).</summary>
    public const double ScreenTextContrast = 7;

    /// <summary>The most the readability floor veils a background (the dim slider's top too).</summary>
    public const double MaxScreenVeil = 0.9;

    /// <summary>
    /// How far an accent's stops may be deepened toward black so that white ink reads on my accent bubbles; an accent
    /// that needs more (a light one) gets dark ink instead.
    /// </summary>
    public const double MaxAccentDeepening = 0.2;

    // Light/dark neutrals; keep in sync with app.css.
    private const string LightBg = "#F6F3FF";
    private const string LightText = "#1C1634";
    private const string LightText2 = "#615A7C";
    private const string DarkBg = "#0D0B1E";
    private const string DarkText = "#F3F0FF";
    private const string DarkText2 = "#B4ACD5";
    private const string DarkText3 = "#7D76A2";
    private const string None = "rgba(0,0,0,0)";
    /// <summary>A failed status on a deep bubble (white ink), as released.</summary>
    private const string FailedOnDeep = "#FFD3DC";
    /// <summary>The rose a failed status starts from on a light bubble (dark ink): app.css's --sm-danger.</summary>
    private const string FailedOnLight = "#E2334D";
    /// <summary>The day's sky where it is darkest behind text (the dawn's violet light): accent text by day reads 4.5:1 on it.</summary>
    private const string LightSkyShade = "#E1D9F8";
    /// <summary>The night's sky where it is lightest behind text: accent text at night reads 4.5:1 on it.</summary>
    private const string DarkSkyLight = "#221A4A";
    /// <summary>The phone a gradient's top is judged on (width : height), for the color under the status bar.</summary>
    private const double ScreenWidth = 9;
    private const double ScreenHeight = 19.5;
    private const double ReadableStep = 0.025;

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

    /// <summary>The other person's bubbles: white/indigo (as before) and a few calm tints, light by day and deep at night.</summary>
    public IReadOnlyList<BubbleTintInfo> IncomingTints { get; } =
    [
        new(AppearanceRules.DefaultTint, "theme.tint.default", "#FFFFFF", "#231D45"),
        new("lavender", "theme.tint.lavender", "#EEE9FF", "#2E2655"),
        new("peach", "theme.tint.peach", "#FFEEE4", "#3A2434"),
        new("mint", "theme.tint.mint", "#E5F6ED", "#17332F"),
        new("sky", "theme.tint.sky", "#E5EFFF", "#1B2B4B"),
        new("sand", "theme.tint.sand", "#F7F0E1", "#332A24"),
    ];

    /// <summary>Gradients the background editor offers: soft ones for the day, deep ones for the night, one sunset.</summary>
    public IReadOnlyList<GradientPreset> BackdropGradients { get; } =
    [
        new("dawn", "theme.gradient.dawn", ["#FFE7D6", "#FFDCE8", "#E4DCFF"], 165),
        new("lilac", "theme.gradient.lilac", ["#EEE8FF", "#DED4FF"], 180),
        new("mint", "theme.gradient.mint", ["#E1F6EB", "#D4EEF4"], 160),
        new("lagoon", "theme.gradient.lagoon", ["#DCEFFF", "#E2E0FF"], 200),
        new("peach", "theme.gradient.peach", ["#FFF1E1", "#FFDACD"], 150),
        new("sunset", "theme.gradient.sunset", ["#F7A48B", "#B977CF", "#5A63D8"], 160),
        new("dusk", "theme.gradient.dusk", ["#3A1F50", "#1A1236"], 170),
        new("night", "theme.gradient.night", ["#161C40", "#0B0A1C"], 180),
        new("forest", "theme.gradient.forest", ["#12342C", "#0D1A20"], 165),
        new("evening", "theme.gradient.evening", ["#4A2030", "#22122B", "#110D24"], 170),
    ];

    /// <summary>Ready-made accents of the custom accent picker: deep, gradient-friendly colors that keep white text readable.</summary>
    public IReadOnlyList<PaletteColor> AccentPalette { get; } =
    [
        new("#6A4BFF", "theme.color.violet"), new("#8E44EC", "theme.color.purple"), new("#C2338F", "theme.color.raspberry"),
        new("#E0457B", "theme.color.pink"), new("#E5533D", "theme.color.coral"), new("#D9480F", "theme.color.orange"),
        new("#B7791F", "theme.color.amber"), new("#2F9E44", "theme.color.green"), new("#0F9488", "theme.color.teal"),
        new("#0B84C6", "theme.color.azure"), new("#3157E8", "theme.color.blue"), new("#4C5FD5", "theme.color.indigo"),
    ];

    /// <summary>Ready-made colors for my bubbles: deep ones for white text and a few light ones for dark text.</summary>
    public IReadOnlyList<PaletteColor> BubblePalette { get; } =
    [
        new("#6A4BFF", "theme.color.violet"), new("#C2338F", "theme.color.raspberry"), new("#E5533D", "theme.color.coral"),
        new("#2F9E44", "theme.color.green"), new("#0F9488", "theme.color.teal"), new("#3157E8", "theme.color.blue"),
        new("#FFD8B8", "theme.color.peach"), new("#FFE08A", "theme.color.yellow"), new("#C7EFD3", "theme.color.mint"),
        new("#CFE3FF", "theme.color.sky"), new("#E4D7FF", "theme.color.lavender"), new("#3F4A5C", "theme.color.graphite"),
    ];

    public AccentInfo DefaultAccent => Accents[0];

    public AccentInfo? FindAccent(string? key) => Accents.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));

    public WallpaperInfo FindWallpaper(string? key) =>
        Wallpapers.FirstOrDefault(w => string.Equals(w.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Wallpapers[0];

    public BubbleTintInfo FindTint(string? key) =>
        IncomingTints.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) ?? IncomingTints[0];

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
    public static string? NormalizeHex(string? value) => AppearanceRules.NormalizeHex(value);

    /// <summary>
    /// The theme state for <paramref name="appearance"/>: CSS variables for both themes, the pictures to show (their
    /// page sources from <paramref name="photoSource"/>; without one the pictures are left out) and the flags.
    /// </summary>
    public ThemeState BuildState(AppearanceSettings appearance, Func<BackdropPhoto, string>? photoSource = null)
    {
        var hex = AccentHexOf(appearance);
        var accent = Color.Parse(hex);
        var accent2 = Color.Parse(SecondHexOf(hex));
        var fontPx = 16.0 * Math.Clamp(appearance.FontScalePercent, 85, 140) / 100.0;
        // The roundness setting (4..24, default 16) maps onto the softer shapes of this design (5..30 px, default 20).
        var radius = Math.Round(Math.Clamp(appearance.BubbleRadius, 4, 24) * 1.25);
        var images = new List<ThemeImage>();
        var scenes = new List<ThemeScene>(2);

        var css = new StringBuilder(6144);
        css.Append("html:root{");
        Var(css, "--sm-font-size", $"{fontPx.ToString("0.##", CultureInfo.InvariantCulture)}px");
        Var(css, "--sm-bubble-radius", $"{radius.ToString(CultureInfo.InvariantCulture)}px");
        css.Append('}');

        foreach (var dark in new[] { false, true })
        {
            css.Append(dark ? "html:root[data-theme=\"dark\"]{" : "html:root[data-theme=\"light\"]{");
            AppendAccent(css, accent, accent2, dark);
            AppendBubbles(css, appearance, dark);
            AppendBackgrounds(css, appearance, dark, images, photoSource);
            css.Append('}');
            scenes.Add(new ThemeScene(dark ? "dark" : "light", OwnSlots(appearance, dark), SystemBars(appearance, dark).Hex));
        }

        var mode = appearance.Theme switch
        {
            ThemeMode.Light => "light",
            ThemeMode.Dark => "dark",
            _ => "system",
        };
        return new ThemeState(mode, css.ToString(), !appearance.Animations, appearance.CompactChatList, images, scenes);
    }

    /// <summary>
    /// The slots that show a gradient or a photo of the user's own in a theme ("app", "chat", space-separated): the
    /// chats count when their own background is one, or when they follow an app background of one's own.
    /// </summary>
    public static string OwnSlots(AppearanceSettings appearance, bool dark)
    {
        var app = AppBackdropOf(appearance, dark) is not null;
        var chats = BackdropRules.Normalize(appearance.ChatBackground) is { } chat ? chat.Kind != BackdropKind.Sky : app;
        return (app, chats) switch
        {
            (true, true) => "app chat",
            (true, false) => "app",
            (false, true) => "chat",
            _ => "",
        };
    }

    /// <summary>
    /// The system bars over the app's background in a theme: whether their icons are dark, and the color under them (the
    /// top of the background as the root screens show it). The readability floor keeps that color on the theme's side.
    /// </summary>
    public (bool DarkContent, string Hex) SystemBars(AppearanceSettings appearance, bool dark)
    {
        if (AppBackdropOf(appearance, dark) is not { } own) return (!dark, SystemBarColor(dark));
        var top = own.Kind == BackdropKind.Gradient && CustomColors(own.Colors) is { Count: >= 2 } colors
            ? TopOf(ScreenGradient(colors, dark).Select(Color.Parse).ToList(), own.Angle)
            : Color.Parse(dark ? DarkBg : LightBg);
        return (DarkContentOn(top), top.Hex);
    }

    /// <summary>Dark system-bar icons read better than white ones over <paramref name="background"/>.</summary>
    public static bool DarkContentOn(Color background) => Contrast(Color.Parse(LightText), background) >= Contrast(Color.White, background);

    /// <summary>The color at the top edge (its middle) of a phone screen filled with an even gradient at <paramref name="angle"/>.</summary>
    public static Color TopOf(IReadOnlyList<Color> stops, int angle)
    {
        if (stops.Count == 0) return Color.Parse(LightBg);
        if (stops.Count == 1) return stops[0];
        var rad = BackdropRules.WrapAngle(angle) * Math.PI / 180;
        // CSS: the gradient line runs through the center at the angle; the top edge's middle lies (h/2)·cos above it.
        var length = Math.Abs(ScreenWidth * Math.Sin(rad)) + Math.Abs(ScreenHeight * Math.Cos(rad));
        var t = Math.Clamp(0.5 + ScreenHeight / 2 * Math.Cos(rad) / length, 0, 1) * (stops.Count - 1);
        var index = Math.Min((int)Math.Floor(t), stops.Count - 2);
        return Rounded(Color.Mix(stops[index], stops[index + 1], t - index));
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

    /// <summary>Outgoing bubble background (the accent gradient, kept readable) for an accent, as used in chats.</summary>
    public string BubbleOutCss(string accentHex, bool dark) => AccentBubble(accentHex, OutgoingBubbleStyle.Gradient, dark).Background;

    /// <summary>
    /// My bubbles in the accent <paramref name="accentHex"/> painted as <paramref name="style"/> (Gradient or Solid). They
    /// carry white ink like every accent fill: the stops are deepened toward black where that is enough (at most
    /// <see cref="MaxAccentDeepening"/>); only an accent too light for that gets the ink that reads better.
    /// </summary>
    public BubblePaint AccentBubble(string accentHex, OutgoingBubbleStyle style, bool dark)
    {
        var hex = NormalizeHex(accentHex) ?? DefaultAccent.Hex;
        var (from, to) = BubbleStops(Color.Parse(hex), Color.Parse(SecondHexOf(hex)), dark);
        IReadOnlyList<Color> stops = style == OutgoingBubbleStyle.Solid ? [Color.Mix(from, to, 0.5)] : [from, to];
        return ReadableFor(stops, Color.White, MaxAccentDeepening) is { } deepened ? Painted(deepened, Color.White) : Bubble(stops);
    }

    /// <summary>
    /// Text on an accent fill (buttons, badges, the send orb, the dock pill; --sm-on-accent): white unless the fill's
    /// middle is very light, as released.
    /// </summary>
    public static Color OnAccentInk(Color from, Color to) => Color.Mix(from, to, 0.5).Luminance > 0.42 ? Color.Parse(LightText) : Color.White;

    /// <summary>
    /// Accent-colored text and icons straight on the theme's surfaces and sky (--sm-accent-text: links on the lock screen,
    /// the profile's actions, the chat header's call, the unread divider): <paramref name="accent"/> (at night its lighter
    /// version) moved away from the background in small steps — toward black by day, toward white at night — until it
    /// reads <see cref="MinTextContrast"/> on the sky where that is hardest; most accents stay as they are or deepen a
    /// little, a very light custom accent gets a deep shade of its hue instead of nearly vanishing.
    /// </summary>
    public static Color AccentTextInk(Color accent, bool dark)
    {
        var background = Color.Parse(dark ? DarkSkyLight : LightSkyShade);
        var away = dark ? Color.White : Color.Black;
        for (var step = 0; step * ReadableStep <= 1; step++)
        {
            var ink = Rounded(Color.Mix(accent, away, step * ReadableStep));
            if (Contrast(ink, background) >= MinTextContrast) return ink;
        }
        return away;
    }

    /// <summary>My bubbles as the appearance paints them in a theme (Gradient, Solid or my own colors).</summary>
    public BubblePaint OutgoingPaint(AppearanceSettings appearance, bool dark)
    {
        if (appearance.OutgoingBubble == OutgoingBubbleStyle.Custom && CustomColors(appearance.OutgoingColors) is { Count: > 0 } custom)
            return CustomBubble(custom, dark);
        return AccentBubble(AccentHexOf(appearance), appearance.OutgoingBubble, dark);
    }

    /// <summary>My bubbles in my own colors (one: flat, two: a gradient), toned for the night like the accents are.</summary>
    public BubblePaint CustomBubble(IReadOnlyList<string> colors, bool dark)
    {
        var stops = CustomColors(colors).Select(Color.Parse).Select(c => dark ? Color.Mix(c, Color.Parse(DarkBg), 0.1) : c).ToList();
        return stops.Count == 0 ? AccentBubble(DefaultAccent.Hex, OutgoingBubbleStyle.Gradient, dark) : Bubble(stops);
    }

    /// <summary>The other person's bubbles in a tint (the default one is exactly app.css's white/indigo).</summary>
    public BubblePaint IncomingPaint(string? tint, bool dark)
    {
        var info = FindTint(tint);
        if (info.Key == AppearanceRules.DefaultTint)
            return dark ? new("#231D45", "#231D45", DarkText, "rgba(243,240,255,0.5)") : new("#FFFFFF", "#FFFFFF", LightText, "#9A93B2");
        var (ink, stops) = Readable([Color.Parse(dark ? info.Dark : info.Light)], Color.Parse(DarkText), Color.Parse(LightText));
        var fill = stops[0].Hex;
        return new BubblePaint(fill, fill, ink.Hex, MetaOn(ink, stops, 0.5).Hex);
    }

    /// <summary>The app's background in a theme: its own (when chosen and valid), else null (a sky is in effect).</summary>
    public static Backdrop? AppBackdropOf(AppearanceSettings appearance, bool dark) =>
        string.Equals(appearance.Wallpaper, CustomWallpaperKey, StringComparison.OrdinalIgnoreCase) && appearance.CustomBackground is { } custom
            ? BackdropRules.Normalize(custom.For(dark), allowSky: false)
            : null;

    /// <summary>
    /// A still picture of the app's background in a theme (tiles, previews), as the root screens show it: a gradient of
    /// one's own under its readability floor. A photo's floor is measured on the picture (<see cref="ScreenVeil"/>).
    /// </summary>
    public BackdropPaint AppPaint(AppearanceSettings appearance, bool dark)
    {
        if (AppBackdropOf(appearance, dark) is { } own) return Paint(own, dark, screen: true);
        var sky = dark ? FindWallpaper(appearance.Wallpaper).Dark : FindWallpaper(appearance.Wallpaper).Light;
        return new BackdropPaint(sky.PreviewCss, sky.Base, sky.Stars);
    }

    /// <summary>A still picture of the chats' background in a theme: their own, else the app's.</summary>
    public BackdropPaint ChatPaint(AppearanceSettings appearance, bool dark) =>
        BackdropRules.Normalize(appearance.ChatBackground) is { } chat ? Paint(chat, dark) : AppPaint(appearance, dark);

    /// <summary>
    /// A still picture of a background in a theme. <paramref name="screen"/>: it is the app's, behind the root screens'
    /// text — a gradient is veiled as its readability floor needs (<see cref="ScreenGradient"/>).
    /// </summary>
    public BackdropPaint Paint(Backdrop backdrop, bool dark, bool screen = false)
    {
        switch (backdrop.Kind)
        {
            case BackdropKind.Gradient when CustomColors(backdrop.Colors) is { Count: >= 2 } colors:
                var shown = screen ? ScreenGradient(colors, dark) : colors;
                return new BackdropPaint(GradientCss(shown, backdrop.Angle), AverageHex(shown), 0);
            case BackdropKind.Photo when backdrop.Photo is { } photo:
                var bg = dark ? DarkBg : LightBg;
                return new BackdropPaint($"linear-gradient({bg}, {bg})", bg, 0, photo, backdrop.Blur, Level(backdrop.Dim));
            default:
                var sky = dark ? FindWallpaper(backdrop.Sky).Dark : FindWallpaper(backdrop.Sky).Light;
                return new BackdropPaint(sky.PreviewCss, sky.Base, sky.Stars);
        }
    }

    /// <summary>
    /// CSS custom properties for a chat with a background of its own (inline on the chat's root): the chat variant of
    /// SkyBackdrop and the chat screen's edges read them. <paramref name="photoUrl"/> and <paramref name="tone"/> (its
    /// average color) are the picture's once it is loaded.
    /// </summary>
    public string ChatStyle(Backdrop backdrop, bool dark, string? photoUrl, string? tone = null)
    {
        var css = new StringBuilder(512);
        foreach (var (name, value) in ChatVars(backdrop, dark))
        {
            var shown = name switch
            {
                "--sm-chat-photo" when photoUrl is not null => $"url(\"{photoUrl}\")",
                "--sm-chat-bg" when backdrop.Kind == BackdropKind.Photo && NormalizeHex(tone) is { } toneHex =>
                    Color.Mix(Color.Parse(toneHex), Color.Parse(dark ? DarkBg : LightBg), Level(backdrop.Dim)).Hex,
                _ => value,
            };
            Var(css, name, shown);
        }
        return css.ToString();
    }

    /// <summary>
    /// The color a chat's own background fades the screen's edges into (its --sm-chat-bg), which the system bars sit on
    /// in a chat: a gradient's average, a photo's tone (once measured) under its veil; null for a sky (it suits the theme)
    /// and for a photo whose tone is not known yet.
    /// </summary>
    public string? ChatBaseOf(Backdrop backdrop, bool dark, string? tone) => backdrop.Kind switch
    {
        BackdropKind.Gradient when CustomColors(backdrop.Colors) is { Count: >= 2 } colors => AverageHex(colors),
        BackdropKind.Photo when NormalizeHex(tone) is { } toneHex =>
            Color.Mix(Color.Parse(toneHex), Color.Parse(dark ? DarkBg : LightBg), Level(backdrop.Dim)).Hex,
        _ => null,
    };

    /// <summary>Tokens for a mock screen in one theme (background editor previews show both themes side by side).</summary>
    public PreviewPalette Palette(AppearanceSettings appearance, bool dark)
    {
        var accent = Color.Parse(AccentHexOf(appearance));
        var accentInk = dark ? Color.Mix(accent, Color.White, accent.Luminance < 0.12 ? 0.34 : 0.22) : accent;
        return dark
            ? new PreviewPalette(DarkText, DarkText2, "rgba(27,22,54,0.58)", "rgba(30,25,60,0.62)", DarkBg, accentInk.Hex,
                OutgoingPaint(appearance, true), IncomingPaint(appearance.IncomingTint, true))
            : new PreviewPalette(LightText, LightText2, "rgba(255,255,255,0.62)", "rgba(255,255,255,0.78)", LightBg, accentInk.Hex,
                OutgoingPaint(appearance, false), IncomingPaint(appearance.IncomingTint, false));
    }

    /// <summary>Hex background behind the system bars for the given theme (the top of the default sky).</summary>
    public static string SystemBarColor(bool dark) => dark ? "#150F38" : "#ECE6FF";

    /// <summary>
    /// The text root screens put straight on the app's background in a theme and what it needs there: titles and names
    /// (--sm-text) 7:1, previews and footers (--sm-text-2) 4.5:1, and at night the chat rows' times and the section
    /// overlines (--sm-text-3) 3:1, as on the theme's own sky (about 4:1 there). By day --sm-text-3 is a pale grey that
    /// stays under 3:1 even on the plain background (2.7:1), so no veil could hold it there: it gets what the first two
    /// leave (2:1 or more; about 2.5:1 on the day's sky). boot.js reads the same inks with every picture.
    /// </summary>
    public static ScreenFloor ScreenFloorOf(bool dark) => dark
        ? new ScreenFloor(DarkBg,
            [new ScreenInk(DarkText, ScreenTextContrast), new ScreenInk(DarkText2, MinTextContrast), new ScreenInk(DarkText3, MinMetaContrast)])
        : new ScreenFloor(LightBg, [new ScreenInk(LightText, ScreenTextContrast), new ScreenInk(LightText2, MinTextContrast)]);

    /// <summary>
    /// The readability floor over <paramref name="colors"/> (a gradient's samples, a picture's 8×8 cells) in a theme: the
    /// least veil of the theme's background (0..<see cref="MaxScreenVeil"/>, in hundredths) under which every ink of
    /// <see cref="ScreenFloorOf"/> keeps its contrast on every one of them. boot.js does the same for pictures.
    /// </summary>
    public static double ScreenVeil(IEnumerable<Color> colors, bool dark)
    {
        var veil = Color.Parse(dark ? DarkBg : LightBg);
        var samples = colors.Select(Rounded).ToList();
        for (var step = 0; step < MaxScreenVeil * 100; step++)
        {
            var amount = step / 100.0;
            if (ReadsOnScreen(samples.Select(sample => Rounded(Color.Mix(sample, veil, amount))), dark)) return amount;
        }
        return MaxScreenVeil;
    }

    /// <summary>Rows of cells a picture is measured in (boot.js draws it down to <see cref="PictureRows"/>×8 cells).</summary>
    public const int PictureRows = 8;

    /// <summary>
    /// A picture's readability floor row by row: the least veil each band of the screen needs (its 8 cells, read left to
    /// right and top to bottom as boot.js lists them), so a light sky over dark hills keeps its sky. Empty when the
    /// cells are not a full 8×8 grid.
    /// </summary>
    public static IReadOnlyList<double> ScreenVeilRows(IReadOnlyList<Color> cells, bool dark)
    {
        if (cells.Count != PictureRows * 8) return [];
        return Enumerable.Range(0, PictureRows).Select(row => ScreenVeil(cells.Skip(row * 8).Take(8), dark)).ToList();
    }

    /// <summary>
    /// The veil over a picture as one top-to-bottom gradient of the theme's background: at least <paramref name="dim"/>,
    /// and at each row's edges at least what the rows on both sides need, so no part of a row gets less than its floor
    /// (boot.js builds the same for the page, --sm-sky-veil).
    /// </summary>
    public static string ScreenVeilCss(IReadOnlyList<double> rows, double dim, bool dark)
    {
        var veil = Color.Parse(dark ? DarkBg : LightBg);
        var stops = Enumerable.Range(0, rows.Count + 1).Select(edge =>
        {
            var need = Math.Max(edge > 0 ? rows[edge - 1] : 0, edge < rows.Count ? rows[edge] : 0);
            var at = rows.Count == 0 ? 0 : edge * 100.0 / rows.Count;
            return $"{veil.Rgba(Math.Max(dim, need))} {Num(at)}%";
        });
        return $"linear-gradient(to bottom, {string.Join(", ", stops)})";
    }

    /// <summary>Every ink of <see cref="ScreenFloorOf"/> keeps its contrast on every one of <paramref name="backgrounds"/>.</summary>
    public static bool ReadsOnScreen(IEnumerable<Color> backgrounds, bool dark)
    {
        var inks = ScreenFloorOf(dark).Inks.Select(i => (Ink: Color.Parse(i.Color), i.Min)).ToArray();
        return backgrounds.All(background => inks.All(i => Contrast(i.Ink, background) >= i.Min));
    }

    /// <summary>
    /// A gradient of one's own as the root screens show it in a theme: its stops veiled toward the theme's background as
    /// much as its readability floor needs along its whole length (none for colors that already suit the theme), checked
    /// once more on the colors as written out.
    /// </summary>
    public static IReadOnlyList<string> ScreenGradient(IReadOnlyList<string> colors, bool dark)
    {
        var stops = colors.Select(Color.Parse).ToList();
        if (stops.Count == 0) return colors;
        var amount = ScreenVeil(Samples(stops), dark);
        if (amount <= 0) return colors;
        var veil = Color.Parse(dark ? DarkBg : LightBg);
        while (true)
        {
            var shown = stops.Select(stop => Rounded(Color.Mix(stop, veil, amount))).ToList();
            if (amount >= MaxScreenVeil || ReadsOnScreen(Samples(shown), dark)) return shown.Select(c => c.Hex).ToList();
            amount = Math.Min(MaxScreenVeil, amount + 0.01);
        }
    }

    /// <summary>A CSS linear gradient of evenly spaced stops at <paramref name="angle"/> degrees.</summary>
    public static string GradientCss(IReadOnlyList<string> colors, int angle)
    {
        if (colors.Count == 0) return "none";
        if (colors.Count == 1) return $"linear-gradient({colors[0]}, {colors[0]})";
        var stops = string.Join(", ", colors.Select((c, i) => $"{c} {(i * 100 / (colors.Count - 1)).ToString(CultureInfo.InvariantCulture)}%"));
        return $"linear-gradient({BackdropRules.WrapAngle(angle).ToString(CultureInfo.InvariantCulture)}deg, {stops})";
    }

    /// <summary>The average of colors ("#RRGGBB"): a gradient's base color.</summary>
    public static string AverageHex(IReadOnlyList<string> colors)
    {
        if (colors.Count == 0) return LightBg;
        var parsed = colors.Select(Color.Parse).ToList();
        return new Color(parsed.Average(c => c.R), parsed.Average(c => c.G), parsed.Average(c => c.B)).Hex;
    }

    /// <summary>WCAG contrast ratio of two colors (1..21).</summary>
    public static double Contrast(Color a, Color b)
    {
        var (hi, lo) = a.Luminance >= b.Luminance ? (a.Luminance, b.Luminance) : (b.Luminance, a.Luminance);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// The ink for a fill of <paramref name="stops"/> (one: flat; more: an even gradient) and the fill kept readable:
    /// whichever of <paramref name="lightInk"/> and <paramref name="darkInk"/> contrasts more with the fill's least
    /// contrasting part, and when even that stays under <see cref="MinTextContrast"/>, the stops that fall short are
    /// moved away from the ink (darker under light ink, lighter under dark ink) just enough; a gradient whose middle
    /// still falls short moves a little more as a whole.
    /// </summary>
    public static (Color Ink, IReadOnlyList<Color> Stops) Readable(IReadOnlyList<Color> stops, Color lightInk, Color darkInk)
    {
        if (stops.Count == 0) return (lightInk, stops);
        // Judged on the colors as written out (whole RGB values), which is what the page paints.
        stops = stops.Select(Rounded).ToList();
        var ink = WorstContrast(lightInk, stops) >= WorstContrast(darkInk, stops) ? lightInk : darkInk;
        return (ink, ReadableFor(stops, ink) ?? stops);
    }

    /// <summary>
    /// The stops of a fill moved away from <paramref name="ink"/> (toward black under a light ink, toward white under a
    /// dark one), each just enough, and the whole fill a little more while its middle falls short, until the ink reads
    /// at <see cref="MinTextContrast"/> along all of it — never more than <paramref name="cap"/> (0..1) of the way.
    /// Null when that is not enough.
    /// </summary>
    public static IReadOnlyList<Color>? ReadableFor(IReadOnlyList<Color> stops, Color ink, double cap = 1)
    {
        if (stops.Count == 0) return stops;
        var rounded = stops.Select(Rounded).ToList();
        var away = ink.Luminance >= 0.5 ? Color.Black : Color.White;
        var most = (int)Math.Round(Math.Clamp(cap, 0, 1) / ReadableStep);
        var steps = rounded.Select(stop =>
        {
            var n = 0;
            while (n < most && Contrast(ink, Moved(stop, n)) < MinTextContrast) n++;
            return n;
        }).ToArray();
        var adjusted = rounded.Select((stop, i) => Moved(stop, steps[i])).ToList();
        while (WorstContrast(ink, adjusted) < MinTextContrast)
        {
            var moved = false;
            for (var i = 0; i < steps.Length; i++)
            {
                if (steps[i] >= most) continue;
                steps[i]++;
                moved = true;
            }
            if (!moved) return null;
            adjusted = rounded.Select((stop, i) => Moved(stop, steps[i])).ToList();
        }
        return adjusted;

        Color Moved(Color stop, int n) => n == 0 ? stop : Rounded(Color.Mix(stop, away, n * ReadableStep));
    }

    /// <summary>
    /// The meta color (time, ticks) on a fill: the ink softened toward the fill by up to <paramref name="soften"/>, as long
    /// as it keeps at least <see cref="MinMetaContrast"/> — up to <see cref="MinTextContrast"/> where the ink has room
    /// for it — on every part of the fill. One solid color.
    /// </summary>
    public static Color MetaOn(Color ink, IReadOnlyList<Color> stops, double soften)
    {
        if (stops.Count == 0) return ink;
        var target = Math.Clamp(WorstContrast(ink, stops) * 0.62, MinMetaContrast, MinTextContrast);
        var fill = Color.Mix(stops[0], stops[^1], 0.5);
        for (var amount = soften; amount > 0.001; amount -= 0.02)
        {
            var meta = Rounded(Color.Mix(ink, fill, amount));
            if (WorstContrast(meta, stops) >= target) return meta;
        }
        return Rounded(ink);
    }

    /// <summary>
    /// A failed status on a fill: the released pale rose on a deep bubble (white ink), a deep rose on a light one (dark
    /// ink), moved away from the fill until it keeps <see cref="MinMetaContrast"/> on every part of it.
    /// </summary>
    public static Color FailedOn(Color ink, IReadOnlyList<Color> stops)
    {
        var light = ink.Luminance >= 0.5;
        var start = Color.Parse(light ? FailedOnDeep : FailedOnLight);
        var away = light ? Color.White : Color.Black;
        var color = start;
        for (var step = 1; step <= 40 && stops.Count > 0 && WorstContrast(color, stops) < MinMetaContrast; step++)
            color = Rounded(Color.Mix(start, away, step * ReadableStep));
        return color;
    }

    /// <summary>The lowest contrast of <paramref name="ink"/> along a fill of evenly spaced stops (sampled in eighths).</summary>
    public static double WorstContrast(Color ink, IReadOnlyList<Color> stops) => Samples(stops).Min(c => Contrast(ink, c));

    /// <summary>Colors along an even gradient of <paramref name="stops"/>, as the page blends them (in sRGB).</summary>
    public static IEnumerable<Color> Samples(IReadOnlyList<Color> stops)
    {
        if (stops.Count == 1)
        {
            yield return stops[0];
            yield break;
        }
        for (var i = 0; i <= 8; i++)
        {
            var t = i / 8.0 * (stops.Count - 1);
            var index = Math.Min((int)Math.Floor(t), stops.Count - 2);
            yield return Color.Mix(stops[index], stops[index + 1], t - index);
        }
    }

    private static Color Rounded(Color color) => Color.Parse(color.Hex);

    private static IReadOnlyList<string> CustomColors(IReadOnlyList<string>? colors) =>
        (colors ?? []).Select(NormalizeHex).OfType<string>().ToList();

    /// <summary>A bubble of my own colors: the ink that reads better, the fill nudged until it reads (<see cref="Readable"/>).</summary>
    private static BubblePaint Bubble(IReadOnlyList<Color> stops)
    {
        var (ink, readable) = Readable(stops, Color.White, Color.Parse(LightText));
        return Painted(readable, ink);
    }

    /// <summary>A bubble of readable <paramref name="stops"/> with <paramref name="ink"/>: its background, solid, meta and failed colors.</summary>
    private static BubblePaint Painted(IReadOnlyList<Color> stops, Color ink)
    {
        var solid = Color.Mix(stops[0], stops[^1], 0.5);
        var background = stops.Count == 1
            ? stops[0].Hex
            : $"linear-gradient(135deg, {string.Join(", ", stops.Select((c, i) => $"{c.Hex} {(i * 100 / (stops.Count - 1)).ToString(CultureInfo.InvariantCulture)}%"))})";
        // As soft as the released 72 % white / 62 % dark ink, where the fill leaves room for it.
        var meta = MetaOn(ink, stops, ink == Color.White ? 0.28 : 0.38);
        return new BubblePaint(background, solid.Hex, ink.Hex, meta.Hex, FailedOn(ink, stops).Hex);
    }

    private static (Color From, Color To) BubbleStops(Color accent, Color accent2, bool dark) =>
        dark ? (Color.Mix(accent, Color.Parse(DarkBg), 0.1), Color.Mix(accent2, Color.Parse(DarkBg), 0.14)) : (accent, accent2);

    private static void AppendAccent(StringBuilder css, Color accent, Color accent2, bool dark)
    {
        var (from, to) = BubbleStops(accent, accent2, dark);
        var gradient = $"linear-gradient(135deg, {from.Hex} 0%, {to.Hex} 100%)";
        Var(css, "--sm-grad", gradient);
        Var(css, "--sm-on-accent", OnAccentInk(from, to).Hex);
        if (!dark)
        {
            Var(css, "--sm-accent", accent.Hex);
            Var(css, "--sm-accent-text", AccentTextInk(accent, dark: false).Hex);
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
            Var(css, "--sm-accent-text", AccentTextInk(light, dark: true).Hex);
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

    private void AppendBubbles(StringBuilder css, AppearanceSettings appearance, bool dark)
    {
        var mine = OutgoingPaint(appearance, dark);
        Var(css, "--sm-bubble-out-bg", mine.Background);
        Var(css, "--sm-bubble-out", mine.Solid);
        Var(css, "--sm-bubble-out-text", mine.Ink);
        Var(css, "--sm-bubble-out-meta", mine.Meta);
        if (mine.Danger is { } danger) Var(css, "--sm-bubble-out-danger", danger);
        // The default tint keeps app.css's own values.
        if (FindTint(appearance.IncomingTint).Key == AppearanceRules.DefaultTint) return;
        var theirs = IncomingPaint(appearance.IncomingTint, dark);
        Var(css, "--sm-bubble-in", theirs.Background);
        Var(css, "--sm-bubble-in-text", theirs.Ink);
        Var(css, "--sm-bubble-in-meta", theirs.Meta);
    }

    /// <summary>
    /// The sky behind the app's screens and the chats' default background. The chats follow the app unless they have a
    /// background of their own; a photo's picture itself is set by boot.js (--sm-sky-photo / --sm-chat-photo). The app's
    /// own background keeps the root screens' text readable: a gradient is veiled here as its floor needs, a picture by
    /// boot.js once it measured it (--sm-sky-dim-min, the larger of it and the user's dim wins).
    /// </summary>
    private void AppendBackgrounds(StringBuilder css, AppearanceSettings appearance, bool dark, List<ThemeImage> images, Func<BackdropPhoto, string>? photoSource)
    {
        var theme = dark ? "dark" : "light";
        if (AppBackdropOf(appearance, dark) is { } own)
        {
            var paint = Paint(own, dark, screen: true);
            var gradient = own.Kind == BackdropKind.Gradient
                ? GradientCss(ScreenGradient(own.Colors, dark), own.Angle)
                : $"linear-gradient({paint.Base}, {paint.Base})";
            Var(css, "--sm-sky", gradient);
            Var(css, "--sm-orb-1", None);
            Var(css, "--sm-orb-2", None);
            Var(css, "--sm-orb-3", None);
            Var(css, "--sm-stars", "0");
            Var(css, "--sm-clouds", "0");
            Var(css, "--sm-chat-bg", own.Kind == BackdropKind.Photo ? VeiledTone("--sm-sky-tone", paint, "--sm-sky-dim-min") : paint.Base);
            Var(css, "--sm-chat-wallpaper", gradient);
            if (own is { Kind: BackdropKind.Photo, Photo: { } photo })
            {
                Var(css, "--sm-sky-dim", Num(paint.Dim));
                if (photoSource is not null) images.Add(new ThemeImage("sky", theme, photoSource(photo), own.Blur, ScreenFloorOf(dark)));
            }
            // The lock screen keeps the theme's calm sky (the default one) over a background of one's own: the floor
            // above holds the screens' text colors only, and the lock's rose hint and accent link need the sky.
            var calm = dark ? FindWallpaper(DefaultWallpaperKey).Dark : FindWallpaper(DefaultWallpaperKey).Light;
            Var(css, "--sm-calm-sky", calm.Gradient);
            Var(css, "--sm-calm-orb-1", calm.Orb1);
            Var(css, "--sm-calm-orb-2", calm.Orb2);
            Var(css, "--sm-calm-orb-3", calm.Orb3);
            Var(css, "--sm-calm-stars", Num(calm.Stars));
            Var(css, "--sm-calm-clouds", dark ? "0" : "1");
        }
        else
        {
            var sky = dark ? FindWallpaper(appearance.Wallpaper).Dark : FindWallpaper(appearance.Wallpaper).Light;
            Var(css, "--sm-sky", sky.Gradient);
            Var(css, "--sm-orb-1", sky.Orb1);
            Var(css, "--sm-orb-2", sky.Orb2);
            Var(css, "--sm-orb-3", sky.Orb3);
            Var(css, "--sm-stars", Num(sky.Stars));
            Var(css, "--sm-chat-bg", sky.Base);
            Var(css, "--sm-chat-wallpaper", sky.Gradient);
        }

        if (BackdropRules.Normalize(appearance.ChatBackground) is not { } chat) return;
        foreach (var (name, value) in ChatVars(chat, dark)) Var(css, name, value);
        if (chat is { Kind: BackdropKind.Photo, Photo: { } chatPhoto } && photoSource is not null)
            images.Add(new ThemeImage("chat", theme, photoSource(chatPhoto), chat.Blur));
    }

    /// <summary>Every chat token for a background of the chats' own (none left to fall back to the app's).</summary>
    private IEnumerable<(string Name, string Value)> ChatVars(Backdrop backdrop, bool dark)
    {
        var paint = Paint(backdrop, dark);
        if (backdrop.Kind == BackdropKind.Sky)
        {
            var sky = dark ? FindWallpaper(backdrop.Sky).Dark : FindWallpaper(backdrop.Sky).Light;
            yield return ("--sm-chat-wallpaper", sky.Gradient);
            yield return ("--sm-chat-bg", sky.Base);
            yield return ("--sm-chat-orb-1", sky.Orb1);
            yield return ("--sm-chat-orb-2", sky.Orb2);
            yield return ("--sm-chat-orb-3", sky.Orb3);
            yield return ("--sm-chat-stars", Num(sky.Stars));
            yield return ("--sm-chat-clouds", dark ? "0" : "1");
        }
        else
        {
            yield return ("--sm-chat-wallpaper", backdrop.Kind == BackdropKind.Gradient ? GradientCss(backdrop.Colors, backdrop.Angle) : $"linear-gradient({paint.Base}, {paint.Base})");
            yield return ("--sm-chat-bg", backdrop.Kind == BackdropKind.Photo ? VeiledTone("--sm-chat-tone", paint) : paint.Base);
            yield return ("--sm-chat-orb-1", None);
            yield return ("--sm-chat-orb-2", None);
            yield return ("--sm-chat-orb-3", None);
            yield return ("--sm-chat-stars", "0");
            yield return ("--sm-chat-clouds", "0");
        }
        yield return ("--sm-chat-dim", Num(paint.Dim));
        // A plain veil of their own: the app's picture's row-by-row veil (--sm-sky-veil) is not theirs.
        yield return ("--sm-chat-veil", dark ? DarkBg : LightBg);
        yield return ("--sm-chat-photo", "none");
    }

    /// <summary>
    /// A photo's base color: its tone (measured by boot.js once the picture loaded, <paramref name="toneVar"/>) under its
    /// veil, else the theme's background. The chat screen's edges fade into it. <paramref name="floorVar"/>: the veil is
    /// at least that readability floor (set by boot.js with the picture).
    /// </summary>
    private static string VeiledTone(string toneVar, BackdropPaint paint, string? floorVar = null)
    {
        var level = floorVar is null
            ? $"{Math.Round(paint.Dim * 100).ToString(CultureInfo.InvariantCulture)}%"
            : $"calc(max({Num(paint.Dim)}, var({floorVar}, 0)) * 100%)";
        return $"color-mix(in srgb, var({toneVar}, {paint.Base}), {paint.Base} {level})";
    }

    private static double Level(int value) => Math.Clamp(value, 0, BackdropRules.MaxLevel) / (double)BackdropRules.MaxLevel;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Var(StringBuilder css, string name, string value) => css.Append(name).Append(':').Append(value).Append(';');

    /// <summary>Small sRGB helper (mixing, luminance, HSL) — enough for deriving theme tokens.</summary>
    public readonly record struct Color(double R, double G, double B)
    {
        public static Color White => new(255, 255, 255);
        public static Color Black => new(0, 0, 0);

        public static Color Parse(string hex)
        {
            var v = hex.TrimStart('#');
            if (v.Length != 6 || !v.All(Uri.IsHexDigit)) return new Color(106, 75, 255);
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

        /// <summary>Hue 0..360, saturation and lightness 0..1.</summary>
        public (double H, double S, double L) ToHsl()
        {
            double r = R / 255.0, g = G / 255.0, b = B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double h = 0, s = 0, l = (max + min) / 2, d = max - min;
            if (d > 0)
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                h *= 60;
            }
            return (h, s, l);
        }

        /// <summary>A color from hue (degrees), saturation and lightness (0..1).</summary>
        public static Color FromHsl(double h, double s, double l)
        {
            s = Math.Clamp(s, 0, 1);
            l = Math.Clamp(l, 0, 1);
            h = (h % 360 + 360) % 360 / 360;
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            return new Color(Hue(p, q, h + 1.0 / 3) * 255, Hue(p, q, h) * 255, Hue(p, q, h - 1.0 / 3) * 255);
        }

        /// <summary>The same color with its hue turned by <paramref name="hue"/> degrees and saturation/lightness nudged (0..1 units).</summary>
        public Color Shift(double hue, double saturation = 0, double lightness = 0)
        {
            var (h, s, l) = ToHsl();
            return FromHsl(h + hue, s + saturation, l + lightness);
        }

        /// <summary>WCAG relative luminance 0..1.</summary>
        public double Luminance => 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);

        private static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        private static double Channel(double c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
    }
}
