using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class CompositionTests
{
    internal static ServiceProvider BuildProvider(Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSkyMessageClientCore(o => o.DefaultServerUrl = "https://sky.test/");

        var store = new InMemorySecureStore();
        services.AddSingleton<ISecureStore>(store);
        services.AddSingleton<IPreferencesStore>(store);
        services.AddSingleton<ILocalCacheStore>(new InMemoryCacheStore());
        services.AddSingleton<IOutboxFileStore>(new InMemoryOutboxFiles());
        services.AddSingleton<IInterProcessLock>(new SharedLock());
        services.AddSingleton(Fakes.NoMediaFiles());
        services.AddSingleton(Fakes.Network());
        var handlers = Substitute.For<IHttpHandlerFactory>();
        handlers.CreateHandler(Arg.Any<ConnectionSettings>()).Returns(_ => new StubHandler(_ => throw new HttpRequestException("offline")));
        services.AddSingleton(handlers);
        foreach (var type in new[]
                 {
                     typeof(IPlatformInfo), typeof(IFilePickerService), typeof(IMediaCompressor), typeof(IPhoneNumberService),
                     typeof(IDeviceContactsService), typeof(IPermissionService), typeof(ILocalNotificationService), typeof(IAudioRouteService),
                     typeof(ICallPlatformService), typeof(IShareService), typeof(IFileSaveService), typeof(IFileOpener), typeof(IAppLifecycle),
                     typeof(IExternalLauncher), typeof(IDeepLinkSource), typeof(ISystemUiService), typeof(IHapticsService),
                     typeof(IBackgroundConnectionService), typeof(IScreenSecurityService), typeof(IAudioFocusService),
                 })
        {
            services.AddSingleton(type, Substitute.For([type], []));
        }
        overrides?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Every_public_service_resolves_as_a_singleton()
    {
        using var provider = BuildProvider();
        var contracts = new[]
        {
            typeof(IClientBootstrapper), typeof(IAuthService), typeof(ISkyApi), typeof(IServerEndpointService), typeof(IRealtimeService),
            typeof(ISettingsService), typeof(IMediaUrlBuilder), typeof(IMediaContentSource), typeof(IMediaUploader),
            typeof(IMediaDownloadService), typeof(ILinkBuilder), typeof(INotificationCoordinator), typeof(IChatListStore),
            typeof(IMessageStore), typeof(IUserStore), typeof(IContactStore), typeof(ITypingStore), typeof(IDraftStore),
            typeof(IAccountLocalStore), typeof(ICallService), typeof(IScreenCaptureGuard), typeof(IAudioFocusCoordinator),
        };

        foreach (var contract in contracts)
        {
            var first = provider.GetRequiredService(contract);
            first.ShouldNotBeNull();
            provider.GetRequiredService(contract).ShouldBeSameAs(first, contract.Name);
        }
        provider.GetRequiredService<IAuthService>().ShouldBeSameAs(provider.GetRequiredService<SkyMessage.Client.Core.Infrastructure.ITokenSource>());
    }

    [Fact]
    public async Task Bootstrapper_starts_offline_without_waiting_for_the_network()
    {
        using var provider = BuildProvider();
        var bootstrapper = provider.GetRequiredService<IClientBootstrapper>();

        var start = bootstrapper.StartAsync();
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        bootstrapper.IsStarted.ShouldBeTrue();
        provider.GetRequiredService<IAuthService>().State.ShouldBe(AuthState.SignedOut);
        bootstrapper.StartAsync().ShouldBeSameAs(start, "StartAsync is idempotent");
    }

    [Fact]
    public async Task Settings_are_snapshots_and_the_proxy_password_stays_in_the_secure_store()
    {
        using var provider = BuildProvider();
        var settings = provider.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();
        var before = settings.Current;

        await settings.UpdateAsync(s =>
        {
            s.Chats.SendByEnter = false;
            s.Connection.Proxy = new ProxySettings { Type = ProxyType.Socks5, Host = " proxy.test ", Port = 1080, Password = "secret" };
        });

        before.Chats.SendByEnter.ShouldBeTrue("published snapshots are never mutated");
        settings.Current.Chats.SendByEnter.ShouldBeFalse();
        settings.Current.Connection.Proxy.Host.ShouldBe("proxy.test");
        var store = (InMemorySecureStore)provider.GetRequiredService<ISecureStore>();
        store.Values[SettingsService.Key].ShouldNotContain("secret");
        store.Values[SettingsService.ProxyPasswordKey].ShouldBe("secret");
    }
}

public sealed class NotificationCoordinatorTests
{
    private sealed class Harness
    {
        public Guid Me { get; } = Guid.CreateVersion7();
        public IChatListStore Chats { get; } = Substitute.For<IChatListStore>();
        public IAppLifecycle App { get; } = Substitute.For<IAppLifecycle>();
        public ILocalNotificationService Notifications { get; } = Substitute.For<ILocalNotificationService>();
        public AppSettings Settings { get; } = new();
        public NotificationCoordinator Coordinator { get; }
        public List<InAppNotification> InApp { get; } = [];

        public Harness()
        {
            var users = Substitute.For<IUserStore>();
            users.DisplayNameOf(Arg.Any<Guid>()).Returns("Anna");
            Chats.TitleOf(Arg.Any<ChatSummaryDto>()).Returns(c => c.Arg<ChatSummaryDto>().Title);
            Coordinator = new NotificationCoordinator(Substitute.For<IRealtimeService>(), Fakes.Auth(Me, "me_user"), Chats, users,
                Fakes.Settings(Settings), App, Notifications, Substitute.For<IServiceProvider>(), TimeProvider.System, Log.For<NotificationCoordinator>());
            Coordinator.InAppNotificationRaised += InApp.Add;
        }

        public ChatSummaryDto AddChat(ChatType type = ChatType.Private, DateTimeOffset? mutedUntil = null)
        {
            var chat = new ChatSummaryDto { Id = Guid.CreateVersion7(), Type = type, Title = "Family", MutedUntil = mutedUntil };
            Chats.Get(chat.Id).Returns(chat);
            return chat;
        }
    }

    [Fact]
    public async Task Foreground_messages_raise_an_in_app_banner_except_for_the_open_chat_and_my_own()
    {
        var h = new Harness();
        h.App.IsForeground.Returns(true);
        var chat = h.AddChat();
        var open = h.AddChat();
        h.Chats.ActiveChatId.Returns(open.Id);

        await h.Coordinator.HandleAsync(Fakes.Message(chat.Id, 1, Guid.CreateVersion7(), "hi"));
        await h.Coordinator.HandleAsync(Fakes.Message(open.Id, 1, Guid.CreateVersion7(), "on screen"));
        await h.Coordinator.HandleAsync(Fakes.Message(chat.Id, 2, h.Me, "mine"));

        h.InApp.Count.ShouldBe(1);
        h.InApp[0].ChatId.ShouldBe(chat.Id);
        h.InApp[0].Body.ShouldBe("hi");
        await h.Notifications.DidNotReceive().ShowMessageAsync(Arg.Any<LocalNotification>());
    }

    [Fact]
    public async Task Muted_chats_stay_silent_unless_I_am_mentioned()
    {
        var h = new Harness();
        h.App.IsForeground.Returns(false);
        var muted = h.AddChat(ChatType.Group, DateTimeOffset.MaxValue);

        await h.Coordinator.HandleAsync(Fakes.Message(muted.Id, 1, Guid.CreateVersion7(), "general chatter"));
        await h.Coordinator.HandleAsync(Fakes.Message(muted.Id, 2, Guid.CreateVersion7(), "hey @me_user, look"));

        await h.Notifications.Received(1).ShowMessageAsync(Arg.Is<LocalNotification>(n => n.ChatId == muted.Id && n.Body == "Anna: hey @me_user, look"));
    }

    [Fact]
    public async Task Hidden_previews_and_disabled_notifications_are_respected()
    {
        var h = new Harness();
        h.App.IsForeground.Returns(false);
        var chat = h.AddChat();
        h.Settings.Notifications.ShowPreview = false;
        h.Settings.Language = "en";

        await h.Coordinator.HandleAsync(Fakes.Message(chat.Id, 1, Guid.CreateVersion7(), "secret text"));
        h.Settings.Notifications.Enabled = false;
        await h.Coordinator.HandleAsync(Fakes.Message(chat.Id, 2, Guid.CreateVersion7(), "another"));

        await h.Notifications.Received(1).ShowMessageAsync(Arg.Is<LocalNotification>(n => n.Body == "New message"));
    }
}
