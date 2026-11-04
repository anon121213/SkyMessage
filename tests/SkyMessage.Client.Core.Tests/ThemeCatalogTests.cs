using System.Text.RegularExpressions;
using Shouldly;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.UI.Services;
using Xunit;
using Color = SkyMessage.Client.UI.Services.ThemeCatalog.Color;

namespace SkyMessage.Client.Core.Tests;

/// <summary>
/// Building the theme (ThemeCatalog): bubble ink chosen and kept readable (WCAG 4.5:1) for any colors, the time and a
/// failed status on them (3:1), the accents saved by 0.2.61 looking as they did (white text on their fills), the
/// readability floor that keeps the root screens' text readable over a background of one's own, and the backgrounds —
/// skies, gradients, photos per theme — in the CSS, the pictures boot.js shows and what each theme shows beyond its CSS.
/// </summary>
public sealed partial class ThemeCatalogTests
{
    private static readonly ThemeCatalog Catalog = new();
    private static readonly Color DarkInk = Color.Parse("#1C1634");
    private static readonly Color LightText = Color.Parse("#1C1634");
    private static readonly Color LightText2 = Color.Parse("#615A7C");
    private static readonly Color DarkText = Color.Parse("#F3F0FF");
    private static readonly Color DarkText2 = Color.Parse("#B4ACD5");

    /// <summary>Root-screen text reads on every part of a background in a theme: titles 7:1, secondary text 4.5:1.</summary>
    private static void ShouldReadOnScreen(IReadOnlyList<Color> stops, bool dark, string because)
    {
        foreach (var sample in ThemeCatalog.Samples(stops))
        {
            ThemeCatalog.Contrast(dark ? DarkText : LightText, sample).ShouldBeGreaterThanOrEqualTo(7, because);
            ThemeCatalog.Contrast(dark ? DarkText2 : LightText2, sample).ShouldBeGreaterThanOrEqualTo(4.5, because);
        }
    }

    private static string Block(ThemeState state, bool dark)
    {
        var marker = dark ? "html:root[data-theme=\"dark\"]{" : "html:root[data-theme=\"light\"]{";
        var start = state.Css.IndexOf(marker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        var end = state.Css.IndexOf('}', start);
        return state.Css[(start + marker.Length)..end];
    }

    /// <summary>The value in effect: the last declaration of <paramref name="name"/> in the block, as in CSS.</summary>
    private static string? VarOf(string block, string name)
    {
        var matches = Regex.Matches(block, $"(?:^|;)(?={Regex.Escape(name)}:){Regex.Escape(name)}:([^;]*)");
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
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
        var stops = StopsOf(VarOf(light, "--sm-bubble-out-bg")!);
        ThemeCatalog.WorstContrast(Color.Parse(VarOf(light, "--sm-bubble-out-meta")!), stops).ShouldBeGreaterThanOrEqualTo(ThemeCatalog.MinMetaContrast);
        ThemeCatalog.WorstContrast(Color.Parse(VarOf(light, "--sm-bubble-out-danger")!), stops).ShouldBeGreaterThanOrEqualTo(ThemeCatalog.MinMetaContrast,
            "a failed status shows on a light bubble too");
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
            var sky = VarOf(block, "--sm-sky")!;
            if (!dark) sky.ShouldBe("linear-gradient(200deg, #FFE7D6 0%, #E4DCFF 100%)", "colors that suit the theme are shown as they are");
            sky.ShouldStartWith("linear-gradient(200deg, ");
            VarOf(block, "--sm-orb-2").ShouldBe("rgba(0,0,0,0)", "no drifting lights over one's own colors");
            VarOf(block, "--sm-stars").ShouldBe("0");
            VarOf(block, "--sm-clouds").ShouldBe("0");
            VarOf(block, "--sm-chat-wallpaper").ShouldBe(sky, "chats follow the app");
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
        VarOf(night, "--sm-chat-bg").ShouldBe("color-mix(in srgb, var(--sm-sky-tone, #0D0B1E), #0D0B1E calc(max(0.45, var(--sm-sky-dim-min, 0)) * 100%))",
            "the chats' edges fade into the picture under the veil it really gets (its dim, or the floor boot.js measured)");
        var image = state.Images.ShouldHaveSingleItem();
        (image.Slot, image.Theme, image.Src, image.Blur).ShouldBe(("sky", "dark", "/_sm/backdrops/0a1b2c3d4e5f.jpg", 30));
        var floor = image.Floor.ShouldNotBeNull("boot.js veils the app's picture until the root screens' text reads on it");
        floor.Veil.ShouldBe("#0D0B1E");
        floor.Inks.ShouldBe([new ScreenInk("#F3F0FF", 7), new ScreenInk("#B4ACD5", 4.5)]);

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
            var bg = dark ? "#0D0B1E" : "#F6F3FF";
            VarOf(block, "--sm-chat-bg").ShouldBe($"color-mix(in srgb, var(--sm-chat-tone, {bg}), {bg} 20%)", "the edges fade into the picture's tone under its veil");
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
        var withUrl = Catalog.ChatStyle(photo, dark: false, "blob:https://0.0.0.1/1234", "#204060");
        withUrl.ShouldContain("--sm-chat-photo:url(\"blob:https://0.0.0.1/1234\");");
        withUrl.ShouldContain("--sm-chat-dim:0.15;");
        withUrl.ShouldContain($"--sm-chat-bg:{Color.Mix(Color.Parse("#204060"), Color.Parse("#F6F3FF"), 0.15).Hex};");
        Catalog.ChatStyle(photo, dark: false, null).ShouldContain("--sm-chat-bg:color-mix(in srgb, var(--sm-chat-tone, #F6F3FF), #F6F3FF 15%);",
            customMessage: "until the picture is there, its base is the theme's background");
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

    [Theory]
    [InlineData("#7C6CF0")]
    [InlineData("#B05AE0")]
    [InlineData("#2FA36B")]
    [InlineData("#5A9E3A")]
    [InlineData("#C94B6D")]
    public void Mid_tone_accents_saved_by_0_2_61_keep_white_text_on_buttons_and_bubbles(string hex)
    {
        // What sliding the hue strip from the default violet gives: white text on every accent fill in 0.2.61.
        var state = Catalog.BuildState(new AppearanceSettings { CustomAccentHex = hex });
        foreach (var dark in new[] { false, true })
        {
            var block = Block(state, dark);
            VarOf(block, "--sm-on-accent").ShouldBe("#FFFFFF", $"{hex} dark={dark}: buttons, badges, the orb and the dock pill");
            VarOf(block, "--sm-bubble-out-text").ShouldBe("#FFFFFF", $"{hex} dark={dark}");
            foreach (var style in new[] { OutgoingBubbleStyle.Gradient, OutgoingBubbleStyle.Solid })
            {
                var paint = Catalog.AccentBubble(hex, style, dark);
                paint.Ink.ShouldBe("#FFFFFF", $"{hex} {style} dark={dark}");
                ShouldRead(paint, $"{hex} {style} dark={dark}");
            }
        }

        // Deepened only a little: never more than a fifth of the way to black.
        var accent = Color.Parse(hex);
        var deepest = Color.Mix(accent, Color.Black, ThemeCatalog.MaxAccentDeepening + 0.001);
        StopsOf(Catalog.AccentBubble(hex, OutgoingBubbleStyle.Gradient, dark: false).Background)[0].Luminance.ShouldBeGreaterThanOrEqualTo(deepest.Luminance);
    }

    [Fact]
    public void Text_on_accent_fills_follows_the_released_rule()
    {
        // 0.2.61: white unless the gradient's middle is very light (luminance above 0.42).
        var random = new Random(611);
        for (var i = 0; i < 300; i++)
        {
            var hex = $"#{random.Next(0, 0x1000000):X6}";
            var second = Color.Parse(Catalog.SecondHexOf(hex));
            var expected = Color.Mix(Color.Parse(hex), second, 0.5).Luminance > 0.42 ? "#1C1634" : "#FFFFFF";
            VarOf(Block(Catalog.BuildState(new AppearanceSettings { CustomAccentHex = hex }), dark: false), "--sm-on-accent").ShouldBe(expected, hex);
        }

        // A light accent keeps its dark text, and its bubbles take dark ink too.
        var light = Block(Catalog.BuildState(new AppearanceSettings { CustomAccentHex = "#FFE08A" }), dark: false);
        VarOf(light, "--sm-on-accent").ShouldBe("#1C1634");
        VarOf(light, "--sm-bubble-out-text").ShouldBe("#1C1634");
    }

    [Fact]
    public void The_time_and_a_failed_status_on_any_bubble_keep_3_to_1()
    {
        var random = new Random(3141);
        var paints = new List<(string Name, BubblePaint Paint)>();
        for (var i = 0; i < 200; i++)
        {
            var a = $"#{random.Next(0, 0x1000000):X6}";
            var b = $"#{random.Next(0, 0x1000000):X6}";
            foreach (var dark in new[] { false, true })
            {
                paints.Add(($"{a} dark={dark}", Catalog.CustomBubble([a], dark)));
                paints.Add(($"{a} → {b} dark={dark}", Catalog.CustomBubble([a, b], dark)));
                paints.Add(($"accent {a} dark={dark}", Catalog.AccentBubble(a, OutgoingBubbleStyle.Gradient, dark)));
            }
        }

        foreach (var (name, paint) in paints)
        {
            var stops = StopsOf(paint.Background);
            ThemeCatalog.WorstContrast(Color.Parse(paint.Meta), stops).ShouldBeGreaterThanOrEqualTo(ThemeCatalog.MinMetaContrast, name);
            ThemeCatalog.WorstContrast(Color.Parse(paint.Danger!), stops).ShouldBeGreaterThanOrEqualTo(ThemeCatalog.MinMetaContrast, name);
            paint.Meta.ShouldNotBe(paint.Ink, $"{name}: the time stays softer than the text where the fill leaves room") ;
        }

        // The released violet: a pale rose for a failed message, a time as soft as before.
        var violet = Catalog.AccentBubble("#6A4BFF", OutgoingBubbleStyle.Gradient, dark: false);
        violet.Danger.ShouldBe("#FFD3DC");
        ThemeCatalog.WorstContrast(Color.Parse(violet.Meta), StopsOf(violet.Background)).ShouldBeInRange(3.0, 3.6);
    }

    [Fact]
    public void An_own_gradient_that_does_not_suit_a_theme_is_veiled_until_the_root_screens_text_reads()
    {
        var night = Catalog.BackdropGradients.Single(g => g.Key == "night");
        var a = new AppearanceSettings
        {
            Wallpaper = ThemeCatalog.CustomWallpaperKey,
            CustomBackground = new AppBackground
            {
                SameForBoth = false,
                Light = new Backdrop { Kind = BackdropKind.Gradient, Colors = [.. night.Colors], Angle = night.Angle },
                Dark = new Backdrop { Kind = BackdropKind.Gradient, Colors = [.. night.Colors], Angle = night.Angle },
            },
        };
        var state = Catalog.BuildState(a);

        // By day the night gradient gets a light veil: dark text on it reads.
        var day = StopsOf(VarOf(Block(state, dark: false), "--sm-sky")!);
        ShouldReadOnScreen(day, dark: false, "night colors by day");
        VarOf(Block(state, dark: false), "--sm-chat-wallpaper").ShouldBe(VarOf(Block(state, dark: false), "--sm-sky"), "chats that follow the app look like it");
        // At night it suits: shown as it is.
        VarOf(Block(state, dark: true), "--sm-sky").ShouldBe(night.Css);

        // The default light gradient shared with the night: veiled toward the night, light text reads.
        var shared = new AppearanceSettings
        {
            Wallpaper = ThemeCatalog.CustomWallpaperKey,
            CustomBackground = new AppBackground { SameForBoth = true, Light = BackdropRules.DefaultGradient(dark: false) },
        };
        var sharedState = Catalog.BuildState(shared);
        VarOf(Block(sharedState, dark: false), "--sm-sky").ShouldBe(ThemeCatalog.GradientCss(BackdropRules.DefaultGradient(dark: false).Colors, BackdropRules.DefaultAngle));
        ShouldReadOnScreen(StopsOf(VarOf(Block(sharedState, dark: true), "--sm-sky")!), dark: true, "the light gradient at night");
    }

    [Fact]
    public void The_floor_veils_just_as_much_as_needed_and_every_ready_made_gradient_reads_in_both_themes()
    {
        foreach (var preset in Catalog.BackdropGradients)
        {
            foreach (var dark in new[] { false, true })
            {
                var stops = preset.Colors.Select(Color.Parse).ToList();
                var shown = ThemeCatalog.ScreenGradient(preset.Colors, dark).Select(Color.Parse).ToList();
                ShouldReadOnScreen(shown, dark, $"{preset.Key} dark={dark}");

                var veil = ThemeCatalog.ScreenVeil(ThemeCatalog.Samples(stops), dark);
                if (veil <= 0) continue;
                // A hundredth less would not do.
                var bg = Color.Parse(dark ? "#0D0B1E" : "#F6F3FF");
                var lessVeil = (Math.Round(veil * 100) - 1) / 100.0;
                var less = ThemeCatalog.Samples(stops).Select(c => Color.Parse(Color.Mix(Color.Parse(c.Hex), bg, lessVeil).Hex)).ToList();
                var reads = less.All(c => ThemeCatalog.Contrast(dark ? DarkText : LightText, c) >= 7 && ThemeCatalog.Contrast(dark ? DarkText2 : LightText2, c) >= 4.5);
                reads.ShouldBeFalse($"{preset.Key} dark={dark}: {veil} is the least veil");
            }
        }

        // The soft ones need nothing by day, the deep ones nothing at night.
        foreach (var key in new[] { "dawn", "mint", "lagoon", "peach" })
            ThemeCatalog.ScreenGradient(Catalog.BackdropGradients.Single(g => g.Key == key).Colors, dark: false).ShouldBe(Catalog.BackdropGradients.Single(g => g.Key == key).Colors);
        foreach (var key in new[] { "dusk", "night", "forest", "evening" })
            ThemeCatalog.ScreenGradient(Catalog.BackdropGradients.Single(g => g.Key == key).Colors, dark: true).ShouldBe(Catalog.BackdropGradients.Single(g => g.Key == key).Colors);
    }

    [Fact]
    public void A_photos_floor_is_measured_on_its_cells()
    {
        // A light picture at night: veiled until light text reads on its lightest cell; a dark one needs nothing.
        var light = Enumerable.Repeat(Color.Parse("#E8E4DA"), 63).Append(Color.Parse("#FFFFFF")).ToList();
        var night = ThemeCatalog.ScreenVeil(light, dark: true);
        night.ShouldBeInRange(0.6, ThemeCatalog.MaxScreenVeil);
        var veiled = Color.Parse(Color.Mix(Color.White, Color.Parse("#0D0B1E"), night).Hex);
        ThemeCatalog.Contrast(DarkText2, veiled).ShouldBeGreaterThanOrEqualTo(4.5, "the lightest cell decides");
        ThemeCatalog.ScreenVeil(Enumerable.Repeat(Color.Parse("#14102A"), 64), dark: true).ShouldBe(0);

        // By day a landscape with dark hills needs a light veil; a pale sky nothing.
        ThemeCatalog.ScreenVeil([Color.Parse("#DDE8F5"), Color.Parse("#3B4A2E")], dark: false).ShouldBeGreaterThan(0.5);
        ThemeCatalog.ScreenVeil([Color.Parse("#F1F4FA")], dark: false).ShouldBe(0);
        ThemeCatalog.ScreenVeil([Color.Black], dark: false).ShouldBeLessThanOrEqualTo(ThemeCatalog.MaxScreenVeil);
    }

    [Fact]
    public void A_photo_is_veiled_row_by_row_only_where_its_text_needs_it()
    {
        // A pale sky over dark hills, by day: the sky keeps the user's own dim, the hills get the floor.
        var cells = Enumerable.Range(0, 64).Select(i => i < 40 ? Color.Parse("#E6EEF8") : Color.Parse("#3B4A2E")).ToList();
        var rows = ThemeCatalog.ScreenVeilRows(cells, dark: false);
        rows.Count.ShouldBe(ThemeCatalog.PictureRows);
        rows.Take(5).ShouldAllBe(r => r == 0);
        rows.Skip(5).ShouldAllBe(r => r > 0.5);
        ThemeCatalog.ScreenVeilRows(cells.Take(10).ToList(), dark: false).ShouldBeEmpty("only a full 8×8 grid is measured");

        // At every row edge the gradient veils at least what both neighbours need, and never less than the dim.
        var css = ThemeCatalog.ScreenVeilCss(rows, 0.4, dark: false);
        var alphas = Regex.Matches(css, @"rgba\(246,243,255,([0-9.]+)\) ([0-9.]+)%").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        alphas.Count.ShouldBe(9);
        for (var edge = 0; edge <= 8; edge++)
        {
            var need = Math.Max(edge > 0 ? rows[edge - 1] : 0, edge < 8 ? rows[edge] : 0);
            alphas[edge].ShouldBeGreaterThanOrEqualTo(Math.Max(0.4, need) - 0.0005, $"edge {edge}");
        }
        alphas[0].ShouldBe(0.4, "the sky only gets the user's dim");
        css.ShouldStartWith("linear-gradient(to bottom, ");
    }

    [Fact]
    public void Each_theme_says_which_backgrounds_are_ones_own_and_what_lies_under_the_system_bars()
    {
        var sky = Catalog.BuildState(new AppearanceSettings());
        sky.Scenes.ShouldBe([new ThemeScene("light", "", "#ECE6FF"), new ThemeScene("dark", "", "#150F38")]);

        var a = new AppearanceSettings
        {
            Wallpaper = ThemeCatalog.CustomWallpaperKey,
            CustomBackground = new AppBackground
            {
                SameForBoth = false,
                Light = new Backdrop { Kind = BackdropKind.Gradient, Colors = ["#FFE7D6", "#E4DCFF"], Angle = 180 },
                Dark = new Backdrop { Kind = BackdropKind.Photo, Photo = new BackdropPhoto { Id = "0a1b2c3d4e5f" }, Dim = 40 },
            },
            ChatBackground = new Backdrop { Kind = BackdropKind.Sky, Sky = "sea" },
        };
        var state = Catalog.BuildState(a);
        state.Scenes[0].ShouldBe(new ThemeScene("light", "app", "#FFE7D6"), "the gradient's top (180°: its first color) lies under the status bar");
        state.Scenes[1].ShouldBe(new ThemeScene("dark", "app", "#0D0B1E"));
        Catalog.SystemBars(a, dark: false).DarkContent.ShouldBeTrue();
        Catalog.SystemBars(a, dark: true).DarkContent.ShouldBeFalse();

        a.ChatBackground = new Backdrop { Kind = BackdropKind.Gradient, Colors = ["#161C40", "#0B0A1C"] };
        ThemeCatalog.OwnSlots(a, dark: false).ShouldBe("app chat");
        a.Wallpaper = "dawn";
        ThemeCatalog.OwnSlots(a, dark: true).ShouldBe("chat");
        a.ChatBackground = null;
        ThemeCatalog.OwnSlots(a, dark: true).ShouldBe("", "chats follow a sky");
    }

    [Fact]
    public void The_top_of_a_gradient_and_the_icons_over_it()
    {
        IReadOnlyList<Color> stops = [Color.Parse("#000000"), Color.Parse("#FFFFFF")];
        ThemeCatalog.TopOf(stops, 180).ShouldBe(Color.Parse("#000000"), "180° runs top to bottom");
        ThemeCatalog.TopOf(stops, 0).ShouldBe(Color.Parse("#FFFFFF"), "0° runs bottom to top");
        ThemeCatalog.TopOf(stops, 90).Hex.ShouldBe("#808080", "90° runs across: the top edge's middle is the middle");

        ThemeCatalog.DarkContentOn(Color.Parse("#ECE6FF")).ShouldBeTrue();
        ThemeCatalog.DarkContentOn(Color.Parse("#150F38")).ShouldBeFalse();

        // A chat's own background: its gradient's average, a photo's tone under its veil, nothing for a sky.
        Catalog.ChatBaseOf(new Backdrop { Kind = BackdropKind.Gradient, Colors = ["#000000", "#FFFFFF"] }, dark: true, null).ShouldBe("#808080");
        Catalog.ChatBaseOf(new Backdrop { Kind = BackdropKind.Photo, Photo = new BackdropPhoto(), Dim = 50 }, dark: false, "#000000")
            .ShouldBe(Color.Mix(Color.Black, Color.Parse("#F6F3FF"), 0.5).Hex);
        Catalog.ChatBaseOf(new Backdrop { Kind = BackdropKind.Photo, Photo = new BackdropPhoto() }, dark: false, null).ShouldBeNull("its tone is not known yet");
        Catalog.ChatBaseOf(new Backdrop { Kind = BackdropKind.Sky, Sky = "sea" }, dark: false, null).ShouldBeNull();
    }

    [GeneratedRegex("#[0-9A-Fa-f]{6}")]
    private static partial Regex HexRegex();
}
