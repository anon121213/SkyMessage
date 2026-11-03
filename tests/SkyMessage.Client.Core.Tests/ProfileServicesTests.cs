using System.Collections.Concurrent;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>A wall clock in a fixed UTC+3 zone (no tz database needed), frozen unless advanced.</summary>
internal sealed class ZonedClock(DateTimeOffset start) : TimeProvider
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
    private long _ticks = start.UtcTicks;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
}

/// <summary>Settings whose updates apply (and raise Changed) like the real service, without storage.</summary>
internal sealed class LiveSettings(AppSettings? initial = null) : ISettingsService
{
    private AppSettings _current = initial ?? new AppSettings();

    public AppSettings Current => _current;

    public event Action? Changed;

    public Task LoadAsync() => Task.CompletedTask;

    public Task UpdateAsync(Action<AppSettings> mutate)
    {
        var copy = _current.Clone();
        mutate(copy);
        _current = copy;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ResetAsync() => UpdateAsync(_ => { });
}

internal sealed class MemoryLocalStore : IAccountLocalStore
{
    public ConcurrentDictionary<string, object?> Values { get; } = new();

    public Task<T?> GetAsync<T>(string key) => Task.FromResult(Values.TryGetValue(key, out var value) ? (T?)value : default);

    public Task SetAsync<T>(string key, T value)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        Values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class BirthdayServiceTests
{
    private sealed class Harness
    {
        public Guid Me { get; } = Guid.CreateVersion7();
        public ZonedClock Clock { get; }
        public IContactStore Contacts { get; } = Substitute.For<IContactStore>();
        public IUserStore Users { get; } = Substitute.For<IUserStore>();
        public IChatListStore Chats { get; } = Substitute.For<IChatListStore>();
        public LiveSettings Settings { get; } = new();
        public MemoryLocalStore Local { get; } = new();
        public IReminderNotificationService Reminders { get; } = Substitute.For<IReminderNotificationService>();
        public List<(string Group, IReadOnlyList<ScheduledReminder> Reminders)> Scheduled { get; } = [];
        public BirthdayService Service { get; }
        private readonly SkyMessage.Client.Core.Infrastructure.AccountLifecycle _lifecycle;
        private readonly List<ContactDto> _contacts = [];

        public Harness(DateTimeOffset now)
        {
            Clock = new ZonedClock(now);
            Contacts.Contacts.Returns(_ => (IReadOnlyList<ContactDto>)_contacts.ToList());
            Contacts.IsContact(Arg.Any<Guid>()).Returns(c => _contacts.Any(x => x.User.Id == c.Arg<Guid>()));
            Chats.Chats.Returns(new List<ChatSummaryDto>());
            Chats.ArchivedChats.Returns(new List<ChatSummaryDto>());
            Reminders.IsSupported.Returns(true);
            Reminders.ScheduleAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ScheduledReminder>>())
                .Returns(Task.CompletedTask)
                .AndDoes(c =>
                {
                    lock (Scheduled) Scheduled.Add((c.Arg<string>(), c.Arg<IReadOnlyList<ScheduledReminder>>()));
                });
            (_, _lifecycle) = Fakes.Account();
            Service = new BirthdayService(Contacts, Users, Chats, Settings, Local, Reminders, _lifecycle, Substitute.For<IServiceProvider>(), Clock,
                Log.For<BirthdayService>());
        }

        public UserSummaryDto Contact(string name, BirthdayDto? birthday)
        {
            var user = new UserSummaryDto { Id = Guid.CreateVersion7(), Username = name.ToLowerInvariant() + "_x", DisplayName = name, IsContact = true, Birthday = birthday };
            _contacts.Add(new ContactDto { User = user });
            return user;
        }

        public Task SignInAsync() => _lifecycle.ActivateAsync(Me);

        public IReadOnlyList<ScheduledReminder> LastSchedule()
        {
            lock (Scheduled) return Scheduled.Last(s => s.Group == BirthdayService.ReminderGroup).Reminders;
        }
    }

    private static readonly DateTimeOffset MorningUtc = new(2026, 11, 4, 5, 0, 0, TimeSpan.Zero); // 08:00 at UTC+3

    [Fact]
    public async Task Contacts_with_a_birthday_today_are_listed_and_reminded_at_ten_local_time()
    {
        var h = new Harness(MorningUtc);
        var anna = h.Contact("Anna", new BirthdayDto { Day = 4, Month = 11, Year = 1990 });
        var oleg = h.Contact("Oleg", new BirthdayDto { Day = 10, Month = 11 });
        h.Contact("Ivan", new BirthdayDto { Day = 25, Month = 12 });
        h.Contact("Masha", null);
        await h.SignInAsync();

        await h.Service.RefreshNowAsync();

        h.Service.Today.ShouldBe(new DateOnly(2026, 11, 4));
        h.Service.Celebrating.Select(p => (p.User.Id, p.Age)).ShouldBe([(anna.Id, (int?)36)]);
        var reminders = h.LastSchedule();
        reminders.Select(r => r.At).ShouldBe([
            new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.FromHours(3)),
        ]);
        reminders[0].Notification.Title.ShouldBe("Birthday today: Anna");
        reminders[0].Notification.Body.ShouldBe("Turning 36. Send your wishes!");
        reminders[0].Notification.Link.ShouldBe(new Uri("skymessage://u/anna_x"));
        reminders[1].Notification.Body.ShouldBe("Send your wishes!");
        reminders[1].Notification.Key.ShouldContain(oleg.Id.ToString("N"));
    }

    [Fact]
    public async Task The_reminder_links_to_the_chat_when_there_is_one()
    {
        var h = new Harness(MorningUtc);
        var anna = h.Contact("Anna", new BirthdayDto { Day = 4, Month = 11 });
        var chat = new ChatSummaryDto { Id = Guid.CreateVersion7(), Type = ChatType.Private, Peer = anna };
        h.Chats.Chats.Returns(new List<ChatSummaryDto> { chat });
        await h.SignInAsync();

        await h.Service.RefreshNowAsync();

        h.LastSchedule().Single().Notification.Link.ShouldBe(DeepLinks.AppChatUri(chat.Id));
    }

    [Fact]
    public async Task A_reminder_already_due_is_not_scheduled_but_the_card_shows_until_closed_for_the_day()
    {
        var h = new Harness(MorningUtc.AddHours(4)); // 12:00 local
        h.Contact("Anna", new BirthdayDto { Day = 4, Month = 11 });
        await h.SignInAsync();

        await h.Service.RefreshNowAsync();

        h.Service.Celebrating.Count.ShouldBe(1);
        h.LastSchedule().ShouldBeEmpty();
        h.Service.IsCardDismissed.ShouldBeFalse();

        await h.Service.DismissCardAsync();
        h.Service.IsCardDismissed.ShouldBeTrue();
        h.Local.Values[BirthdayService.DismissedKey].ShouldBe("2026-11-04");

        h.Clock.Advance(TimeSpan.FromDays(1));
        h.Service.IsCardDismissed.ShouldBeFalse("a new day brings the card back");
    }

    [Fact]
    public async Task Turning_reminders_off_hides_the_card_and_cancels_the_alarms()
    {
        var h = new Harness(MorningUtc);
        h.Contact("Anna", new BirthdayDto { Day = 4, Month = 11 });
        await h.SignInAsync();
        await h.Service.RefreshNowAsync();
        h.LastSchedule().Count.ShouldBe(1);

        await h.Settings.UpdateAsync(s => s.Notifications.BirthdayReminders = false);
        await h.Service.RefreshNowAsync();

        h.Service.Celebrating.ShouldBeEmpty();
        h.LastSchedule().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_mark_follows_the_latest_known_birthday_of_anyone()
    {
        var h = new Harness(MorningUtc);
        await h.SignInAsync();
        var stranger = new UserSummaryDto { Id = Guid.CreateVersion7(), DisplayName = "Stranger" };

        h.Service.IsBirthdayToday(stranger).ShouldBeFalse();
        h.Users.Get(stranger.Id).Returns(stranger with { Birthday = new BirthdayDto { Day = 4, Month = 11 } });
        h.Service.IsBirthdayToday(stranger).ShouldBeTrue();
        h.Service.IsBirthdayToday(stranger with { IsDeleted = true }).ShouldBeFalse();
        h.Service.IsBirthdayToday(null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_leap_day_birthday_is_reminded_on_february_28_in_a_common_year()
    {
        var h = new Harness(new DateTimeOffset(2027, 2, 20, 5, 0, 0, TimeSpan.Zero));
        h.Contact("Lena", new BirthdayDto { Day = 29, Month = 2, Year = 2000 });
        await h.SignInAsync();

        await h.Service.RefreshNowAsync();

        var reminder = h.LastSchedule().Single();
        reminder.At.ShouldBe(new DateTimeOffset(2027, 2, 28, 10, 0, 0, TimeSpan.FromHours(3)));
        reminder.Notification.Body.ShouldBe("Turning 27. Send your wishes!");
    }

    [Fact]
    public async Task Unchanged_reminders_are_not_handed_to_the_os_again_and_signing_out_cancels_them()
    {
        var h = new Harness(MorningUtc);
        h.Contact("Anna", new BirthdayDto { Day = 6, Month = 11 });
        await h.SignInAsync();
        await h.Service.RefreshNowAsync();
        await h.Service.RefreshNowAsync();

        lock (h.Scheduled) h.Scheduled.Count(s => s.Reminders.Count > 0).ShouldBe(1);

        await ((SkyMessage.Client.Core.Infrastructure.IAccountScoped)h.Service).OnSignedOutAsync(h.Me);
        h.LastSchedule().ShouldBeEmpty();
        h.Service.Celebrating.ShouldBeEmpty();
    }
}

public sealed class ContactSyncServiceTests
{
    private sealed class Harness
    {
        public Guid Me { get; } = Guid.CreateVersion7();
        public IContactStore Contacts { get; } = Substitute.For<IContactStore>();
        public IDeviceContactsService Device { get; } = Substitute.For<IDeviceContactsService>();
        public IDeviceContactsWatcher Watcher { get; } = Substitute.For<IDeviceContactsWatcher>();
        public IPermissionService Permissions { get; } = Substitute.For<IPermissionService>();
        public LiveSettings Settings { get; } = new();
        public MemoryLocalStore Local { get; } = new();
        public IReminderNotificationService Reminders { get; } = Substitute.For<IReminderNotificationService>();
        public List<ReminderNotification> Shown { get; } = [];
        public List<IReadOnlyList<PhoneBookMatch>> AddedBatches { get; } = [];
        public List<PhoneBookMatch> Matches { get; } = [];
        public HashSet<Guid> InContacts { get; } = [];
        public ContactSyncService Service { get; }
        private readonly SkyMessage.Client.Core.Infrastructure.AccountLifecycle _lifecycle;

        public Harness(bool enabled = true)
        {
            Device.IsSupported.Returns(true);
            Device.RequestPermissionAsync().Returns(true);
            Watcher.IsSupported.Returns(true);
            Permissions.CheckAsync(AppPermission.Contacts).Returns(PermissionState.Granted);
            Contacts.FindDeviceContactsAsync(Arg.Any<CancellationToken>()).Returns(_ => Matches
                .Select(m => m with { AlreadyContact = InContacts.Contains(m.User.Id) }).ToList());
            Contacts.IsContact(Arg.Any<Guid>()).Returns(c => InContacts.Contains(c.Arg<Guid>()));
            Contacts.AddSyncedAsync(Arg.Any<IReadOnlyList<PhoneBookMatch>>()).Returns(c =>
            {
                var batch = c.Arg<IReadOnlyList<PhoneBookMatch>>();
                lock (AddedBatches) AddedBatches.Add(batch);
                foreach (var m in batch) InContacts.Add(m.User.Id);
                return Task.FromResult<IReadOnlyList<ContactDto>>(batch.Select(m => new ContactDto { User = m.User, IsSynced = true }).ToList());
            });
            Reminders.IsSupported.Returns(true);
            Reminders.ShowAsync(Arg.Any<ReminderNotification>()).Returns(Task.CompletedTask).AndDoes(c =>
            {
                lock (Shown) Shown.Add(c.Arg<ReminderNotification>());
            });
            if (enabled) Settings.Current.Contacts.SyncPhoneContacts = true;
            (_, _lifecycle) = Fakes.Account();
            Service = new ContactSyncService(Contacts, Device, Watcher, Permissions, Settings, Local, Reminders, _lifecycle,
                Substitute.For<IServiceProvider>(), TimeProvider.System, Log.For<ContactSyncService>());
        }

        public PhoneBookMatch Found(string name, string? username = null)
        {
            var match = new PhoneBookMatch(name, "+7900" + Random.Shared.Next(1000000, 9999999),
                new UserSummaryDto { Id = Guid.CreateVersion7(), Username = username ?? "friend" + Matches.Count, DisplayName = name }, false);
            Matches.Add(match);
            return match;
        }

        public Task SignInAsync() => _lifecycle.ActivateAsync(Me);
    }

    [Fact]
    public async Task The_first_sync_adds_everyone_quietly_and_later_ones_announce_newcomers_once()
    {
        var h = new Harness();
        h.Found("Мама");
        h.Found("Олег");
        await h.SignInAsync();
        await Wait.UntilAsync(() => h.AddedBatches.Count == 1, "the sign-in sync");

        h.AddedBatches[0].Count.ShouldBe(2);
        h.Shown.ShouldBeEmpty("nobody is announced on the first sync");

        var anna = h.Found("Анна", "anna_s");
        var result = await h.Service.SyncNowAsync();

        result.Added.ShouldBe(1);
        result.Announced.Single().User.Id.ShouldBe(anna.User.Id);
        h.Shown.Single().Title.ShouldBe("Анна is now on SkyMessage");
        h.Shown.Single().Link.ShouldBe(new Uri("skymessage://u/anna_s"));
        (await h.Service.SyncNowAsync()).Added.ShouldBe(0);
        h.Shown.Count.ShouldBe(1, "announced once");
    }

    [Fact]
    public async Task Someone_removed_by_hand_is_not_brought_back()
    {
        var h = new Harness();
        var oleg = h.Found("Олег");
        await h.SignInAsync();
        await Wait.UntilAsync(() => h.AddedBatches.Count == 1, "the sign-in sync");

        h.InContacts.Remove(oleg.User.Id);
        var result = await h.Service.SyncNowAsync();

        result.Added.ShouldBe(0);
        h.AddedBatches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sync_never_asks_for_the_permission_by_itself()
    {
        var h = new Harness();
        h.Found("Олег");
        h.Permissions.CheckAsync(AppPermission.Contacts).Returns(PermissionState.Denied);
        await h.SignInAsync();

        var result = await h.Service.SyncNowAsync();

        result.ShouldBe(ContactSyncResult.None);
        h.Service.IsBlocked.ShouldBeTrue();
        await h.Device.DidNotReceive().RequestPermissionAsync();
        await h.Contacts.DidNotReceive().FindDeviceContactsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Off_means_no_reading_and_turning_on_asks_once_then_syncs_and_watches()
    {
        var h = new Harness(enabled: false);
        h.Found("Олег");
        await h.SignInAsync();

        (await h.Service.SyncNowAsync()).ShouldBe(ContactSyncResult.None);
        h.Watcher.DidNotReceive().SetEnabled(true);

        (await h.Service.SetEnabledAsync(true)).ShouldBeTrue();

        await h.Device.Received(1).RequestPermissionAsync();
        h.Settings.Current.Contacts.SyncPhoneContacts.ShouldBeTrue();
        await Wait.UntilAsync(() => h.AddedBatches.Count == 1, "turning it on syncs");
        h.Watcher.Received().SetEnabled(true);

        await h.Service.SetEnabledAsync(false);
        h.Watcher.Received().SetEnabled(false);
    }

    [Fact]
    public async Task A_refused_permission_keeps_sync_off()
    {
        var h = new Harness(enabled: false);
        h.Device.RequestPermissionAsync().Returns(false);
        await h.SignInAsync();

        (await h.Service.SetEnabledAsync(true)).ShouldBeFalse();

        h.Settings.Current.Contacts.SyncPhoneContacts.ShouldBeFalse();
    }

    [Fact]
    public async Task Removing_synced_contacts_turns_sync_off_and_forgets_whom_it_found()
    {
        var h = new Harness();
        h.Found("Олег");
        h.Contacts.RemoveSyncedAsync().Returns(1);
        await h.SignInAsync();
        await Wait.UntilAsync(() => h.AddedBatches.Count == 1, "the sign-in sync");

        (await h.Service.RemoveSyncedAsync()).ShouldBe(1);

        h.Settings.Current.Contacts.SyncPhoneContacts.ShouldBeFalse();
        h.Local.Values.ContainsKey(ContactSyncService.KnownKey).ShouldBeFalse();
        h.Service.IsEnabled.ShouldBeFalse();
    }
}

public sealed class SyncedContactsStoreTests
{
    [Fact]
    public async Task Sync_adds_marked_contacts_and_removing_them_keeps_the_ones_added_by_hand()
    {
        var me = Guid.CreateVersion7();
        var api = Substitute.For<ISkyApi>();
        var manual = new UserSummaryDto { Id = Guid.CreateVersion7(), Username = "anna", DisplayName = "Anna", IsContact = true };
        var found = new UserSummaryDto { Id = Guid.CreateVersion7(), Username = "oleg", DisplayName = "Oleg" };
        api.GetContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<ContactDto> { new() { User = manual } });
        api.GetBlockedAsync(Arg.Any<CancellationToken>()).Returns(new List<UserSummaryDto>());
        AddContactsBulkRequest? sent = null;
        api.AddContactsBulkAsync(Arg.Do<AddContactsBulkRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new List<ContactDto> { new() { User = found with { IsContact = true }, IsSynced = true } });
        api.RemoveSyncedContactsAsync(Arg.Any<CancellationToken>()).Returns(new RemoveSyncedContactsResultDto { Removed = 1 });
        var users = Substitute.For<IUserStore>();
        var (cache, lifecycle) = Fakes.Account();
        var contacts = new ContactStore(api, Substitute.For<IRealtimeService>(), Fakes.Auth(me), users, Substitute.For<IDeviceContactsService>(),
            Substitute.For<IPlatformInfo>(), cache, lifecycle, Log.For<ContactStore>());
        await lifecycle.ActivateAsync(me);
        await contacts.LoadAsync(force: true);

        var added = await contacts.AddSyncedAsync([new PhoneBookMatch("Олег", "+79001234567", found, false)]);

        sent!.Synced.ShouldBeTrue();
        sent.Contacts.Single().CustomName.ShouldBe("Олег");
        added.Single().IsSynced.ShouldBeTrue();
        contacts.IsContact(found.Id).ShouldBeTrue();

        (await contacts.RemoveSyncedAsync()).ShouldBe(1);

        contacts.IsContact(found.Id).ShouldBeFalse();
        contacts.IsContact(manual.Id).ShouldBeTrue();
        (await contacts.AddSyncedAsync([new PhoneBookMatch("Anna", "+79007654321", manual, true)])).ShouldBeEmpty("people already in contacts stay as they are");
    }
}
