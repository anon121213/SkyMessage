using System.Text.Json;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>
/// Colors and backgrounds in the appearance settings: defaults, settings saved by 0.2.61 (before they existed), the
/// round trip through storage, and repairs of anything that cannot be shown.
/// </summary>
public sealed class AppearanceSettingsTests
{
    private static readonly Guid ChatA = Guid.Parse("0199a1b2-0000-7000-8000-00000000000a");
    private static readonly Guid ChatB = Guid.Parse("0199a1b2-0000-7000-8000-00000000000b");

    private static async Task<(SettingsService Service, InMemorySecureStore Store)> LoadAsync(string? storedJson, InMemorySecureStore? store = null)
    {
        store ??= new InMemorySecureStore();
        if (storedJson is not null) await store.SetAsync(SettingsService.Key, storedJson);
        var service = new SettingsService(store, store, Substitute.For<IPlatformInfo>(), Log.For<SettingsService>());
        await service.LoadAsync();
        return (service, store);
    }

    private static BackdropPhoto Photo(string id = "0a1b2c3d4e5f", string extension = ".jpg") =>
        new() { Id = id, Extension = extension, Width = 1080, Height = 2400 };

    [Fact]
    public void New_settings_keep_the_look_the_app_had()
    {
        var a = new AppearanceSettings();

        a.OutgoingBubble.ShouldBe(OutgoingBubbleStyle.Gradient);
        a.OutgoingColors.ShouldBeEmpty();
        a.IncomingTint.ShouldBe("default");
        a.Wallpaper.ShouldBe("aurora");
        a.CustomBackground.ShouldBeNull();
        a.ChatBackground.ShouldBeNull("chats show the app's background");
        a.ChatBackgrounds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Settings_saved_by_0_2_61_keep_every_choice_and_get_the_new_defaults()
    {
        // What 0.2.61 stored: no bubble styles, tints or backgrounds of one's own yet.
        const string json = """
            {"appearance":{"theme":"Dark","accent":"teal","customAccentHex":"#12ab34","fontScalePercent":115,"wallpaper":"sea",
             "bubbleRadius":20,"animations":false,"compactChatList":true},
             "chats":{"sendByEnter":false,"quickReaction":"❤️"},"notifications":{"soundPack":"bells"},"language":"ru"}
            """;

        var (service, _) = await LoadAsync(json);
        var a = service.Current.Appearance;

        a.Theme.ShouldBe(ThemeMode.Dark);
        a.Accent.ShouldBe("teal");
        a.CustomAccentHex.ShouldBe("#12ab34");
        a.FontScalePercent.ShouldBe(115);
        a.Wallpaper.ShouldBe("sea");
        a.BubbleRadius.ShouldBe(20);
        a.Animations.ShouldBeFalse();
        a.CompactChatList.ShouldBeTrue();
        a.OutgoingBubble.ShouldBe(OutgoingBubbleStyle.Gradient);
        a.OutgoingColors.ShouldBeEmpty();
        a.IncomingTint.ShouldBe("default");
        a.CustomBackground.ShouldBeNull();
        a.ChatBackground.ShouldBeNull();
        a.ChatBackgrounds.ShouldBeEmpty();
        service.Current.Chats.QuickReaction.ShouldBe("❤️");
        service.Current.Notifications.SoundPack.ShouldBe("bells");
    }

    [Fact]
    public async Task Colors_and_backgrounds_survive_a_restart()
    {
        var store = new InMemorySecureStore();
        var (service, _) = await LoadAsync(null, store);
        await service.UpdateAsync(s =>
        {
            var a = s.Appearance;
            a.OutgoingBubble = OutgoingBubbleStyle.Custom;
            a.OutgoingColors = ["#FFD8B8", "#e4d7ff"];
            a.IncomingTint = "mint";
            a.Wallpaper = BackdropRules.CustomWallpaper;
            a.CustomBackground = new AppBackground
            {
                SameForBoth = false,
                Light = new Backdrop { Kind = BackdropKind.Gradient, Colors = ["#E9E2FF", "#FFE9EF", "#FFF2E0"], Angle = 210 },
                Dark = new Backdrop { Kind = BackdropKind.Photo, Photo = Photo(), Blur = 30, Dim = 45 },
            };
            a.ChatBackground = new Backdrop { Kind = BackdropKind.Sky, Sky = "dusk" };
            a.ChatBackgrounds[ChatA] = new Backdrop { Kind = BackdropKind.Photo, Photo = Photo("ffeeddccbbaa", ".png"), Blur = 0, Dim = 15 };
            a.ChatBackgrounds[ChatB] = new Backdrop { Kind = BackdropKind.Gradient, Colors = ["#161C40", "#0B0A1C"], Angle = 180 };
        });

        var stored = store.Values[SettingsService.Key];
        stored.ShouldContain("\"outgoingBubble\":\"Custom\"");
        stored.ShouldContain(ChatA.ToString());

        var (reloaded, _) = await LoadAsync(null, store);
        var r = reloaded.Current.Appearance;
        r.OutgoingBubble.ShouldBe(OutgoingBubbleStyle.Custom);
        r.OutgoingColors.ShouldBe(["#FFD8B8", "#E4D7FF"], "colors are kept as #RRGGBB in upper case");
        r.IncomingTint.ShouldBe("mint");
        r.Wallpaper.ShouldBe("custom");
        r.CustomBackground.ShouldNotBeNull();
        r.CustomBackground.SameForBoth.ShouldBeFalse();
        r.CustomBackground.Light.Colors.ShouldBe(["#E9E2FF", "#FFE9EF", "#FFF2E0"]);
        r.CustomBackground.Light.Angle.ShouldBe(210);
        r.CustomBackground.Dark.Kind.ShouldBe(BackdropKind.Photo);
        r.CustomBackground.Dark.Photo!.FileName.ShouldBe("0a1b2c3d4e5f.jpg");
        r.CustomBackground.Dark.Blur.ShouldBe(30);
        r.CustomBackground.Dark.Dim.ShouldBe(45);
        r.CustomBackground.For(dark: true).ShouldBeSameAs(r.CustomBackground.Dark);
        r.CustomBackground.For(dark: false).ShouldBeSameAs(r.CustomBackground.Light);
        r.ChatBackground!.Sky.ShouldBe("dusk");
        r.ChatBackgrounds.Keys.ShouldBe([ChatA, ChatB], ignoreOrder: true);
        r.ChatBackgrounds[ChatA].Photo!.FileName.ShouldBe("ffeeddccbbaa.png");
        r.ChatBackgrounds[ChatB].Colors.ShouldBe(["#161C40", "#0B0A1C"]);
    }

    [Fact]
    public async Task One_background_for_both_themes_shows_its_light_side_at_night()
    {
        var (service, _) = await LoadAsync("""
            {"appearance":{"wallpaper":"custom","customBackground":{"sameForBoth":true,
             "light":{"kind":"Gradient","colors":["#112233","#445566"],"angle":90},
             "dark":{"kind":"Gradient","colors":["#000000","#111111"]}}}}
            """);

        var custom = service.Current.Appearance.CustomBackground!;
        custom.For(dark: true).Colors.ShouldBe(["#112233", "#445566"]);
        custom.Dark.Colors.ShouldBe(["#000000", "#111111"], "the night side is kept for when the themes are separated again");
    }

    [Fact]
    public async Task Unknown_names_from_a_newer_version_fall_back_without_losing_the_other_settings()
    {
        var (service, _) = await LoadAsync("""
            {"appearance":{"theme":"Light","outgoingBubble":"Neon","incomingTint":"rainbow",
             "chatBackground":{"kind":"Video","sky":"aurora"}},"language":"en"}
            """);

        var a = service.Current.Appearance;
        a.Theme.ShouldBe(ThemeMode.Light);
        a.OutgoingBubble.ShouldBe(OutgoingBubbleStyle.Gradient);
        a.IncomingTint.ShouldBe("default");
        a.ChatBackground.ShouldNotBeNull("an unknown kind reads as a sky, and this one names one");
        a.ChatBackground.Kind.ShouldBe(BackdropKind.Sky);
        service.Current.Language.ShouldBe("en");
    }

    [Fact]
    public async Task Broken_colors_and_backgrounds_are_repaired_or_dropped()
    {
        var json = JsonSerializer.Serialize(new
        {
            appearance = new
            {
                wallpaper = "Custom",
                outgoingBubble = "Custom",
                outgoingColors = new[] { "red", "#12345", "aabbcc", "#DDEEFF", "#001122" },
                incomingTint = " Peach ",
                customBackground = new
                {
                    sameForBoth = false,
                    light = new { kind = "Sky", sky = "aurora" },
                    dark = new { kind = "Photo", photo = new { id = "../../etc", extension = ".jpg" } },
                },
                chatBackground = new { kind = "Gradient", colors = new[] { "#ABCDEF" }, angle = 30 },
                chatBackgrounds = new Dictionary<string, object>
                {
                    [ChatA.ToString()] = new { kind = "Gradient", colors = new[] { "#111111", "#222222", "#333333", "#444444" }, angle = -30 },
                    [ChatB.ToString()] = new { kind = "Photo", photo = new { id = "0a1b2c3d4e5f", extension = ".heic" } },
                    [Guid.Empty.ToString()] = new { kind = "Sky", sky = "sea" },
                    [Guid.NewGuid().ToString()] = new { kind = "Photo", photo = new { id = "0a1b2c3d4e5f", extension = ".webp" }, blur = 250, dim = -5 },
                },
            },
        }, SkyJson.Options);

        var (service, _) = await LoadAsync(json);
        var a = service.Current.Appearance;

        a.OutgoingColors.ShouldBe(["#AABBCC", "#DDEEFF"], "invalid ones go, two at most stay");
        a.OutgoingBubble.ShouldBe(OutgoingBubbleStyle.Custom);
        a.IncomingTint.ShouldBe("peach");
        a.CustomBackground.ShouldBeNull("a sky is no own background and the photo's name is not a file name");
        a.Wallpaper.ShouldBe("aurora", "'custom' without a background of one's own falls back to the default sky");
        a.ChatBackground.ShouldBeNull("a gradient needs two colors");
        a.ChatBackgrounds.Count.ShouldBe(2);
        a.ChatBackgrounds[ChatA].Colors.ShouldBe(["#111111", "#222222", "#333333"]);
        a.ChatBackgrounds[ChatA].Angle.ShouldBe(330);
        var photo = a.ChatBackgrounds.Values.Single(b => b.Kind == BackdropKind.Photo);
        photo.Blur.ShouldBe(100);
        photo.Dim.ShouldBe(0);
        photo.Photo!.FileName.ShouldBe("0a1b2c3d4e5f.webp");
    }

    [Fact]
    public async Task My_own_bubble_style_without_colors_is_the_gradient_again()
    {
        var (service, _) = await LoadAsync("""{"appearance":{"outgoingBubble":"Custom","outgoingColors":[]}}""");
        service.Current.Appearance.OutgoingBubble.ShouldBe(OutgoingBubbleStyle.Gradient);

        await service.UpdateAsync(s => s.Appearance.OutgoingBubble = OutgoingBubbleStyle.Solid);
        service.Current.Appearance.OutgoingBubble.ShouldBe(OutgoingBubbleStyle.Solid);
    }

    [Fact]
    public void A_half_broken_own_background_starts_its_broken_side_over()
    {
        var gradient = new Backdrop { Kind = BackdropKind.Gradient, Colors = ["#102030", "#405060"] };

        var separate = BackdropRules.Normalize(new AppBackground { SameForBoth = false, Light = gradient, Dark = new Backdrop { Kind = BackdropKind.Photo } })!;
        separate.Light.Colors.ShouldBe(["#102030", "#405060"]);
        separate.Dark.ShouldBeEquivalentTo(BackdropRules.DefaultGradient(dark: true));

        var shared = BackdropRules.Normalize(new AppBackground { SameForBoth = true, Light = new Backdrop { Kind = BackdropKind.Sky, Sky = "sea" }, Dark = gradient })!;
        shared.For(dark: false).Colors.ShouldBe(["#102030", "#405060"], "the side that can be shown serves both themes");

        BackdropRules.Normalize(new AppBackground { Light = new Backdrop { Kind = BackdropKind.Photo }, Dark = new Backdrop { Kind = BackdropKind.Sky, Sky = "sea" } })
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("0a1b2c3d4e5f", ".jpg", true)]
    [InlineData("0A1B2C3D4E5F", ".jpg", false)]
    [InlineData("0a1b2c3d4e5", ".jpg", false)]
    [InlineData("0a1b2c3d4e5g", ".png", false)]
    [InlineData("0a1b2c3d4e5f", ".heic", false)]
    [InlineData("0a1b2c3d4e5f", "", false)]
    [InlineData("..%2f..%2fa", ".jpg", false)]
    public void Only_safe_picture_names_are_accepted(string id, string extension, bool ok) =>
        BackdropRules.IsWellFormed(new BackdropPhoto { Id = id, Extension = extension }).ShouldBe(ok);

    [Theory]
    [InlineData("image/jpeg", "a.jpg", ".jpg")]
    [InlineData("image/jpg", "x", ".jpg")]
    [InlineData("image/png", "a.jpg", ".png")]
    [InlineData("", "photo.JPEG", ".jpg")]
    [InlineData("application/octet-stream", "photo.webp", ".webp")]
    [InlineData("image/gif", "a.gif", ".gif")]
    [InlineData("image/heic", "IMG_1.HEIC", null)]
    [InlineData("video/mp4", "clip.mp4", null)]
    public void Pictures_are_stored_in_formats_a_page_can_show(string contentType, string fileName, string? extension) =>
        BackdropRules.ExtensionOf(contentType, fileName).ShouldBe(extension);

    [Fact]
    public void Every_picture_a_setting_uses_is_known_even_while_a_sky_is_chosen()
    {
        var a = new AppearanceSettings
        {
            Wallpaper = "dawn",
            CustomBackground = new AppBackground
            {
                SameForBoth = false,
                Light = new Backdrop { Kind = BackdropKind.Photo, Photo = Photo("111111111111") },
                Dark = new Backdrop { Kind = BackdropKind.Photo, Photo = Photo("222222222222", ".png") },
            },
            ChatBackground = new Backdrop { Kind = BackdropKind.Photo, Photo = Photo("333333333333") },
            ChatBackgrounds = { [ChatA] = new Backdrop { Kind = BackdropKind.Photo, Photo = Photo("444444444444") }, [ChatB] = new Backdrop { Kind = BackdropKind.Sky, Sky = "sea" } },
        };

        BackdropRules.FileNamesIn(a).ShouldBe(["111111111111.jpg", "222222222222.png", "333333333333.jpg", "444444444444.jpg"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("#6c5ce7", "#6C5CE7")]
    [InlineData("6C5CE7", "#6C5CE7")]
    [InlineData(" #abcdef ", "#ABCDEF")]
    [InlineData("#abc", null)]
    [InlineData("# abcde", null)]
    [InlineData("#gggggg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Hex_colors_are_normalized(string? value, string? expected) => AppearanceRules.NormalizeHex(value).ShouldBe(expected);
}
