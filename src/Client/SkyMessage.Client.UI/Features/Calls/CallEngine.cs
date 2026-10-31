using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace SkyMessage.Client.UI.Features.Calls;

/// <summary>Local media as reported by calls.js.</summary>
public sealed record LocalMediaInfo
{
    public bool MicEnabled { get; init; }
    public bool CameraEnabled { get; init; }
    public bool ScreenSharing { get; init; }
    public bool Speaking { get; init; }
    /// <summary>3 excellent, 2 good, 1 poor, 0 lost, -1 unknown.</summary>
    public int Quality { get; init; } = -1;
    /// <summary>"user" (front) or "environment" (back).</summary>
    public string? Facing { get; init; }
    public bool MicPublished { get; init; }
}

/// <summary>A remote person in the room (all their devices merged by metadata.userId).</summary>
public sealed record RemoteMediaInfo
{
    /// <summary>User id ("N" format).</summary>
    public string UserId { get; init; } = "";
    public bool IsSpeaking { get; init; }
    public bool HasVideo { get; init; }
    public bool HasScreen { get; init; }
    public bool IsMuted { get; init; }
    public int Quality { get; init; } = -1;
    public bool IsActiveSpeaker { get; init; }
}

/// <summary>Everything calls.js needs to join a LiveKit room.</summary>
public sealed record CallConnectOptions
{
    public required string Url { get; init; }
    public required string Token { get; init; }
    public required string MyUserId { get; init; }
    public bool MicEnabled { get; init; } = true;
    public bool CameraEnabled { get; init; }
    public bool Mobile { get; init; }
    public bool LowData { get; init; }
    /// <summary>auto, low, medium, high.</summary>
    public string Quality { get; init; } = "auto";
    public CallAudioOptions Audio { get; init; } = new();
    public string? VideoDeviceId { get; init; }
    public string? SpeakerId { get; init; }
}

public sealed record CallAudioOptions
{
    public string? DeviceId { get; init; }
    public bool EchoCancellation { get; init; } = true;
    public bool NoiseSuppression { get; init; } = true;
    public bool AutoGainControl { get; init; } = true;
}

/// <summary>
/// .NET side of the call media engine (<c>js/calls.js</c> over livekit-client). Owned by <c>CallOverlay</c>; events
/// are raised on whatever thread the JS interop callback arrives on — handlers marshal to the renderer.
/// </summary>
public sealed class CallEngine(IJSRuntime js, ILogger logger) : IAsyncDisposable
{
    public const string ModulePath = "./_content/SkyMessage.Client.UI/js/calls.js";

    private Task<IJSObjectReference>? _module;
    private IJSObjectReference? _engine;
    private DotNetObjectReference<CallEngine>? _self;
    private Task<IJSObjectReference?>? _creating;
    private bool _disposed;

    /// <summary>connecting, connected, reconnecting, disconnected, failed, replaced (+ optional detail).</summary>
    public event Action<string, string?>? StateChanged;
    public event Action<LocalMediaInfo>? LocalChanged;
    public event Action<IReadOnlyList<RemoteMediaInfo>>? ParticipantsChanged;
    /// <summary>audioBlocked, deviceError, cameraInterrupted, devicesChanged, micPublished (+ optional detail).</summary>
    public event Action<string, string?>? Notice;

    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    /// <summary>Loads livekit-client in the background so connecting is faster (e.g. while a call rings).</summary>
    public async Task PreloadAsync()
    {
        try
        {
            var module = await Module;
            await module.InvokeAsync<bool>("preload");
        }
        catch (Exception ex) when (IsGone(ex))
        {
        }
    }

    public async Task ConnectAsync(CallConnectOptions options)
    {
        var engine = await EnsureEngineAsync();
        if (engine is null) return;
        try
        {
            await engine.InvokeVoidAsync("connect", options);
        }
        catch (Exception ex) when (IsGone(ex))
        {
            if (!_disposed) StateChanged?.Invoke("failed", "script");
        }
    }

    public Task DisconnectAsync() => CallVoidAsync("disconnect");

    /// <summary>Binds the element holding the video slots (null when the call screen is minimized).</summary>
    public async Task SetStageAsync(ElementReference? stage)
    {
        var engine = stage is null ? _engine : await EnsureEngineAsync();
        if (engine is null || _disposed) return;
        try
        {
            await engine.InvokeVoidAsync("setStage", stage);
        }
        catch (Exception ex) when (IsGone(ex))
        {
        }
    }

    public Task<MediaResult> SetMicrophoneAsync(bool enabled) => CallResultAsync("setMicEnabled", enabled);

    public Task<MediaResult> SetCameraAsync(bool enabled) => CallResultAsync("setCameraEnabled", enabled);

    public Task<MediaResult> SwitchCameraAsync() => CallResultAsync("switchCamera");

    public Task<MediaResult> SetScreenShareAsync(bool enabled) => CallResultAsync("setScreenShare", enabled);

    public Task<MediaResult> SetSpeakerAsync(string? deviceId) => CallResultAsync("setSpeaker", deviceId);

    public Task<MediaResult> SetMicrophoneDeviceAsync(string? deviceId) => CallResultAsync("setMicrophoneDevice", deviceId);

    public Task<MediaResult> RestartCameraAsync() => CallResultAsync("restartCamera");

    /// <summary>True when the camera should be on but stopped while the app was in background.</summary>
    public async Task<bool> CheckCameraAsync()
    {
        var engine = _engine;
        if (engine is null || _disposed) return false;
        try
        {
            return await engine.InvokeAsync<bool>("checkCamera", false);
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return false;
        }
    }

    /// <summary>Resumes remote audio after the browser blocked autoplay (call from a click handler).</summary>
    public async Task<bool> StartAudioAsync()
    {
        var engine = _engine;
        if (engine is null || _disposed) return true;
        try
        {
            return await engine.InvokeAsync<bool>("startAudio");
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return false;
        }
    }

    [JSInvokable]
    public void OnEngineState(string state, string? detail)
    {
        if (!_disposed) StateChanged?.Invoke(state, detail);
    }

    [JSInvokable]
    public void OnLocalMedia(LocalMediaInfo info)
    {
        if (!_disposed) LocalChanged?.Invoke(info);
    }

    [JSInvokable]
    public void OnParticipants(RemoteMediaInfo[] participants)
    {
        if (!_disposed) ParticipantsChanged?.Invoke(participants);
    }

    [JSInvokable]
    public void OnEngineNotice(string kind, string? detail)
    {
        if (!_disposed) Notice?.Invoke(kind, detail);
    }

    private async Task<IJSObjectReference?> EnsureEngineAsync()
    {
        if (_disposed) return null;
        if (_engine is not null) return _engine;
        return await (_creating ??= CreateAsync());
    }

    private async Task<IJSObjectReference?> CreateAsync()
    {
        try
        {
            var module = await Module;
            _self ??= DotNetObjectReference.Create(this);
            _engine = await module.InvokeAsync<IJSObjectReference>("createEngine", _self);
            return _engine;
        }
        catch (Exception ex) when (IsGone(ex))
        {
            _creating = null;
            return null;
        }
    }

    private async Task CallVoidAsync(string method, params object?[] args)
    {
        var engine = _engine;
        if (engine is null || _disposed) return;
        try
        {
            await engine.InvokeVoidAsync(method, args);
        }
        catch (Exception ex) when (IsGone(ex))
        {
        }
    }

    private async Task<MediaResult> CallResultAsync(string method, params object?[] args)
    {
        var engine = await EnsureEngineAsync();
        if (engine is null) return MediaResult.Failed("unavailable");
        try
        {
            return await engine.InvokeAsync<MediaResult?>(method, args) ?? MediaResult.Failed("failed");
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return MediaResult.Failed("failed");
        }
    }

    private bool IsGone(Exception ex)
    {
        if (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException or OperationCanceledException) return true;
        if (ex is JSException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Call media script failed");
            return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        StateChanged = null;
        LocalChanged = null;
        ParticipantsChanged = null;
        Notice = null;
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            try
            {
                await engine.InvokeVoidAsync("dispose");
                await engine.DisposeAsync();
            }
            catch (Exception ex) when (IsGone(ex))
            {
            }
        }
        if (_module is not null)
        {
            try
            {
                var module = await _module;
                await module.DisposeAsync();
            }
            catch (Exception ex) when (IsGone(ex))
            {
            }
        }
        _self?.Dispose();
    }
}
