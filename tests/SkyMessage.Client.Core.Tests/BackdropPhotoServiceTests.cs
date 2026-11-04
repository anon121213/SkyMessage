using System.Collections.Concurrent;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>
/// Background pictures: picked, downscaled and kept on the device; deleted once no setting refers to them (replaced,
/// reset, discarded by the editor, or left over), never while an editor — of this tab or, for an hour, of another —
/// still holds one. Backgrounds of single chats go with chats that are gone and with the account signing out.
/// </summary>
public sealed class BackdropPhotoServiceTests
{
    private static readonly Guid Chat = Guid.Parse("0199a1b2-0000-7000-8000-0000000000c1");

    private sealed class MemoryStore(TimeProvider clock) : IBackdropImageStore
    {
        public ConcurrentDictionary<string, (byte[] Bytes, string Type)> Files { get; } = new();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _savedAt = new();
        public bool IsSupported => true;

        public Task SaveAsync(string fileName, byte[] content, string contentType, CancellationToken cancellationToken = default)
        {
            Files[fileName] = (content, contentType);
            _savedAt[fileName] = clock.GetUtcNow();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string fileName)
        {
            Files.TryRemove(fileName, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredBackdropFile>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredBackdropFile>>(Files.Keys.Select(name => new StoredBackdropFile(name, _savedAt.GetValueOrDefault(name))).ToList());

        public string PageSource(string fileName) => "idb:" + fileName;
    }

    private sealed class Rig
    {
        public static readonly Guid Me = Guid.Parse("0199a1b2-0000-7000-8000-0000000000a1");

        public IFilePickerService Picker { get; } = Substitute.For<IFilePickerService>();
        public IMediaCompressor Compressor { get; } = Substitute.For<IMediaCompressor>();
        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        public MemoryStore Store { get; }
        public SettingsService Settings { get; }
        public InMemorySecureStore Preferences { get; } = new();
        public IRealtimeService Realtime { get; } = Substitute.For<IRealtimeService>();
        public IChatListStore Chats { get; } = Substitute.For<IChatListStore>();
        public IAuthService Auth { get; } = Substitute.For<IAuthService>();
        public BackdropPhotoService Service { get; }

        public Rig()
        {
            Store = new MemoryStore(Clock);
            Settings = new SettingsService(Preferences, Preferences, Substitute.For<IPlatformInfo>(), Log.For<SettingsService>());
            Compressor.CanCompressImages.Returns(true);
            Auth.State.Returns(AuthState.SignedIn);
            Auth.UserId.Returns(Me);
            Service = new BackdropPhotoService(Picker, Compressor, Store, Settings, Preferences, Realtime, Chats, Auth, Clock, Log.For<BackdropPhotoService>());
        }

        /// <summary>Time passes: whatever was stored so far is no longer new.</summary>
        public void Later() => Clock.Advance(BackdropPhotoService.FreshFor + TimeSpan.FromMinutes(1));

        /// <summary>The next pick returns a photo the compressor turns into <paramref name="bytes"/> of <paramref name="type"/>.</summary>
        public void Picks(byte[] bytes, string type = "image/jpeg", string name = "IMG_0001.jpg", int width = 1080, int height = 2400)
        {
            var file = new PickedFile { FileName = name, ContentType = "image/jpeg", Size = bytes.Length * 3L, OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(bytes)) };
            Picker.PickImageAsync(Arg.Any<CancellationToken>()).Returns(file);
            Compressor.CompressImageAsync(file, Arg.Any<ImageCompressionOptions>(), Arg.Any<CancellationToken>()).Returns(new CompressedMedia
            {
                // A compressor hands back the original when it cannot convert it (e.g. HEIC it cannot decode).
                FileName = type is "image/jpeg" or "image/png" ? Path.ChangeExtension(name, type == "image/png" ? ".png" : ".jpg") : name,
                ContentType = type,
                Size = bytes.Length,
                Width = width,
                Height = height,
                OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(bytes)),
            });
        }

        public async Task<BackdropPhoto> ImportAsync(byte[]? bytes = null)
        {
            Picks(bytes ?? [1, 2, 3, 4]);
            var result = await Service.ImportAsync(2400);
            return result.Photo.ShouldNotBeNull();
        }
    }

    private static Backdrop PhotoBackdrop(BackdropPhoto photo, int blur = 0, int dim = 15) => new() { Kind = BackdropKind.Photo, Photo = photo, Blur = blur, Dim = dim };

    [Fact]
    public async Task A_picked_photo_is_downscaled_to_the_screen_and_kept_on_the_device()
    {
        var rig = new Rig();
        rig.Picks([9, 8, 7, 6, 5], width: 1080, height: 2340);

        var result = await rig.Service.ImportAsync(2340);

        var photo = result.Photo.ShouldNotBeNull();
        result.Problem.ShouldBe(BackdropPhotoProblem.None);
        photo.Id.Length.ShouldBe(12);
        photo.Extension.ShouldBe(".jpg");
        photo.Width.ShouldBe(1080);
        photo.Height.ShouldBe(2340);
        BackdropRules.IsWellFormed(photo).ShouldBeTrue();
        rig.Store.Files[photo.FileName].Bytes.ShouldBe(new byte[] { 9, 8, 7, 6, 5 });
        rig.Store.Files[photo.FileName].Type.ShouldBe("image/jpeg");
        rig.Service.SourceOf(photo).ShouldBe("idb:" + photo.FileName);
        await rig.Compressor.Received(1).CompressImageAsync(Arg.Any<PickedFile>(),
            Arg.Is<ImageCompressionOptions>(o => o.MaxDimension == 2340 && o.StripMetadata && o.Quality == BackdropPhotoService.Quality), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(200, BackdropPhotoService.MinDimension)]
    [InlineData(12000, BackdropPhotoService.MaxDimension)]
    public async Task The_size_a_screen_asks_for_stays_sensible(int asked, int used)
    {
        var rig = new Rig();
        rig.Picks([1, 2, 3]);

        await rig.Service.ImportAsync(asked);

        await rig.Compressor.Received(1).CompressImageAsync(Arg.Any<PickedFile>(), Arg.Is<ImageCompressionOptions>(o => o.MaxDimension == used), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_cancelled_picker_stores_nothing()
    {
        var rig = new Rig();
        rig.Picker.PickImageAsync(Arg.Any<CancellationToken>()).Returns((PickedFile?)null);

        var result = await rig.Service.ImportAsync(2400);

        result.IsCancelled.ShouldBeTrue();
        rig.Store.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Photos_a_page_cannot_show_or_that_stay_too_large_are_refused()
    {
        var rig = new Rig();
        rig.Picks([1, 2, 3], type: "image/heic", name: "IMG_1.HEIC");
        (await rig.Service.ImportAsync(2400)).Problem.ShouldBe(BackdropPhotoProblem.Format);

        var huge = new PickedFile { FileName = "big.jpg", ContentType = "image/jpeg", OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream()) };
        rig.Picker.PickImageAsync(Arg.Any<CancellationToken>()).Returns(huge);
        rig.Compressor.CompressImageAsync(huge, Arg.Any<ImageCompressionOptions>(), Arg.Any<CancellationToken>()).Returns(new CompressedMedia
        {
            FileName = "big.jpg",
            ContentType = "image/jpeg",
            Size = -1,
            OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(new byte[BackdropRules.MaxBytes + 1])),
        });
        (await rig.Service.ImportAsync(2400)).Problem.ShouldBe(BackdropPhotoProblem.TooLarge);

        rig.Store.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_photo_that_cannot_be_prepared_is_reported_not_thrown()
    {
        var rig = new Rig();
        var file = new PickedFile { FileName = "a.jpg", ContentType = "image/jpeg", OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream()) };
        rig.Picker.PickImageAsync(Arg.Any<CancellationToken>()).Returns(file);
        rig.Compressor.CompressImageAsync(file, Arg.Any<ImageCompressionOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("decoder"));

        (await rig.Service.ImportAsync(2400)).Problem.ShouldBe(BackdropPhotoProblem.Failed);
    }

    [Fact]
    public async Task A_chat_photo_that_is_replaced_or_reset_is_deleted()
    {
        var rig = new Rig();
        var first = await rig.ImportAsync();
        await rig.Service.SetChatAsync(Chat, PhotoBackdrop(first));
        rig.Settings.Current.Appearance.ChatBackgrounds[Chat].Photo!.FileName.ShouldBe(first.FileName);

        var second = await rig.ImportAsync();
        await rig.Service.SetChatAsync(Chat, PhotoBackdrop(second, blur: 40));
        rig.Store.Files.Keys.ShouldBe([second.FileName], "the first photo is gone with its background");

        await rig.Service.SetChatAsync(Chat, null);
        rig.Settings.Current.Appearance.ChatBackgrounds.ShouldNotContainKey(Chat);
        rig.Store.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_apps_own_photo_stays_while_a_sky_is_chosen_and_goes_with_a_reset()
    {
        var rig = new Rig();
        var photo = await rig.ImportAsync();
        await rig.Service.SetAppBackgroundAsync(new AppBackground { Light = PhotoBackdrop(photo, 24, 40) });
        rig.Settings.Current.Appearance.Wallpaper.ShouldBe(BackdropRules.CustomWallpaper);

        await rig.Settings.UpdateAsync(s => s.Appearance.Wallpaper = "sea");
        await Task.Delay(50);
        rig.Store.Files.ContainsKey(photo.FileName).ShouldBeTrue("it can be picked again from the sky list");

        await rig.Settings.UpdateAsync(s => s.Appearance = new AppearanceSettings());
        await Wait.UntilAsync(() => rig.Store.Files.IsEmpty, "a reset forgets the own background and its photo");
    }

    [Fact]
    public async Task A_photo_the_editor_did_not_keep_is_discarded_but_never_one_in_use()
    {
        var rig = new Rig();
        var used = await rig.ImportAsync();
        await rig.Service.SetChatDefaultAsync(PhotoBackdrop(used));
        var unused = await rig.ImportAsync();

        await rig.Service.DiscardAsync(unused);
        await rig.Service.DiscardAsync(used);

        rig.Store.Files.Keys.ShouldBe([used.FileName]);
    }

    [Fact]
    public async Task Cleanup_removes_leftovers_but_not_a_photo_an_editor_still_holds()
    {
        var rig = new Rig();
        var kept = await rig.ImportAsync();
        await rig.Service.SetChatAsync(Chat, PhotoBackdrop(kept));
        await rig.Store.SaveAsync("aaaaaaaaaaaa.jpg", [1], "image/jpeg");
        var held = await rig.ImportAsync();
        rig.Later();

        await rig.Service.CleanupAsync();

        rig.Store.Files.Keys.ShouldBe([kept.FileName, held.FileName], ignoreOrder: true);

        await rig.Service.DiscardAsync(held);
        await rig.Service.CleanupAsync();
        rig.Store.Files.Keys.ShouldBe([kept.FileName]);
    }

    [Fact]
    public async Task Cleanup_keeps_a_photo_another_tab_saved_meanwhile()
    {
        var rig = new Rig();
        await rig.Store.SaveAsync("bbbbbbbbbbbb.jpg", [1], "image/jpeg");
        await rig.Store.SaveAsync("cccccccccccc.jpg", [2], "image/jpeg");
        // Another tab of this browser saved settings that use one of them; this tab's settings do not know it.
        var otherTab = new AppSettings();
        otherTab.Appearance.ChatBackgrounds[Chat] = PhotoBackdrop(new BackdropPhoto { Id = "bbbbbbbbbbbb", Extension = ".jpg" });
        await rig.Preferences.SetAsync(SettingsService.Key, JsonSerializer.Serialize(otherTab, SkyJson.Options));
        rig.Later();

        await rig.Service.CleanupAsync();

        rig.Store.Files.Keys.ShouldBe(["bbbbbbbbbbbb.jpg"]);
    }

    [Fact]
    public async Task Cleanup_leaves_a_photo_stored_within_the_hour_another_tabs_editor_may_hold()
    {
        var rig = new Rig();
        // Picked in another tab's open editor: only that tab knows it, and neither tab's settings refer to it yet.
        await rig.Store.SaveAsync("dddddddddddd.jpg", [1], "image/jpeg");
        rig.Clock.Advance(TimeSpan.FromMinutes(20));

        await rig.Service.CleanupAsync();
        rig.Store.Files.Keys.ShouldBe(["dddddddddddd.jpg"]);

        rig.Later();
        await rig.Service.CleanupAsync();
        rig.Store.Files.ShouldBeEmpty("an hour on, nothing holds it any more");
    }

    [Fact]
    public async Task A_full_chat_list_drops_the_backgrounds_of_chats_that_are_gone()
    {
        var rig = new Rig();
        var photo = await rig.ImportAsync();
        await rig.Service.SetChatAsync(Chat, PhotoBackdrop(photo));
        var other = Guid.Parse("0199a1b2-0000-7000-8000-0000000000c2");
        await rig.Service.SetChatAsync(other, new Backdrop { Kind = BackdropKind.Sky, Sky = "sea" });

        // Left while this device was offline: the server's list no longer has it.
        rig.Chats.Synced += Raise.Event<Action<IReadOnlyCollection<Guid>>>((IReadOnlyCollection<Guid>)new HashSet<Guid> { other });

        await Wait.UntilAsync(() => !rig.Settings.Current.Appearance.ChatBackgrounds.ContainsKey(Chat) && rig.Store.Files.IsEmpty);
        rig.Settings.Current.Appearance.ChatBackgrounds.Keys.ShouldBe([other]);
    }

    [Fact]
    public async Task Signing_out_takes_the_single_chats_backgrounds_along_and_keeps_the_devices()
    {
        var rig = new Rig();
        var app = await rig.ImportAsync();
        await rig.Service.SetAppBackgroundAsync(new AppBackground { Light = PhotoBackdrop(app, 24, 40) });
        var chats = await rig.ImportAsync();
        await rig.Service.SetChatDefaultAsync(PhotoBackdrop(chats));
        var mine = await rig.ImportAsync();
        await rig.Service.SetChatAsync(Chat, PhotoBackdrop(mine));

        rig.Auth.State.Returns(AuthState.SignedOut);
        rig.Auth.UserId.Returns((Guid?)null);
        rig.Auth.Changed += Raise.Event<Action>();

        await Wait.UntilAsync(() => rig.Settings.Current.Appearance.ChatBackgrounds.Count == 0 && !rig.Store.Files.ContainsKey(mine.FileName));
        rig.Store.Files.Keys.ShouldBe([app.FileName, chats.FileName], ignoreOrder: true);
        rig.Settings.Current.Appearance.ChatBackground.ShouldNotBeNull("the chats' background is the device's");
        rig.Settings.Current.Appearance.CustomBackground.ShouldNotBeNull();
    }

    [Fact]
    public async Task Another_account_signing_in_does_not_inherit_the_old_ones_chat_backgrounds()
    {
        var rig = new Rig();
        await rig.Service.SetChatAsync(Chat, new Backdrop { Kind = BackdropKind.Sky, Sky = "dusk" });

        rig.Auth.UserId.Returns(Guid.Parse("0199a1b2-0000-7000-8000-0000000000a2"));
        rig.Auth.Changed += Raise.Event<Action>();

        await Wait.UntilAsync(() => rig.Settings.Current.Appearance.ChatBackgrounds.Count == 0);
    }

    [Fact]
    public async Task Unreadable_stored_settings_delete_nothing()
    {
        var rig = new Rig();
        await rig.Store.SaveAsync("bbbbbbbbbbbb.jpg", [1], "image/jpeg");
        await rig.Preferences.SetAsync(SettingsService.Key, "{ not json");

        await rig.Service.CleanupAsync();

        rig.Store.Files.Keys.ShouldBe(["bbbbbbbbbbbb.jpg"]);
    }

    [Fact]
    public async Task A_chat_that_is_gone_takes_its_background_along()
    {
        var rig = new Rig();
        var photo = await rig.ImportAsync();
        await rig.Service.SetChatAsync(Chat, PhotoBackdrop(photo));
        var other = Guid.Parse("0199a1b2-0000-7000-8000-0000000000c2");
        await rig.Service.SetChatAsync(other, new Backdrop { Kind = BackdropKind.Sky, Sky = "sea" });

        rig.Realtime.ChatRemoved += Raise.Event<Action<ChatRefEvent>>(new ChatRefEvent { ChatId = Chat });

        await Wait.UntilAsync(() => !rig.Settings.Current.Appearance.ChatBackgrounds.ContainsKey(Chat) && rig.Store.Files.IsEmpty);
        rig.Settings.Current.Appearance.ChatBackgrounds.ShouldContainKey(other);
    }

    [Fact]
    public async Task A_photo_saved_into_a_background_is_no_longer_held_by_the_editor()
    {
        var rig = new Rig();
        var photo = await rig.ImportAsync();
        await rig.Service.SetChatAsync(Chat, PhotoBackdrop(photo));
        await rig.Service.SetChatAsync(Chat, new Backdrop { Kind = BackdropKind.Sky, Sky = "dawn" });

        rig.Store.Files.ShouldBeEmpty("saved once, it belongs to the settings: replaced, it goes");
    }
}
