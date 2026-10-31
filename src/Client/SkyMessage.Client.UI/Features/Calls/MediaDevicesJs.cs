using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace SkyMessage.Client.UI.Features.Calls;

/// <summary>A capture or output device as reported by the browser engine (labels need permission).</summary>
public sealed record MediaDeviceItem(string Id, string Label, string GroupId);

/// <summary>
/// Result of <see cref="MediaDevicesJs.ListAsync"/>. Permission values: granted, denied, prompt, none (no such
/// device), busy, insecure, unsupported, unknown.
/// </summary>
public sealed record MediaDeviceList
{
    public IReadOnlyList<MediaDeviceItem> AudioInputs { get; init; } = [];
    public IReadOnlyList<MediaDeviceItem> VideoInputs { get; init; } = [];
    public IReadOnlyList<MediaDeviceItem> AudioOutputs { get; init; } = [];
    public string AudioPermission { get; init; } = "unknown";
    public string VideoPermission { get; init; } = "unknown";
    public bool Supported { get; init; } = true;

    public static MediaDeviceList Empty { get; } = new() { Supported = false };
}

/// <summary>What the WebView/browser can do with media.</summary>
public sealed record MediaCapabilities
{
    public bool Media { get; init; }
    public bool Enumerate { get; init; }
    /// <summary>HTMLMediaElement.setSinkId (output device selection) — desktop browsers only.</summary>
    public bool SpeakerSelection { get; init; }
    public bool ScreenShare { get; init; }
    public bool SecureContext { get; init; } = true;
    public bool Recorder { get; init; }
}

/// <summary>Outcome of a JS media operation; <see cref="Error"/> is a short code (denied, none, busy, insecure…).</summary>
public sealed record MediaResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? DeviceId { get; init; }
    public string? Facing { get; init; }

    public static MediaResult Failed(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// Wrapper over <c>js/devices.js</c> (device lists, microphone test, camera preview, speaker test tone). Not a DI
/// service: components create one with their <see cref="IJSRuntime"/> and dispose it. Calls never throw on a
/// disconnected page; they return empty/failed results instead.
/// </summary>
public sealed class MediaDevicesJs(IJSRuntime js, ILogger logger) : IAsyncDisposable
{
    public const string ModulePath = "./_content/SkyMessage.Client.UI/js/devices.js";

    private Task<IJSObjectReference>? _module;
    private bool _disposed;

    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    public async Task<MediaCapabilities> GetCapabilitiesAsync()
    {
        try
        {
            var module = await Module;
            return await module.InvokeAsync<MediaCapabilities?>("capabilities") ?? new MediaCapabilities();
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return new MediaCapabilities();
        }
    }

    /// <summary>Lists devices; asks for access first when labels are hidden and the matching flag is set.</summary>
    public async Task<MediaDeviceList> ListAsync(bool requestAudio, bool requestVideo)
    {
        try
        {
            var module = await Module;
            return await module.InvokeAsync<MediaDeviceList?>("listDevices", requestAudio, requestVideo) ?? MediaDeviceList.Empty;
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return MediaDeviceList.Empty;
        }
    }

    /// <summary>Calls <paramref name="method"/> on <paramref name="target"/> when devices change.</summary>
    public async Task<IJSObjectReference?> WatchAsync<T>(DotNetObjectReference<T> target, string method) where T : class
    {
        try
        {
            var module = await Module;
            return await module.InvokeAsync<IJSObjectReference>("watchDevices", target, method);
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>A microphone test bound to a meter element (its <c>--level</c> CSS variable goes 0…1).</summary>
    public async Task<IJSObjectReference?> CreateMicTestAsync<T>(DotNetObjectReference<T> target, ElementReference meter) where T : class
    {
        try
        {
            var module = await Module;
            return await module.InvokeAsync<IJSObjectReference>("createMicTest", target, meter);
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return null;
        }
    }

    public async Task<IJSObjectReference?> CreateCameraPreviewAsync(ElementReference video)
    {
        try
        {
            var module = await Module;
            return await module.InvokeAsync<IJSObjectReference>("createCameraPreview", video);
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>Plays our short test chime through <paramref name="sinkId"/> (null = default output).</summary>
    public async Task<MediaResult> PlayTestToneAsync(string? sinkId)
    {
        try
        {
            var module = await Module;
            return await module.InvokeAsync<MediaResult?>("playTestTone", sinkId) ?? MediaResult.Failed("failed");
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return MediaResult.Failed("unsupported");
        }
    }

    /// <summary>Text of a static file shipped with the app (relative to the page), null when unavailable.</summary>
    public async Task<string?> FetchTextAsync(string url)
    {
        try
        {
            var module = await Module;
            return await module.InvokeAsync<string?>("fetchText", url);
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>Invokes a method on a JS handle created by this module, swallowing "page gone" errors.</summary>
    public async Task<TResult?> CallAsync<TResult>(IJSObjectReference? handle, string method, params object?[] args)
    {
        if (handle is null || _disposed) return default;
        try
        {
            return await handle.InvokeAsync<TResult>(method, args);
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return default;
        }
    }

    public async Task CallVoidAsync(IJSObjectReference? handle, string method, params object?[] args)
    {
        if (handle is null) return;
        try
        {
            await handle.InvokeVoidAsync(method, args);
        }
        catch (Exception ex) when (IsGone(ex))
        {
        }
    }

    /// <summary>Disposes a handle (calls its JS dispose() first).</summary>
    public async Task ReleaseAsync(IJSObjectReference? handle)
    {
        if (handle is null) return;
        try
        {
            await handle.InvokeVoidAsync("dispose");
        }
        catch (Exception ex) when (IsGone(ex))
        {
        }
        try
        {
            await handle.DisposeAsync();
        }
        catch (Exception ex) when (IsGone(ex))
        {
        }
    }

    private bool IsGone(Exception ex)
    {
        if (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException or OperationCanceledException) return true;
        if (ex is JSException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Media devices script call failed");
            return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_module is null) return;
        try
        {
            var module = await _module;
            await module.DisposeAsync();
        }
        catch (Exception ex) when (IsGone(ex))
        {
        }
    }
}
