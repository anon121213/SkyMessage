using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>A contact whose birthday is today.</summary>
/// <param name="Age">The age they turn, when their birthday shows the year.</param>
public sealed record BirthdayPerson(UserSummaryDto User, BirthdayDto Birthday, int? Age);

/// <summary>
/// Texts of reminder notifications, from the UI's localizer (singleton without JS; Core resolves it lazily, English
/// when there is none).
/// </summary>
public interface IReminderTexts
{
    /// <summary>"Сегодня день рождения: Анна".</summary>
    string BirthdayTitle(string name);
    /// <summary>"Исполняется 36 — поздравьте!" (<paramref name="age"/> null: without the age).</summary>
    string BirthdayBody(int? age);
    /// <summary>"Анна теперь в SkyMessage".</summary>
    string JoinedTitle(string name);
    string JoinedBody();
}

/// <summary>
/// Contacts' birthdays (as their privacy lets me see them, <see cref="UserSummaryDto.Birthday"/>): who has one today
/// (the chat list's card, closable for the day), the 🎂 mark for anyone whose birthday shows, and the reminders at
/// 10:00 local time on the day — scheduled for the next <see cref="BirthdayService.ScheduleDays"/> days through
/// <see cref="IReminderNotificationService"/> and again at every start, whenever contacts, their birthdays or the
/// setting change, and after midnight. With <see cref="NotificationSettings.BirthdayReminders"/> off there is no card
/// and no reminder (the mark stays).
/// </summary>
public interface IBirthdayService
{
    /// <summary>The device's date now.</summary>
    DateOnly Today { get; }
    /// <summary>Contacts with a birthday today, by name; empty while reminders are off.</summary>
    IReadOnlyList<BirthdayPerson> Celebrating { get; }
    /// <summary>Today's card was closed: it stays closed until tomorrow.</summary>
    bool IsCardDismissed { get; }
    Task DismissCardAsync();
    /// <summary>This person's birthday (as I may see it) is today — anyone, not only contacts.</summary>
    bool IsBirthdayToday(UserSummaryDto? user);
    /// <summary>Celebrating, the card or the date changed (any thread).</summary>
    event Action? Changed;
}

internal sealed class BirthdayService : IBirthdayService, IAccountScoped
{
    public const string ReminderGroup = "birthdays";
    /// <summary>How far ahead reminders are scheduled (the app starts again long before they run out).</summary>
    public const int ScheduleDays = 14;
    /// <summary>Local time of day the reminder comes.</summary>
    public static readonly TimeSpan ReminderTime = TimeSpan.FromHours(10);
    internal const string DismissedKey = "birthdays.card.dismissed";
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(400);

    private readonly IContactStore _contacts;
    private readonly IUserStore _users;
    private readonly IChatListStore _chats;
    private readonly ISettingsService _settings;
    private readonly IAccountLocalStore _local;
    private readonly IReminderNotificationService _reminders;
    private readonly AccountLifecycle _lifecycle;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<BirthdayService> _logger;
    private readonly CoalescingAction _refresh;
    private readonly object _lock = new();
    private volatile IReadOnlyList<BirthdayPerson> _celebrating = [];
    private volatile string? _dismissedOn;
    private DateOnly _computedFor;
    private string? _scheduledSignature;
    private CancellationTokenSource? _midnight;
    private IReminderTexts? _texts;
    private bool _textsResolved;

    public BirthdayService(
        IContactStore contacts,
        IUserStore users,
        IChatListStore chats,
        ISettingsService settings,
        IAccountLocalStore local,
        IReminderNotificationService reminders,
        AccountLifecycle lifecycle,
        IServiceProvider services,
        TimeProvider time,
        ILogger<BirthdayService> logger)
    {
        _contacts = contacts;
        _users = users;
        _chats = chats;
        _settings = settings;
        _local = local;
        _reminders = reminders;
        _lifecycle = lifecycle;
        _services = services;
        _time = time;
        _logger = logger;
        _refresh = new CoalescingAction(RefreshDelay, RefreshAsync, time, logger);
        contacts.Changed += _refresh.Request;
        users.UserChanged += id =>
        {
            if (contacts.IsContact(id)) _refresh.Request();
        };
        settings.Changed += _refresh.Request;
        lifecycle.Register(this);
    }

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime);

    public IReadOnlyList<BirthdayPerson> Celebrating => _celebrating;

    public bool IsCardDismissed => _dismissedOn == Today.ToString("yyyy-MM-dd");

    public event Action? Changed;

    public bool IsBirthdayToday(UserSummaryDto? user)
    {
        if (user is null || user.IsDeleted) return false;
        var birthday = (_users.Get(user.Id) ?? user).Birthday;
        return birthday is not null && Birthdays.IsOn(birthday, Today);
    }

    public async Task DismissCardAsync()
    {
        var today = Today.ToString("yyyy-MM-dd");
        _dismissedOn = today;
        EventRaiser.Raise(Changed, _logger);
        await _local.SetAsync(DismissedKey, today).ConfigureAwait(false);
    }

    /// <summary>Recomputes now (tests; the stores' events debounce through the same path).</summary>
    internal Task RefreshNowAsync() => RefreshAsync(CancellationToken.None);

    async Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        _dismissedOn = await _local.GetAsync<string>(DismissedKey).ConfigureAwait(false);
        WatchMidnight();
        _refresh.Request();
    }

    async Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        _refresh.Cancel();
        lock (_lock)
        {
            _midnight?.Cancel();
            _midnight = null;
            _scheduledSignature = null;
        }
        _celebrating = [];
        _dismissedOn = null;
        EventRaiser.Raise(Changed, _logger);
        await _reminders.ScheduleAsync(ReminderGroup, []).ConfigureAwait(false);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (_lifecycle.ActiveUserId is null) return;
        var today = Today;
        var enabled = _settings.Current.Notifications.BirthdayReminders;
        var people = enabled ? Contacts().ToList() : [];

        var celebrating = people
            .Where(p => Birthdays.IsOn(p.Birthday, today))
            .Select(p => new BirthdayPerson(p.User, p.Birthday, Birthdays.AgeOn(p.Birthday, today)))
            .OrderBy(p => p.User.ContactName ?? p.User.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var changed = _computedFor != today || !celebrating.Select(Signature).SequenceEqual(_celebrating.Select(Signature));
        _computedFor = today;
        _celebrating = celebrating;
        if (changed) EventRaiser.Raise(Changed, _logger);

        await ScheduleAsync(people, today).ConfigureAwait(false);
    }

    /// <summary>Reminders for the coming days at <see cref="ReminderTime"/>; the past ones of today are left out.</summary>
    private async Task ScheduleAsync(IReadOnlyList<(UserSummaryDto User, BirthdayDto Birthday)> people, DateOnly today)
    {
        if (!_reminders.IsSupported) return;
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var texts = Texts();
        var reminders = new List<ScheduledReminder>();
        foreach (var (user, birthday) in people)
        {
            var date = Birthdays.Next(birthday, today);
            if (date.DayNumber - today.DayNumber >= ScheduleDays) continue;
            var at = LocalInstant(date, ReminderTime, zone);
            if (at <= now) continue;
            var name = user.ContactName ?? user.DisplayName;
            reminders.Add(new ScheduledReminder(at, new ReminderNotification(
                $"birthday:{user.Id:N}:{date:yyyyMMdd}",
                texts.BirthdayTitle(name),
                texts.BirthdayBody(Birthdays.AgeOn(birthday, date)),
                LinkTo(user))));
        }
        reminders.Sort((a, b) => a.At.CompareTo(b.At));

        // Same reminders as last time: nothing to tell the OS.
        var signature = string.Join('|', reminders.Select(r => $"{r.Notification.Key}@{r.At.ToUnixTimeSeconds()}#{r.Notification.Title}#{r.Notification.Body}"));
        lock (_lock)
        {
            if (signature == _scheduledSignature) return;
            _scheduledSignature = signature;
        }
        await _reminders.ScheduleAsync(ReminderGroup, reminders).ConfigureAwait(false);
    }

    private IEnumerable<(UserSummaryDto User, BirthdayDto Birthday)> Contacts()
    {
        foreach (var contact in _contacts.Contacts)
        {
            var user = _users.Get(contact.User.Id) ?? contact.User;
            if (user.IsDeleted || user.Birthday is not { } birthday) continue;
            yield return (user, birthday);
        }
    }

    /// <summary>The chat with them when there is one, else their profile.</summary>
    private Uri? LinkTo(UserSummaryDto user)
    {
        var chat = _chats.Chats.Concat(_chats.ArchivedChats).FirstOrDefault(c => c.Type == ChatType.Private && c.Peer?.Id == user.Id);
        if (chat is not null) return DeepLinks.AppChatUri(chat.Id);
        return string.IsNullOrEmpty(user.Username) ? null : new Uri($"{DeepLinks.AppScheme}://u/{Uri.EscapeDataString(user.Username)}");
    }

    /// <summary>The instant of a local wall-clock time (a time skipped by a DST change moves on by the gap).</summary>
    internal static DateTimeOffset LocalInstant(DateOnly date, TimeSpan timeOfDay, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.FromTimeSpan(timeOfDay), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>After midnight the card, the marks and the reminders move on to the new day.</summary>
    private void WatchMidnight()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _midnight?.Cancel();
            cts = _midnight = new CancellationTokenSource();
        }
        LoopAsync(cts.Token).Forget(_logger);

        async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var now = _time.GetUtcNow();
                var next = LocalInstant(Today.AddDays(1), TimeSpan.Zero, _time.LocalTimeZone);
                var wait = next - now + TimeSpan.FromSeconds(2);
                if (wait < TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
                try
                {
                    await Task.Delay(wait, _time, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _refresh.Request();
            }
        }
    }

    private IReminderTexts Texts()
    {
        if (!_textsResolved)
        {
            _texts = _services.GetService<IReminderTexts>();
            _textsResolved = true;
        }
        return _texts ?? EnglishReminderTexts.Instance;
    }

    private static string Signature(BirthdayPerson p) => $"{p.User.Id}:{p.User.ContactName ?? p.User.DisplayName}:{p.Age}";
}

/// <summary>Fallback when the UI registers no <see cref="IReminderTexts"/>.</summary>
internal sealed class EnglishReminderTexts : IReminderTexts
{
    public static readonly EnglishReminderTexts Instance = new();

    public string BirthdayTitle(string name) => $"Birthday today: {name}";

    public string BirthdayBody(int? age) => age is { } a ? $"Turning {a}. Send your wishes!" : "Send your wishes!";

    public string JoinedTitle(string name) => $"{name} is now on SkyMessage";

    public string JoinedBody() => "Say hello!";
}
