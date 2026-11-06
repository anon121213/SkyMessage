using System.Globalization;
using System.Text.Json;
using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;
using AndroidUri = Android.Net.Uri;

namespace SkyMessage.App.Droid;

/// <summary>
/// Birthday reminders and "joined SkyMessage" notices on the "Reminders" channel. Future reminders are AlarmManager
/// alarms allowed while idle: exact on Android 8–11 (no permission needed there; an inexact alarm may come days late
/// on those versions), inexact on 12+ (at most an hour late there; exact alarms would need a permission). They wake
/// <see cref="ReminderReceiver"/>, which posts the notification from the alarm's own extras, so a reminder also
/// arrives when the app is not running — unless it comes on another day than its own or more than
/// <see cref="ReminderRules.MaxDelay"/> late (then it is dropped: "today" would be wrong). Each group's reminders are
/// stored until their alarm goes off, so the next schedule cancels the old alarms and <see cref="ReminderBootReceiver"/>
/// sets the rest again after a reboot or an update (alarms do not survive those): the ones still to come and one missed
/// while the phone was off that is still due — never one already shown or dropped.
/// Tapping a reminder opens its link (a chat or a profile) through MainActivity.
/// </summary>
public sealed class AndroidReminderService(NativeStrings strings, ILogger<AndroidReminderService> logger) : IReminderNotificationService
{
    public const string Channel = "reminders.v1";
    public const string ActionReminder = "com.skymessage.action.REMINDER";
    private const string ExtraKey = "com.skymessage.extra.REMINDER_KEY";
    private const string ExtraTitle = "com.skymessage.extra.REMINDER_TITLE";
    private const string ExtraBody = "com.skymessage.extra.REMINDER_BODY";
    private const string ExtraLink = "com.skymessage.extra.REMINDER_LINK";
    private const string ExtraAt = "com.skymessage.extra.REMINDER_AT";
    private const string ExtraDay = "com.skymessage.extra.REMINDER_DAY";
    private const string DayFormat = "yyyyMMdd";
    private const string StoreName = "sky.reminders";
    private const string GroupsKey = "groups";
    private static readonly object Gate = new();

    private static Context Context => Android.App.Application.Context;

    public bool IsSupported => true;

    public Task ShowAsync(ReminderNotification notification)
    {
        try
        {
            Post(Context, notification.Key, notification.Title, notification.Body, notification.Link?.ToString(), strings);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reminder notification failed");
        }
        return Task.CompletedTask;
    }

    public Task ScheduleAsync(string group, IReadOnlyList<ScheduledReminder> reminders)
    {
        try
        {
            var entries = reminders
                .Select(r => new StoredReminder(r.Notification.Key, r.At.ToUnixTimeMilliseconds(), r.Notification.Title, r.Notification.Body,
                    r.Notification.Link?.ToString(), r.Day?.ToString(DayFormat, CultureInfo.InvariantCulture)))
                .ToList();
            lock (Gate)
            {
                foreach (var old in Load(group)) Cancel(Context, old);
                Save(group, entries);
                foreach (var entry in entries) Arm(Context, entry);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduling reminders failed");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// After a reboot or an update. A reminder leaves the store when its alarm goes off (<see cref="OnAlarm"/>), so the
    /// ones stored are still to come — armed again — or came due while the phone was off and never went off: armed for
    /// now, and shown if still worth it. Ones too late for that are forgotten.
    /// </summary>
    internal static void RearmAll(Context context)
    {
        lock (Gate)
        {
            var oldest = DateTimeOffset.UtcNow.Subtract(ReminderRules.MaxDelay).ToUnixTimeMilliseconds();
            foreach (var group in Groups())
            {
                var stored = Load(group);
                var pending = stored.Where(e => e.AtMs > oldest).ToList();
                if (pending.Count != stored.Count) Save(group, pending);
                foreach (var entry in pending) Arm(context, entry);
            }
        }
    }

    /// <summary>
    /// An alarm went off: the notification it carries, unless it is late by hours or on another day. Either way it
    /// leaves the store, so a reboot or an update later that day does not bring it back.
    /// </summary>
    internal static void OnAlarm(Context context, Intent intent)
    {
        var key = intent.GetStringExtra(ExtraKey);
        if (string.IsNullOrEmpty(key)) return;
        try
        {
            Forget(key);
        }
        catch (Exception)
        {
            // The notification still comes; only a reboot today could bring it back.
        }
        var at = intent.GetLongExtra(ExtraAt, 0);
        var day = DateOnly.TryParseExact(intent.GetStringExtra(ExtraDay), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : (DateOnly?)null;
        if (at > 0)
        {
            var now = DateTimeOffset.UtcNow;
            if (!ReminderRules.IsDue(DateTimeOffset.FromUnixTimeMilliseconds(at), day, now, LocalToday(now))) return;
        }
        Post(context, key, intent.GetStringExtra(ExtraTitle) ?? "SkyMessage", intent.GetStringExtra(ExtraBody) ?? "",
            intent.GetStringExtra(ExtraLink), AppServices.Get<NativeStrings>());
    }

    /// <summary>The phone's date now, in its current time zone (Java's, which follows a zone change at once).</summary>
    private static DateOnly LocalToday(DateTimeOffset now)
    {
        var ms = now.ToUnixTimeMilliseconds();
        var offset = Java.Util.TimeZone.Default?.GetOffset(ms) ?? 0;
        return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms + offset).UtcDateTime);
    }

    private static void Post(Context context, string key, string title, string body, string? link, NativeStrings? strings)
    {
        var manager = NotificationManagerCompat.From(context)!;
        if (!manager.AreNotificationsEnabled()) return;
        EnsureChannel(context, strings);

        var code = CodeOf(key);
        var data = Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == DeepLinks.AppScheme ? AndroidUri.Parse(uri.ToString()) : null;
        var content = AppIntents.ActivityPendingIntent(context, code, AppIntents.ActivityIntent(context, data is null ? null : Intent.ActionView, data: data));
        var notification = new NotificationCompat.Builder(context, Channel)
            .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
            .SetColor(NotificationChannels.AccentColor)!
            .SetContentTitle(title)!
            .SetContentText(body)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))!
            .SetContentIntent(content)!
            .SetAutoCancel(true)!
            .SetCategory(NotificationCompat.CategoryReminder)!
            .SetVisibility(NotificationCompat.VisibilityPrivate)!
            .SetPriority(NotificationCompat.PriorityDefault)!
            .Build()!;
        manager.Notify(code, notification);
    }

    private static void EnsureChannel(Context context, NativeStrings? strings)
    {
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        var name = strings?["platform.channel.reminders"] ?? "Reminders";
        var existing = manager.GetNotificationChannel(Channel);
        if (existing is not null && existing.Name == name) return;
        var channel = new NotificationChannel(Channel, name, NotificationImportance.Default)
        {
            Description = strings?["platform.channel.reminders.description"] ?? "",
            LockscreenVisibility = NotificationVisibility.Private,
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }

    private static void Arm(Context context, StoredReminder entry)
    {
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms) return;
        var pending = AlarmIntent(context, entry);
        // Before Android 12 an inexact alarm may come as late as 3/4 of the time until it (days, two weeks ahead), and an
        // exact one needs no permission; from 12 on inexact alarms are at most an hour late and exact ones need one.
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, entry.AtMs, pending);
        else alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, entry.AtMs, pending);
    }

    private static void Cancel(Context context, StoredReminder entry)
    {
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms) return;
        var pending = AlarmIntent(context, entry);
        alarms.Cancel(pending);
        pending.Cancel();
    }

    /// <summary>One PendingIntent per reminder: its data URI keeps alarms with equal request codes apart.</summary>
    private static PendingIntent AlarmIntent(Context context, StoredReminder entry)
    {
        var intent = new Intent(context, typeof(ReminderReceiver))
            .SetAction(ActionReminder)!
            .SetData(AndroidUri.Parse("skymessage-reminder://" + Uri.EscapeDataString(entry.Key)))!
            .PutExtra(ExtraKey, entry.Key)!
            .PutExtra(ExtraTitle, entry.Title)!
            .PutExtra(ExtraBody, entry.Body)!
            .PutExtra(ExtraLink, entry.Link)!
            .PutExtra(ExtraAt, entry.AtMs)!
            .PutExtra(ExtraDay, entry.Day)!;
        return PendingIntent.GetBroadcast(context, CodeOf(entry.Key), intent, AppIntents.ImmutableUpdate)!;
    }

    private static int CodeOf(string key)
    {
        // Stable across processes (string.GetHashCode is randomized per process).
        var hash = 17;
        foreach (var c in key) hash = unchecked(hash * 31 + c);
        return 0x20000 + (hash & 0x0FFFFFFF);
    }

    private static IReadOnlyList<string> Groups() =>
        (Preferences.Default.Get(GroupsKey, "", StoreName) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);

    private static List<StoredReminder> Load(string group)
    {
        try
        {
            var json = Preferences.Default.Get("group." + group, "", StoreName);
            return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<StoredReminder>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Takes a reminder whose alarm went off out of its group.</summary>
    private static void Forget(string key)
    {
        lock (Gate)
        {
            foreach (var group in Groups())
            {
                var entries = Load(group);
                if (entries.RemoveAll(e => e.Key == key) > 0) Save(group, entries);
            }
        }
    }

    private static void Save(string group, List<StoredReminder> entries)
    {
        Preferences.Default.Set("group." + group, JsonSerializer.Serialize(entries), StoreName);
        var groups = Groups().ToList();
        if (!groups.Contains(group))
        {
            groups.Add(group);
            Preferences.Default.Set(GroupsKey, string.Join('|', groups), StoreName);
        }
    }

    /// <param name="Day">The local day it is about (<see cref="DayFormat"/>), null when any day will do.</param>
    private sealed record StoredReminder(string Key, long AtMs, string Title, string Body, string? Link, string? Day = null);
}

/// <summary>A reminder's alarm: posts the notification it carries (works with the app closed).</summary>
[BroadcastReceiver(Name = "com.skymessage.messenger.ReminderReceiver", Exported = false, Enabled = true)]
public sealed class ReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != AndroidReminderService.ActionReminder) return;
        try
        {
            AndroidReminderService.OnAlarm(context, intent);
        }
        catch (Exception)
        {
            // An OS callback must never crash the process.
        }
    }
}

/// <summary>Alarms are gone after a reboot or an app update: the stored reminders still to come are set again.</summary>
[BroadcastReceiver(Name = "com.skymessage.messenger.ReminderBootReceiver", Exported = false, Enabled = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced, "android.intent.action.QUICKBOOT_POWERON"])]
public sealed class ReminderBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        try
        {
            AndroidReminderService.RearmAll(context);
        }
        catch (Exception)
        {
            // Core schedules them again at the next start anyway.
        }
    }
}
