using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Features.Chat;
using SkyMessage.Client.UI.Kit;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Calls;

/// <summary>
/// The call screen, rendered once by MainLayout above everything. Incoming (ringtone), outgoing (ringback), 1:1 and
/// group layouts, a minimized bar at the top of the shell, the ended screen. It owns the LiveKit engine (calls.js),
/// connects it whenever <see cref="ICallService.Current"/> carries join info, reports media back through
/// ReportMediaState/ReportLocalMedia/ReportParticipants and executes <see cref="ICallService.CommandRequested"/>.
/// The expanded screen owns a history entry, so Back minimizes it.
/// </summary>
public partial class CallOverlay : IAsyncDisposable
{
    private enum ViewMode
    {
        Incoming,
        Outgoing,
        Connecting,
        Active,
        Reconnecting,
        Ended,
    }

    private enum SoundMode
    {
        None,
        Ringtone,
        Ringback,
    }

    private sealed record CallView(ViewMode Mode, CallSession Session, string Title, string Status, bool IsGroup);

    /// <summary>What was on screen when the call went away: kept for the fade-out.</summary>
    private sealed record LeavingView(CallView View, bool Expanded);

    private const int LeaveMs = 240;

    [Inject] private ICallService Calls { get; set; } = default!;
    [Inject] private IAuthService Auth { get; set; } = default!;
    [Inject] private IUserStore Users { get; set; } = default!;
    [Inject] private IChatListStore ChatList { get; set; } = default!;
    [Inject] private ISettingsService Settings { get; set; } = default!;
    [Inject] private ISoundService Sounds { get; set; } = default!;
    [Inject] private IMediaUrlBuilder MediaUrls { get; set; } = default!;
    [Inject] private NavigationService Nav { get; set; } = default!;
    [Inject] private LayoutService Layout { get; set; } = default!;
    [Inject] private IPlatformInfo Platform { get; set; } = default!;
    [Inject] private IPermissionService Permissions { get; set; } = default!;
    [Inject] private IAudioRouteService AudioRoutes { get; set; } = default!;
    [Inject] private IAppLifecycle Lifecycle { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private FormatService Format { get; set; } = default!;
    [Inject] private HapticFeedback Haptics { get; set; } = default!;
    [Inject] private ILogger<CallOverlay> Logger { get; set; } = default!;

    private CallEngine? _engine;
    private MediaDevicesJs? _devices;
    private MediaCapabilities? _caps;

    private bool _expanded;
    private OverlayHandle? _overlay;
    private Guid? _seenIncomingId;
    private bool _seenCurrent;
    private CallSession? _endedSession;
    private DateTimeOffset? _endedAt;

    private string? _engineToken;
    private string? _staleToken;
    private bool _engineConnected;
    private bool _wantMic = true;
    private bool _wantCamera;
    private LocalMediaInfo? _local;
    private IReadOnlyList<RemoteMediaInfo> _remote = [];
    private bool _audioBlocked;
    private bool _cameraInterrupted;
    private bool _replaced;
    private string? _micProblem;
    private bool _swapped;

    private IReadOnlyList<AudioRoute> _routes = [];
    private string? _chosenRouteId;
    private AudioRouteKind? _chosenRouteKind;
    private IReadOnlyList<MediaDeviceItem> _speakers = [];
    private IReadOnlyList<MediaDeviceItem> _microphones = [];
    private ContextMenu? _audioMenu;

    private ElementReference _stage;
    private string? _boundStageId;
    // Drag down to minimize (calls.js attachMinimize), bound to the expanded screen element it was attached to.
    private ElementReference _screen;
    private ElementReference _backdrop;
    private JsModule? _gestureModule;
    private JsObject? _gesture;
    private string? _gestureFor;
    private bool _minimizing;
    private DotNetObjectReference<CallOverlay>? _self;
    // As last rendered, and the leftover of a call that just went away (see StartLeaving).
    private CallView? _lastView;
    private bool _lastExpanded;
    private LeavingView? _leaving;
    private int _leaveVersion;
    private Timer? _ticker;
    private SoundMode _sound;
    // The incoming call arrived in background on a phone app: Core posted the OS incoming-call notification, which
    // rings by itself (channel ringtone, silent mode respected) while the app stays in background.
    private bool _osRinging;
    private bool _syncing;
    private bool _syncAgain;

    // ---- derived state ----

    private bool MicOn => _local?.MicEnabled ?? _wantMic;

    private bool CameraOn => _local?.CameraEnabled ?? _wantCamera;

    private bool ScreenOn => _local?.ScreenSharing ?? false;

    private bool LocalSpeaking => _local?.Speaking ?? false;

    private int LocalQuality => _local?.Quality ?? -1;

    private bool FrontCamera => !string.Equals(_local?.Facing, "environment", StringComparison.OrdinalIgnoreCase);

    private bool MirrorLocal => FrontCamera && Settings.Current.Devices.MirrorFrontCamera;

    private bool CanFlip => CameraOn && (Platform.IsMobile || _hasSeveralCameras);

    private bool _hasSeveralCameras;

    /// <summary>
    /// Desktop browsers show their screen (getDisplayMedia). Phones do not: the Android WebView has no screen capture,
    /// and sharing from the app would need native capture published outside the WebView's media engine.
    /// </summary>
    private bool OffersScreenShare => !Platform.IsMobile && _caps?.ScreenShare == true;

    private bool CanScreenShare => OffersScreenShare && _engineConnected;

    private bool HasAudioMenu => AudioRoutes.IsSupported || _caps?.Enumerate == true;

    private AudioRouteKind? ActiveRouteKind => AudioRoutes.IsSupported ? _routes.FirstOrDefault(r => r.IsActive)?.Kind : null;

    private Guid? MyId => Auth.UserId;

    private CallView? Display
    {
        get
        {
            var incoming = Calls.Incoming;
            var current = Calls.Current;
            if (incoming is not null && !CallUi.IsLive(current))
            {
                var group = incoming.Call.Kind == CallKind.Group;
                var status = group ? L["calls.incoming.group"] : incoming.Call.IsVideo ? L["calls.incoming.video"] : L["calls.incoming.audio"];
                return new CallView(ViewMode.Incoming, incoming, TitleOf(incoming), status, group);
            }
            if (current is null) return null;
            var mode = current.Phase switch
            {
                CallPhase.OutgoingRinging => ViewMode.Outgoing,
                CallPhase.Active => ViewMode.Active,
                CallPhase.Reconnecting => ViewMode.Reconnecting,
                CallPhase.Ended => ViewMode.Ended,
                _ => ViewMode.Connecting,
            };
            return new CallView(mode, current, TitleOf(current), StatusOf(current, mode), current.Call.Kind == CallKind.Group);
        }
    }

    private string TitleOf(CallSession session)
    {
        if (session.PeerUserId is { } peer) return Users.NameOf(peer, L);
        if (ChatList.Get(session.Call.ChatId) is { } chat) return ChatList.TitleOf(chat);
        return string.IsNullOrWhiteSpace(session.Title) ? L["calls.unknownChat"] : session.Title;
    }

    private string StatusOf(CallSession session, ViewMode mode)
    {
        switch (mode)
        {
            case ViewMode.Outgoing:
                return L["calls.status.ringing"];
            case ViewMode.Connecting:
                return session.Call.Kind == CallKind.Group ? L["calls.status.joining"] : L["calls.status.connecting"];
            case ViewMode.Reconnecting:
                return L["calls.status.reconnecting"];
            case ViewMode.Ended:
                var text = CallUi.EndText(L, session.EndStatus, session.IsOutgoing);
                if (session.ConnectedAt is { } started && session.EndStatus is CallStatus.Ended or null)
                {
                    var end = session.Call.EndedAt ?? _endedAt ?? DateTimeOffset.UtcNow;
                    var length = end - started;
                    if (length > TimeSpan.Zero) text += " · " + Format.Duration(length);
                }
                return text;
            default:
                var elapsed = session.ConnectedAt is { } since ? Format.Duration(DateTimeOffset.UtcNow - since) : "";
                if (session.Call.Kind != CallKind.Group) return elapsed;
                var people = L.Plural("calls.participants", _remote.Count + 1);
                return string.IsNullOrEmpty(elapsed) ? people : $"{people} · {elapsed}";
        }
    }

    /// <summary>The remote person in a 1:1 call as reported by the media engine.</summary>
    private RemoteMediaInfo? Peer(CallSession session)
    {
        if (session.PeerUserId is not { } peer) return _remote.FirstOrDefault();
        var key = peer.ToString("N");
        return _remote.FirstOrDefault(r => r.UserId == key) ?? _remote.FirstOrDefault();
    }

    private RemoteMediaInfo? ScreenSharer => _remote.FirstOrDefault(r => r.HasScreen);

    private static Guid ParseId(string id) => Guid.TryParse(id, out var value) ? value : Guid.Empty;

    // ---- lifecycle ----

    protected override void OnInitialized()
    {
        Calls.Changed += OnCallsChanged;
        Calls.CommandRequested += OnCommand;
        Lifecycle.ForegroundChanged += OnForegroundChanged;
        if (AudioRoutes.IsSupported) AudioRoutes.RoutesChanged += OnRoutesChanged;
        CallUi.ExpandRequested += OnExpandRequested;
        RenderOn<Guid>(h => Users.UserChanged += h, h => Users.UserChanged -= h, IsShown);
        RenderOn(h => Layout.Changed += h, h => Layout.Changed -= h);
        RenderOn(h => MediaUrls.Changed += h, h => MediaUrls.Changed -= h);

        // A rebuilt WebView (or a reloaded page section) finds a call in progress: join again for a fresh token
        // instead of reusing the one the previous screen connected with.
        var current = Calls.Current;
        if (current is { Phase: not CallPhase.Ended, JoinInfo: { } join } && current.Call.Id != Guid.Empty)
        {
            _staleToken = join.LiveKitToken;
            _ = RejoinAsync(current);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _lastView = Display;
        _lastExpanded = _expanded;
        CallUi.SetCovering(_expanded && _lastView is not null);
        if (firstRender) await SyncAsync();
        await BindStageAsync();
        await BindGestureAsync();
    }

    private bool IsShown(Guid userId)
    {
        var view = Calls.Incoming ?? Calls.Current;
        if (view is null) return false;
        if (view.PeerUserId == userId) return true;
        var key = userId.ToString("N");
        return _remote.Any(r => r.UserId == key);
    }

    private async Task RejoinAsync(CallSession session)
    {
        try
        {
            await Calls.JoinAsync(session.Call.Id, session.IsCameraOn);
        }
        catch (Exception ex)
        {
            // The call may have ended meanwhile (Core reconciles it); otherwise the token we have is the best option.
            Logger.LogDebug(ex, "Re-joining the call after a restart failed");
        }
        await InvokeAsync(async () =>
        {
            // A successful join replaced JoinInfo with a fresh token; if nothing changed, connect with the old one.
            _staleToken = null;
            await SyncAsync();
        });
    }

    private void OnCallsChanged() => _ = InvokeAsync(SyncAsync);

    private void OnExpandRequested() => _ = InvokeAsync(async () =>
    {
        if (Display is not null) await ExpandAsync();
    });

    private async Task SyncAsync()
    {
        if (IsDisposed) return;
        if (_syncing)
        {
            _syncAgain = true;
            return;
        }
        _syncing = true;
        try
        {
            do
            {
                _syncAgain = false;
                await SyncOnceAsync();
            }
            while (_syncAgain && !IsDisposed);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Call screen update failed");
        }
        finally
        {
            _syncing = false;
        }
        if (!IsDisposed) StateHasChanged();
    }

    private async Task SyncOnceAsync()
    {
        var incoming = Calls.Incoming;
        var current = Calls.Current;
        var showIncoming = incoming is not null && !CallUi.IsLive(current);
        var expand = false;

        if (showIncoming && incoming!.Call.Id != _seenIncomingId)
        {
            _seenIncomingId = incoming.Call.Id;
            expand = true;
            _ = EnsureEngine().PreloadAsync();
            _osRinging = await OsRingsForIncomingAsync();
        }
        if (incoming is null)
        {
            _seenIncomingId = null;
            _osRinging = false;
        }

        if (CallUi.IsLive(current))
        {
            if (!_seenCurrent)
            {
                _seenCurrent = true;
                expand = true;
                ResetMedia(current!);
            }
        }
        else if (current is null)
        {
            _seenCurrent = false;
        }

        if (current is { Phase: CallPhase.Ended } && !ReferenceEquals(current, _endedSession))
        {
            var first = _endedSession is null || _endedSession.Call.Id != current.Call.Id || _endedSession.Call.ChatId != current.Call.ChatId;
            _endedSession = current;
            if (first)
            {
                _endedAt = DateTimeOffset.UtcNow;
                PlayEndTone(current);
            }
        }
        else if (current is null || CallUi.IsLive(current))
        {
            // A live call since the last end: its own end is a new one even with the same ids (a redial that is busy
            // again before the first busy screen went away has no call id either).
            _endedSession = null;
        }

        UpdateEngine(current);
        UpdateSounds(showIncoming, current);
        UpdateTicker(current);

        if (showIncoming || current is not null)
        {
            StopLeaving();
        }
        else if (_lastView is { } last && _leaving is null)
        {
            StartLeaving(last, _lastExpanded);
        }

        if (!showIncoming && current is null)
        {
            await CollapseAsync();
            _remote = [];
            _local = null;
            _replaced = false;
            _audioBlocked = false;
            _cameraInterrupted = false;
            _micProblem = null;
        }
        else if (expand)
        {
            await ExpandAsync();
        }
    }

    private void ResetMedia(CallSession session)
    {
        _wantMic = !session.IsMicMuted;
        _wantCamera = session.IsCameraOn;
        _local = null;
        _remote = [];
        _audioBlocked = false;
        _cameraInterrupted = false;
        _replaced = false;
        _micProblem = null;
        _swapped = false;
        _endedAt = null;
        _chosenRouteId = null;
        _chosenRouteKind = null;
        _routes = [];
    }

    // ---- media engine ----

    private CallEngine EnsureEngine()
    {
        if (_engine is not null) return _engine;
        var engine = new CallEngine(JS, Logger);
        engine.StateChanged += (state, detail) => _ = InvokeAsync(() => OnEngineState(state, detail));
        engine.LocalChanged += info => _ = InvokeAsync(() => OnLocalMedia(info));
        engine.ParticipantsChanged += list => _ = InvokeAsync(() => OnParticipants(list));
        engine.Notice += (kind, detail) => _ = InvokeAsync(() => OnEngineNotice(kind, detail));
        _engine = engine;
        _boundStageId = null;
        return engine;
    }

    private void UpdateEngine(CallSession? current)
    {
        string? wanted = current is { Phase: not CallPhase.Ended, JoinInfo: { } join } && !string.IsNullOrEmpty(current.LiveKitUrl)
            ? join.LiveKitToken
            : null;
        if (wanted is not null && wanted == _staleToken) wanted = null;
        if (wanted == _engineToken) return;

        var previous = _engineToken;
        _engineToken = wanted;
        _engineConnected = false;
        if (previous is not null && _engine is not null) _ = _engine.DisconnectAsync();
        if (wanted is null)
        {
            _remote = [];
            return;
        }
        _replaced = false;
        _ = ConnectEngineAsync(current!, wanted);
    }

    private async Task ConnectEngineAsync(CallSession session, string token)
    {
        var engine = EnsureEngine();
        var caps = await GetCapabilitiesAsync();
        if (_engineToken != token || IsDisposed) return;
        var settings = Settings.Current;
        var devices = settings.Devices;
        var options = new CallConnectOptions
        {
            Url = session.LiveKitUrl!,
            Token = token,
            MyUserId = MyId?.ToString("N") ?? "",
            MicEnabled = _wantMic,
            CameraEnabled = _wantCamera,
            Mobile = Platform.IsMobile,
            LowData = settings.Data.LowDataMode,
            Quality = devices.CallVideoQuality.ToString().ToLowerInvariant(),
            Audio = new CallAudioOptions
            {
                DeviceId = devices.MicrophoneId,
                EchoCancellation = devices.EchoCancellation,
                NoiseSuppression = devices.NoiseSuppression,
                AutoGainControl = devices.AutoGainControl,
            },
            VideoDeviceId = devices.CameraId,
            SpeakerId = caps.SpeakerSelection ? devices.SpeakerId : null,
        };
        RequestRender();
        await engine.ConnectAsync(options);
    }

    private async Task<MediaCapabilities> GetCapabilitiesAsync()
    {
        if (_caps is not null) return _caps;
        _devices ??= new MediaDevicesJs(JS, Logger);
        var caps = await _devices.GetCapabilitiesAsync();
        // A failed probe (script not ready) returns all-false: ask again next time instead of caching it.
        if (caps.Media) _caps = caps;
        return caps;
    }

    /// <summary>
    /// Desktop: whether there is another camera to flip to. Counted again on connect, when the camera turns on and when
    /// devices change: until camera access is granted the browser lists cameras without ids, so they are not counted.
    /// </summary>
    private async Task CountCamerasAsync()
    {
        if (Platform.IsMobile || IsDisposed) return;
        try
        {
            var caps = await GetCapabilitiesAsync();
            if (!caps.Enumerate || _devices is null || IsDisposed) return;
            var list = await _devices.ListAsync(false, false);
            var several = list.VideoInputs.Count > 1;
            if (several == _hasSeveralCameras || IsDisposed) return;
            _hasSeveralCameras = several;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Counting cameras failed");
        }
    }

    private async Task BindStageAsync()
    {
        if (_engine is null) return;
        var stageId = _expanded && Display is not null ? _stage.Id : null;
        if (stageId == _boundStageId) return;
        _boundStageId = stageId;
        await _engine.SetStageAsync(stageId is null ? null : _stage);
    }

    private void OnEngineState(string state, string? detail)
    {
        if (IsDisposed || _engineToken is null) return;
        switch (state)
        {
            case "connecting":
                Calls.ReportMediaState(CallMediaState.Connecting);
                break;
            case "connected":
                _engineConnected = true;
                _replaced = false;
                Calls.ReportMediaState(CallMediaState.Connected);
                _ = CountCamerasAsync();
                break;
            case "reconnecting":
                Calls.ReportMediaState(CallMediaState.Reconnecting);
                break;
            case "disconnected":
                _engineConnected = false;
                Logger.LogInformation("Call media disconnected ({Reason})", detail);
                Calls.ReportMediaState(CallMediaState.Disconnected);
                break;
            case "failed":
                _engineConnected = false;
                Logger.LogWarning("Call media could not connect ({Reason})", detail);
                Calls.ReportMediaState(CallMediaState.Failed);
                break;
            case "replaced":
                // The same session joined the room elsewhere (another tab): the call goes on there.
                _engineConnected = false;
                _replaced = true;
                break;
        }
        StateHasChanged();
    }

    private void OnLocalMedia(LocalMediaInfo info)
    {
        if (IsDisposed) return;
        var cameraStarted = info.CameraEnabled && _local?.CameraEnabled != true;
        _local = info;
        if (info.CameraEnabled) _cameraInterrupted = false;
        if (info.MicEnabled) _micProblem = null;
        // Camera access is granted now, so the browser lists every camera with its id.
        if (cameraStarted) _ = CountCamerasAsync();
        ReportLocal();
        StateHasChanged();
    }

    private void OnParticipants(IReadOnlyList<RemoteMediaInfo> participants)
    {
        if (IsDisposed || _engineToken is null || !CallUi.IsLive(Calls.Current)) return;
        _remote = participants;
        var states = new List<CallParticipantState>(participants.Count);
        foreach (var p in participants)
        {
            if (Guid.TryParse(p.UserId, out var id))
                states.Add(new CallParticipantState(id, p.IsSpeaking, p.HasVideo, p.IsMuted, p.Quality, p.HasScreen));
        }
        Calls.ReportParticipants(states);
        StateHasChanged();
    }

    private void OnEngineNotice(string kind, string? detail)
    {
        if (IsDisposed) return;
        switch (kind)
        {
            case "audioBlocked":
                _audioBlocked = detail == "true";
                break;
            case "cameraInterrupted":
                _cameraInterrupted = true;
                break;
            case "micPublished":
                _ = ApplyRouteAsync();
                break;
            case "devicesChanged":
                if (_audioMenu?.IsOpen == true) _ = LoadDeviceMenuAsync();
                _ = CountCamerasAsync();
                break;
            case "deviceError":
                var parts = (detail ?? "").Split(':', 2);
                var what = parts[0];
                var code = parts.Length > 1 ? parts[1] : "failed";
                if (what == "microphone") _micProblem = code;
                else if (what == "camera") ShowCameraProblem(code);
                break;
        }
        StateHasChanged();
    }

    private void ReportLocal()
    {
        // Not for an ended call: route changes after hang-up (the audio route reset) would replace the ended session
        // that Core is about to clear, and the "Call ended" screen would stay.
        if (!CallUi.IsLive(Calls.Current)) return;
        Calls.ReportLocalMedia(!MicOn, CameraOn, ScreenOn, ActiveRouteKind);
    }

    // ---- commands & controls ----

    private void OnCommand(CallCommand command) => _ = InvokeAsync(() => ExecuteAsync(command));

    private async Task ExecuteAsync(CallCommand command)
    {
        switch (command)
        {
            case CallCommand.ToggleMicrophone:
                await ToggleMicAsync();
                break;
            case CallCommand.ToggleCamera:
                await ToggleCameraAsync();
                break;
            case CallCommand.SwitchCamera:
                await SwitchCameraAsync();
                break;
            case CallCommand.Hangup:
                // Core already hangs up; make sure the media is released right away.
                if (_engine is not null) await _engine.DisconnectAsync();
                break;
        }
    }

    private async Task AcceptAsync(bool video)
    {
        try
        {
            await Calls.AcceptAsync(video);
        }
        catch (Exception ex)
        {
            Toasts.Error(ex);
        }
    }

    private async Task DeclineAsync()
    {
        try
        {
            await Calls.DeclineAsync();
        }
        catch (Exception ex)
        {
            Toasts.Error(ex);
        }
    }

    private async Task HangupAsync()
    {
        if (_engine is not null) _ = _engine.DisconnectAsync();
        try
        {
            await Calls.HangupAsync();
        }
        catch (Exception ex)
        {
            Toasts.Error(ex);
        }
    }

    private async Task ToggleMicAsync()
    {
        var enable = !MicOn;
        _wantMic = enable;
        if (_engine is null || _engineToken is null)
        {
            _local = (_local ?? new LocalMediaInfo { CameraEnabled = _wantCamera }) with { MicEnabled = enable };
            ReportLocal();
            StateHasChanged();
            return;
        }
        var result = await _engine.SetMicrophoneAsync(enable);
        if (!result.Ok && enable)
        {
            _micProblem = result.Error;
            Toasts.Error(ClientErrorCodes.PermissionDenied);
        }
    }

    private async Task ToggleCameraAsync()
    {
        var enable = !CameraOn;
        if (enable)
        {
            var permission = await Permissions.RequestAsync(AppPermission.Camera);
            if (permission is PermissionState.Denied or PermissionState.Restricted)
            {
                Toasts.Error(ClientErrorCodes.PermissionDenied);
                return;
            }
        }
        _wantCamera = enable;
        _cameraInterrupted = false;
        if (_engine is null || _engineToken is null)
        {
            _local = (_local ?? new LocalMediaInfo { MicEnabled = _wantMic }) with { CameraEnabled = enable };
            ReportLocal();
            StateHasChanged();
            return;
        }
        var result = await _engine.SetCameraAsync(enable);
        if (!result.Ok && enable) ShowCameraProblem(result.Error);
    }

    private async Task SwitchCameraAsync()
    {
        if (_engine is null || !CameraOn) return;
        var result = await _engine.SwitchCameraAsync();
        if (!result.Ok && result.Error == "single") Toasts.Info(L["calls.error.singleCamera"]);
        else if (!result.Ok && result.Error is not null) ShowCameraProblem(result.Error);
    }

    private async Task ToggleScreenAsync()
    {
        if (_engine is null) return;
        var result = await _engine.SetScreenShareAsync(!ScreenOn);
        if (!result.Ok && result.Error is not "cancelled") Toasts.Show(L["calls.error.screen"], ToastKind.Error);
    }

    private async Task RestartCameraAsync()
    {
        _cameraInterrupted = false;
        if (_engine is null) return;
        var result = await _engine.RestartCameraAsync();
        if (!result.Ok) ShowCameraProblem(result.Error);
    }

    private async Task EnableAudioAsync()
    {
        if (_engine is null) return;
        _audioBlocked = !await _engine.StartAudioAsync();
    }

    private async Task MoveCallHereAsync()
    {
        var current = Calls.Current;
        if (current is null) return;
        _replaced = false;
        try
        {
            await Calls.JoinAsync(current.Call.Id, _wantCamera);
        }
        catch (Exception ex)
        {
            _replaced = true;
            Toasts.Error(ex);
        }
    }

    private void ShowCameraProblem(string? code)
    {
        _wantCamera = false;
        Toasts.Show(code is "denied" or "insecure" ? L.Error(ClientErrorCodes.PermissionDenied) : L["calls.error.camera"], ToastKind.Error);
    }

    private void Swap() => _swapped = !_swapped;

    // ---- audio output ----

    private void OnRoutesChanged() => _ = InvokeAsync(async () =>
    {
        await RefreshRoutesAsync();
        ReportLocal();
        StateHasChanged();
    });

    private async Task RefreshRoutesAsync()
    {
        if (!AudioRoutes.IsSupported) return;
        try
        {
            _routes = await AudioRoutes.GetRoutesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Audio routes unavailable");
        }
    }

    /// <summary>
    /// Phones: puts call audio where it belongs after the microphone is (re)published — the WebView's WebRTC stack
    /// may re-pick a device when capture restarts. Explicit choice in this call, else a connected headset, else the
    /// default from settings (speaker for video calls).
    /// </summary>
    private async Task ApplyRouteAsync()
    {
        if (!AudioRoutes.IsSupported || Calls.Current is not { } current) return;
        await RefreshRoutesAsync();
        if (_routes.Count == 0) return;
        AudioRoute? target = null;
        if (_chosenRouteId is not null || _chosenRouteKind is not null)
            target = _routes.FirstOrDefault(r => _chosenRouteId is not null && r.Id == _chosenRouteId)
                     ?? _routes.FirstOrDefault(r => r.Kind == _chosenRouteKind);
        target ??= _routes.FirstOrDefault(r => r.Kind == AudioRouteKind.Bluetooth) ?? _routes.FirstOrDefault(r => r.Kind == AudioRouteKind.Wired);
        if (target is null)
        {
            var preferred = Settings.Current.Devices.PreferredAudioRoute
                            ?? (current.Call.IsVideo || CameraOn ? AudioRouteKind.Speaker : AudioRouteKind.Earpiece);
            target = _routes.FirstOrDefault(r => r.Kind == preferred);
        }
        if (target is null || target.IsActive)
        {
            ReportLocal();
            return;
        }
        await SetRouteAsync(target);
    }

    private async Task SetRouteAsync(AudioRoute route)
    {
        try
        {
            if (!string.IsNullOrEmpty(route.Id)) await AudioRoutes.SetRouteAsync(route.Id);
            else await AudioRoutes.SetRouteAsync(route.Kind);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Audio route not applied");
        }
        await RefreshRoutesAsync();
        ReportLocal();
        StateHasChanged();
    }

    private async Task SelectRouteAsync(AudioRoute route)
    {
        _chosenRouteId = route.Id;
        _chosenRouteKind = route.Kind;
        await SetRouteAsync(route);
    }

    private async Task OpenAudioMenuAsync(MouseEventArgs e)
    {
        if (AudioRoutes.IsSupported)
        {
            await RefreshRoutesAsync();
            // Only earpiece and speaker: one tap switches between them.
            if (_routes.Count == 2 && _routes.All(r => r.Kind is AudioRouteKind.Earpiece or AudioRouteKind.Speaker))
            {
                var other = _routes.FirstOrDefault(r => !r.IsActive) ?? _routes[0];
                await SelectRouteAsync(other);
                return;
            }
        }
        else
        {
            await LoadDeviceMenuAsync();
        }
        if (_audioMenu is not null) await _audioMenu.OpenAsync(e.ClientX, e.ClientY);
    }

    private async Task LoadDeviceMenuAsync()
    {
        _devices ??= new MediaDevicesJs(JS, Logger);
        var caps = await GetCapabilitiesAsync();
        var list = await _devices.ListAsync(false, false);
        _speakers = caps.SpeakerSelection ? list.AudioOutputs : [];
        _microphones = list.AudioInputs;
        StateHasChanged();
    }

    private async Task SelectSpeakerAsync(string? deviceId)
    {
        if (_engine is not null)
        {
            var result = await _engine.SetSpeakerAsync(deviceId);
            if (!result.Ok) Toasts.Show(L["calls.error.device"], ToastKind.Error);
        }
        await Settings.UpdateAsync(s => s.Devices.SpeakerId = deviceId);
    }

    private async Task SelectMicrophoneAsync(string? deviceId)
    {
        if (_engine is not null)
        {
            var result = await _engine.SetMicrophoneDeviceAsync(deviceId);
            if (!result.Ok)
            {
                // The engine went back to the previous microphone; keep that one as the saved choice.
                Toasts.Show(L["calls.error.device"], ToastKind.Error);
                return;
            }
        }
        await Settings.UpdateAsync(s => s.Devices.MicrophoneId = deviceId);
    }

    private static string DeviceLabel(MediaDeviceItem device, string fallback, int index) =>
        string.IsNullOrWhiteSpace(device.Label) ? $"{fallback} {index + 1}" : device.Label;

    // ---- sounds & timer ----

    /// <summary>
    /// True when Core shows the ringing OS notification for the incoming call that just arrived: a phone app in
    /// background with notifications allowed (Android also needs the permission and the app's notifications on).
    /// </summary>
    private async Task<bool> OsRingsForIncomingAsync()
    {
        if (!Platform.IsNativeApp || !Platform.IsMobile || Lifecycle.IsForeground || !Settings.Current.Notifications.CallsEnabled) return false;
        try
        {
            return await Permissions.CheckAsync(AppPermission.Notifications) == PermissionState.Granted;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Checking the notification permission failed");
            return false;
        }
    }

    private void UpdateSounds(bool showIncoming, CallSession? current)
    {
        var wanted = SoundMode.None;
        // While the OS notification rings in background, a second (in-app) ringtone would play over it at media
        // volume, ignoring silent mode. Back in the foreground the in-app ringtone takes over.
        if (showIncoming)
            wanted = Settings.Current.Notifications.CallsEnabled && (Lifecycle.IsForeground || !_osRinging) ? SoundMode.Ringtone : SoundMode.None;
        else if (current is { Phase: CallPhase.OutgoingRinging }) wanted = SoundMode.Ringback;
        if (wanted == _sound) return;
        var was = _sound;
        _sound = wanted;
        switch (wanted)
        {
            case SoundMode.Ringtone:
                Sounds.StartRingtone(Settings.Current.Notifications.Ringtone);
                break;
            case SoundMode.Ringback:
                Sounds.StartRingback();
                break;
            default:
                // When an end tone replaced the loop, PlayEndTone already set _sound to None and this returned above. Here
                // the loop is still playing (e.g. a ringtone that started during the previous call's "ended" screen).
                if (was != SoundMode.None) Sounds.Stop();
                break;
        }
    }

    private void PlayEndTone(CallSession ended)
    {
        _sound = SoundMode.None;
        if (ended.EndStatus == CallStatus.Busy) Sounds.PlayBusy();
        else if (ended.IsOutgoing || ended.ConnectedAt is not null) Sounds.PlayCallEnd();
        else Sounds.Stop();
    }

    private void UpdateTicker(CallSession? current)
    {
        var wanted = current is { Phase: CallPhase.Active or CallPhase.Reconnecting, ConnectedAt: not null };
        if (wanted && _ticker is null)
        {
            _ticker = new Timer(_ => RequestRender(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else if (!wanted && _ticker is not null)
        {
            _ticker.Dispose();
            _ticker = null;
        }
    }

    private void OnForegroundChanged(bool foreground)
    {
        // The ringtone depends on the foreground state (UpdateSounds).
        _ = InvokeAsync(async () =>
        {
            if (!IsDisposed && _seenIncomingId is not null) await SyncAsync();
        });
        if (!foreground) return;
        _ = InvokeAsync(async () =>
        {
            await Task.Delay(1600);
            if (IsDisposed || _engine is null || !_engineConnected || !_wantCamera) return;
            if (await _engine.CheckCameraAsync())
            {
                _cameraInterrupted = true;
                StateHasChanged();
            }
        });
    }

    // ---- expand / minimize ----

    private async Task ExpandAsync()
    {
        if (_expanded) return;
        _expanded = true;
        StateHasChanged();
        // Dialogs and menus underneath would otherwise steal Back (and could cover the call screen).
        await Nav.CloseAllOverlaysAsync();
        if (!_expanded || _overlay is not null) return;
        var handle = await Nav.OpenOverlayAsync(OnOverlayDismissed);
        if (_expanded && _overlay is null) _overlay = handle;
        else await handle.CloseAsync();
    }

    /// <summary>Back / Escape / a navigation closed the expanded screen: keep the call, show the bar.</summary>
    private Task OnOverlayDismissed()
    {
        _overlay = null;
        return InvokeAsync(async () =>
        {
            await PlayCollapseAsync();
            _expanded = false;
            StateHasChanged();
        });
    }

    private async Task MinimizeAsync()
    {
        await PlayCollapseAsync();
        _expanded = false;
        StateHasChanged();
        await CloseOverlayAsync();
    }

    /// <summary>The expanded screen shrinks into the call bar before it goes (instant with reduced motion).</summary>
    private async Task PlayCollapseAsync()
    {
        if (!_expanded || _minimizing || _gesture is not { } gesture) return;
        _minimizing = true;
        try
        {
            await gesture.InvokeAsync<bool>("collapse");
        }
        finally
        {
            _minimizing = false;
        }
    }

    private async Task BindGestureAsync()
    {
        var id = _expanded && Display is not null ? _screen.Id : null;
        if (id == _gestureFor) return;
        _gestureFor = id;
        var previous = _gesture;
        _gesture = null;
        if (previous is not null) await previous.DisposeAsync();
        if (id is null || IsDisposed) return;
        _gestureModule ??= new JsModule(JS, CallEngine.ModulePath);
        _self ??= DotNetObjectReference.Create(this);
        var handle = await _gestureModule.AttachAsync("attachMinimize", _screen, _backdrop, _self);
        if (_gestureFor == id && !IsDisposed) _gesture = handle;
        else await handle.DisposeAsync();
    }

    /// <summary>calls.js: the drag down crossed the point where letting go minimizes.</summary>
    [JSInvokable]
    public Task OnMinimizeArmed() => InvokeAsync(Haptics.Selection);

    /// <summary>calls.js: the drag down was released past the commit point and the screen already collapsed.</summary>
    [JSInvokable]
    public Task OnMinimizeGesture() => InvokeAsync(MinimizeAsync);

    private async Task CollapseAsync()
    {
        if (!_expanded && _overlay is null) return;
        _expanded = false;
        await CloseOverlayAsync();
    }

    private async Task CloseOverlayAsync()
    {
        if (_overlay is { } overlay)
        {
            _overlay = null;
            await overlay.CloseAsync();
        }
    }

    private async Task OnBarKeyDown(KeyboardEventArgs e)
    {
        if (e.Key is "Enter" or " ") await ExpandAsync();
    }

    // ---- leaving ----

    /// <summary>
    /// The call is gone (ended screen timed out, an incoming call stopped ringing): keep rendering what was shown for
    /// <see cref="LeaveMs"/> with a leaving class, so the screen fades out and the bar slides away instead of vanishing.
    /// </summary>
    private void StartLeaving(CallView view, bool expanded)
    {
        _leaving = new LeavingView(view, expanded);
        _ = EndLeavingAsync(++_leaveVersion);
    }

    private async Task EndLeavingAsync(int version)
    {
        await Task.Delay(LeaveMs);
        await InvokeAsync(() =>
        {
            if (version != _leaveVersion || IsDisposed || _leaving is null) return;
            _leaving = null;
            StateHasChanged();
        });
    }

    private void StopLeaving()
    {
        if (_leaving is null) return;
        _leaving = null;
        _leaveVersion++;
    }

    // ---- teardown ----

    protected override void Dispose(bool disposing)
    {
        Calls.Changed -= OnCallsChanged;
        Calls.CommandRequested -= OnCommand;
        Lifecycle.ForegroundChanged -= OnForegroundChanged;
        if (AudioRoutes.IsSupported) AudioRoutes.RoutesChanged -= OnRoutesChanged;
        CallUi.ExpandRequested -= OnExpandRequested;
        CallUi.SetCovering(false);
        _ticker?.Dispose();
        _ticker = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sound != SoundMode.None) Sounds.Stop();
        _sound = SoundMode.None;
        if (_overlay is { IsOpen: true } overlay)
        {
            _overlay = null;
            try
            {
                await overlay.CloseAsync();
            }
            catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
            {
            }
        }
        if (_gesture is not null) await _gesture.DisposeAsync();
        _gesture = null;
        if (_gestureModule is not null) await _gestureModule.DisposeAsync();
        if (_engine is not null) await _engine.DisposeAsync();
        if (_devices is not null) await _devices.DisposeAsync();
        _self?.Dispose();
        Dispose();
    }
}
