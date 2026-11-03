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
/// Birthday reminders and "joined SkyMessage" notices on the "Reminders" channel. Future reminders are inexact
/// AlarmManager alarms (allowed while idle; no exact-alarm permission needed, a few minutes late is fine) that wake
/// <see cref="ReminderReceiver"/>, which posts the notification from the alarm's own extras, so a reminder also
/// arrives when the app is not running. Each group's reminders are stored, so the next schedule cancels the old alarms
/// and <see cref="ReminderBootReceiver"/> sets them again after a reboot or an update (alarms do not survive those).
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
    private const string StoreName = "sky.reminders";
    private const string GroupsKey = "groups";
    private static readonly int AccentColor = unchecked((int)0xFF6A4BFF);
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
                    r.Notification.Link?.ToString()))
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

    /// <summary>After a reboot or an update: the stored reminders still to come are armed again.</summary>
    internal static void RearmAll(Context context)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var group in Groups())
                foreach (var entry in Load(group).Where(e => e.AtMs > now))
                    Arm(context, entry);
        }
    }

    /// <summary>An alarm went off: the notification it carries.</summary>
    internal static void OnAlarm(Context context, Intent intent)
    {
        var key = intent.GetStringExtra(ExtraKey);
        if (string.IsNullOrEmpty(key)) return;
        Post(context, key, intent.GetStringExtra(ExtraTitle) ?? "SkyMessage", intent.GetStringExtra(ExtraBody) ?? "",
            intent.GetStringExtra(ExtraLink), AppServices.Get<NativeStrings>());
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
            .SetColor(AccentColor)!
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
        alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, entry.AtMs, AlarmIntent(context, entry));
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
            .PutExtra(ExtraLink, entry.Link)!;
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

    private sealed record StoredReminder(string Key, long AtMs, string Title, string Body, string? Link);
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
