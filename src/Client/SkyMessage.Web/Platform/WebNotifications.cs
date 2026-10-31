using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Web.Platform;

/// <summary>
/// Notifications through the service worker registration (<c>sw.js</c>): one per chat (tag), incoming calls
/// with Accept / Decline actions that stay until handled, app badge where supported. Clicks come back through
/// <see cref="WebBridge.NotificationAction"/> (or the start URL when no window was open).
/// </summary>
public sealed class WebNotificationService : ILocalNotificationService
{
    private readonly WebBridge _bridge;
    private readonly IServiceProvider _services;
    private readonly ILogger<WebNotificationService> _logger;
    private readonly object _gate = new();
    private readonly Queue<(Guid, CallNotificationAction)> _pending = new();
    private Action<Guid, CallNotificationAction>? _handlers;
    private ILocalizer? _localizer;

    public WebNotificationService(WebBridge bridge, IServiceProvider services, ILogger<WebNotificationService> logger)
    {
        _bridge = bridge;
        _services = services;
        _logger = logger;
        bridge.NotificationAction += OnClick;
        if (bridge.State.CallAction is { } initial) ParseInitialCallAction(initial);
    }

    private ILocalizer Strings => _localizer ??= _services.GetService<ILocalizer>() ?? new Localizer(_services);

    /// <summary>Raised for accept/decline from notifications once Core has started (see <see cref="DispatchPendingAsync"/>).</summary>
    public event Action<Guid, CallNotificationAction>? CallActionInvoked
    {
        add
        {
            bool pending;
            lock (_gate)
            {
                _handlers += value;
                pending = _pending.Count > 0;
            }
            if (pending) _ = DispatchPendingAsync();
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
            return await _bridge.Js.InvokeAsync<string>("skyPlatform.notify.request").ConfigureAwait(false) == "granted";
        }
        catch (JSException)
        {
            return false;
        }
    }

    public Task ShowMessageAsync(LocalNotification notification) => ShowAsync(new
    {
        tag = ChatTag(notification.ChatId),
        title = notification.Title,
        body = notification.Body,
        silent = notification.Silent,
        data = new { kind = "message", chatId = notification.ChatId, url = DeepLinks.ChatPath(notification.ChatId).TrimStart('/') },
    });

    public Task CancelForChatAsync(Guid chatId) => CloseTagAsync(ChatTag(chatId));

    public Task ShowIncomingCallAsync(CallDto call, string callerName)
    {
        var actions = new List<object> { new { action = "accept", title = Strings["platform.call.accept"] } };
        if (call.IsVideo) actions.Insert(0, new { action = "acceptVideo", title = Strings["platform.call.acceptVideo"] });
        actions.Add(new { action = "decline", title = Strings["platform.call.decline"] });
        return ShowAsync(new
        {
            tag = CallTag(call.Id),
            title = callerName,
            body = Strings[call.IsVideo ? "platform.call.incomingVideo" : "platform.call.incoming"],
            requireInteraction = true,
            vibrate = new[] { 600, 300, 600, 300, 600 },
            actions,
            data = new { kind = "call", callId = call.Id, chatId = call.ChatId },
        });
    }

    public Task CancelCallAsync(Guid callId) => CloseTagAsync(CallTag(callId));

    public Task SetBadgeCountAsync(int count)
    {
        _bridge.Sync?.InvokeVoid("skyPlatform.notify.badge", Math.Max(0, count));
        return Task.CompletedTask;
    }

    private async Task ShowAsync(object options)
    {
        try
        {
            await _bridge.Js.InvokeAsync<bool>("skyPlatform.notify.show", options).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Showing a notification failed");
        }
    }

    private async Task CloseTagAsync(string tag)
    {
        try
        {
            await _bridge.Js.InvokeVoidAsync("skyPlatform.notify.closeTag", tag).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Closing notifications failed");
        }
    }

    private void OnClick(string action, string dataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(dataJson) ? "{}" : dataJson);
            var root = document.RootElement;
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (kind == "call" && root.TryGetProperty("callId", out var c) && Guid.TryParse(c.GetString(), out var callId))
            {
                if (MapAction(action) is { } callAction) Raise(callId, callAction);
                return;
            }
            if (kind == "message" && root.TryGetProperty("chatId", out var chat) && Guid.TryParse(chat.GetString(), out var chatId))
                (_services.GetService<IDeepLinkSource>() as WebDeepLinkSource)?.Publish(new Uri(new Uri(_bridge.State.InitialUrl), DeepLinks.ChatPath(chatId)));
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Unreadable notification click");
        }
    }

    private void ParseInitialCallAction(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || !Guid.TryParse(value[(separator + 1)..], out var callId)) return;
        if (MapAction(value[..separator]) is { } action) Raise(callId, action);
    }

    private static CallNotificationAction? MapAction(string action) => action switch
    {
        "accept" => CallNotificationAction.Accept,
        "acceptVideo" => CallNotificationAction.AcceptVideo,
        "decline" => CallNotificationAction.Decline,
        _ => null,
    };

    private void Raise(Guid callId, CallNotificationAction action)
    {
        _ = CancelCallAsync(callId);
        lock (_gate) _pending.Enqueue((callId, action));
        _ = DispatchPendingAsync();
    }

    /// <summary>
    /// Answering or declining needs the API, which refuses requests until Core has restored the session — and the
    /// start URL of a notification opened without a window (<c>?smcall=</c>) is read before that. Actions wait for
    /// Core's start (idempotent; Core subscribes while it is built).
    /// </summary>
    private async Task DispatchPendingAsync()
    {
        // Leave the caller first: Core subscribes while it is being constructed, and resolving it then would recurse.
        await Task.Yield();
        try
        {
            if (_services.GetService<IClientBootstrapper>() is { } bootstrapper) await bootstrapper.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Core did not start for a call action");
        }
        List<(Guid CallId, CallNotificationAction Action)> actions;
        lock (_gate)
        {
            if (_handlers is null) return;
            actions = [.. _pending];
            _pending.Clear();
        }
        foreach (var (callId, action) in actions) Dispatch(callId, action);
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

    private static string ChatTag(Guid chatId) => $"chat-{chatId:N}";

    private static string CallTag(Guid callId) => $"call-{callId:N}";
}
