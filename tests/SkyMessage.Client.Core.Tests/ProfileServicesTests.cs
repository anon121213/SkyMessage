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

/// <summary>
/// A frozen wall clock in the UTC+3 test zone whose timers (Task.Delay with this provider) fire only when the test moves
/// it past them, in order.
/// </summary>
internal sealed class SteppingClock(DateTimeOffset start) : TimeProvider
{
    private readonly List<Entry> _timers = [];
    private long _ticks = start.UtcTicks;

    public override TimeZoneInfo LocalTimeZone => ZonedClock.Zone;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>When the timers still waiting are due.</summary>
    public IReadOnlyList<DateTimeOffset> Pending
    {
        get
        {
            lock (_timers) return _timers.Where(t => !t.Disposed).Select(t => t.Due).Order().ToList();
        }
    }

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _ticks, by.Ticks);
        var now = GetUtcNow();
        List<Entry> due;
        lock (_timers)
        {
            due = _timers.Where(t => t.Due <= now).OrderBy(t => t.Due).ToList();
            _timers.RemoveAll(t => t.Due <= now);
        }
        foreach (var timer in due)
            if (!timer.Disposed) timer.Callback(timer.State);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var entry = new Entry(GetUtcNow() + dueTime, callback, state);
        if (dueTime != Timeout.InfiniteTimeSpan)
            lock (_timers) _timers.Add(entry);
        return entry;
    }

    private sealed class Entry(DateTimeOffset due, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; } = due;
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public volatile bool Disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
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
        public SteppingClock Clock { get; }
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
            Clock = new SteppingClock(now);
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

        public int HandedToOs()
        {
            lock (Scheduled) return Scheduled.Count(s => s.Group == BirthdayService.ReminderGroup && s.Reminders.Count > 0);
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
        reminders.Select(r => r.Day).ShouldBe([new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 10)], "the OS drops a reminder that comes on another day");
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
    public async Task People_i_blocked_get_no_card_reminder_or_mark()
    {
        var h = new Harness(MorningUtc);
        var anna = h.Contact("Anna", new BirthdayDto { Day = 4, Month = 11 });
        h.Contacts.IsBlocked(anna.Id).Returns(true);
        await h.SignInAsync();

        await h.Service.RefreshNowAsync();

        h.Service.Celebrating.ShouldBeEmpty();
        h.LastSchedule().ShouldBeEmpty();
        h.Service.IsBirthdayToday(anna).ShouldBeFalse();
    }

    [Fact]
    public async Task After_midnight_the_reminders_go_to_the_os_again_even_when_unchanged()
    {
        var h = new Harness(MorningUtc); // 08:00 local
        h.Contact("Anna", new BirthdayDto { Day = 6, Month = 11 });
        await h.SignInAsync();
        await h.Service.RefreshNowAsync();
        await h.Service.RefreshNowAsync();
        h.HandedToOs().ShouldBe(1);

        h.Clock.Advance(TimeSpan.FromHours(16) + TimeSpan.FromSeconds(3)); // past local midnight
        h.Clock.Advance(TimeSpan.FromSeconds(1)); // the debounced refresh

        await Wait.UntilAsync(() => h.HandedToOs() == 2, "an alarm the OS dropped is set anew");
        h.Service.Today.ShouldBe(new DateOnly(2026, 11, 5));
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
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        private int _lookUps;
        private int _reads;
        private int _numbers;

        public Guid Me { get; } = Guid.CreateVersion7();
        public SteppingClock Clock { get; } = new(Start);
        public IContactStore Contacts { get; } = Substitute.For<IContactStore>();
        public IDeviceContactsService Device { get; } = Substitute.For<IDeviceContactsService>();
        public IDeviceContactsWatcher Watcher { get; } = Substitute.For<IDeviceContactsWatcher>();
        public IPermissionService Permissions { get; } = Substitute.For<IPermissionService>();
        public LiveSettings Settings { get; } = new();
        public MemoryLocalStore Local { get; } = new();
        public IReminderNotificationService Reminders { get; } = Substitute.For<IReminderNotificationService>();
        public List<ReminderNotification> Shown { get; } = [];
        /// <summary>The phone book, in its order.</summary>
        public List<PhoneBookEntry> PhoneBook { get; } = [];
        /// <summary>SkyMessage users findable by their number.</summary>
        public Dictionary<string, UserSummaryDto> Registered { get; } = [];
        /// <summary>My contacts on the server.</summary>
        public Dictionary<Guid, ContactDto> Book { get; } = [];
        public HashSet<Guid> Blocked { get; } = [];
        public Func<Exception?> LookUpFails { get; set; } = () => null;
        /// <summary>Requests the server still takes in its window (then 429, like the real one); null: no limit.</summary>
        public int? QuotaLeft { get; set; }
        /// <summary>How many numbers each look-up request carried.</summary>
        public List<int> Sent { get; } = [];
        public ContactSyncService Service { get; }
        private readonly SkyMessage.Client.Core.Infrastructure.AccountLifecycle _lifecycle;

        public int LookUps => Volatile.Read(ref _lookUps);
        public int Reads => Volatile.Read(ref _reads);
        public ContactSyncState State => Local.Values[ContactSyncService.StateKey].ShouldBeOfType<ContactSyncState>();

        public Harness(bool enabled = true)
        {
            Device.IsSupported.Returns(true);
            Device.RequestPermissionAsync().Returns(true);
            Watcher.IsSupported.Returns(true);
            Permissions.CheckAsync(AppPermission.Contacts).Returns(PermissionState.Granted);
            Contacts.ReadPhoneBookAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Interlocked.Increment(ref _reads);
                lock (PhoneBook) return (IReadOnlyList<PhoneBookEntry>)PhoneBook.ToList();
            });
            Contacts.MatchPhoneBookAsync(Arg.Any<IReadOnlyList<PhoneBookEntry>>(), Arg.Any<CancellationToken>()).Returns(c =>
            {
                Interlocked.Increment(ref _lookUps);
                var entries = c.Arg<IReadOnlyList<PhoneBookEntry>>();
                lock (Sent) Sent.Add(entries.Count);
                if (LookUpFails() is { } error) throw error;
                lock (Book)
                {
                    if (QuotaLeft is { } left)
                    {
                        if (left == 0) throw new ApiException(429, ErrorCodes.RateLimited);
                        QuotaLeft = left - 1;
                    }
                    return (IReadOnlyList<PhoneBookMatch>)entries
                        .Where(e => Registered.ContainsKey(e.Number))
                        .Select(e => new PhoneBookMatch(e.Name, e.Number, Registered[e.Number], Book.ContainsKey(Registered[e.Number].Id)))
                        .ToList();
                }
            });
            Contacts.Contacts.Returns(_ =>
            {
                lock (Book) return (IReadOnlyList<ContactDto>)Book.Values.ToList();
            });
            Contacts.IsContact(Arg.Any<Guid>()).Returns(c =>
            {
                lock (Book) return Book.ContainsKey(c.Arg<Guid>());
            });
            Contacts.IsBlocked(Arg.Any<Guid>()).Returns(c => Blocked.Contains(c.Arg<Guid>()));
            // Like the store and the server: new people come in synced, synced ones take the new name, my own stay.
            Contacts.AddSyncedAsync(Arg.Any<IReadOnlyList<PhoneBookMatch>>()).Returns(c =>
            {
                var changed = new List<ContactDto>();
                lock (Book)
                {
                    foreach (var match in c.Arg<IReadOnlyList<PhoneBookMatch>>())
                    {
                        if (!Book.TryGetValue(match.User.Id, out var contact))
                            Book[match.User.Id] = contact = new ContactDto { User = match.User, CustomName = match.DeviceContactName, IsSynced = true };
                        else if (contact.IsSynced && contact.CustomName != match.DeviceContactName)
                            Book[match.User.Id] = contact = contact with { CustomName = match.DeviceContactName };
                        else
                            continue;
                        changed.Add(contact);
                    }
                }
                return Task.FromResult<IReadOnlyList<ContactDto>>(changed);
            });
            Contacts.RemoveSyncedAsync(Arg.Any<IReadOnlyCollection<Guid>>()).Returns(c =>
            {
                lock (Book)
                {
                    var removed = c.Arg<IReadOnlyCollection<Guid>>().Where(id => Book.TryGetValue(id, out var contact) && contact.IsSynced).ToList();
                    foreach (var id in removed) Book.Remove(id);
                    return Task.FromResult<IReadOnlyList<Guid>>(removed);
                }
            });
            Reminders.IsSupported.Returns(true);
            Reminders.ShowAsync(Arg.Any<ReminderNotification>()).Returns(Task.CompletedTask).AndDoes(c =>
            {
                lock (Shown) Shown.Add(c.Arg<ReminderNotification>());
            });
            if (enabled) Settings.Current.Contacts.SyncPhoneContacts = true;
            (_, _lifecycle) = Fakes.Account();
            Service = new ContactSyncService(Contacts, Device, Watcher, Permissions, Settings, Local, Reminders, _lifecycle,
                Substitute.For<IServiceProvider>(), Clock, Log.For<ContactSyncService>());
        }

        /// <summary>A new number in the phone book; registered: someone on SkyMessage already.</summary>
        public (string Number, UserSummaryDto? User) Save(string name, bool registered = true, string? username = null, string? number = null)
        {
            number ??= "+7900" + (1000000 + Interlocked.Increment(ref _numbers)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (PhoneBook) PhoneBook.Add(new PhoneBookEntry(number, name));
            var user = registered ? Join(number, username ?? "friend" + _numbers) : null;
            return (number, user);
        }

        /// <summary>The person with this number signs up (or lets others find them by it).</summary>
        public UserSummaryDto Join(string number, string username)
        {
            var name = PhoneBook.First(e => e.Number == number).Name;
            var user = new UserSummaryDto { Id = Guid.CreateVersion7(), Username = username, DisplayName = name };
            Registered[number] = user;
            return user;
        }

        public void Rename(string number, string name)
        {
            lock (PhoneBook)
            {
                var i = PhoneBook.FindIndex(e => e.Number == number);
                PhoneBook[i] = PhoneBook[i] with { Name = name };
            }
        }

        public void Delete(string number)
        {
            lock (PhoneBook) PhoneBook.RemoveAll(e => e.Number == number);
        }

        public Task SignInAsync() => _lifecycle.ActivateAsync(Me);

        /// <summary>The sign-in sync has finished.</summary>
        public Task SettledAsync() =>
            Wait.UntilAsync(() => Local.Values.ContainsKey(ContactSyncService.StateKey) && !Service.IsSyncing, "the sign-in sync");

        public Task IdleAfterAsync(int lookUps, string because) => Wait.UntilAsync(() => LookUps == lookUps && !Service.IsSyncing, because);
    }

    [Fact]
    public async Task The_first_sync_adds_everyone_quietly_and_later_ones_announce_only_people_who_really_joined()
    {
        var h = new Harness();
        var mama = h.Save("Мама");
        var oleg = h.Save("Олег");
        var anna = h.Save("Анна", registered: false);
        await h.SignInAsync();
        await h.SettledAsync();

        h.Book.Keys.ShouldBe([mama.User!.Id, oleg.User!.Id], ignoreOrder: true);
        h.Book.Values.ShouldAllBe(c => c.IsSynced);
        h.Shown.ShouldBeEmpty("nobody is announced on the first sync");

        // Анна (her number was in the phone book already) signs up; the daily check finds her.
        var annaUser = h.Join(anna.Number, "anna_s");
        h.Clock.Advance(ContactSyncService.FullCheckEvery);
        await Wait.UntilAsync(() => h.Shown.Count == 1, "the daily check announces Анна");

        h.Shown.Single().Title.ShouldBe("Анна is now on SkyMessage");
        h.Shown.Single().Link.ShouldBe(new Uri("skymessage://u/anna_s"));
        h.Book.ContainsKey(annaUser.Id).ShouldBeTrue();

        // A number saved just now belongs to someone who may have been here for years: added quietly.
        h.Save("Пётр");
        var result = await h.Service.SyncNowAsync();

        result.LookedUp.ShouldBeTrue();
        result.Added.ShouldBe(1);
        result.Announced.ShouldBeEmpty();
        (await h.Service.SyncNowAsync()).Added.ShouldBe(0);
        h.Shown.Count.ShouldBe(1, "announced once");
    }

    [Fact]
    public async Task An_unchanged_phone_book_goes_to_the_server_once_a_day()
    {
        var h = new Harness();
        h.Save("Мама");
        await h.SignInAsync();
        await h.SettledAsync();
        h.LookUps.ShouldBe(1);

        (await h.Service.SyncNowAsync()).LookedUp.ShouldBeFalse("the next start, same numbers");
        h.Clock.Advance(TimeSpan.FromHours(23));
        (await h.Service.SyncNowAsync()).LookedUp.ShouldBeFalse();
        h.LookUps.ShouldBe(1);
        h.Reads.ShouldBe(3, "the phone book itself is read every time");

        h.Save("Олег");
        (await h.Service.SyncNowAsync()).LookedUp.ShouldBeTrue("the numbers changed");
        h.LookUps.ShouldBe(2);
        h.Local.Values[ContactSyncService.StateKey].ShouldBeOfType<ContactSyncState>().Numbers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Phone_book_changes_sync_at_most_every_half_hour()
    {
        var h = new Harness();
        h.Save("Мама");
        await h.SignInAsync();
        await h.SettledAsync();

        h.Save("Олег");
        h.Watcher.Changed += Raise.Event<Action>();
        h.Watcher.Changed += Raise.Event<Action>();

        h.Clock.Pending.ShouldContain(Start + ContactSyncService.WatcherSpacing);
        h.Clock.Advance(ContactSyncService.WatcherSpacing - TimeSpan.FromMinutes(1));
        h.Reads.ShouldBe(1, "no sooner than half an hour after the last sync");
        h.Clock.Advance(TimeSpan.FromMinutes(1));

        await h.IdleAfterAsync(2, "the change synced");
        h.Book.Count.ShouldBe(2);
        h.Reads.ShouldBe(2, "both changes in one sync");
    }

    [Fact]
    public async Task A_rate_limited_look_up_waits_for_the_window_to_end()
    {
        var h = new Harness();
        h.Save("Мама");
        await h.SignInAsync();
        await h.SettledAsync();
        h.LookUpFails = () => new ApiException(429, ErrorCodes.RateLimited);

        h.Save("Олег");
        (await h.Service.SyncNowAsync()).LookedUp.ShouldBeFalse();
        h.Save("Ира");
        await h.Service.SyncNowAsync();

        h.LookUps.ShouldBe(2, "no look-up while paused");
        h.LookUpFails = () => null;
        h.Clock.Advance(ContactSyncService.RateLimitPause);
        await h.IdleAfterAsync(3, "the pause ended");
        h.Book.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_sync_during_a_pause_with_the_daily_check_overdue_reads_the_phone_book_once_and_waits_for_the_pause()
    {
        var h = new Harness();
        h.Save("Мама");
        await h.SignInAsync();
        await h.SettledAsync();
        h.LookUpFails = () => new ApiException(429, ErrorCodes.RateLimited);
        h.Clock.Advance(ContactSyncService.FullCheckEvery);
        await h.IdleAfterAsync(2, "the daily check gets a 429");
        var resume = Start + ContactSyncService.FullCheckEvery + ContactSyncService.RateLimitPause;
        h.Clock.Pending.ShouldBe([resume]);

        var reads = h.Reads;
        (await h.Service.SyncNowAsync()).LookedUp.ShouldBeFalse("an app start during the pause");
        await Task.Delay(200);

        h.Reads.ShouldBe(reads + 1, "nothing is scheduled for a time already past, so nothing runs again at once");
        h.LookUps.ShouldBe(2);
        h.Clock.Pending.ShouldBe([resume], "the next sync comes when the pause ends");

        h.LookUpFails = () => null;
        h.Clock.Advance(ContactSyncService.RateLimitPause);
        await h.IdleAfterAsync(3, "the look-up after the pause");
        h.Clock.Pending.ShouldBe([resume + ContactSyncService.FullCheckEvery]);
    }

    [Fact]
    public async Task A_phone_book_too_big_for_one_window_is_looked_up_over_several_and_keeps_what_each_part_found()
    {
        var h = new Harness();
        // More numbers than one window takes (10 requests of 1000), a SkyMessage user every 500th.
        const int count = Limits.PhoneLookupsPerWindow * Limits.FindByPhonesMax + 1500;
        for (var i = 0; i < count; i++) h.Save("Контакт " + i, registered: i % 500 == 0);
        // Sync sends the numbers in key order: who is among the first n requests.
        var inOrder = h.PhoneBook.OrderBy(e => ContactSyncService.NumberKey(e.Number), StringComparer.Ordinal).ToList();
        int FoundIn(int requests) => inOrder.Take(requests * Limits.FindByPhonesMax).Count(e => h.Registered.ContainsKey(e.Number));
        h.QuotaLeft = 3; // the import used most of this window
        const int perRun = ContactSyncService.LookUpsPerRun;

        await h.SignInAsync();
        await h.IdleAfterAsync(4, "three requests, then a 429");

        h.Book.Count.ShouldBe(FoundIn(3), "the people found before the 429 stay");
        h.State.Round.ShouldNotBeNull().Done.Count.ShouldBe(3 * Limits.FindByPhonesMax);

        h.QuotaLeft = Limits.PhoneLookupsPerWindow; // the server's next window
        h.Clock.Advance(ContactSyncService.RateLimitPause);
        await h.IdleAfterAsync(4 + perRun, "the next part");

        h.Book.Count.ShouldBe(FoundIn(3 + perRun));
        h.QuotaLeft.ShouldBe(Limits.PhoneLookupsPerWindow - perRun, "half the window stays for the import");

        h.QuotaLeft = Limits.PhoneLookupsPerWindow;
        h.Clock.Advance(ContactSyncService.RateLimitPause);
        await h.IdleAfterAsync(4 + perRun + 4, "the rest");

        h.Book.Count.ShouldBe(h.Registered.Count);
        h.State.Round.ShouldBeNull();
        h.State.Numbers.Count.ShouldBe(count);
        h.Sent.ShouldAllBe(n => n <= Limits.FindByPhonesMax);
        h.Shown.ShouldBeEmpty("the first sync stays quiet, however long it takes");
        (await h.Service.SyncNowAsync()).LookedUp.ShouldBeFalse("done until the daily check");
        h.Clock.Pending.ShouldBe([Start + 2 * ContactSyncService.RateLimitPause + ContactSyncService.FullCheckEvery]);
    }

    [Fact]
    public async Task Synced_contacts_follow_the_phone_book_and_the_users_own_stay_as_they_are()
    {
        var h = new Harness();
        h.Save("Мама", registered: false);
        var olya = h.Save("Оля");
        var brother = h.Save("Брат");
        h.Book[brother.User!.Id] = new ContactDto { User = brother.User, CustomName = "Саша" };
        await h.SignInAsync();
        await h.SettledAsync();
        h.Book[olya.User!.Id].IsSynced.ShouldBeTrue();

        h.Rename(olya.Number, "Ольга");
        h.Rename(brother.Number, "Александр");
        var renamed = await h.Service.SyncNowAsync();

        renamed.LookedUp.ShouldBeFalse("names only: nothing to look up");
        renamed.Renamed.ShouldBe(1);
        h.Book[olya.User.Id].CustomName.ShouldBe("Ольга");
        h.Book[brother.User.Id].CustomName.ShouldBe("Саша");

        h.Delete(olya.Number);
        h.Delete(brother.Number);
        var removed = await h.Service.SyncNowAsync();

        removed.Removed.ShouldBe(1);
        h.Book.ContainsKey(olya.User.Id).ShouldBeFalse();
        h.Book.ContainsKey(brother.User.Id).ShouldBeTrue("a contact added by hand stays");

        // Her number comes back, and so does she — quietly: the number is new to the phone book.
        h.Save("Оля", registered: false, number: olya.Number);
        var back = await h.Service.SyncNowAsync();
        back.Added.ShouldBe(1);
        back.Announced.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_phone_book_that_reads_empty_removes_nobody()
    {
        var h = new Harness();
        var olya = h.Save("Оля");
        await h.SignInAsync();
        await h.SettledAsync();

        lock (h.PhoneBook) h.PhoneBook.Clear();

        (await h.Service.SyncNowAsync()).Removed.ShouldBe(0);
        h.Book.ContainsKey(olya.User!.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task Someone_removed_by_hand_is_not_brought_back()
    {
        var h = new Harness();
        var oleg = h.Save("Олег");
        await h.SignInAsync();
        await h.SettledAsync();

        lock (h.Book) h.Book.Remove(oleg.User!.Id);
        h.Clock.Advance(ContactSyncService.FullCheckEvery);
        await h.IdleAfterAsync(2, "the daily check");

        h.Book.ShouldBeEmpty();
    }

    [Fact]
    public async Task Turning_sync_on_again_adds_people_quietly()
    {
        var h = new Harness();
        h.Save("Мама");
        var anna = h.Save("Анна", registered: false);
        await h.SignInAsync();
        await h.SettledAsync();
        await h.Service.SetEnabledAsync(false);

        h.Join(anna.Number, "anna_s");
        (await h.Service.SetEnabledAsync(true)).ShouldBeTrue();

        await h.IdleAfterAsync(2, "turning it on looks up");
        h.Book.Count.ShouldBe(2);
        h.Shown.ShouldBeEmpty("she may have joined any time while sync was off");
    }

    [Fact]
    public async Task Sync_never_asks_for_the_permission_by_itself()
    {
        var h = new Harness();
        h.Save("Олег");
        h.Permissions.CheckAsync(AppPermission.Contacts).Returns(PermissionState.Denied);
        await h.SignInAsync();

        var result = await h.Service.SyncNowAsync();

        result.ShouldBe(ContactSyncResult.None);
        h.Service.IsBlocked.ShouldBeTrue();
        await h.Device.DidNotReceive().RequestPermissionAsync();
        await h.Contacts.DidNotReceive().ReadPhoneBookAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Off_means_no_reading_and_turning_on_asks_once_then_syncs_and_watches()
    {
        var h = new Harness(enabled: false);
        h.Save("Олег");
        await h.SignInAsync();

        (await h.Service.SyncNowAsync()).ShouldBe(ContactSyncResult.None);
        h.Watcher.DidNotReceive().SetEnabled(true);

        (await h.Service.SetEnabledAsync(true)).ShouldBeTrue();

        await h.Device.Received(1).RequestPermissionAsync();
        h.Settings.Current.Contacts.SyncPhoneContacts.ShouldBeTrue();
        await h.IdleAfterAsync(1, "turning it on syncs");
        h.Book.Count.ShouldBe(1);
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
        h.Save("Олег");
        h.Contacts.RemoveSyncedAsync().Returns(1);
        await h.SignInAsync();
        await h.SettledAsync();

        (await h.Service.RemoveSyncedAsync()).ShouldBe(1);

        h.Settings.Current.Contacts.SyncPhoneContacts.ShouldBeFalse();
        h.Local.Values.ContainsKey(ContactSyncService.StateKey).ShouldBeFalse();
        h.Service.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Numbers_are_kept_as_short_hashes_of_their_digits()
    {
        var key = ContactSyncService.NumberKey("+7 (900) 123-45-67");

        key.ShouldBe(ContactSyncService.NumberKey("+79001234567"));
        key.ShouldNotBe(ContactSyncService.NumberKey("89001234567"));
        key.Length.ShouldBe(16);
        key.ShouldNotContain("9001234567");
    }
}

public sealed class SyncedContactsStoreTests
{
    [Fact]
    public async Task Sync_adds_and_renames_its_own_contacts_and_removing_them_keeps_the_ones_added_by_hand()
    {
        var me = Guid.CreateVersion7();
        var api = Substitute.For<ISkyApi>();
        var manual = new UserSummaryDto { Id = Guid.CreateVersion7(), Username = "anna", DisplayName = "Anna", IsContact = true };
        var found = new UserSummaryDto { Id = Guid.CreateVersion7(), Username = "oleg", DisplayName = "Oleg" };
        api.GetContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<ContactDto> { new() { User = manual, CustomName = "Аня" } });
        api.GetBlockedAsync(Arg.Any<CancellationToken>()).Returns(new List<UserSummaryDto>());
        var sent = new List<AddContactsBulkRequest>();
        api.AddContactsBulkAsync(Arg.Do<AddContactsBulkRequest>(sent.Add), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<AddContactsBulkRequest>().Contacts
                .Select(r => new ContactDto { User = found with { IsContact = true, ContactName = r.CustomName }, CustomName = r.CustomName, IsSynced = true })
                .ToList());
        api.RemoveSyncedContactsAsync(Arg.Any<CancellationToken>()).Returns(new RemoveSyncedContactsResultDto { Removed = 1 });
        var users = Substitute.For<IUserStore>();
        var (cache, lifecycle) = Fakes.Account();
        var contacts = new ContactStore(api, Substitute.For<IRealtimeService>(), Fakes.Auth(me), users, Substitute.For<IDeviceContactsService>(),
            Substitute.For<IPlatformInfo>(), cache, lifecycle, Log.For<ContactStore>());
        await lifecycle.ActivateAsync(me);
        await contacts.LoadAsync(force: true);

        var added = await contacts.AddSyncedAsync([new PhoneBookMatch("Олег", "+79001234567", found, false)]);

        sent.Single().Synced.ShouldBeTrue();
        sent.Single().Contacts.Single().CustomName.ShouldBe("Олег");
        added.Single().IsSynced.ShouldBeTrue();
        contacts.IsContact(found.Id).ShouldBeTrue();

        // The phone book renamed both: the synced contact goes to the server with its new name, my own does not.
        var renamed = await contacts.AddSyncedAsync([
            new PhoneBookMatch("Олежка", "+79001234567", found, true),
            new PhoneBookMatch("Анна Б.", "+79007654321", manual, true),
        ]);
        sent.Count.ShouldBe(2);
        sent[1].Contacts.Select(r => (r.UserId, r.CustomName)).ShouldBe([(found.Id, (string?)"Олежка")]);
        renamed.Single().CustomName.ShouldBe("Олежка");
        contacts.Contacts.Single(c => c.User.Id == manual.Id).CustomName.ShouldBe("Аня");
        (await contacts.AddSyncedAsync([new PhoneBookMatch("Олежка", "+79001234567", found, true)])).ShouldBeEmpty("nothing changed");
        sent.Count.ShouldBe(2);

        // Removing by id takes only synced contacts.
        (await contacts.RemoveSyncedAsync([manual.Id])).ShouldBeEmpty();
        await api.DidNotReceive().RemoveContactAsync(manual.Id, Arg.Any<CancellationToken>());

        (await contacts.RemoveSyncedAsync()).ShouldBe(1);

        contacts.IsContact(found.Id).ShouldBeFalse();
        contacts.IsContact(manual.Id).ShouldBeTrue();
        (await contacts.AddSyncedAsync([new PhoneBookMatch("Anna", "+79007654321", manual, true)])).ShouldBeEmpty("people already in contacts stay as they are");
    }

    [Fact]
    public async Task Removing_synced_contacts_by_id_tells_the_server_about_each()
    {
        var me = Guid.CreateVersion7();
        var api = Substitute.For<ISkyApi>();
        var synced = new UserSummaryDto { Id = Guid.CreateVersion7(), Username = "olya", DisplayName = "Olya", IsContact = true };
        api.GetContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<ContactDto> { new() { User = synced, CustomName = "Оля", IsSynced = true } });
        api.GetBlockedAsync(Arg.Any<CancellationToken>()).Returns(new List<UserSummaryDto>());
        var (cache, lifecycle) = Fakes.Account();
        var contacts = new ContactStore(api, Substitute.For<IRealtimeService>(), Fakes.Auth(me), Substitute.For<IUserStore>(),
            Substitute.For<IDeviceContactsService>(), Substitute.For<IPlatformInfo>(), cache, lifecycle, Log.For<ContactStore>());
        await lifecycle.ActivateAsync(me);
        await contacts.LoadAsync(force: true);

        (await contacts.RemoveSyncedAsync([synced.Id, Guid.CreateVersion7()])).ShouldBe([synced.Id]);

        await api.Received(1).RemoveContactAsync(synced.Id, Arg.Any<CancellationToken>());
        contacts.IsContact(synced.Id).ShouldBeFalse();
    }
}

public sealed class RussianNamesTests
{
    [Theory]
    [InlineData("Анна", "Анны")]
    [InlineData("Мама", "Мамы")]
    [InlineData("мама", "мамы")]
    [InlineData("МАМА", "МАМЫ")]
    [InlineData("Ольга", "Ольги")]
    [InlineData("Саша", "Саши")]
    [InlineData("Бабушка", "Бабушки")]
    [InlineData("Таня", "Тани")]
    [InlineData("Мария", "Марии")]
    [InlineData("Илья", "Ильи")]
    [InlineData("Ирина", "Ирины")]
    [InlineData("Олег", "Олега")]
    [InlineData("Андрей", "Андрея")]
    [InlineData("Николай", "Николая")]
    [InlineData("Юрий", "Юрия")]
    [InlineData("Павел", "Павла")]
    [InlineData("Лев", "Льва")]
    [InlineData("Пётр", "Петра")]
    [InlineData("Игорь", "Игоря")]
    [InlineData("Любовь", "Любови")]
    [InlineData("Дочь", "Дочери")]
    [InlineData("Любимая", "Любимой")]
    [InlineData("Крёстный", "Крёстного")]
    [InlineData("Петрова", "Петровой")]
    [InlineData("Ковальская", "Ковальской")]
    [InlineData("Достоевский", "Достоевского")]
    [InlineData("Иван Петров", "Ивана Петрова")]
    [InlineData("Анна Петрова", "Анны Петровой")]
    [InlineData("Ирина Ильина", "Ирины Ильиной")]
    [InlineData("Иванова Мария", "Ивановой Марии")]
    [InlineData("Анна Сергеевна", "Анны Сергеевны")]
    [InlineData("Иван Иванович Петров", "Ивана Ивановича Петрова")]
    [InlineData("Петрова Анна Сергеевна", "Петровой Анны Сергеевны")]
    [InlineData("Тётя Света", "Тёти Светы")]
    [InlineData("Дядя Коля", "Дяди Коли")]
    [InlineData("Константин Ильин", "Константина Ильина")]
    [InlineData("John", "John")]
    [InlineData("John Smith", "John Smith")]
    // Short names in -ая are names, not adjectives.
    [InlineData("Рая", "Раи")]
    [InlineData("Тая", "Таи")]
    [InlineData("Зая", "Заи")]
    [InlineData("Аглая", "Аглаи")]
    [InlineData("Тётя Рая", "Тёти Раи")]
    [InlineData("Бабушка Рая", "Бабушки Раи")]
    [InlineData("Родная", "Родной")]
    [InlineData("Милая", "Милой")]
    [InlineData("Дорогая", "Дорогой")]
    [InlineData("Крёстная", "Крёстной")]
    // Left as they are, which is the right form.
    [InlineData("Черных", "Черных")]
    [InlineData("Седых", "Седых")]
    [InlineData("Долгих", "Долгих")]
    [InlineData("Иван Черных", "Ивана Черных")]
    [InlineData("Анна Седых", "Анны Седых")]
    [InlineData("Шевченко", "Шевченко")]
    [InlineData("Анна Шевченко", "Анны Шевченко")]
    [InlineData("Нелли", "Нелли")]
    [InlineData("Ренато", "Ренато")]
    [InlineData("Тётя Нелли", "Тёти Нелли")]
    [InlineData("Ирэн", "Ирэн")]
    [InlineData("Мариам", "Мариам")]
    [InlineData("Мариам Петрова", "Мариам Петровой")]
    [InlineData("Гульнур Ахметова", "Гульнур Ахметовой")]
    [InlineData("Ким Петрова", "Ким Петровой")]
    // A man's name, or one the rest of the name shows to be a man's.
    [InlineData("Ким Петров", "Кима Петрова")]
    [InlineData("Ашот Петров", "Ашота Петрова")]
    [InlineData("Эмиль Ахметов", "Эмиля Ахметова")]
    [InlineData("Георгий", "Георгия")]
    [InlineData("Генрих", "Генриха")]
    [InlineData("Брат", "Брата")]
    [InlineData("Сосед", "Соседа")]
    [InlineData("Котик", "Котика")]
    [InlineData("Михалыч", "Михалыча")]
    public void Declines_names_it_is_sure_of(string name, string genitive) => RussianNames.Genitive(name).ShouldBe(genitive);

    [Theory]
    [InlineData("Саша работа")]
    [InlineData("Саша Работа")]
    [InlineData("Анна Ким")]
    [InlineData("Олег Шевчук")]
    [InlineData("Ильина")]
    [InlineData("Солнышко")]
    [InlineData("Мама ❤️")]
    [InlineData("Мама 2")]
    [InlineData("Анна-Мария")]
    [InlineData("Artur Петров")]
    [InlineData("Старший")]
    [InlineData("Один Два Три Четыре")]
    [InlineData("")]
    [InlineData("   ")]
    // A single word in a consonant may be a woman's, who keeps it as it is.
    [InlineData("Ковальчук")]
    [InlineData("Петросян")]
    [InlineData("Ануш")]
    [InlineData("Ким")]
    [InlineData("Иван Петрова")]
    // The ending depends on the stress, or the word is not a name.
    [InlineData("Старшая")]
    [InlineData("Большая")]
    [InlineData("Синяя")]
    [InlineData("Счастье")]
    [InlineData("Солнце")]
    [InlineData("Родители")]
    [InlineData("Ребята")]
    [InlineData("Марек")]
    [InlineData("Адель")]
    public void Leaves_names_it_is_unsure_of(string name) => RussianNames.Genitive(name).ShouldBeNull();
}

public sealed class ReminderRulesTests
{
    [Fact]
    public void A_reminder_shows_on_its_own_day_and_not_hours_late()
    {
        var at = new DateTimeOffset(2026, 11, 4, 7, 0, 0, TimeSpan.Zero); // 10:00 at UTC+3
        var day = new DateOnly(2026, 11, 4);

        ReminderRules.IsDue(at, day, at, day).ShouldBeTrue();
        ReminderRules.IsDue(at, day, at.AddHours(5), day).ShouldBeTrue("a little late is fine");
        ReminderRules.IsDue(at, day, at + ReminderRules.MaxDelay + TimeSpan.FromMinutes(1), day).ShouldBeFalse("hours late");
        ReminderRules.IsDue(at, day, at.AddMinutes(30), day.AddDays(1)).ShouldBeFalse("another day (the phone moved to another time zone)");
        ReminderRules.IsDue(at, null, at.AddHours(1), day.AddDays(1)).ShouldBeTrue("a reminder without a day of its own");
        ReminderRules.IsDue(at, day, at.AddHours(-2), day).ShouldBeFalse("not before its time");
    }
}
