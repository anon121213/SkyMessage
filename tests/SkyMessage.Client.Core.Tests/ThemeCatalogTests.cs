using System.Text.RegularExpressions;
using Shouldly;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.UI.Services;
using Xunit;
using Color = SkyMessage.Client.UI.Services.ThemeCatalog.Color;

namespace SkyMessage.Client.Core.Tests;

/// <summary>
/// Building the theme (ThemeCatalog): bubble ink chosen and kept readable (WCAG 4.5:1) for any colors, the palette
/// accents looking as they did, and the backgrounds — skies, gradients, photos per theme — in the CSS and the pictures
/// boot.js shows.
/// </summary>
public sealed partial class ThemeCatalogTests
{
    private static readonly ThemeCatalog Catalog = new();
    private static readonly Color DarkInk = Color.Parse("#1C1634");

    private static string Block(ThemeState state, bool dark)
    {
        var marker = dark ? "html:root[data-theme=\"dark\"]{" : "html:root[data-theme=\"light\"]{";
        var start = state.Css.IndexOf(marker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        var end = state.Css.IndexOf('}', start);
        return state.Css[(start + marker.Length)..end];
    }

    private static string? VarOf(string block, string name)
    {
        var match = Regex.Match(block, $"(?:^|;){Regex.Escape(name)}:([^;]*);");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IReadOnlyList<Color> StopsOf(string background) =>
        HexRegex().Matches(background).Select(m => Color.Parse(m.Value)).ToList();

    private static void ShouldRead(BubblePaint paint, string because)
    {
        var stops = StopsOf(paint.Background);
        stops.ShouldNotBeEmpty(because);
        ThemeCatalog.WorstContrast(Color.Parse(paint.Ink), stops).ShouldBeGreaterThanOrEqualTo(ThemeCatalog.MinTextContrast, because);
    }

    [Fact]
    public void The_palette_accents_keep_white_ink_and_their_colors()
    {
        foreach (var accent in Catalog.Accents)
        {
            foreach (var dark in new[] { false, true })
            {
                foreach (var style in new[] { OutgoingBubbleStyle.Gradient, OutgoingBubbleStyle.Solid })
                {
                    var paint = Catalog.AccentBubble(accent.Hex, style, dark);
                    paint.Ink.ShouldBe("#FFFFFF", $"{accent.Key} {style} dark={dark}");
                    ShouldRead(paint, $"{accent.Key} {style} dark={dark}");
                }
            }
        }

        // A stop that already reads well is left exactly as it was: the default violet bubble is the released one.
        Catalog.AccentBubble("#6A4BFF", OutgoingBubbleStyle.Gradient, dark: false).Background.ShouldBe("linear-gradient(135deg, #6A4BFF 0%, #9A48F0 100%)");
        var state = Catalog.BuildState(new AppearanceSettings());
        VarOf(Block(state, dark: false), "--sm-bubble-out-bg").ShouldBe("linear-gradient(135deg, #6A4BFF 0%, #9A48F0 100%)");
        VarOf(Block(state, dark: false), "--sm-bubble-out-text").ShouldBe("#FFFFFF");
        VarOf(Block(state, dark: false), "--sm-on-accent").ShouldBe("#FFFFFF");
    }

    [Fact]
    public void A_stop_that_falls_short_is_deepened_just_enough()
    {
        // The blue accent's lighter end gives white text 4.45:1.
        ThemeCatalog.Contrast(Color.White, Color.Parse("#1E7BC8")).ShouldBeLessThan(4.5);

        var paint = Catalog.AccentBubble("#3157E8", OutgoingBubbleStyle.Gradient, dark: false);
        var stops = StopsOf(paint.Background);

        stops[0].Hex.ShouldBe("#3157E8", "the end that reads well is untouched");
        stops[1].Hex.ShouldNotBe("#1E7BC8");
        ThemeCatalog.Contrast(Color.White, stops[1]).ShouldBeInRange(4.5, 5.0, "only as deep as needed");
    }

    [Theory]
    [InlineData("#FFE08A", "#1C1634")]
    [InlineData("#C7EFD3", "#1C1634")]
    [InlineData("#FFFFFF", "#1C1634")]
    [InlineData("#2E1A6B", "#FFFFFF")]
    [InlineData("#000000", "#FFFFFF")]
    [InlineData("#C2338F", "#FFFFFF")]
    public void My_own_color_gets_the_ink_that_reads_better(string color, string ink)
    {
        foreach (var dark in new[] { false, true })
        {
            var paint = Catalog.CustomBubble([color], dark);
            paint.Ink.ShouldBe(ink, $"{color} dark={dark}");
            ShouldRead(paint, $"{color} dark={dark}");
            paint.Background.ShouldNotContain("gradient", Case.Insensitive, "one color is a flat bubble");
        }
    }

    [Fact]
    public void Any_colors_end_up_readable_flat_or_as_a_gradient()
    {
        var random = new Random(20261104);
        string Next() => $"#{random.Next(0, 0x1000000):X6}";

        for (var i = 0; i < 400; i++)
        {
            var colors = i % 2 == 0 ? new[] { Next() } : new[] { Next(), Next() };
            foreach (var dark in new[] { false, true })
            {
                ShouldRead(Catalog.CustomBubble(colors, dark), $"{string.Join(" → ", colors)} dark={dark}");
            }
        }

        // Mid-tones read badly with either ink: they are moved until one reads.
        foreach (var gray in new[] { "#777777", "#7A7A7A", "#808080", "#1E7BC8", "#D9480F" })
        {
            ShouldRead(Catalog.CustomBubble([gray], dark: false), gray);
            ShouldRead(Catalog.CustomBubble([gray, "#FFFFFF"], dark: false), gray + " → white");
        }
    }

    [Fact]
    public void A_gradient_is_checked_along_its_length_not_only_at_its_ends()
    {
        // Red to green in sRGB dips darker in the middle than either end: dark ink must clear the middle too.
        var stops = new[] { Color.Parse("#FF4040"), Color.Parse("#40FF40") };
        var (ink, readable) = ThemeCatalog.Readable(stops, Color.White, DarkInk);

        ThemeCatalog.WorstContrast(ink, readable).ShouldBeGreaterThanOrEqualTo(ThemeCatalog.MinTextContrast);
        ThemeCatalog.Samples(readable).Count().ShouldBe(9);
    }

    [Fact]
    public void My_bubbles_follow_the_chosen_style()
    {
        var a = new AppearanceSettings { Accent = "teal", OutgoingBubble = OutgoingBubbleStyle.Solid };
        var solid = VarOf(Block(Catalog.BuildState(a), dark: false), "--sm-bubble-out-bg")!;
        solid.ShouldStartWith("#", customMessage: "a solid bubble is one color");

        a.OutgoingBubble = OutgoingBubbleStyle.Custom;
        a.OutgoingColors = ["#FFD8B8", "#E4D7FF"];
        var light = Block(Catalog.BuildState(a), dark: false);
        VarOf(light, "--sm-bubble-out-bg").ShouldBe("linear-gradient(135deg, #FFD8B8 0%, #E4D7FF 100%)");
        VarOf(light, "--sm-bubble-out-text").ShouldBe("#1C1634");
        VarOf(light, "--sm-bubble-out-meta").ShouldBe("rgba(28,22,52,0.62)");
        VarOf(light, "--sm-grad").ShouldBe("linear-gradient(135deg, #0B7F80 0%, #0B8A5F 100%)", "buttons keep the accent");
    }

    [Fact]
    public void The_default_tint_keeps_the_released_bubbles_and_every_tint_reads()
    {
        var state = Catalog.BuildState(new AppearanceSettings());
        VarOf(Block(state, dark: false), "--sm-bubble-in").ShouldBeNull("app.css keeps its white/indigo");
        VarOf(Block(state, dark: true), "--sm-bubble-in").ShouldBeNull();

        foreach (var tint in Catalog.IncomingTints.Skip(1))
        {
            var tinted = Catalog.BuildState(new AppearanceSettings { IncomingTint = tint.Key });
            foreach (var dark in new[] { false, true })
            {
                var block = Block(tinted, dark);
                var bubble = Color.Parse(VarOf(block, "--sm-bubble-in")!);
                var ink = Color.Parse(VarOf(block, "--sm-bubble-in-text")!);
                ThemeCatalog.Contrast(ink, bubble).ShouldBeGreaterThanOrEqualTo(ThemeCatalog.MinTextContrast, $"{tint.Key} dark={dark}");
                ink.ShouldBe(dark ? Color.Parse("#F3F0FF") : DarkInk, $"tints stay light by day and deep at night ({tint.Key})");
            }
        }
    }

    [Fact]
    public void A_sky_paints_the_app_and_the_chats_as_before()
    {
        var state = Catalog.BuildState(new AppearanceSettings { Wallpaper = "sea" });
        var sea = Catalog.FindWallpaper("sea");

        foreach (var dark in new[] { false, true })
        {
            var block = Block(state, dark);
            var sky = dark ? sea.Dark : sea.Light;
            VarOf(block, "--sm-sky").ShouldBe(sky.Gradient);
            VarOf(block, "--sm-orb-1").ShouldBe(sky.Orb1);
            VarOf(block, "--sm-chat-wallpaper").ShouldBe(sky.Gradient);
            VarOf(block, "--sm-chat-bg").ShouldBe(sky.Base);
            VarOf(block, "--sm-chat-photo").ShouldBeNull("chats follow the app");
            VarOf(block, "--sm-sky-dim").ShouldBeNull();
        }
        state.Images.ShouldBeEmpty();
    }

    [Fact]
    public void An_own_gradient_replaces_the_sky_in_both_themes()
    {
        var a = new AppearanceSettings
        {
            Wallpaper = ThemeCatalog.CustomWallpaperKey,
            CustomBackground = new AppBackground { Light = new Backdrop { Kind = BackdropKind.Gradient, Colors = ["#FFE7D6", "#E4DCFF"], Angle = 200 } },
        };

        var state = Catalog.BuildState(a, p => "idb:" + p.FileName);

        foreach (var dark in new[] { false, true })
        {
            var block = Block(state, dark);
            VarOf(block, "--sm-sky").ShouldBe("linear-gradient(200deg, #FFE7D6 0%, #E4DCFF 100%)");
            VarOf(block, "--sm-orb-2").ShouldBe("rgba(0,0,0,0)", "no drifting lights over one's own colors");
            VarOf(block, "--sm-stars").ShouldBe("0");
            VarOf(block, "--sm-clouds").ShouldBe("0");
            VarOf(block, "--sm-chat-wallpaper").ShouldBe("linear-gradient(200deg, #FFE7D6 0%, #E4DCFF 100%)", "chats follow the app");
        }
        state.Images.ShouldBeEmpty();

        a.Wallpaper = "dawn";
        VarOf(Block(Catalog.BuildState(a), dark: false), "--sm-sky").ShouldBe(Catalog.FindWallpaper("dawn").Light.Gradient, "a sky chosen again wins");
    }

    [Fact]
    public void Separate_backgrounds_per_theme_and_a_photo_with_its_blur_and_dim()
    {
        var photo = new BackdropPhoto { Id = "0a1b2c3d4e5f", Extension = ".jpg" };
        var a = new AppearanceSettings
        {
            Wallpaper = ThemeCatalog.CustomWallpaperKey,
            CustomBackground = new AppBackground
            {
                SameForBoth = false,
                Light = new Backdrop { Kind = BackdropKind.Gradient, Colors = ["#E9E2FF", "#FFE9EF", "#FFF2E0"], Angle = 160 },
                Dark = new Backdrop { Kind = BackdropKind.Photo, Photo = photo, Blur = 30, Dim = 45 },
            },
        };

        var state = Catalog.BuildState(a, p => "/_sm/backdrops/" + p.FileName);

        VarOf(Block(state, dark: false), "--sm-sky").ShouldBe("linear-gradient(160deg, #E9E2FF 0%, #FFE9EF 50%, #FFF2E0 100%)");
        VarOf(Block(state, dark: false), "--sm-sky-dim").ShouldBeNull();
        var night = Block(state, dark: true);
        VarOf(night, "--sm-sky-dim").ShouldBe("0.45");
        VarOf(night, "--sm-stars").ShouldBe("0");
        state.Images.ShouldBe([new ThemeImage("sky", "dark", "/_sm/backdrops/0a1b2c3d4e5f.jpg", 30)]);

        Catalog.BuildState(a).Images.ShouldBeEmpty("without a place for pictures there are none to show");
    }

    [Fact]
    public void The_chats_own_background_overrides_the_apps_for_chats_only()
    {
        var photo = new BackdropPhoto { Id = "ffeeddccbbaa", Extension = ".png" };
        var a = new AppearanceSettings
        {
            Wallpaper = "aurora",
            ChatBackground = new Backdrop { Kind = BackdropKind.Photo, Photo = photo, Blur = 0, Dim = 20 },
        };

        var state = Catalog.BuildState(a, p => "idb:" + p.FileName);

        foreach (var dark in new[] { false, true })
        {
            var block = Block(state, dark);
            VarOf(block, "--sm-sky").ShouldBe(dark ? Catalog.FindWallpaper("aurora").Dark.Gradient : Catalog.FindWallpaper("aurora").Light.Gradient);
            VarOf(block, "--sm-chat-photo").ShouldBe("none", "boot.js sets the picture itself; nothing of the app's shows meanwhile");
            VarOf(block, "--sm-chat-dim").ShouldBe("0.2");
            VarOf(block, "--sm-chat-orb-1").ShouldBe("rgba(0,0,0,0)");
            VarOf(block, "--sm-chat-stars").ShouldBe("0");
        }
        state.Images.ShouldBe([new ThemeImage("chat", "light", "idb:ffeeddccbbaa.png", 0), new ThemeImage("chat", "dark", "idb:ffeeddccbbaa.png", 0)]);
    }

    [Fact]
    public void One_chats_background_is_painted_inline_on_its_screen()
    {
        var gradient = new Backdrop { Kind = BackdropKind.Gradient, Colors = ["#161C40", "#0B0A1C"], Angle = 180 };
        var style = Catalog.ChatStyle(gradient, dark: true, photoUrl: null);
        style.ShouldContain("--sm-chat-wallpaper:linear-gradient(180deg, #161C40 0%, #0B0A1C 100%);");
        style.ShouldContain($"--sm-chat-bg:{ThemeCatalog.AverageHex(["#161C40", "#0B0A1C"])};", customMessage: "the edges of the screen fade into its colors");
        style.ShouldContain("--sm-chat-photo:none;");

        var sky = Catalog.ChatStyle(new Backdrop { Kind = BackdropKind.Sky, Sky = "mint" }, dark: false, photoUrl: null);
        sky.ShouldContain($"--sm-chat-orb-1:{Catalog.FindWallpaper("mint").Light.Orb1};");
        sky.ShouldContain("--sm-chat-clouds:1;");

        var photo = new Backdrop { Kind = BackdropKind.Photo, Photo = new BackdropPhoto { Id = "0a1b2c3d4e5f" }, Dim = 15 };
        var withUrl = Catalog.ChatStyle(photo, dark: false, "blob:https://0.0.0.1/1234");
        withUrl.ShouldContain("--sm-chat-photo:url(\"blob:https://0.0.0.1/1234\");");
        withUrl.ShouldContain("--sm-chat-dim:0.15;");
    }

    [Fact]
    public void A_custom_wallpaper_without_a_background_shows_the_default_sky()
    {
        var state = Catalog.BuildState(new AppearanceSettings { Wallpaper = ThemeCatalog.CustomWallpaperKey });
        VarOf(Block(state, dark: false), "--sm-sky").ShouldBe(Catalog.FindWallpaper(ThemeCatalog.DefaultWallpaperKey).Light.Gradient);
    }

    [Fact]
    public void Previews_get_the_tokens_of_the_theme_they_show()
    {
        var a = new AppearanceSettings { IncomingTint = "sand" };
        var day = Catalog.Palette(a, dark: false);
        var night = Catalog.Palette(a, dark: true);

        day.Text.ShouldBe("#1C1634");
        night.Text.ShouldBe("#F3F0FF");
        day.Incoming.Background.ShouldBe(Catalog.FindTint("sand").Light);
        night.Incoming.Background.ShouldBe(Catalog.FindTint("sand").Dark);
        night.Outgoing.ShouldBe(Catalog.OutgoingPaint(a, dark: true));
    }

    [GeneratedRegex("#[0-9A-Fa-f]{6}")]
    private static partial Regex HexRegex();
}
