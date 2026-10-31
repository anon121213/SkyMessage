using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class ServiceBehaviorTests
{
    private static MediaDownloadService Downloads(NetworkKind kind, bool metered = false, AppSettings? settings = null)
    {
        var network = Substitute.For<INetworkStatusService>();
        network.Kind.Returns(kind);
        network.IsConnected.Returns(kind != NetworkKind.None);
        network.IsMetered.Returns(metered);
        var routing = Substitute.For<IEndpointRouting>();
        var transport = new ApiTransport(
            new HttpClientProvider(Substitute.For<IHttpHandlerFactory>(), Fakes.Settings(), TimeProvider.System, Log.For<HttpClientProvider>()),
            routing, TimeProvider.System);
        var fetcher = new MediaFetcher(new AuthorizedSender(transport, Substitute.For<ITokenSource>()), routing, TimeProvider.System);
        var endpoints = Substitute.For<IServerEndpointService>();
        endpoints.BaseUri.Returns(new Uri("https://sky.test/"));
        var urls = new MediaUrlBuilder(new ClientCoreOptions(), endpoints, new AuthSessionStore(new InMemorySecureStore(), Log.For<AuthSessionStore>()),
            Log.For<MediaUrlBuilder>());
        var (_, lifecycle) = Fakes.Account();
        return new MediaDownloadService(fetcher, Fakes.NoMediaFiles(), Substitute.For<IFileOpener>(), Substitute.For<IFileSaveService>(),
            Substitute.For<IExternalLauncher>(), network, Fakes.Settings(settings), urls, Fakes.Trimmer(), lifecycle, TimeProvider.System,
            Log.For<MediaDownloadService>());
    }

    private static MediaDto Media(MediaKind kind, long megabytes) => new() { Id = Guid.CreateVersion7(), Kind = kind, SizeBytes = megabytes * 1024 * 1024 };

    [Fact]
    public void Auto_download_follows_the_network_type_and_size_limits()
    {
        var wifi = Downloads(NetworkKind.Wifi);
        var cellular = Downloads(NetworkKind.Cellular);
        var meteredWifi = Downloads(NetworkKind.Wifi, metered: true);
        var offline = Downloads(NetworkKind.None);

        wifi.ShouldAutoDownload(Media(MediaKind.Video, 30)).ShouldBeTrue();
        wifi.ShouldAutoDownload(Media(MediaKind.File, 60)).ShouldBeFalse("over the Wi-Fi file limit (50 MB)");
        cellular.ShouldAutoDownload(Media(MediaKind.Photo, 2)).ShouldBeTrue();
        cellular.ShouldAutoDownload(Media(MediaKind.Video, 2)).ShouldBeFalse("videos are off on mobile data by default");
        cellular.ShouldAutoDownload(Media(MediaKind.Voice, 1)).ShouldBeTrue();
        meteredWifi.ShouldAutoDownload(Media(MediaKind.Video, 2)).ShouldBeFalse("a metered hotspot counts as mobile data");
        offline.ShouldAutoDownload(Media(MediaKind.Photo, 1)).ShouldBeFalse();
    }

    [Fact]
    public async Task Browser_downloads_go_through_a_signed_url_with_download_flag()
    {
        var launcher = Substitute.For<IExternalLauncher>();
        var endpoints = Substitute.For<IServerEndpointService>();
        endpoints.BaseUri.Returns(new Uri("https://sky.test/"));
        var sessions = new AuthSessionStore(new InMemorySecureStore(), Log.For<AuthSessionStore>());
        sessions.Set(new StoredSession
        {
            UserId = Guid.CreateVersion7(),
            SessionId = Guid.CreateVersion7(),
            RefreshToken = "r",
            MediaKey = MediaSignature.EncodeKey(MediaSignature.NewKey()),
        });
        var urls = new MediaUrlBuilder(new ClientCoreOptions(), endpoints, sessions, Log.For<MediaUrlBuilder>());
        var routing = Substitute.For<IEndpointRouting>();
        var transport = new ApiTransport(
            new HttpClientProvider(Substitute.For<IHttpHandlerFactory>(), Fakes.Settings(), TimeProvider.System, Log.For<HttpClientProvider>()),
            routing, TimeProvider.System);
        var (_, lifecycle) = Fakes.Account();
        var downloads = new MediaDownloadService(new MediaFetcher(new AuthorizedSender(transport, Substitute.For<ITokenSource>()), routing, TimeProvider.System),
            Fakes.NoMediaFiles(), Substitute.For<IFileOpener>(), Substitute.For<IFileSaveService>(), launcher, Fakes.Network(), Fakes.Settings(),
            urls, Fakes.Trimmer(), lifecycle, TimeProvider.System, Log.For<MediaDownloadService>());
        var media = Media(MediaKind.File, 1);

        (await downloads.DownloadAsync(media)).ShouldBeNull();

        await launcher.Received(1).OpenUriAsync(Arg.Is<Uri>(u =>
            u.AbsolutePath == $"/api/media/{media.Id}/content" && u.Query.Contains("t=") && u.Query.EndsWith("&download=1")));
    }

    [Fact]
    public async Task Typing_is_throttled_per_action_and_cancel_is_sent_only_after_activity()
    {
        var realtime = Substitute.For<IRealtimeService>();
        var (_, lifecycle) = Fakes.Account();
        var typing = new TypingStore(realtime, Fakes.Auth(Guid.CreateVersion7()), lifecycle, TimeProvider.System, Log.For<TypingStore>());
        var chatId = Guid.CreateVersion7();

        typing.NotifyLocalActivity(chatId, TypingAction.Cancel);
        typing.NotifyLocalActivity(chatId, TypingAction.Typing);
        typing.NotifyLocalActivity(chatId, TypingAction.Typing);
        typing.NotifyLocalActivity(chatId, TypingAction.RecordingVoice);
        typing.NotifyLocalActivity(chatId, TypingAction.Cancel);
        await Task.Delay(20);

        await realtime.Received(1).SendTypingAsync(chatId, TypingAction.Typing);
        await realtime.Received(1).SendTypingAsync(chatId, TypingAction.RecordingVoice);
        await realtime.Received(1).SendTypingAsync(chatId, TypingAction.Cancel);
    }

    [Fact]
    public async Task Remote_typing_expires_and_ends_with_the_message()
    {
        var realtime = Substitute.For<IRealtimeService>();
        var (_, lifecycle) = Fakes.Account();
        var typing = new TypingStore(realtime, Fakes.Auth(Guid.CreateVersion7()), lifecycle, TimeProvider.System, Log.For<TypingStore>());
        var chatId = Guid.CreateVersion7();
        var anna = Guid.CreateVersion7();

        realtime.Typing += Raise.Event<Action<TypingEvent>>(new TypingEvent { ChatId = chatId, UserId = anna, Action = TypingAction.Typing });
        typing.Get(chatId).Single().UserId.ShouldBe(anna);
        realtime.MessageCreated += Raise.Event<Action<MessageDto>>(Fakes.Message(chatId, 1, anna));

        typing.Get(chatId).ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Drafts_survive_a_restart_of_the_account()
    {
        var store = new InMemoryCacheStore();
        var me = Guid.CreateVersion7();
        var chatId = Guid.CreateVersion7();
        var (cache, lifecycle) = Fakes.Account(store);
        var drafts = new DraftStore(cache, lifecycle, TimeProvider.System, Log.For<DraftStore>());
        await lifecycle.ActivateAsync(me);

        drafts.Set(chatId, new DraftDto("half a thought", null, null, DateTimeOffset.UnixEpoch));
        await Wait.UntilAsync(() => store.Entries.ContainsKey(LocalCache.AccountKey(me, "drafts")), "persisted (debounced)");

        var (cache2, lifecycle2) = Fakes.Account(store.Clone());
        var restored = new DraftStore(cache2, lifecycle2, TimeProvider.System, Log.For<DraftStore>());
        await lifecycle2.ActivateAsync(me);
        restored.Get(chatId)!.Text.ShouldBe("half a thought");

        restored.Set(chatId, new DraftDto("   ", null, null, DateTimeOffset.UnixEpoch));
        restored.Get(chatId).ShouldBeNull("whitespace without a reply clears the draft");
    }

    [Fact]
    public void Share_links_prefer_the_public_base_url()
    {
        var endpoints = Substitute.For<IServerEndpointService>();
        endpoints.BaseUri.Returns(new Uri("http://10.0.2.2:8080/"));
        var links = new LinkBuilder(endpoints);

        links.ProfileLink("@anna").ShouldBe("http://10.0.2.2:8080/u/anna");
        endpoints.Info.Returns(new ServerInfoDto { PublicBaseUrl = "https://sky.example.org" });
        links.InviteLink("AbC_1").ShouldBe("https://sky.example.org/join/AbC_1");
    }

    [Fact]
    public async Task Phone_book_matches_map_back_to_device_names_with_the_right_regions()
    {
        var me = Guid.CreateVersion7();
        var api = Substitute.For<ISkyApi>();
        var device = Substitute.For<IDeviceContactsService>();
        device.IsSupported.Returns(true);
        device.RequestPermissionAsync().Returns(true);
        device.GetContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<DeviceContact>
        {
            new("Мама", ["8 912 345-67-89"]),
            new("Somchai", ["081 234 5678"], ["+66812345678"]),
            new("Me", ["+66 99 999 9999"]),
        });
        var anna = new UserSummaryDto { Id = Guid.CreateVersion7(), Username = "anna", DisplayName = "Anna" };
        var somchai = new UserSummaryDto { Id = Guid.CreateVersion7(), Username = "somchai", DisplayName = "Somchai" };
        FindByPhonesRequest? request = null;
        api.FindByPhonesAsync(Arg.Do<FindByPhonesRequest>(r => request = r), Arg.Any<CancellationToken>()).Returns(new List<PhoneMatchDto>
        {
            new() { PhoneNumber = "8 912 345-67-89", User = anna },
            new() { PhoneNumber = "+66812345678", User = somchai },
            new() { PhoneNumber = "+66 99 999 9999", User = new UserSummaryDto { Id = me } },
        });
        var platform = Substitute.For<IPlatformInfo>();
        platform.CountryCode.Returns("th");
        var auth = Fakes.Auth(me);
        auth.Me.Returns(new MeDto { Id = me, PhoneNumber = "+79123456789" });
        var (cache, lifecycle) = Fakes.Account();
        var contacts = new ContactStore(api, Substitute.For<IRealtimeService>(), auth, Substitute.For<IUserStore>(), device, platform, cache, lifecycle,
            Log.For<ContactStore>());

        var matches = await contacts.FindDeviceContactsAsync();

        request!.DefaultRegions.Take(2).ShouldBe(["TH", "RU"], "SIM country first, then my own number's country");
        request.PhoneNumbers.ShouldContain("+66812345678", "OS-normalized numbers are preferred");
        matches.Select(m => m.DeviceContactName).ShouldBe(["Somchai", "Мама"], ignoreOrder: true);
        matches.ShouldNotContain(m => m.User.Id == me);
    }
}
