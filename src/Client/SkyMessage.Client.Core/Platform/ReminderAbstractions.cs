namespace SkyMessage.Client.Core.Platform;

// Implemented by each host like the rest of PlatformAbstractions.cs (singletons; "IsSupported = false" is fine).

/// <summary>A notification that is not about a message or a call: a birthday, a friend who joined.</summary>
/// <param name="Key">Stable id: the same key replaces the earlier notification instead of adding one.</param>
/// <param name="Link">Opened through <see cref="IDeepLinkSource"/> when tapped (a chat or a profile link); null opens the app.</param>
public sealed record ReminderNotification(string Key, string Title, string Body, Uri? Link);

/// <summary>A reminder due at <paramref name="At"/> (the OS may deliver it a little late to save battery).</summary>
/// <param name="Day">
/// The local calendar day it is about (a birthday): a reminder the OS delivers on another day, or hours late, is
/// dropped instead of shown.
/// </param>
public sealed record ScheduledReminder(DateTimeOffset At, ReminderNotification Notification, DateOnly? Day = null);

/// <summary>
/// Local reminders. Android: shown on the app's "Reminders" channel; future ones are AlarmManager alarms, which do not
/// survive a reboot or an update, so Core schedules them again at every start and after midnight (and the app after a
/// reboot from what it stored). A reminder that arrives on another <see cref="ScheduledReminder.Day"/>, or more than
/// <see cref="ReminderRules.MaxDelay"/> late, is not shown. Other hosts: <see cref="IsSupported"/> = false — the apps
/// then remind inside the UI only. Implementations never throw.
/// </summary>
public interface IReminderNotificationService
{
    bool IsSupported { get; }
    /// <summary>Shows it now (no-op when notifications are off or not allowed).</summary>
    Task ShowAsync(ReminderNotification notification);
    /// <summary>Replaces every pending reminder of <paramref name="group"/> with <paramref name="reminders"/> (empty cancels them).</summary>
    Task ScheduleAsync(string group, IReadOnlyList<ScheduledReminder> reminders);
}

/// <summary>
/// Tells when the phone book changes, so contact sync can read it again (Android: a ContentObserver on the contacts
/// provider, registered only while sync is on and the permission is granted). Other hosts: <see cref="IsSupported"/> =
/// false and sync runs at app start only. <see cref="Changed"/> may fire in bursts on any thread; callers debounce.
/// </summary>
public interface IDeviceContactsWatcher
{
    bool IsSupported { get; }
    void SetEnabled(bool enabled);
    event Action? Changed;
}

/// <summary>When a scheduled reminder is still worth showing (hosts apply it when the OS delivers the alarm).</summary>
public static class ReminderRules
{
    /// <summary>How late a reminder may still come (a phone that was off, a battery saver holding the alarm back).</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(6);

    /// <summary>
    /// Show it now? Not before its time (a clock moved back), not more than <see cref="MaxDelay"/> late, and only on
    /// its own local <paramref name="day"/> when it has one.
    /// </summary>
    public static bool IsDue(DateTimeOffset at, DateOnly? day, DateTimeOffset now, DateOnly today) =>
        now >= at - TimeSpan.FromMinutes(5) && now - at <= MaxDelay && (day is null || day == today);
}

/// <summary>Hosts without OS reminders (browser, iOS/Mac for now): the UI reminds in the app.</summary>
public sealed class UnsupportedReminderNotificationService : IReminderNotificationService
{
    public bool IsSupported => false;

    public Task ShowAsync(ReminderNotification notification) => Task.CompletedTask;

    public Task ScheduleAsync(string group, IReadOnlyList<ScheduledReminder> reminders) => Task.CompletedTask;
}

/// <summary>Hosts that cannot watch the phone book: contact sync runs at app start only.</summary>
public sealed class UnsupportedDeviceContactsWatcher : IDeviceContactsWatcher
{
    public bool IsSupported => false;

    public void SetEnabled(bool enabled)
    {
    }

    public event Action? Changed
    {
        add { }
        remove { }
    }
}
