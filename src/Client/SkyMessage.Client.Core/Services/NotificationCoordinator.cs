using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Decides whether an incoming message notifies (SPEC §8.1): never for my own messages or system events, not for
/// the chat on screen while in foreground, not when notifications (or group notifications) are off, not for muted
/// chats unless it mentions/replies to me and MentionsBypassMute is on. Missed calls notify too. Foreground raises
/// <see cref="InAppNotificationRaised"/> (UI banner + tone); background shows an OS notification whose tap opens
/// <see cref="DeepLinks.AppChatUri"/> (platform side). While the app passcode locks the app nothing of a chat is on
/// screen: messages notify as in the background, and with "hide notification text" the OS notification only says
/// "SkyMessage: New message" (the chat's earlier notification is replaced, so its text goes too).
/// </summary>
internal sealed class NotificationCoordinator : INotificationCoordinator
{
    private const int RememberedMessages = 512;

    private readonly IAuthService _auth;
    private readonly IChatListStore _chats;
    private readonly IUserStore _users;
    private readonly ISettingsService _settings;
    private readonly IAppLifecycle _appLifecycle;
    private readonly ILocalNotificationService _notifications;
    private readonly IAppLockService _appLock;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationCoordinator> _logger;
    private readonly object _lock = new();
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = new();
    private IMessagePreviewFormatter? _formatter;
    private bool _formatterResolved;

    public NotificationCoordinator(
        IRealtimeService realtime,
        IAuthService auth,
        IChatListStore chats,
        IUserStore users,
        ISettingsService settings,
        IAppLifecycle appLifecycle,
        ILocalNotificationService notifications,
        IAppLockService appLock,
        IServiceProvider services,
        TimeProvider time,
        ILogger<NotificationCoordinator> logger)
    {
        _auth = auth;
        _chats = chats;
        _users = users;
        _settings = settings;
        _appLifecycle = appLifecycle;
        _notifications = notifications;
        _appLock = appLock;
        _services = services;
        _time = time;
        _logger = logger;
        realtime.MessageCreated += message => HandleAsync(message).Forget(_logger);
    }

    public event Action<InAppNotification>? InAppNotificationRaised;

    internal async Task HandleAsync(MessageDto message)
    {
        if (_auth.UserId is not { } me || message.SenderId == me || message.IsDeleted) return;
        if (!IsNotifiable(message, me) || !Remember(message.Id)) return;

        var settings = _settings.Current.Notifications;
        if (!settings.Enabled) return;

        if (OnScreen && _chats.ActiveChatId == message.ChatId) return;

        ChatSummaryDto? chat;
        try
        {
            chat = _chats.Get(message.ChatId) ?? await _chats.GetOrFetchAsync(message.ChatId).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            chat = null;
        }
        if (chat is null) return;

        var mention = MessageRules.MentionsMe(message, me, _auth.Me?.Username);
        if (chat.Type == ChatType.Group && !settings.GroupsEnabled && !mention) return;
        if (ChatRules.IsMuted(chat, _time.GetUtcNow()) && !(mention && settings.MentionsBypassMute)) return;
        // Re-check after the await: the user may have opened the chat meanwhile.
        if (OnScreen && _chats.ActiveChatId == message.ChatId) return;

        if (message.SenderId is { } senderId && _users.Get(senderId) is null)
        {
            try
            {
                await _users.GetOrFetchAsync(senderId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException)
            {
                // The placeholder name will do.
            }
        }

        var ru = CoreStrings.IsRussian(_settings);
        var title = _chats.TitleOf(chat);
        var senderName = message.SenderId is { } sid ? _users.DisplayNameOf(sid) : null;
        string body;
        if (!settings.ShowPreview)
        {
            body = CoreStrings.NewMessage(ru);
        }
        else
        {
            var preview = Preview(message, ru);
            body = chat.Type == ChatType.Group && senderName is not null ? $"{senderName}: {preview}" : preview;
        }
        var avatar = chat.Type == ChatType.Group
            ? _chats.AvatarOf(chat)
            : message.SenderId is { } s ? _users.Get(s)?.AvatarMediaId ?? _chats.AvatarOf(chat) : _chats.AvatarOf(chat);

        if (OnScreen)
        {
            EventRaiser.Raise(InAppNotificationRaised, new InAppNotification(chat.Id, message.SenderId, title, body, avatar), _logger);
            return;
        }
        if (_appLock.ShouldHideNotificationText())
        {
            title = CoreStrings.AppName;
            body = CoreStrings.NewMessage(ru);
            // The chat's notification may still show earlier lines: it starts over with the hidden text only.
            await _notifications.CancelForChatAsync(chat.Id).ConfigureAwait(false);
        }
        await _notifications.ShowMessageAsync(new LocalNotification(chat.Id, message.Id, title, body, Silent: !settings.Sound)).ConfigureAwait(false);
    }

    /// <summary>The app is in front and its chats are visible (not behind the passcode lock screen).</summary>
    private bool OnScreen => _appLifecycle.IsForeground && !_appLock.IsLocked;

    private static bool IsNotifiable(MessageDto message, Guid me) => message.Type switch
    {
        MessageType.System => false,
        // Only calls I did not answer are worth a notification (the call itself rang already).
        MessageType.Call => message.Call is { Status: CallStatus.Missed or CallStatus.Cancelled } call && call.InitiatorId != me,
        _ => true,
    };

    private bool Remember(Guid messageId)
    {
        lock (_lock)
        {
            if (!_seen.Add(messageId)) return false;
            _seenOrder.Enqueue(messageId);
            while (_seenOrder.Count > RememberedMessages) _seen.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    private string Preview(MessageDto message, bool ru)
    {
        if (!_formatterResolved)
        {
            try
            {
                _formatter = _services.GetService<IMessagePreviewFormatter>();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No preview formatter available");
            }
            _formatterResolved = true;
        }
        if (_formatter is { } formatter)
        {
            try
            {
                return formatter.FormatPreview(message);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Preview formatter failed");
            }
        }
        return CoreStrings.Preview(message, ru);
    }
}
