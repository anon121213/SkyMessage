using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// App start and the sign-in/out lifecycle (SPEC §8.1 IClientBootstrapper). <see cref="StartAsync"/> restores the
/// settings, the session, the endpoint memory and the stores' local caches, then returns — realtime, the chat list,
/// contacts and the outbox continue in the background (never waiting for the network). On sign-out it stops
/// realtime, cancels notifications and the background connection; calls and stores clean up through the account
/// lifecycle. All stores are constructed here so they subscribe to realtime before the first event.
/// </summary>
internal sealed class ClientBootstrapper : IClientBootstrapper
{
    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly ServerEndpointService _endpoints;
    private readonly IRealtimeService _realtime;
    private readonly AccountLifecycle _lifecycle;
    private readonly IChatListStore _chatList;
    private readonly IContactStore _contacts;
    private readonly ILocalNotificationService _notifications;
    private readonly IBackgroundConnectionService _background;
    private readonly ILogger<ClientBootstrapper> _logger;
    private readonly object _lock = new();
    private Task? _start;
    private volatile bool _started;

    public ClientBootstrapper(
        ISettingsService settings,
        IAuthService auth,
        ServerEndpointService endpoints,
        IRealtimeService realtime,
        AccountLifecycle lifecycle,
        IChatListStore chatList,
        IMessageStore messages,
        IUserStore users,
        IContactStore contacts,
        ITypingStore typing,
        IDraftStore drafts,
        ICallService calls,
        INotificationCoordinator notificationCoordinator,
        IAudioFocusCoordinator audioFocus,
        IMediaUrlBuilder mediaUrls,
        IMediaDownloadService downloads,
        ILocalNotificationService notifications,
        IBackgroundConnectionService background,
        ILogger<ClientBootstrapper> logger)
    {
        // messages, users, typing, drafts, calls, coordinators, urls, downloads: constructed for their subscriptions.
        _ = (messages, users, typing, drafts, calls, notificationCoordinator, audioFocus, mediaUrls, downloads);
        _settings = settings;
        _auth = auth;
        _endpoints = endpoints;
        _realtime = realtime;
        _lifecycle = lifecycle;
        _chatList = chatList;
        _contacts = contacts;
        _notifications = notifications;
        _background = background;
        _logger = logger;
    }

    public bool IsStarted => _started;

    public Task StartAsync()
    {
        lock (_lock) return _start ??= StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        await _settings.LoadAsync().ConfigureAwait(false);
        await _auth.InitializeAsync().ConfigureAwait(false);
        await _endpoints.InitializeAsync().ConfigureAwait(false);

        _realtime.SessionRevoked += _ =>
        {
            _logger.LogInformation("The server revoked this session");
            _auth.LogoutAsync(revokeOnServer: false).Forget(_logger);
        };
        _realtime.UserChanged += e =>
        {
            if (_auth.UserId is { } me && e.UserId == me) _auth.RefreshMeAsync().Forget(_logger);
        };
        _settings.Changed += () => ApplyBackgroundConnectionAsync().Forget(_logger);
        _lifecycle.OnSignIn(OnSignedInAsync);
        _lifecycle.OnSignOut(OnSignedOutAsync);

        if (_auth.State == AuthState.SignedIn && _auth.UserId is { } userId)
            await _lifecycle.ActivateAsync(userId).ConfigureAwait(false);
        else
            _endpoints.RefreshInfoAsync().Forget(_logger);

        _started = true;
    }

    /// <summary>Runs after the stores restored their caches; starts network work without waiting for it.</summary>
    private Task OnSignedInAsync(Guid userId)
    {
        _realtime.StartAsync().Forget(_logger);
        _endpoints.RefreshInfoAsync().Forget(_logger);
        _chatList.LoadAsync().Forget(_logger);
        _contacts.LoadAsync().Forget(_logger);
        ApplyBackgroundConnectionAsync().Forget(_logger);
        return Task.CompletedTask;
    }

    private async Task OnSignedOutAsync(Guid userId)
    {
        await _realtime.StopAsync().ConfigureAwait(false);
        foreach (var chat in _chatList.Chats.Concat(_chatList.ArchivedChats).ToList())
        {
            try
            {
                await _notifications.CancelForChatAsync(chat.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not cancel notifications of a chat");
            }
        }
        await ApplyBackgroundConnectionAsync().ConfigureAwait(false);
    }

    private async Task ApplyBackgroundConnectionAsync()
    {
        if (!_background.IsSupported) return;
        var wanted = _auth.State == AuthState.SignedIn && _settings.Current.Notifications.KeepConnectedInBackground;
        if (_background.IsEnabled == wanted) return;
        try
        {
            await _background.SetEnabledAsync(wanted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not switch the background connection");
        }
    }
}
