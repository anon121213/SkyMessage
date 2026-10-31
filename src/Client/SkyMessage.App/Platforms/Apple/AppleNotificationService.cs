using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;
using UIKit;
using UserNotifications;

namespace SkyMessage.App.Apple;

/// <summary>
/// Local notifications via UNUserNotificationCenter: messages threaded per chat with our synthesized tone,
/// incoming calls with Accept / Video / Decline actions (category per call kind) and the ringtone (iOS plays
/// notification sounds once, ≤ 30 s). Responses come through <see cref="AppleNotificationDelegate"/>.
/// </summary>
public sealed class AppleNotificationService : ILocalNotificationService
{
    private const string CallCategory = "SKY_CALL";
    private const string VideoCallCategory = "SKY_VIDEO_CALL";
    private const string ActionAccept = "ACCEPT";
    private const string ActionAcceptVideo = "ACCEPT_VIDEO";
    private const string ActionDecline = "DECLINE";
    private const string KindKey = "kind";
    private const string ChatKey = "chatId";
    private const string CallKey = "callId";

    private readonly IServiceProvider _services;
    private readonly NativeStrings _strings;
    private readonly ILogger<AppleNotificationService> _logger;
    private readonly object _gate = new();
    private readonly Queue<(Guid, CallNotificationAction)> _pending = new();
    private Action<Guid, CallNotificationAction>? _handlers;
    private string? _categoriesLanguage;

    public AppleNotificationService(IServiceProvider services, NativeStrings strings, ILogger<AppleNotificationService> logger)
    {
        _services = services;
        _strings = strings;
        _logger = logger;
        AppleNotificationDelegate.Instance.Attach(this);
    }

    private static UNUserNotificationCenter Center => UNUserNotificationCenter.Current;

    public event Action<Guid, CallNotificationAction>? CallActionInvoked
    {
        add
        {
            List<(Guid, CallNotificationAction)> flush;
            lock (_gate)
            {
                _handlers += value;
                flush = [.. _pending];
                _pending.Clear();
            }
            if (flush.Count > 0) _ = Task.Run(() => { foreach (var (id, action) in flush) Dispatch(id, action); });
        }
        remove
        {
            lock (_gate) _handlers -= value;
        }
    }

    public async Task<bool> RequestPermissionAsync()
    {
        try
        {
            var (granted, _) = await Center.RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound).ConfigureAwait(false);
            return granted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification permission request failed");
            return false;
        }
    }

    public async Task ShowMessageAsync(LocalNotification notification)
    {
        try
        {
            var settings = _services.GetService<ISettingsService>()?.Current.Notifications;
            var content = new UNMutableNotificationContent
            {
                Title = notification.Title,
                Body = notification.Body,
                ThreadIdentifier = notification.ChatId.ToString(),
                UserInfo = NSDictionary.FromObjectsAndKeys(
                    [new NSString("message"), new NSString(notification.ChatId.ToString())],
                    [new NSString(KindKey), new NSString(ChatKey)]),
            };
            if (!notification.Silent && settings?.Sound != false && SoundCatalog.MessageSound(settings ?? new NotificationSettings()) is { } sound)
                content.Sound = Sound(sound);
            var id = $"msg-{notification.ChatId:N}-{notification.MessageId ?? Guid.NewGuid():N}";
            await Center.AddNotificationRequestAsync(UNNotificationRequest.FromIdentifier(id, content, null)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Message notification failed");
        }
    }

    public async Task CancelForChatAsync(Guid chatId)
    {
        try
        {
            var thread = chatId.ToString();
            var delivered = await Center.GetDeliveredNotificationsAsync().ConfigureAwait(false);
            var ids = delivered.Where(n => n.Request.Content.ThreadIdentifier == thread).Select(n => n.Request.Identifier).ToArray();
            if (ids.Length > 0) Center.RemoveDeliveredNotifications(ids);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancelling chat notifications failed");
        }
    }

    public async Task ShowIncomingCallAsync(CallDto call, string callerName)
    {
        try
        {
            EnsureCategories();
            var settings = _services.GetService<ISettingsService>()?.Current.Notifications;
            var content = new UNMutableNotificationContent
            {
                Title = callerName,
                Body = _strings[call.IsVideo ? "platform.call.incomingVideo" : "platform.call.incoming"],
                CategoryIdentifier = call.IsVideo ? VideoCallCategory : CallCategory,
                ThreadIdentifier = "calls",
                UserInfo = NSDictionary.FromObjectsAndKeys(
                    [new NSString("call"), new NSString(call.Id.ToString()), new NSString(call.ChatId.ToString())],
                    [new NSString(KindKey), new NSString(CallKey), new NSString(ChatKey)]),
            };
            if (settings?.Sound != false) content.Sound = Sound(SoundCatalog.RingtoneSound(settings ?? new NotificationSettings()));
            await Center.AddNotificationRequestAsync(UNNotificationRequest.FromIdentifier(CallId(call.Id), content, null)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incoming call notification failed");
        }
    }

    public Task CancelCallAsync(Guid callId)
    {
        try
        {
            Center.RemoveDeliveredNotifications([CallId(callId)]);
            Center.RemovePendingNotificationRequests([CallId(callId)]);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancelling the call notification failed");
        }
        return Task.CompletedTask;
    }

    public Task SetBadgeCountAsync(int count)
    {
        var value = Math.Max(0, count);
        try
        {
            if (OperatingSystem.IsIOSVersionAtLeast(16) || OperatingSystem.IsMacCatalystVersionAtLeast(16))
            {
                Center.SetBadgeCount(value, _ => { });
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
#pragma warning disable CA1422
                    UIApplication.SharedApplication.ApplicationIconBadgeNumber = value;
#pragma warning restore CA1422
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Setting the badge failed");
        }
        return Task.CompletedTask;
    }

    /// <summary>Called by the notification center delegate.</summary>
    internal void OnResponse(UNNotificationResponse response)
    {
        var info = response.Notification.Request.Content.UserInfo;
        var kind = info?[KindKey]?.ToString();
        if (kind == "call" && Guid.TryParse(info?[CallKey]?.ToString(), out var callId))
        {
            var action = response.ActionIdentifier?.ToString() switch
            {
                ActionAccept => CallNotificationAction.Accept,
                ActionAcceptVideo => CallNotificationAction.AcceptVideo,
                ActionDecline => CallNotificationAction.Decline,
                _ => (CallNotificationAction?)null,
            };
            if (action is { } a) Raise(callId, a);
            return;
        }
        if (kind == "message" && Guid.TryParse(info?[ChatKey]?.ToString(), out var chatId)
                              && response.IsDefaultAction)
        {
            (_services.GetService<IDeepLinkSource>() as DeepLinkSource)?.Publish(DeepLinks.AppChatUri(chatId));
        }
    }

    private void Raise(Guid callId, CallNotificationAction action)
    {
        _ = CancelCallAsync(callId);
        lock (_gate)
        {
            if (_handlers is null)
            {
                _pending.Enqueue((callId, action));
                return;
            }
        }
        Dispatch(callId, action);
    }

    private void Dispatch(Guid callId, CallNotificationAction action)
    {
        Action<Guid, CallNotificationAction>? handlers;
        lock (_gate) handlers = _handlers;
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

    private void EnsureCategories()
    {
        var language = _strings.Language;
        if (_categoriesLanguage == language) return;
        _categoriesLanguage = language;
        var accept = UNNotificationAction.FromIdentifier(ActionAccept, _strings["platform.call.accept"], UNNotificationActionOptions.Foreground);
        var acceptVideo = UNNotificationAction.FromIdentifier(ActionAcceptVideo, _strings["platform.call.acceptVideo"], UNNotificationActionOptions.Foreground);
        var decline = UNNotificationAction.FromIdentifier(ActionDecline, _strings["platform.call.decline"], UNNotificationActionOptions.Destructive);
        var audio = UNNotificationCategory.FromIdentifier(CallCategory, [accept, decline], [], UNNotificationCategoryOptions.None);
        var video = UNNotificationCategory.FromIdentifier(VideoCallCategory, [acceptVideo, accept, decline], [], UNNotificationCategoryOptions.None);
        Center.SetNotificationCategories(new NSSet<UNNotificationCategory>(audio, video));
    }

    /// <summary>
    /// The pack's CAF at the bundle root (tools/tones). iOS plays notification sounds only from the bundle or
    /// Library/Sounds and only as PCM, IMA4, µ-law or a-law, so the user's own sound (any format) is heard in the
    /// app, and its pack's sound in notifications.
    /// </summary>
    private static UNNotificationSound Sound(SoundChoice sound)
    {
        var pack = sound.Fallback;
        return UNNotificationSound.GetSound($"{SoundCatalog.ResourceName(pack.Pack, pack.Key)}.caf");
    }

    private static string CallId(Guid callId) => $"call-{callId:N}";
}

/// <summary>
/// Set as UNUserNotificationCenter's delegate in FinishedLaunching (before MAUI starts), so taps that launched
/// the app are not lost; responses are forwarded once the service exists.
/// </summary>
public sealed class AppleNotificationDelegate : UNUserNotificationCenterDelegate
{
    public static AppleNotificationDelegate Instance { get; } = new();

    private readonly object _gate = new();
    private readonly List<UNNotificationResponse> _early = [];
    private AppleNotificationService? _service;

    internal void Attach(AppleNotificationService service)
    {
        List<UNNotificationResponse> early;
        lock (_gate)
        {
            _service = service;
            early = [.. _early];
            _early.Clear();
        }
        foreach (var response in early) service.OnResponse(response);
    }

    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
    {
        completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List | UNNotificationPresentationOptions.Sound);
    }

    public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
    {
        try
        {
            AppleNotificationService? service;
            lock (_gate)
            {
                service = _service;
                if (service is null) _early.Add(response);
            }
            if (service is null && AppServices.Get<ILocalNotificationService>() is AppleNotificationService resolved)
            {
                // Resolving the service attaches it, which replays the queued response.
                service = resolved;
            }
            else
            {
                service?.OnResponse(response);
            }
        }
        finally
        {
            completionHandler();
        }
    }
}
