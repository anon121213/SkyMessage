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
/// Activity PendingIntents so the app is in the foreground before the call service starts.
/// </summary>
public sealed class AndroidNotificationService : ILocalNotificationService
{
    private const string MessagesGroup = "com.skymessage.MESSAGES";
    private const int MaxLinesPerChat = 6;
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
            lock (_gate)
            {
                if (!_chats.TryGetValue(notification.ChatId, out lines!))
                {
                    lines = new ChatLines();
                    _chats[notification.ChatId] = lines;
                }
                lines.Title = notification.Title;
                lines.Add(notification.Body);
                totalChats = _chats.Count;
            }

            var id = AppIntents.CodeOf(notification.ChatId);
            var content = AppIntents.ActivityPendingIntent(Context, id,
                AppIntents.ActivityIntent(Context, Intent.ActionView, data: Android.Net.Uri.Parse(DeepLinks.AppChatUri(notification.ChatId).ToString())));

            var builder = new NotificationCompat.Builder(Context, channel)
                .SetSmallIcon(Resource.Drawable.ic_stat_skymessage)!
                .SetColor(AccentColor)!
                .SetContentTitle(notification.Title)!
                .SetContentText(notification.Body)!
                .SetContentIntent(content)!
                .SetAutoCancel(true)!
                .SetCategory(NotificationCompat.CategoryMessage)!
                .SetGroup(MessagesGroup)!
                .SetVisibility(NotificationCompat.VisibilityPrivate)!
                .SetPublicVersion(PublicVersion(channel))!
                .SetPriority(NotificationCompat.PriorityHigh)!
                .SetNumber(lines.Count)!
                .SetSilent(notification.Silent)!
                .SetOnlyAlertOnce(false)!;

            if (lines.Count > 1)
            {
                var style = new NotificationCompat.InboxStyle().SetBigContentTitle(notification.Title)!;
                foreach (var line in lines.Lines) style.AddLine(line);
                if (lines.Count > lines.Lines.Count)
                    style.SetSummaryText(_strings.Format("platform.notification.moreMessages", lines.Count - lines.Lines.Count));
                builder.SetStyle(style);
            }
            else
            {
                builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(notification.Body));
            }

            Manager.Notify(id, builder.Build()!);
            if (totalChats > 1) PostSummary(channel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Message notification failed");
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

    private void PostSummary(string channel)
    {
        List<string> titles;
        int total;
        lock (_gate)
        {
            titles = _chats.Values.Select(c => c.Title).Where(t => !string.IsNullOrEmpty(t)).Take(6).ToList();
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

    private sealed class ChatLines
    {
        private readonly List<string> _lines = [];

        public string Title { get; set; } = "";

        public int Count { get; private set; }

        public IReadOnlyList<string> Lines => _lines;

        public void Add(string line)
        {
            Count++;
            _lines.Add(line);
            if (_lines.Count > MaxLinesPerChat) _lines.RemoveAt(0);
        }
    }
}
