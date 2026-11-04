using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;
using Person = AndroidX.Core.App.Person;

namespace SkyMessage.App.Droid;

/// <summary>
/// Message notifications (one per chat with the latest lines, bundled under a summary), the incoming-call
/// notification (call style, full-screen intent, ringtone looped with FLAG_INSISTENT until answered,
/// declined, cancelled or the app comes to the foreground) and call-action dispatch. Accept actions are
/// Activity PendingIntents so the app is in the foreground before the call service starts. With the app passcode's
/// "hide notification text", a hidden message replaces its chat's lines with one neutral line, and
/// <see cref="RedactMessagesAsync"/> does that to every message notification in the shade — read back from the
/// system, so those posted by an earlier run of the process are redacted too.
/// </summary>
public sealed class AndroidNotificationService : ILocalNotificationService
{
    private const string MessagesGroup = "com.skymessage.MESSAGES";
    private const int MaxLinesPerChat = 6;
    /// <summary>A post this recent may not be listed by the system yet (posting is asynchronous there).</summary>
    private const long RecentPostMs = 5_000;
    private static readonly int AccentColor = unchecked((int)0xFF6A4BFF);

    private readonly NotificationChannels _channels;
    private readonly NativeStrings _strings;
    private readonly IPermissionService _permissions;
    private readonly ILogger<AndroidNotificationService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ChatLines> _chats = [];
    private readonly Queue<(Guid CallId, CallNotificationAction Action)> _pendingActions = new();
    private Action<Guid, CallNotificationAction>? _callActionHandlers;
    private Guid? _ringingCallId;
    private int _badge;

    public AndroidNotificationService(NotificationChannels channels, NativeStrings strings, IPermissionService permissions,
        IAppLifecycle lifecycle, ILogger<AndroidNotificationService> logger)
    {
        _channels = channels;
        _strings = strings;
        _permissions = permissions;
        _logger = logger;
        lifecycle.ForegroundChanged += foreground =>
        {
            // The in-app call screen takes over ringing once the user is looking at the app.
            if (foreground && _ringingCallId is { } callId) _ = CancelCallAsync(callId);
        };
    }

    private static Context Context => Android.App.Application.Context;

    private static NotificationManagerCompat Manager => NotificationManagerCompat.From(Context)!;

    /// <summary>Raised for accept/decline from notifications once Core has started (see <see cref="DispatchPendingAsync"/>).</summary>
    public event Action<Guid, CallNotificationAction>? CallActionInvoked
    {
        add
        {
            bool pending;
            lock (_gate)
            {
                _callActionHandlers += value;
                pending = _pendingActions.Count > 0;
            }
            if (pending) _ = Task.Run(DispatchPendingAsync);
        }
        remove
        {
            lock (_gate) _callActionHandlers -= value;
        }
    }

    public async Task<bool> RequestPermissionAsync() =>
        await _permissions.RequestAsync(AppPermission.Notifications).ConfigureAwait(false) == PermissionState.Granted;

    public Task ShowMessageAsync(LocalNotification notification)
    {
        try
        {
            if (!Manager.AreNotificationsEnabled()) return Task.CompletedTask;
            var channel = _channels.Messages(notification.Silent);
            ChatLines lines;
            int totalChats;
            string title;
            int count;
            List<string> shown;
            lock (_gate)
            {
                if (!_chats.TryGetValue(notification.ChatId, out lines!))
                {
                    lines = new ChatLines();
                    _chats[notification.ChatId] = lines;
                }
                if (notification.Hidden) lines.AddHidden(notification.Title, notification.Body);
                else lines.Add(notification.Title, notification.Body);
                totalChats = _chats.Count;
                title = lines.Title;
                count = lines.Count;
                shown = [.. lines.Lines];
            }

            var id = AppIntents.CodeOf(notification.ChatId);
            var builder = MessageBuilder(channel, title, shown, count, ChatIntent(notification.ChatId))
                .SetSilent(notification.Silent)!
                .SetOnlyAlertOnce(false)!;
            Manager.Notify(id, builder.Build()!);
            if (totalChats > 1) PostSummary(channel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Message notification failed");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Every message notification in the shade gets the neutral text, silently, keeping its chat (the tap still opens it)
    /// and its count: chats this process posted from their own lines (also one posted a moment ago, which the system may
    /// not list yet), the rest — an earlier run's — as the system lists them. Dismissed ones stay dismissed. The summary
    /// is posted again without chat names.
    /// </summary>
    public Task RedactMessagesAsync(string title, string body)
    {
        try
        {
            var system = Context.GetSystemService(Context.NotificationService) as NotificationManager;
            var active = system?.GetActiveNotifications() ?? [];
            var activeIds = active.Where(n => n.Tag is null).Select(n => n.Id).ToHashSet();
            var now = Environment.TickCount64;
            List<(Guid ChatId, int Count)> own = [];
            lock (_gate)
            {
                foreach (var (chatId, lines) in _chats)
                {
                    lines.Hide(title, body);
                    if (activeIds.Contains(AppIntents.CodeOf(chatId)) || now - lines.PostedAt < RecentPostMs) own.Add((chatId, lines.Count));
                }
            }

            var silent = _channels.Messages(silent: true);
            var redacted = new HashSet<int>();
            foreach (var (chatId, count) in own)
            {
                var id = AppIntents.CodeOf(chatId);
                Manager.Notify(id, MessageBuilder(silent, title, [body], count, ChatIntent(chatId)).SetSilent(true)!.SetOnlyAlertOnce(true)!.Build()!);
                redacted.Add(id);
            }

            var summaryShown = false;
            foreach (var posted in active)
            {
                var shown = posted.Notification;
                // Calls carry a tag; foreign groups (the background connection) are not a chat's notification.
                if (shown is null || posted.Tag is not null || shown.Group != MessagesGroup) continue;
                if (posted.Id == AppIntents.MessagesSummaryNotificationId || (shown.Flags & NotificationFlags.GroupSummary) != 0)
                {
                    summaryShown = true;
                    continue;
                }
                if (!redacted.Add(posted.Id)) continue;
                var builder = MessageBuilder(shown.ChannelId ?? silent, title, [body], Math.Max(1, shown.Number), shown.ContentIntent)
                    .SetSilent(true)!
                    .SetOnlyAlertOnce(true)!;
                if (shown.When > 0) builder.SetWhen(shown.When);
                Manager.Notify(posted.Id, builder.Build()!);
            }
            if (summaryShown || redacted.Count > 1) PostSummary(silent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redacting message notifications failed");
        }
        return Task.CompletedTask;
    }

    public Task CancelForChatAsync(Guid chatId)
    {
        try
        {
            int remaining;
            lock (_gate)
            {
                _chats.Remove(chatId);
                remaining = _chats.Count;
            }
            Manager.Cancel(AppIntents.CodeOf(chatId));
            if (remaining == 0) Manager.Cancel(AppIntents.MessagesSummaryNotificationId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancelling notifications failed");
        }
        return Task.CompletedTask;
    }

    public Task ShowIncomingCallAsync(CallDto call, string callerName)
    {
        try
        {
            if (!Manager.AreNotificationsEnabled()) return Task.CompletedTask;
            var channel = _channels.IncomingCall();
            var code = AppIntents.CodeOf(call.Id, 0x55);

            var fullScreen = AppIntents.ActivityPendingIntent(Context, code, AppIntents.ActivityIntent(Context, AppIntents.ActionIncomingCall, call.Id));
            var acceptAudio = AppIntents.ActivityPendingIntent(Context, code + 1, AppIntents.ActivityIntent(Context, AppIntents.ActionCallAccept, call.Id));
            var acceptVideo = AppIntents.ActivityPendingIntent(Context, code + 2, AppIntents.ActivityIntent(Context, AppIntents.ActionCallAcceptVideo, call.Id));
            var decline = AppIntents.BroadcastPendingIntent(Context, code + 3, AppIntents.ActionCallDecline, call.Id);

            var person = new Person.Builder().SetName(callerName)!.SetImportant(true)!.Build()!;
            var style = NotificationCompat.CallStyle.ForIncomingCall(person, decline, call.IsVideo ? acceptVideo : acceptAudio)!
                .SetIsVideo(call.IsVideo)!;

            var builder = new NotificationCompat.Builder(Context, channel)
                .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
                .SetColor(AccentColor)!
                .SetContentTitle(callerName)!
                .SetContentText(_strings[call.IsVideo ? "platform.call.incomingVideo" : "platform.call.incoming"])!
                .SetCategory(NotificationCompat.CategoryCall)!
                .SetPriority(NotificationCompat.PriorityMax)!
                .SetVisibility(NotificationCompat.VisibilityPublic)!
                .SetOngoing(true)!
                .SetAutoCancel(false)!
                .SetContentIntent(fullScreen)!
                .SetFullScreenIntent(fullScreen, true)!
                .SetTimeoutAfter((Limits.CallRingTimeoutSeconds + 5) * 1000L)!
                .SetStyle(style)!;
            if (call.IsVideo)
                builder.AddAction(new NotificationCompat.Action(0, _strings["platform.call.acceptAudio"], acceptAudio));

            var notification = builder.Build()!;
            notification.Flags |= NotificationFlags.Insistent;
            Manager.Notify(call.Id.ToString(), AppIntents.IncomingCallNotificationId, notification);
            _ringingCallId = call.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incoming call notification failed");
        }
        return Task.CompletedTask;
    }

    public Task CancelCallAsync(Guid callId)
    {
        try
        {
            Manager.Cancel(callId.ToString(), AppIntents.IncomingCallNotificationId);
            if (_ringingCallId == callId) _ringingCallId = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancelling the call notification failed");
        }
        return Task.CompletedTask;
    }

    /// <summary>Launchers show dots from notifications; the count rides on the bundle summary where supported.</summary>
    public Task SetBadgeCountAsync(int count)
    {
        _badge = Math.Max(0, count);
        if (_badge == 0)
        {
            lock (_gate)
            {
                if (_chats.Count > 0) return Task.CompletedTask;
            }
            try
            {
                Manager.Cancel(AppIntents.MessagesSummaryNotificationId);
            }
            catch (Exception)
            {
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>From MainActivity (accept) and CallActionReceiver (decline).</summary>
    internal void OnCallAction(Guid callId, CallNotificationAction action)
    {
        _ = CancelCallAsync(callId);
        lock (_gate) _pendingActions.Enqueue((callId, action));
        _ = Task.Run(DispatchPendingAsync);
    }

    /// <summary>
    /// Answering or declining needs the API, which refuses requests until Core has restored the session. After a
    /// cold start from the notification nothing may have started Core yet (a Decline broadcast opens no UI), so
    /// queued actions start it (idempotent; Core subscribes while it is built) and wait for it. Runs off the
    /// subscribing thread: resolving Core while it is still being constructed there would recurse.
    /// </summary>
    private async Task DispatchPendingAsync()
    {
        try
        {
            if (AppServices.Get<IClientBootstrapper>() is { } bootstrapper) await bootstrapper.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Core did not start for a call action");
        }
        List<(Guid CallId, CallNotificationAction Action)> actions;
        lock (_gate)
        {
            if (_callActionHandlers is null) return;
            actions = [.. _pendingActions];
            _pendingActions.Clear();
        }
        foreach (var (callId, action) in actions) Dispatch(callId, action);
    }

    private void Dispatch(Guid callId, CallNotificationAction action)
    {
        Action<Guid, CallNotificationAction>? handlers;
        lock (_gate) handlers = _callActionHandlers;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<Guid, CallNotificationAction>)handler)(callId, action);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Call action handler failed");
            }
        }
    }

    /// <summary>A chat's notification: its title, its last lines (one: big text; more: inbox) and the message count.</summary>
    private NotificationCompat.Builder MessageBuilder(string channel, string title, IReadOnlyList<string> lines, int count, PendingIntent? content)
    {
        var builder = new NotificationCompat.Builder(Context, channel)
            .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
            .SetColor(AccentColor)!
            .SetContentTitle(title)!
            .SetContentText(lines.Count > 0 ? lines[^1] : "")!
            .SetContentIntent(content)!
            .SetAutoCancel(true)!
            .SetCategory(NotificationCompat.CategoryMessage)!
            .SetGroup(MessagesGroup)!
            .SetVisibility(NotificationCompat.VisibilityPrivate)!
            .SetPublicVersion(PublicVersion(channel))!
            .SetPriority(NotificationCompat.PriorityHigh)!
            .SetNumber(count)!;
        if (lines.Count > 1)
        {
            var style = new NotificationCompat.InboxStyle().SetBigContentTitle(title)!;
            foreach (var line in lines) style.AddLine(line);
            if (count > lines.Count)
                style.SetSummaryText(_strings.Format("platform.notification.moreMessages", count - lines.Count));
            builder.SetStyle(style);
        }
        else
        {
            builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(lines.Count > 0 ? lines[0] : ""));
        }
        return builder;
    }

    private static PendingIntent ChatIntent(Guid chatId) =>
        AppIntents.ActivityPendingIntent(Context, AppIntents.CodeOf(chatId),
            AppIntents.ActivityIntent(Context, Intent.ActionView, data: Android.Net.Uri.Parse(DeepLinks.AppChatUri(chatId).ToString())));

    /// <summary>The bundle's summary: chat names, except of chats whose text is hidden (the app passcode).</summary>
    private void PostSummary(string channel)
    {
        List<string> titles;
        int total;
        lock (_gate)
        {
            titles = _chats.Values.Where(c => !c.Hidden).Select(c => c.Title).Where(t => !string.IsNullOrEmpty(t)).Take(6).ToList();
            total = _chats.Values.Sum(c => c.Count);
        }
        var style = new NotificationCompat.InboxStyle();
        foreach (var title in titles) style.AddLine(title);
        var content = AppIntents.ActivityPendingIntent(Context, AppIntents.MessagesSummaryNotificationId, AppIntents.ActivityIntent(Context, null));
        var summary = new NotificationCompat.Builder(Context, channel)
            .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
            .SetColor(AccentColor)!
            .SetContentTitle(_strings["platform.notification.newMessage"])!
            .SetContentIntent(content)!
            .SetStyle(style)!
            .SetGroup(MessagesGroup)!
            .SetGroupSummary(true)!
            .SetGroupAlertBehavior(NotificationCompat.GroupAlertChildren)!
            .SetNumber(Math.Max(total, _badge))!
            .SetAutoCancel(true)!
            .Build()!;
        Manager.Notify(AppIntents.MessagesSummaryNotificationId, summary);
    }

    private Notification PublicVersion(string channel) =>
        new NotificationCompat.Builder(Context, channel)
            .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
            .SetColor(AccentColor)!
            .SetContentTitle("SkyMessage")!
            .SetContentText(_strings["platform.notification.newMessage"])!
            .Build()!;

    /// <summary>A chat's shown lines. <see cref="Hidden"/>: one neutral line stands for all of them (the app passcode).</summary>
    private sealed class ChatLines
    {
        private readonly List<string> _lines = [];

        public string Title { get; private set; } = "";

        public int Count { get; private set; }

        public bool Hidden { get; private set; }

        public IReadOnlyList<string> Lines => _lines;

        /// <summary>When this process last posted the chat's notification (Environment.TickCount64).</summary>
        public long PostedAt { get; private set; } = long.MinValue / 2;

        /// <summary>A message with its text: the neutral line of a hidden chat goes (the app is open again).</summary>
        public void Add(string title, string line)
        {
            if (Hidden)
            {
                _lines.Clear();
                Hidden = false;
            }
            Title = title;
            Count++;
            _lines.Add(line);
            if (_lines.Count > MaxLinesPerChat) _lines.RemoveAt(0);
            PostedAt = Environment.TickCount64;
        }

        /// <summary>A message whose text is hidden: the chat shows the neutral line only.</summary>
        public void AddHidden(string title, string line)
        {
            Hide(title, line);
            Count++;
            PostedAt = Environment.TickCount64;
        }

        /// <summary>The earlier lines are no longer shown (the count stays).</summary>
        public void Hide(string title, string line)
        {
            Title = title;
            _lines.Clear();
            _lines.Add(line);
            Hidden = true;
        }
    }
}
