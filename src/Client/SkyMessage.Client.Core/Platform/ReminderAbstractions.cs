namespace SkyMessage.Client.Core.Platform;

// Implemented by each host like the rest of PlatformAbstractions.cs (singletons; "IsSupported = false" is fine).

/// <summary>A notification that is not about a message or a call: a birthday, a friend who joined.</summary>
/// <param name="Key">Stable id: the same key replaces the earlier notification instead of adding one.</param>
/// <param name="Link">Opened through <see cref="IDeepLinkSource"/> when tapped (a chat or a profile link); null opens the app.</param>
public sealed record ReminderNotification(string Key, string Title, string Body, Uri? Link);

/// <summary>A reminder due at <paramref name="At"/> (the OS may deliver it a few minutes late to save battery).</summary>
public sealed record ScheduledReminder(DateTimeOffset At, ReminderNotification Notification);

/// <summary>
/// Local reminders. Android: shown on the app's "Reminders" channel; future ones are AlarmManager alarms, which do not
/// survive a reboot or an update, so Core schedules them again at every start (and the app after a reboot from what
/// it stored). Other hosts: <see cref="IsSupported"/> = false — the apps then remind inside the UI only.
/// Implementations never throw.
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
