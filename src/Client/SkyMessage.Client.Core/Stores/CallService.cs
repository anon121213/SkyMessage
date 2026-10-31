using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// Call signaling and state (SPEC §8.1 ICallService). Permissions are requested before starting/accepting/joining;
/// the native keep-alive (<see cref="ICallPlatformService.OnCallStartedAsync"/>) starts only while the app is in
/// foreground (deferred until it is). An incoming call while in a call is declined as busy; an incoming call
/// answered or declined on another device stops ringing here. Ended calls stay visible for 2 s with their status.
/// Notification buttons and the platform's ongoing-call controls feed <see cref="RequestCommand"/>/accept/decline.
/// </summary>
internal sealed class CallService : ICallService, IAccountScoped
{
    private static readonly TimeSpan EndedLinger = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IncomingSafetyTimeout = TimeSpan.FromSeconds(Limits.CallRingTimeoutSeconds + 15);
    private static readonly TimeSpan SignOutHangupTimeout = TimeSpan.FromSeconds(3);
    /// <summary>How long the incoming-call notification waits for the name of a caller this device did not know.</summary>
    private static readonly TimeSpan CallerLookupWait = TimeSpan.FromSeconds(3);
    /// <summary>How long a notification button waits for the session after a cold start (nobody may be signed in).</summary>
    private static readonly TimeSpan SignInWait = TimeSpan.FromSeconds(30);
    private const int MaxRingingChecks = 5;

    private readonly ISkyApi _api;
    private readonly IAuthService _auth;
    private readonly IChatListStore _chats;
    private readonly IUserStore _users;
    private readonly IPermissionService _permissions;
    private readonly ICallPlatformService _platform;
    private readonly ILocalNotificationService _notifications;
    private readonly IAppLifecycle _appLifecycle;
    private readonly IServerEndpointService _endpoints;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<CallService> _logger;
    private readonly object _lock = new();

    private CallSession? _current;
    private CallSession? _incoming;
    /// <summary>Newest CallUpdated for my call that arrived while my start was still waiting for the call id.</summary>
    private CallDto? _earlyUpdate;
    private Guid? _accepting;
    private bool _mediaConnected;
    private bool _platformStarted;
    private bool _platformPending;
    /// <summary>Completes once an account is active (its session restored or signed in); renewed on sign-out.</summary>
    private TaskCompletionSource _signedIn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile int _generation;

    public CallService(
        ISkyApi api,
        IRealtimeService realtime,
        IAuthService auth,
        IChatListStore chats,
        IUserStore users,
        IPermissionService permissions,
        ICallPlatformService platform,
        ILocalNotificationService notifications,
        IAppLifecycle appLifecycle,
        IServerEndpointService endpoints,
        ISettingsService settings,
        AccountLifecycle lifecycle,
        TimeProvider time,
        ILogger<CallService> logger)
    {
        _api = api;
        _auth = auth;
        _chats = chats;
        _users = users;
        _permissions = permissions;
        _platform = platform;
        _notifications = notifications;
        _appLifecycle = appLifecycle;
        _endpoints = endpoints;
        _settings = settings;
        _time = time;
        _logger = logger;

        realtime.IncomingCall += call => OnIncomingAsync(call).Forget(_logger);
        realtime.CallUpdated += OnCallUpdated;
        realtime.Resynced += () => VerifyAsync().Forget(_logger);
        platform.CommandRequested += RequestCommand;
        notifications.CallActionInvoked += (callId, action) => OnCallActionAsync(callId, action).Forget(_logger);
        appLifecycle.ForegroundChanged += OnForegroundChanged;
        lifecycle.Register(this);
        lifecycle.OnBeforeSignOut(_ => HangupForSignOutAsync());
    }

    public CallSession? Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public CallSession? Incoming
    {
        get
        {
            lock (_lock) return _incoming;
        }
    }

    public event Action? Changed;

    public event Action<CallCommand>? CommandRequested;

    public async Task StartAsync(Guid chatId, bool video)
    {
        var me = _auth.UserId ?? throw new ApiException(401, ErrorCodes.Unauthorized, "Not signed in");
        var generation = _generation;
        lock (_lock)
        {
            if (_current is { Phase: not CallPhase.Ended } active)
            {
                if (active.Call.ChatId == chatId) return;
                throw new ApiException(409, ErrorCodes.CallBusy, "Already in a call");
            }
        }

        var chat = _chats.Get(chatId) ?? await _chats.GetOrFetchAsync(chatId).ConfigureAwait(false);
        var withVideo = await EnsurePermissionsAsync(video).ConfigureAwait(false);
        var kind = chat?.Type == ChatType.Group ? CallKind.Group : CallKind.Private;
        var peer = kind == CallKind.Private ? chat?.Peer?.Id : null;
        var session = new CallSession
        {
            Call = new CallDto
            {
                ChatId = chatId,
                Kind = kind,
                IsVideo = withVideo,
                Status = CallStatus.Ringing,
                InitiatorId = me,
                CreatedAt = _time.GetUtcNow(),
            },
            Phase = kind == CallKind.Group ? CallPhase.Connecting : CallPhase.OutgoingRinging,
            IsOutgoing = true,
            StartWithVideo = withVideo,
            IsCameraOn = withVideo,
            Title = TitleFor(chat, peer),
            PeerUserId = peer,
            AvatarMediaId = AvatarFor(chat, peer),
        };
        SetCurrent(session, mediaConnected: false);

        try
        {
            var info = await _api.StartCallAsync(new StartCallRequest { ChatId = chatId, IsVideo = withVideo }).ConfigureAwait(false);
            if (!ApplyJoinInfo(session, info, me))
            {
                // Hung up (or replaced) while the request was on its way: nobody here takes part in the call it
                // created or joined, so do not leave the callee ringing (or a group call nobody is in).
                if (generation == _generation && !IsFinished(info.Call.Status)) HangupOnServerAsync(info.Call.Id).Forget(_logger);
                return;
            }
            await StartPlatformAsync().ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.CallBusy)
        {
            End(session, CallStatus.Busy, hangupOnServer: false);
        }
        catch (ApiException ex) when (ex.IsTransient)
        {
            ClearIfCurrent(session);
            // Only the answer may have been lost: the call it created would ring on for nobody.
            HangupLostStartAsync(chatId, me, generation).Forget(_logger);
            throw;
        }
        catch
        {
            ClearIfCurrent(session);
            throw;
        }
    }

    public async Task AcceptAsync(bool withVideo)
    {
        var me = _auth.UserId;
        var incoming = Incoming;
        if (me is null || incoming is null) return;

        var video = await EnsurePermissionsAsync(withVideo).ConfigureAwait(false);
        CallSession session;
        lock (_lock)
        {
            // CallUpdated may have refreshed the ringing call during the permission prompt: answer its latest state.
            if (_incoming is not { } latest || latest.Call.Id != incoming.Call.Id) return;
            incoming = latest;
            _incoming = null;
            _accepting = incoming.Call.Id;
            session = incoming with { Phase = CallPhase.Connecting, StartWithVideo = video, IsCameraOn = video };
            _current = session;
            _mediaConnected = false;
        }
        RaiseChanged();
        _notifications.CancelCallAsync(incoming.Call.Id).Forget(_logger);

        try
        {
            var info = await _api.AcceptCallAsync(incoming.Call.Id).ConfigureAwait(false);
            if (!ApplyJoinInfo(session, info, me.Value)) return;
            await StartPlatformAsync().ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 409 || ex.Code is ErrorCodes.CallEnded or ErrorCodes.CallNotFound)
        {
            // The caller hung up while we were answering.
            End(session, CallStatus.Missed, hangupOnServer: false);
        }
        catch (ApiException ex) when (ex.IsTransient)
        {
            // The answer may never have reached the server: ring here again so it can be retried (accepting is idempotent).
            RestoreIncoming(incoming);
            throw;
        }
        catch
        {
            ClearIfCurrent(session);
            throw;
        }
        finally
        {
            lock (_lock)
            {
                if (_accepting == incoming.Call.Id) _accepting = null;
            }
        }
    }

    public async Task DeclineAsync()
    {
        CallSession? incoming;
        lock (_lock)
        {
            incoming = _incoming;
            _incoming = null;
        }
        if (incoming is null) return;
        RaiseChanged();
        _notifications.CancelCallAsync(incoming.Call.Id).Forget(_logger);
        try
        {
            await _api.DeclineCallAsync(incoming.Call.Id).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            _logger.LogDebug("Declining a call failed ({Code})", ex.Code);
        }
    }

    public async Task HangupAsync()
    {
        var current = Current;
        if (current is null || current.Phase == CallPhase.Ended) return;
        var status = current is { IsOutgoing: true, Call.Status: CallStatus.Ringing } && current.Call.Kind == CallKind.Private
            ? CallStatus.Cancelled
            : CallStatus.Ended;
        End(current, status, hangupOnServer: false);
        // A start still waiting for its call id hangs the call up itself once the id arrives (StartAsync).
        if (current.Call.Id == Guid.Empty) return;
        try
        {
            await _api.HangupAsync(current.Call.Id).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            _logger.LogDebug("Hang-up request failed ({Code})", ex.Code);
        }
    }

    public async Task JoinAsync(Guid callId, bool video)
    {
        var me = _auth.UserId ?? throw new ApiException(401, ErrorCodes.Unauthorized, "Not signed in");
        var existing = Current;
        var rejoin = existing is { Phase: not CallPhase.Ended } && existing.Call.Id == callId;
        if (!rejoin && existing is { Phase: not CallPhase.Ended })
            throw new ApiException(409, ErrorCodes.CallBusy, "Already in a call");

        var withVideo = await EnsurePermissionsAsync(video).ConfigureAwait(false);
        var info = await _api.JoinCallAsync(callId).ConfigureAwait(false);
        var chat = _chats.Get(info.Call.ChatId) ?? await _chats.GetOrFetchAsync(info.Call.ChatId).ConfigureAwait(false);
        var peer = info.Call.Kind == CallKind.Private ? PeerOf(info.Call, me) : null;

        CallSession session;
        lock (_lock)
        {
            var baseSession = rejoin && _current is { } held && held.Call.Id == callId
                ? held
                : new CallSession
                {
                    Call = info.Call,
                    IsOutgoing = info.Call.InitiatorId == me,
                    Title = TitleFor(chat, peer),
                    PeerUserId = peer,
                    AvatarMediaId = AvatarFor(chat, peer),
                };
            session = baseSession with
            {
                Call = info.Call,
                JoinInfo = info,
                LiveKitUrl = _endpoints.ResolveLiveKitUrl(info.LiveKitUrl),
                StartWithVideo = withVideo,
                IsCameraOn = withVideo,
                Phase = IsFinished(info.Call.Status) ? CallPhase.Ended : CallPhase.Connecting,
                EndStatus = null,
            };
            _current = session;
            _mediaConnected = false;
            if (_incoming?.Call.Id == callId) _incoming = null;
        }
        RaiseChanged();
        await StartPlatformAsync().ConfigureAwait(false);
    }

    public void ReportMediaState(CallMediaState state)
    {
        CallSession? toEnd = null;
        var endStatus = CallStatus.Ended;
        var changed = false;
        lock (_lock)
        {
            if (_current is not { Phase: not CallPhase.Ended } current) return;
            switch (state)
            {
                case CallMediaState.Connecting:
                    if (current.Phase is CallPhase.Active or CallPhase.Reconnecting)
                        (changed, _current) = (true, current with { Phase = CallPhase.Connecting });
                    break;
                case CallMediaState.Connected:
                    _mediaConnected = true;
                    var phase = PhaseFor(current.Call, current.IsOutgoing);
                    if (phase != current.Phase || (phase == CallPhase.Active && current.ConnectedAt is null))
                    {
                        _current = current with
                        {
                            Phase = phase,
                            ConnectedAt = phase == CallPhase.Active ? current.ConnectedAt ?? _time.GetUtcNow() : current.ConnectedAt,
                        };
                        changed = true;
                    }
                    break;
                case CallMediaState.Reconnecting:
                    if (current.Phase != CallPhase.Reconnecting)
                        (changed, _current) = (true, current with { Phase = CallPhase.Reconnecting });
                    break;
                case CallMediaState.Disconnected:
                    toEnd = current;
                    break;
                case CallMediaState.Failed:
                    toEnd = current;
                    endStatus = CallStatus.Failed;
                    break;
            }
        }
        if (changed) RaiseChanged();
        if (toEnd is null) return;

        End(toEnd, endStatus, hangupOnServer: true);
    }

    public void ReportLocalMedia(bool micMuted, bool cameraOn, bool screenSharing, AudioRouteKind? audioRoute)
    {
        bool cameraChanged;
        lock (_lock)
        {
            // An ended call keeps its final state while it lingers (a replaced record would never be cleared).
            if (_current is not { Phase: not CallPhase.Ended } current) return;
            cameraChanged = current.IsCameraOn != cameraOn;
            var next = current with { IsMicMuted = micMuted, IsCameraOn = cameraOn, IsScreenSharing = screenSharing, AudioRoute = audioRoute };
            if (next == current) return;
            _current = next;
        }
        RaiseChanged();
        // Android 14+ needs the foreground-service type updated, which is only allowed in foreground.
        if (cameraChanged && _platformStarted && _appLifecycle.IsForeground)
            _platform.OnCallMediaChangedAsync(cameraOn).Forget(_logger);
    }

    public void ReportParticipants(IReadOnlyList<CallParticipantState> participants)
    {
        lock (_lock)
        {
            if (_current is not { Phase: not CallPhase.Ended } current) return;
            _current = current with { Participants = participants.ToList() };
        }
        RaiseChanged();
    }

    public void RequestCommand(CallCommand command)
    {
        // Hang up works even when no UI is attached (WebView gone); the UI still disconnects its media.
        if (command == CallCommand.Hangup) HangupAsync().Forget(_logger);
        EventRaiser.Raise(CommandRequested, command, _logger);
    }

    public async Task<IReadOnlyList<CallHistoryItemDto>> GetHistoryAsync(DateTimeOffset? before = null, CancellationToken cancellationToken = default)
    {
        var items = await _api.GetCallHistoryAsync(before, 50, cancellationToken).ConfigureAwait(false);
        _users.Upsert(items.Where(i => i.Peer is not null).Select(i => i.Peer!));
        return items;
    }

    Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        lock (_lock) _signedIn.TrySetResult();
        return Task.CompletedTask;
    }

    async Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        Interlocked.Increment(ref _generation);
        CallSession? current;
        CallSession? incoming;
        bool platformStarted;
        lock (_lock)
        {
            current = _current;
            incoming = _incoming;
            platformStarted = _platformStarted;
            _current = null;
            _incoming = null;
            _earlyUpdate = null;
            _accepting = null;
            _mediaConnected = false;
            _platformStarted = false;
            _platformPending = false;
            if (_signedIn.Task.IsCompleted) _signedIn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (current is null && incoming is null) return;
        RaiseChanged();
        if (current is not null) EventRaiser.Raise(CommandRequested, CallCommand.Hangup, _logger);
        if (incoming is not null) await _notifications.CancelCallAsync(incoming.Call.Id).ConfigureAwait(false);
        if (platformStarted) await _platform.OnCallEndedAsync().ConfigureAwait(false);
    }

    // Realtime

    private async Task OnIncomingAsync(CallDto call)
    {
        if (_auth.UserId is not { } me || call.InitiatorId == me || IsFinished(call.Status)) return;
        var mine = call.Participants.FirstOrDefault(p => p.UserId == me);
        if (mine is not null && mine.Status is not (CallParticipantStatus.Ringing or CallParticipantStatus.Invited)) return;

        // Rings with what this device knows; a chat or caller it has not seen yet is filled in by RingAsync.
        var chat = _chats.Get(call.ChatId);
        var peer = call.Kind == CallKind.Private ? call.InitiatorId : (Guid?)null;
        var session = new CallSession
        {
            Call = call,
            Phase = CallPhase.IncomingRinging,
            IsOutgoing = false,
            StartWithVideo = call.IsVideo,
            Title = TitleFor(chat, peer),
            PeerUserId = peer,
            AvatarMediaId = AvatarFor(chat, peer),
        };

        bool busy;
        lock (_lock)
        {
            if (_current?.Call.Id == call.Id || _incoming?.Call.Id == call.Id) return;
            busy = _current is { Phase: not CallPhase.Ended } || _incoming is not null;
            if (!busy) _incoming = session;
        }
        if (busy)
        {
            // Already talking (or another call rings): the caller hears "busy".
            try
            {
                await _api.DeclineCallAsync(call.Id, busy: true).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                _logger.LogDebug("Busy decline failed ({Code})", ex.Code);
            }
            return;
        }
        RaiseChanged();

        ExpireIncomingLaterAsync(session).Forget(_logger);
        RingAsync(session, chat, peer).Forget(_logger);
    }

    /// <summary>
    /// A chat or caller this device has not seen yet (a new contact, a cold start before the chat list loaded) is
    /// fetched while the call already rings: a slow or failed lookup never holds the call up or loses it. In
    /// background the notification is the ringing, so it waits a moment for the caller's name.
    /// </summary>
    private async Task RingAsync(CallSession ringing, ChatSummaryDto? chat, Guid? peer)
    {
        if (chat is null || peer is { } peerId && _users.Get(peerId) is null)
        {
            var lookup = LookUpCallerAsync(ringing.Call, chat, peer);
            lookup.Forget(_logger);
            if (!lookup.IsCompleted) await Task.WhenAny(lookup, Task.Delay(CallerLookupWait, _time)).ConfigureAwait(false);
            lock (_lock)
            {
                if (_incoming is not { } latest || latest.Call.Id != ringing.Call.Id) return;
                ringing = latest;
            }
        }
        NotifyIncoming(ringing);
    }

    /// <summary>Puts the fetched chat and caller on the ringing (or meanwhile answered) call; a failed fetch keeps the placeholders.</summary>
    private async Task LookUpCallerAsync(CallDto call, ChatSummaryDto? chat, Guid? peer)
    {
        try
        {
            chat ??= await _chats.GetOrFetchAsync(call.ChatId).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            _logger.LogDebug("The chat of an incoming call could not be fetched ({Code})", ex.Code);
        }
        if (peer is { } peerId && _users.Get(peerId) is null) await _users.GetOrFetchAsync(peerId).ConfigureAwait(false);

        var title = TitleFor(chat, peer);
        var avatar = AvatarFor(chat, peer);
        lock (_lock)
        {
            var held = _incoming?.Call.Id == call.Id ? _incoming
                : _current is { Phase: not CallPhase.Ended } current && current.Call.Id == call.Id ? current
                : null;
            if (held is null) return;
            var named = held with { Title = title, AvatarMediaId = avatar };
            if (named == held) return;
            if (ReferenceEquals(held, _incoming)) _incoming = named;
            else _current = named;
        }
        RaiseChanged();
    }

    /// <summary>In background the incoming-call notification rings in place of the call screen.</summary>
    private void NotifyIncoming(CallSession incoming)
    {
        if (_appLifecycle.IsForeground || !_settings.Current.Notifications.CallsEnabled) return;
        var call = incoming.Call;
        // A group whose chat could not be fetched is announced by who calls.
        var caller = call.Kind == CallKind.Private || incoming.Title.Length == 0 ? _users.DisplayNameOf(call.InitiatorId) : incoming.Title;
        _notifications.ShowIncomingCallAsync(call, caller).Forget(_logger);
    }

    private void OnCallUpdated(CallDto call)
    {
        var me = _auth.UserId;
        CallSession? toEnd = null;
        Guid? stopRinging = null;
        var changed = false;
        lock (_lock)
        {
            if (_current is { } current && current.Call.Id == call.Id && current.Phase != CallPhase.Ended)
            {
                if (IsFinished(call.Status))
                {
                    toEnd = current with { Call = call };
                    _current = toEnd;
                }
                else
                {
                    _current = current with { Call = call, Phase = current.Phase == CallPhase.Reconnecting ? current.Phase : PhaseFor(call, current.IsOutgoing) };
                    if (_current.Phase == CallPhase.Active && current.ConnectedAt is null) _current = _current with { ConnectedAt = _time.GetUtcNow() };
                }
                changed = true;
            }
            else if (_current is { Phase: not CallPhase.Ended } pending && pending.Call.Id == Guid.Empty
                     && pending.Call.ChatId == call.ChatId && call.InitiatorId == me)
            {
                // The server publishes before it answers my start, and the callee may answer or decline before the
                // response arrives: ApplyJoinInfo picks this up when it is newer than the response's snapshot.
                _earlyUpdate = call;
            }

            if (_incoming is { } incoming && incoming.Call.Id == call.Id)
            {
                var mine = me is { } id ? call.Participants.FirstOrDefault(p => p.UserId == id) : null;
                var stillRinging = mine is null || mine.Status is CallParticipantStatus.Ringing or CallParticipantStatus.Invited;
                if (IsFinished(call.Status) || (!stillRinging && _accepting != call.Id))
                {
                    // Ended, or answered/declined on another device.
                    _incoming = null;
                    stopRinging = call.Id;
                }
                else
                {
                    _incoming = incoming with { Call = call };
                }
                changed = true;
            }
        }

        if (changed) RaiseChanged();
        if (stopRinging is { } ringing) _notifications.CancelCallAsync(ringing).Forget(_logger);
        if (toEnd is not null) End(toEnd, call.Status, hangupOnServer: false);
    }

    private async Task VerifyAsync()
    {
        var generation = _generation;
        foreach (var session in new[] { Current, Incoming })
        {
            if (session is null || session.Phase == CallPhase.Ended || session.Call.Id == Guid.Empty) continue;
            try
            {
                var call = await _api.GetCallAsync(session.Call.Id).ConfigureAwait(false);
                if (generation == _generation) OnCallUpdated(call);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                if (generation == _generation) OnCallUpdated(session.Call with { Status = CallStatus.Ended });
            }
            catch (ApiException ex)
            {
                _logger.LogDebug("Call verification failed ({Code})", ex.Code);
            }
        }

        // An IncomingCall missed while disconnected (or before the app started) still rings.
        if (_auth.UserId is not { } me || Current is { Phase: not CallPhase.Ended } || Incoming is not null) return;
        var ringingChats = _chats.Chats.Concat(_chats.ArchivedChats)
            .Where(c => c.Type == ChatType.Private && c.ActiveCallId is not null)
            .Take(MaxRingingChecks)
            .ToList();
        foreach (var chat in ringingChats)
        {
            try
            {
                var call = await _api.GetCallAsync(chat.ActiveCallId!.Value).ConfigureAwait(false);
                if (generation != _generation) return;
                if (call.Status == CallStatus.Ringing && call.InitiatorId != me
                    && call.Participants.Any(p => p.UserId == me && p.Status == CallParticipantStatus.Ringing))
                {
                    await OnIncomingAsync(call).ConfigureAwait(false);
                    return;
                }
            }
            catch (ApiException ex)
            {
                _logger.LogDebug("Ringing check failed ({Code})", ex.Code);
            }
        }
    }

    private async Task OnCallActionAsync(Guid callId, CallNotificationAction action)
    {
        // After a cold start a button may come in before the session is restored, and the API refuses every request
        // until then: wait for the account instead of dropping the answer (or leaving the caller ringing).
        Task signedIn;
        lock (_lock) signedIn = _signedIn.Task;
        try
        {
            await signedIn.WaitAsync(SignInWait, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("A call notification action came while nobody was signed in");
            return;
        }

        switch (action)
        {
            case CallNotificationAction.Accept or CallNotificationAction.AcceptVideo:
                if (Incoming?.Call.Id != callId)
                {
                    // Cold start from the notification: the IncomingCall event has not arrived (yet).
                    try
                    {
                        var call = await _api.GetCallAsync(callId).ConfigureAwait(false);
                        await OnIncomingAsync(call).ConfigureAwait(false);
                    }
                    catch (ApiException ex)
                    {
                        _logger.LogDebug("Call from a notification is gone ({Code})", ex.Code);
                        return;
                    }
                }
                if (Incoming?.Call.Id == callId)
                    await AcceptAsync(action == CallNotificationAction.AcceptVideo).ConfigureAwait(false);
                break;
            case CallNotificationAction.Decline:
                if (Incoming?.Call.Id == callId)
                {
                    await DeclineAsync().ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await _api.DeclineCallAsync(callId).ConfigureAwait(false);
                    }
                    catch (ApiException ex)
                    {
                        _logger.LogDebug("Declining from a notification failed ({Code})", ex.Code);
                    }
                }
                break;
            case CallNotificationAction.Hangup:
                if (Current?.Call.Id == callId) RequestCommand(CallCommand.Hangup);
                break;
        }
    }

    private void OnForegroundChanged(bool foreground)
    {
        if (!foreground) return;
        bool pending;
        lock (_lock) pending = _platformPending;
        if (pending) StartPlatformAsync().Forget(_logger);
    }

    // Helpers

    private void SetCurrent(CallSession session, bool mediaConnected)
    {
        lock (_lock)
        {
            _current = session;
            _mediaConnected = mediaConnected;
        }
        RaiseChanged();
    }

    /// <summary>Applies join info to the session that requested it. False when the session ended or was replaced meanwhile.</summary>
    private bool ApplyJoinInfo(CallSession requested, CallJoinInfoDto info, Guid me)
    {
        CallSession? ended = null;
        lock (_lock)
        {
            // A CallUpdated that overtook the start response (answered, declined, busy) is newer than its snapshot.
            var call = info.Call;
            if (_earlyUpdate is { } early && early.Id == call.Id)
            {
                _earlyUpdate = null;
                if (Stage(early.Status) > Stage(call.Status)) call = early;
            }
            if (_current is not { } current) return false;
            // The same call or, for a start, any start in that chat that still waits for its id: the server keeps one
            // unfinished call per chat, so calling there again gets this very call.
            var same = current.Call.Id == call.Id
                       || requested.Call.Id == Guid.Empty && current.Call.Id == Guid.Empty && current.Call.ChatId == call.ChatId;
            if (!same || current.Phase == CallPhase.Ended) return false;
            var outgoing = call.InitiatorId == me;
            if (IsFinished(call.Status))
            {
                // Over before we could join: End() below publishes the final state with its status in one step.
                _current = current with { Call = call, IsOutgoing = outgoing };
                ended = _current;
            }
            else
            {
                _current = current with
                {
                    Call = call,
                    JoinInfo = info,
                    LiveKitUrl = _endpoints.ResolveLiveKitUrl(info.LiveKitUrl),
                    IsOutgoing = outgoing,
                    Phase = PhaseFor(call, outgoing),
                };
            }
        }
        if (ended is not null)
        {
            End(ended, ended.Call.Status, hangupOnServer: false);
            return false;
        }
        RaiseChanged();
        return true;
    }

    private CallPhase PhaseFor(CallDto call, bool outgoing)
    {
        if (IsFinished(call.Status)) return CallPhase.Ended;
        if (call is { Kind: CallKind.Private, Status: CallStatus.Ringing })
            return outgoing ? CallPhase.OutgoingRinging : CallPhase.Connecting;
        return _mediaConnected ? CallPhase.Active : CallPhase.Connecting;
    }

    private void End(CallSession session, CallStatus status, bool hangupOnServer)
    {
        bool platformStarted;
        CallSession ended;
        lock (_lock)
        {
            if (_current is null || _current.Call.Id != session.Call.Id || _current.Phase == CallPhase.Ended && _current.EndStatus is not null) return;
            ended = _current with { Phase = CallPhase.Ended, EndStatus = status };
            _current = ended;
            _mediaConnected = false;
            platformStarted = _platformStarted;
            _platformStarted = false;
            _platformPending = false;
        }
        RaiseChanged();
        if (platformStarted) _platform.OnCallEndedAsync().Forget(_logger);
        if (hangupOnServer && session.Call.Id != Guid.Empty) HangupOnServerAsync(session.Call.Id).Forget(_logger);
        ClearEndedLaterAsync(ended).Forget(_logger);
    }

    private async Task HangupOnServerAsync(Guid callId)
    {
        try
        {
            await _api.HangupAsync(callId).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            _logger.LogDebug("Hang-up request failed ({Code})", ex.Code);
        }
    }

    /// <summary>
    /// A start that failed on the way may still have created the call: hang up my call that nobody answered yet
    /// (a private one still rings; in a group nobody else joined), unless the chat is being called again here (that
    /// start gets this very call: one unfinished call per chat). Best effort.
    /// </summary>
    private async Task HangupLostStartAsync(Guid chatId, Guid me, int generation)
    {
        try
        {
            var call = await _api.GetActiveCallAsync(chatId).ConfigureAwait(false);
            if (call is null || call.InitiatorId != me || generation != _generation) return;
            var unanswered = call.Kind == CallKind.Private
                ? call.Status == CallStatus.Ringing
                : call.Participants.All(p => p.UserId == me || p.Status != CallParticipantStatus.Joined);
            if (!unanswered) return;
            lock (_lock)
            {
                if (_current is { Phase: not CallPhase.Ended } current && current.Call.ChatId == chatId) return;
            }
            await _api.HangupAsync(call.Id).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            _logger.LogDebug("Call clean-up after a failed start did not finish ({Code})", ex.Code);
        }
    }

    private async Task ClearEndedLaterAsync(CallSession ended)
    {
        await Task.Delay(EndedLinger, _time).ConfigureAwait(false);
        lock (_lock)
        {
            if (!ReferenceEquals(_current, ended)) return;
            _current = null;
        }
        RaiseChanged();
    }

    /// <summary>
    /// Accepting failed on the way: the call rings here again (with its latest state) unless it ended, was hung up
    /// or replaced meanwhile. The safety expiry of the original ringing still applies.
    /// </summary>
    private void RestoreIncoming(CallSession ringing)
    {
        CallSession? restored = null;
        lock (_lock)
        {
            if (_current is not { Phase: not CallPhase.Ended } current || current.Call.Id != ringing.Call.Id) return;
            _current = null;
            _mediaConnected = false;
            if (_accepting == ringing.Call.Id) _accepting = null;
            if (_incoming is null) _incoming = restored = ringing with { Call = current.Call };
        }
        RaiseChanged();
        if (restored is not null) NotifyIncoming(restored);
    }

    private void ClearIfCurrent(CallSession session)
    {
        lock (_lock)
        {
            if (_current is null || _current.Call.ChatId != session.Call.ChatId || _current.Phase == CallPhase.Ended) return;
            _current = null;
            _mediaConnected = false;
        }
        RaiseChanged();
    }

    private async Task ExpireIncomingLaterAsync(CallSession session)
    {
        await Task.Delay(IncomingSafetyTimeout, _time).ConfigureAwait(false);
        lock (_lock)
        {
            if (!ReferenceEquals(_incoming, session) && _incoming?.Call.Id != session.Call.Id) return;
            _incoming = null;
        }
        RaiseChanged();
        _notifications.CancelCallAsync(session.Call.Id).Forget(_logger);
    }

    private async Task StartPlatformAsync()
    {
        CallSession? current;
        lock (_lock)
        {
            current = _current;
            if (current is null || current.Phase == CallPhase.Ended || _platformStarted) return;
            if (!_appLifecycle.IsForeground)
            {
                // Android 14+ refuses to start a microphone foreground service from background.
                _platformPending = true;
                return;
            }
            _platformPending = false;
            _platformStarted = true;
        }
        try
        {
            await _platform.OnCallStartedAsync(current.Call, current.Title, current.IsCameraOn).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The platform call keep-alive did not start");
        }
    }

    private async Task HangupForSignOutAsync()
    {
        var current = Current;
        var incoming = Incoming;
        using var timeout = new CancellationTokenSource(SignOutHangupTimeout);
        try
        {
            if (current is { Phase: not CallPhase.Ended } && current.Call.Id != Guid.Empty)
                await _api.HangupAsync(current.Call.Id, timeout.Token).ConfigureAwait(false);
            if (incoming is not null)
                await _api.DeclineCallAsync(incoming.Call.Id, ct: timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            _logger.LogDebug("Call clean-up before sign-out did not finish");
        }
    }

    /// <summary>Microphone is required (throws permission_denied); returns whether the camera may be used.</summary>
    private async Task<bool> EnsurePermissionsAsync(bool video)
    {
        var microphone = await _permissions.RequestAsync(AppPermission.Microphone).ConfigureAwait(false);
        if (microphone is PermissionState.Denied or PermissionState.Restricted)
            throw new ApiException(403, ClientErrorCodes.PermissionDenied, "Microphone access is required for calls.");
        if (!video) return false;
        var camera = await _permissions.RequestAsync(AppPermission.Camera).ConfigureAwait(false);
        return camera is not (PermissionState.Denied or PermissionState.Restricted);
    }

    private string TitleFor(ChatSummaryDto? chat, Guid? peer)
    {
        if (peer is { } peerId) return _users.DisplayNameOf(peerId);
        return chat is not null ? _chats.TitleOf(chat) : "";
    }

    private Guid? AvatarFor(ChatSummaryDto? chat, Guid? peer)
    {
        if (peer is { } peerId) return _users.Get(peerId)?.AvatarMediaId ?? chat?.Peer?.AvatarMediaId;
        return chat is not null ? _chats.AvatarOf(chat) : null;
    }

    private static Guid? PeerOf(CallDto call, Guid me) =>
        call.Participants.Select(p => p.UserId).FirstOrDefault(id => id != me) is { } other && other != Guid.Empty
            ? other
            : call.InitiatorId != me ? call.InitiatorId : null;

    private static bool IsFinished(CallStatus status) => status is not (CallStatus.Ringing or CallStatus.Active);

    /// <summary>Call statuses only move forward: ringing → active → finished.</summary>
    private static int Stage(CallStatus status) => status switch
    {
        CallStatus.Ringing => 0,
        CallStatus.Active => 1,
        _ => 2,
    };

    private void RaiseChanged() => EventRaiser.Raise(Changed, _logger);
}
