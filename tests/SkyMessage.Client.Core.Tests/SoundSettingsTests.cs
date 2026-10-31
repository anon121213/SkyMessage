using System.Security.Cryptography;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>Sound packs in the settings: defaults, settings saved by older versions, repairs, what the channels play.</summary>
public sealed class SoundSettingsTests
{
    private static async Task<NotificationSettings> LoadAsync(string storedJson)
    {
        var store = new InMemorySecureStore();
        await store.SetAsync(SettingsService.Key, storedJson);
        var service = new SettingsService(store, store, Substitute.For<IPlatformInfo>(), Log.For<SettingsService>());
        await service.LoadAsync();
        return service.Current.Notifications;
    }

    private static CustomSound Custom(string id = "a1b2c3d4e5f6", string extension = ".mp3", int durationMs = 2000) => new()
    {
        Id = id,
        Name = "ping",
        Extension = extension,
        ContentType = "audio/mpeg",
        Size = 30_000,
        DurationMs = durationMs,
    };

    [Fact]
    public void New_settings_use_the_wood_pack()
    {
        var n = new NotificationSettings();
        n.SoundPack.ShouldBe("wood");
        n.MessageTone.ShouldBe("chime");
        n.Ringtone.ShouldBe("classic");
        SoundCatalog.Packs.ShouldBe(["wood", "bells", "piano"]);
    }

    [Theory]
    [InlineData("chime", "classic")]
    [InlineData("glass", "soft")]
    [InlineData("pop", "bright")]
    [InlineData("none", "classic")]
    public async Task Settings_saved_before_packs_keep_their_tones_in_the_wood_pack(string messageTone, string ringtone)
    {
        // What versions with a single set of tones stored: no pack, no own sounds.
        var n = await LoadAsync($$$"""{"notifications":{"enabled":true,"sound":true,"messageTone":"{{{messageTone}}}","ringtone":"{{{ringtone}}}"}}""");

        n.SoundPack.ShouldBe(SoundCatalog.Wood);
        n.MessageTone.ShouldBe(messageTone);
        n.Ringtone.ShouldBe(ringtone);
        n.CustomMessageTone.ShouldBeNull();
        n.CustomRingtone.ShouldBeNull();
    }

    [Fact]
    public async Task Unknown_or_oddly_written_choices_are_repaired()
    {
        var n = await LoadAsync("""{"notifications":{"soundPack":"Metal","messageTone":"xylophone","ringtone":"none"}}""");
        n.SoundPack.ShouldBe("wood");
        n.MessageTone.ShouldBe("chime");
        n.Ringtone.ShouldBe("classic", "calls always ring with something");

        n = await LoadAsync("""{"notifications":{"soundPack":" Bells ","messageTone":"GLASS","ringtone":"Soft"}}""");
        n.SoundPack.ShouldBe("bells");
        n.MessageTone.ShouldBe("glass");
        n.Ringtone.ShouldBe("soft");
    }

    [Fact]
    public async Task A_custom_choice_without_its_sound_falls_back_to_the_default()
    {
        var n = await LoadAsync("""{"notifications":{"soundPack":"piano","messageTone":"custom","ringtone":"custom"}}""");
        n.SoundPack.ShouldBe("piano");
        n.MessageTone.ShouldBe("chime");
        n.Ringtone.ShouldBe("classic");
    }

    [Theory]
    [InlineData("../../shared_prefs", ".mp3", 2000)]   // not a file name we made
    [InlineData("a1b2c3d4e5f6", ".exe", 2000)]          // not an audio file we accept
    [InlineData("a1b2c3d4e5f6", ".mp3", 30_000)]        // too long for a message tone
    [InlineData("a1b2c3d4e5f6", ".mp3", 0)]
    public void Saved_custom_sounds_that_could_not_have_passed_the_checks_are_dropped(string id, string extension, int durationMs)
    {
        var n = new NotificationSettings { MessageTone = "custom", CustomMessageTone = Custom(id, extension, durationMs) };
        SoundCatalog.Normalize(n);
        n.CustomMessageTone.ShouldBeNull();
        n.MessageTone.ShouldBe("chime");
    }

    [Fact]
    public void A_kept_custom_sound_survives_choosing_a_pack_tone()
    {
        var n = new NotificationSettings { MessageTone = "pop", CustomMessageTone = Custom(), Ringtone = "custom", CustomRingtone = Custom("0f0e0d0c0b0a", ".m4a", 45_000) };
        SoundCatalog.Normalize(n);
        n.MessageTone.ShouldBe("pop");
        n.CustomMessageTone.ShouldNotBeNull();
        n.Ringtone.ShouldBe("custom");
        n.CustomRingtone!.Extension.ShouldBe(".m4a");
    }

    [Fact]
    public async Task Custom_sounds_round_trip_through_the_stored_settings()
    {
        var store = new InMemorySecureStore();
        var service = new SettingsService(store, store, Substitute.For<IPlatformInfo>(), Log.For<SettingsService>());
        await service.LoadAsync();
        await service.UpdateAsync(s =>
        {
            s.Notifications.SoundPack = "bells";
            s.Notifications.CustomRingtone = Custom("0f0e0d0c0b0a", ".ogg", 20_000);
            s.Notifications.Ringtone = "custom";
        });

        store.Values[SettingsService.Key].ShouldNotContain("fileName", Case.Insensitive);
        var n = await LoadAsync(store.Values[SettingsService.Key]);
        n.SoundPack.ShouldBe("bells");
        n.Ringtone.ShouldBe("custom");
        n.CustomRingtone!.FileName.ShouldBe("0f0e0d0c0b0a.ogg");
        n.CustomRingtone.DurationMs.ShouldBe(20_000);
    }

    [Fact]
    public void The_pack_drives_what_messages_and_calls_sound_like()
    {
        var n = new NotificationSettings { SoundPack = "bells", MessageTone = "glass", Ringtone = "soft" };

        var message = SoundCatalog.MessageSound(n)!;
        message.ShouldBe(new SoundChoice("bells", "glass", null));
        message.Id.ShouldBe("bells-glass");
        SoundCatalog.RingtoneSound(n).Id.ShouldBe("bells-soft");
        SoundCatalog.ResourceName(message.Pack, message.Key).ShouldBe("tone_bells_glass");

        n.MessageTone = "none";
        SoundCatalog.MessageSound(n).ShouldBeNull();
    }

    [Fact]
    public void A_custom_sound_has_its_own_channel_id_and_falls_back_to_the_packs_default()
    {
        var n = new NotificationSettings { SoundPack = "piano", MessageTone = "custom", CustomMessageTone = Custom() };

        var sound = SoundCatalog.MessageSound(n)!;
        sound.IsCustom.ShouldBeTrue();
        sound.Id.ShouldBe("custom-a1b2c3d4e5f6");
        sound.Fallback.ShouldBe(new SoundChoice("piano", "chime", null));

        // A new file is a new channel (Android channel sounds cannot change after creation).
        n.CustomMessageTone = Custom("ffeeddccbbaa");
        SoundCatalog.MessageSound(n)!.Id.ShouldNotBe(sound.Id);

        n.Ringtone = "custom";
        n.CustomRingtone = null;
        SoundCatalog.RingtoneSound(n).ShouldBe(new SoundChoice("piano", "classic", null), "no own ringtone: the pack's default rings");
    }

    [Fact]
    public void Every_pack_sound_has_a_distinct_channel_id()
    {
        var ids = SoundCatalog.Packs.SelectMany(p => SoundCatalog.Keys.Select(k => new SoundChoice(p, k, null).Id)).ToList();
        ids.Distinct().Count().ShouldBe(SoundCatalog.Packs.Count * SoundCatalog.Keys.Count);
    }
}

/// <summary>What a user's own sound may be.</summary>
public sealed class CustomSoundRulesTests
{
    [Theory]
    [InlineData("ping.mp3", "audio/mpeg", ".mp3")]
    [InlineData("Ping.MP3", "", ".mp3")]
    [InlineData("voice.m4a", "audio/mp4", ".m4a")]
    [InlineData("tone.ogg", "application/octet-stream", ".ogg")]
    [InlineData("tone.opus", null, ".opus")]
    [InlineData("bell.wav", "audio/x-wav", ".wav")]
    [InlineData("recording", "audio/x-m4a", ".m4a")]          // no extension: the type decides
    [InlineData("track", "audio/mpeg; charset=binary", ".mp3")]
    [InlineData("clip.mp4", "audio/mp4", ".m4a")]               // an audio-only MP4 stores as m4a
    public void The_extension_comes_from_the_name_or_else_the_type(string name, string? type, string expected) =>
        CustomSoundRules.ExtensionOf(name, type).ShouldBe(expected);

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("movie.mkv", "video/x-matroska")]
    [InlineData("file", "application/octet-stream")]
    public void Other_files_are_refused(string name, string type)
    {
        CustomSoundRules.ExtensionOf(name, type).ShouldBeNull();
        CustomSoundRules.CheckFile(name, type, 1000).ShouldBe(CustomSoundProblem.Format);
    }

    [Fact]
    public void Size_limits()
    {
        CustomSoundRules.CheckFile("a.mp3", "audio/mpeg", 0).ShouldBe(CustomSoundProblem.Empty);
        CustomSoundRules.CheckFile("a.mp3", "audio/mpeg", CustomSoundRules.MaxBytes).ShouldBe(CustomSoundProblem.None);
        CustomSoundRules.CheckFile("a.mp3", "audio/mpeg", CustomSoundRules.MaxBytes + 1).ShouldBe(CustomSoundProblem.TooLarge);
        CustomSoundRules.CheckFile("a.mp3", "audio/mpeg", -1).ShouldBe(CustomSoundProblem.None, "unknown size: checked while reading");
    }

    [Theory]
    [InlineData(CustomSoundSlot.Message, null, CustomSoundProblem.Unreadable)]
    [InlineData(CustomSoundSlot.Message, double.NaN, CustomSoundProblem.Unreadable)]
    [InlineData(CustomSoundSlot.Message, 0.0, CustomSoundProblem.Unreadable)]
    [InlineData(CustomSoundSlot.Message, 0.05, CustomSoundProblem.TooShort)]
    [InlineData(CustomSoundSlot.Message, 2.0, CustomSoundProblem.None)]
    [InlineData(CustomSoundSlot.Message, 10.0, CustomSoundProblem.None)]
    [InlineData(CustomSoundSlot.Message, 10.5, CustomSoundProblem.TooLong)]
    [InlineData(CustomSoundSlot.Ringtone, 10.5, CustomSoundProblem.None)]
    [InlineData(CustomSoundSlot.Ringtone, 60.0, CustomSoundProblem.None)]
    [InlineData(CustomSoundSlot.Ringtone, 61.0, CustomSoundProblem.TooLong)]
    public void Length_limits_per_slot(CustomSoundSlot slot, double? seconds, CustomSoundProblem expected) =>
        CustomSoundRules.CheckDuration(slot, seconds).ShouldBe(expected);

    [Fact]
    public void A_described_sound_gets_a_fresh_file_safe_id_and_a_readable_name()
    {
        var first = CustomSoundRules.Describe("  Мамин звонок (2024).mp3 ", ".mp3", 120_000, 3.4567);
        var second = CustomSoundRules.Describe("x.mp3", ".mp3", 1, 1);

        first.Id.Length.ShouldBe(12);
        first.Id.ShouldAllBe(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c));
        first.Id.ShouldNotBe(second.Id);
        first.Name.ShouldBe("Мамин звонок (2024)");
        first.ContentType.ShouldBe("audio/mpeg");
        first.DurationMs.ShouldBe(3457);
        first.FileName.ShouldBe(first.Id + ".mp3");
        CustomSoundRules.IsWellFormed(first, CustomSoundSlot.Message).ShouldBeTrue();
        CustomSoundRules.DisplayName(new string('я', 100) + ".wav").Length.ShouldBe(CustomSoundRules.MaxNameLength);
    }
}

/// <summary>The catalog and the files tools/tones writes stay in step.</summary>
public sealed class SoundFilesTests
{
    private static readonly string Repo = FindRepo();

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkyMessage.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run inside the repository.");
    }

    private static string Raw => Path.Combine(Repo, "src", "Client", "SkyMessage.App", "Resources", "Raw");

    private static string Web => Path.Combine(Repo, "src", "Client", "SkyMessage.Client.UI", "wwwroot", "sounds");

    [Fact]
    public void Every_pack_has_every_sound_for_android_apple_and_the_in_app_player()
    {
        foreach (var pack in SoundCatalog.Packs)
        foreach (var key in SoundCatalog.Keys)
        {
            var name = SoundCatalog.ResourceName(pack, key);
            File.Exists(Path.Combine(Raw, name + ".ogg")).ShouldBeTrue(name + ".ogg");
            File.Exists(Path.Combine(Raw, name + ".caf")).ShouldBeTrue(name + ".caf");
            File.Exists(Path.Combine(Web, pack, key + ".mp3")).ShouldBeTrue($"{pack}/{key}.mp3");
        }
        Directory.EnumerateFiles(Raw, "tone_*.wav").ShouldBeEmpty("the single-pack WAVs are gone");
        Directory.EnumerateFiles(Raw, "tone_*").Count().ShouldBe(SoundCatalog.Packs.Count * SoundCatalog.Keys.Count * 2);
        // In-app: MP3 only (WebKit before 18.4 cannot decode Ogg), one per pack and key, plus the manifest.
        Directory.EnumerateFiles(Web, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f) == "sounds.json" ? ".json" : Path.GetExtension(f))
            .Distinct().Order().ShouldBe([".json", ".mp3"]);
        Directory.EnumerateFiles(Web, "*.mp3", SearchOption.AllDirectories).Count().ShouldBe(SoundCatalog.Packs.Count * SoundCatalog.Keys.Count);
    }

    [Fact]
    public void The_manifest_lists_every_sound_and_marks_exactly_the_ringing_ones_as_loops()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Web, "sounds.json")));
        var packs = manifest.RootElement.GetProperty("packs");
        packs.EnumerateObject().Select(p => p.Name).Order().ShouldBe(SoundCatalog.Packs.Order());
        foreach (var pack in SoundCatalog.Packs)
        {
            var entries = packs.GetProperty(pack);
            entries.EnumerateObject().Select(e => e.Name).Order().ShouldBe(SoundCatalog.Keys.Order());
            foreach (var key in SoundCatalog.Keys)
            {
                var entry = entries.GetProperty(key);
                var loop = entry.TryGetProperty("loop", out var l) && l.GetBoolean();
                loop.ShouldBe(SoundCatalog.Ringtones.Contains(key) || key == SoundCatalog.Ringback, $"{pack}/{key}");
                // The hash versions the in-app file's URL, so it must be that file's (sounds.js fetches <key>.mp3?v=<hash>).
                var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Web, pack, key + ".mp3"))))[..12];
                entry.GetProperty("hash").GetString().ShouldBe(hash, $"{pack}/{key}");
            }
        }
    }
}
