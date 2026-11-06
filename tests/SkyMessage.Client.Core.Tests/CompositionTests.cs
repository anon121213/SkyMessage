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
                     typeof(IAppUpdateInstaller), typeof(IBackdropImageStore), typeof(IReminderNotificationService),
                     typeof(IDeviceContactsWatcher), typeof(IAppLockPlatform),
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
            typeof(IMediaDownloadService), typeof(ILinkBuilder), typeof(INotificationCoordinator), typeof(IChatListStore), typeof(IChatFolderStore),
            typeof(IMessageStore), typeof(IUserStore), typeof(IContactStore), typeof(ITypingStore), typeof(IDraftStore),
            typeof(IAccountLocalStore), typeof(ICallService), typeof(IScreenCaptureGuard), typeof(IAudioFocusCoordinator),
            typeof(IBirthdayService), typeof(IContactSyncService), typeof(IAppLockService),
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
        public IAppLockService Lock { get; }
        public AppSettings Settings { get; } = new();
        public NotificationCoordinator Coordinator { get; }
        public List<InAppNotification> InApp { get; } = [];

        /// <param name="appLock">A real app passcode service; a substitute by default.</param>
        public Harness(IAppLockService? appLock = null)
        {
            Lock = appLock ?? Substitute.For<IAppLockService>();
            var users = Substitute.For<IUserStore>();
            users.DisplayNameOf(Arg.Any<Guid>()).Returns("Anna");
            Chats.TitleOf(Arg.Any<ChatSummaryDto>()).Returns(c => c.Arg<ChatSummaryDto>().Title);
            Coordinator = new NotificationCoordinator(Substitute.For<IRealtimeService>(), Fakes.Auth(Me, "me_user"), Chats, users,
                Fakes.Settings(Settings), App, Notifications, Lock, Substitute.For<IServiceProvider>(), TimeProvider.System, Log.For<NotificationCoordinator>());
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

    [Fact]
    public async Task A_locked_app_shows_no_banner_and_notifies_through_the_system_even_in_front()
    {
        var h = new Harness();
        h.App.IsForeground.Returns(true);
        h.Lock.IsLocked.Returns(true);
        var chat = h.AddChat();
        // The chat was open when the app locked: nothing of it is on screen now.
        h.Chats.ActiveChatId.Returns(chat.Id);

        await h.Coordinator.HandleAsync(Fakes.Message(chat.Id, 1, Guid.CreateVersion7(), "see you at five"));

        h.InApp.ShouldBeEmpty();
        await h.Notifications.Received(1).ShowMessageAsync(Arg.Is<LocalNotification>(n => n.Title == "Family" && n.Body == "see you at five"));
        await h.Notifications.DidNotReceive().CancelForChatAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task With_hidden_text_a_locked_app_tells_neither_who_wrote_nor_what()
    {
        var h = new Harness();
        h.App.IsForeground.Returns(false);
        h.Lock.IsLocked.Returns(true);
        h.Lock.ShouldHideNotificationText().Returns(true);
        h.Settings.Language = "ru";
        var chat = h.AddChat(ChatType.Group);

        await h.Coordinator.HandleAsync(Fakes.Message(chat.Id, 1, Guid.CreateVersion7(), "секрет"));

        // Hidden: the platform drops the chat's earlier lines itself (Android keeps the count, the web replaces by tag).
        await h.Notifications.Received(1).ShowMessageAsync(Arg.Is<LocalNotification>(n =>
            n.ChatId == chat.Id && n.Title == "SkyMessage" && n.Body == "Новое сообщение" && n.Hidden));
    }

    private static int Redactions(ILocalNotificationService notifications) =>
        notifications.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ILocalNotificationService.RedactMessagesAsync));

    [Fact]
    public async Task Locking_with_hidden_text_redacts_the_notifications_already_shown_once_per_lock()
    {
        var h = new Harness();
        h.Settings.Language = "ru";
        h.Lock.HideNotificationText.Returns(true);
        h.Lock.IsLocked.Returns(true);
        h.Lock.LockCount.Returns(1);

        h.Lock.Changed += Raise.Event<Action>();
        await Wait.UntilAsync(() => Redactions(h.Notifications) == 1, "the lock redacts what the shade shows");
        await h.Notifications.Received(1).RedactMessagesAsync("SkyMessage", "Новое сообщение");

        h.Lock.Changed += Raise.Event<Action>();
        await Task.Delay(50);
        Redactions(h.Notifications).ShouldBe(1, "the same lock redacts once");

        h.Lock.LockCount.Returns(2);
        h.Lock.Changed += Raise.Event<Action>();
        await Wait.UntilAsync(() => Redactions(h.Notifications) == 2, "the next lock redacts again");
    }

    [Fact]
    public async Task Locking_without_hidden_text_leaves_the_notifications_as_they_are()
    {
        var h = new Harness();
        h.Lock.IsLocked.Returns(true);
        h.Lock.LockCount.Returns(1);

        h.Lock.Changed += Raise.Event<Action>();
        h.Lock.IsLocked.Returns(false);
        h.Lock.HideNotificationText.Returns(true);
        h.Lock.Changed += Raise.Event<Action>();
        await Task.Delay(50);

        Redactions(h.Notifications).ShouldBe(0);
    }

    [Fact]
    public async Task A_message_shown_before_the_auto_lock_loses_its_text_when_the_lock_engages()
    {
        // You leave the app, Mama writes 30 s later, the app locks at 1 min: the shade must not show "Mama: ..." any more.
        var device = await LockHarness.WithPasscodeAsync();
        await device.Lock.SetHideNotificationTextAsync(true);
        var h = new Harness(device.Lock);
        h.App.IsForeground.Returns(false);
        h.Settings.Language = "en";
        var chat = h.AddChat();

        device.Leave();
        device.Clocks.Advance(TimeSpan.FromSeconds(30));
        await h.Coordinator.HandleAsync(Fakes.Message(chat.Id, 1, Guid.CreateVersion7(), "call me back"));
        await h.Notifications.Received(1).ShowMessageAsync(Arg.Is<LocalNotification>(n => n.Body == "call me back" && !n.Hidden));
        Redactions(h.Notifications).ShouldBe(0);

        device.Clocks.Advance(TimeSpan.FromSeconds(31));
        device.Lock.CheckAway();

        device.Lock.IsLocked.ShouldBeTrue();
        await Wait.UntilAsync(() => Redactions(h.Notifications) == 1, "the notification from before the lock is redacted");
        await h.Notifications.Received(1).RedactMessagesAsync("SkyMessage", "New message");

        await h.Coordinator.HandleAsync(Fakes.Message(chat.Id, 2, Guid.CreateVersion7(), "still there?"));
        await h.Notifications.Received(1).ShowMessageAsync(Arg.Is<LocalNotification>(n => n.Body == "New message" && n.Hidden));
    }
}
