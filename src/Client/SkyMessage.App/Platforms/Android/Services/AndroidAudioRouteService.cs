using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>
/// Call audio output. Android 12+: communication devices (route ids are <see cref="AudioDeviceInfo.Id"/>);
/// older versions: speakerphone / Bluetooth SCO toggles. The WebView's WebRTC stack may re-pick a device when
/// capture restarts, so the last requested route is re-applied whenever the device list changes (and the call
/// UI calls SetRoute again after publishing its microphone).
/// </summary>
public sealed class AndroidAudioRouteService : IAudioRouteService
{
    private readonly NativeStrings _strings;
    private readonly ILogger<AndroidAudioRouteService> _logger;
    private readonly AudioManager? _audio;
    private readonly object _gate = new();
    private Action? _handlers;
    private AudioRouteKind? _preferredKind;
    private string? _preferredId;

    public AndroidAudioRouteService(NativeStrings strings, ILogger<AndroidAudioRouteService> logger)
    {
        _strings = strings;
        _logger = logger;
        _audio = Android.App.Application.Context.GetSystemService(Context.AudioService) as AudioManager;
        try
        {
            var handler = new Handler(Looper.MainLooper!);
            _audio?.RegisterAudioDeviceCallback(new DeviceCallback(this), handler);
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var executor = Java.Util.Concurrent.Executors.NewSingleThreadExecutor()!;
                _audio?.AddOnCommunicationDeviceChangedListener(executor, new CommunicationListener(this));
                _audio?.AddOnModeChangedListener(executor, new ModeListener(this));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio device callbacks unavailable");
        }
    }

    public bool IsSupported => _audio is not null;

    public event Action? RoutesChanged
    {
        add { lock (_gate) _handlers += value; }
        remove { lock (_gate) _handlers -= value; }
    }

    /// <summary>
    /// Android 12+: the audio mode changed (the WebView switches to communication mode when it opens the microphone
    /// and back when it closes it). Older versions report mode changes to nobody. Raised on a background thread.
    /// </summary>
    internal event Action? ModeChanged;

    public Task<IReadOnlyList<AudioRoute>> GetRoutesAsync()
    {
        try
        {
            return Task.FromResult(OperatingSystem.IsAndroidVersionAtLeast(31) ? ModernRoutes() : LegacyRoutes());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Listing audio routes failed");
            return Task.FromResult<IReadOnlyList<AudioRoute>>([]);
        }
    }

    public Task SetRouteAsync(AudioRouteKind kind)
    {
        _preferredKind = kind;
        _preferredId = null;
        Apply();
        // Android 11 and older report speakerphone/SCO switches to nobody: tell the call (proximity lock) and the UI.
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task SetRouteAsync(string routeId)
    {
        _preferredId = routeId;
        _preferredKind = KindOfRouteId(routeId);
        Apply();
        RaiseChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Where call audio plays right now (null when unknown). Android 12+: the communication device, which defaults to
    /// the earpiece on phones; older versions: the speakerphone/SCO switches, else a wired headset, else the earpiece.
    /// </summary>
    internal AudioRouteKind? CurrentKind
    {
        get
        {
            try
            {
                if (_audio is null) return null;
                if (OperatingSystem.IsAndroidVersionAtLeast(31)) return _audio.CommunicationDevice is { } device ? KindOf(device.Type) : null;
                return LegacyRoutes().FirstOrDefault(r => r.IsActive)?.Kind;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading the audio route failed");
                return null;
            }
        }
    }

    /// <summary>
    /// Whether call audio is on the phone's communication path (MODE_IN_COMMUNICATION, set by the WebView while it
    /// captures the microphone). Only then can it play on the earpiece; otherwise the WebView plays it like media.
    /// </summary>
    internal bool IsInCommunication
    {
        get
        {
            try
            {
                return _audio?.Mode == Mode.InCommunication;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading the audio mode failed");
                return false;
            }
        }
    }

    /// <summary>
    /// Call ended: forget the requested route and hand routing back to the system, so device changes after the
    /// call (plugging in headphones) are not "corrected" and media playback is not stuck on the earpiece.
    /// </summary>
    internal void Reset()
    {
        var hadPreference = _preferredKind is not null || _preferredId is not null;
        _preferredKind = null;
        _preferredId = null;
        if (!hadPreference || _audio is null) return;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                _audio.ClearCommunicationDevice();
            }
            else
            {
#pragma warning disable CA1422 // Pre-Android 12 routing API.
                StopSco();
                _audio.SpeakerphoneOn = false;
#pragma warning restore CA1422
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Releasing the audio route failed");
        }
        RaiseChanged();
    }

    private void Apply()
    {
        if (_audio is null) return;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) ApplyModern();
            else ApplyLegacy();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Applying the audio route failed");
        }
    }

    private void ApplyModern()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31) || _audio is null) return;
        var devices = _audio.AvailableCommunicationDevices;
        AudioDeviceInfo? target = null;
        if (_preferredId is not null) target = devices.FirstOrDefault(d => d.Id.ToString() == _preferredId);
        if (target is null && _preferredKind is { } kind) target = devices.FirstOrDefault(d => KindOf(d.Type) == kind);
        if (target is not null) _audio.SetCommunicationDevice(target);
        else if (_preferredKind is not null) _audio.ClearCommunicationDevice();
    }

    private void ApplyLegacy()
    {
        if (_audio is null || _preferredKind is not { } kind) return;
#pragma warning disable CA1422 // Pre-Android 12 routing API.
        switch (kind)
        {
            case AudioRouteKind.Speaker:
                StopSco();
                _audio.SpeakerphoneOn = true;
                break;
            case AudioRouteKind.Bluetooth:
                _audio.SpeakerphoneOn = false;
                _audio.StartBluetoothSco();
                _audio.BluetoothScoOn = true;
                break;
            default:
                StopSco();
                _audio.SpeakerphoneOn = false;
                break;
        }
#pragma warning restore CA1422
    }

    private void StopSco()
    {
#pragma warning disable CA1422
        if (_audio is null) return;
        if (_audio.BluetoothScoOn)
        {
            _audio.BluetoothScoOn = false;
            _audio.StopBluetoothSco();
        }
#pragma warning restore CA1422
    }

    private IReadOnlyList<AudioRoute> ModernRoutes()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31) || _audio is null) return [];
        var current = _audio.CommunicationDevice;
        var routes = new List<AudioRoute>();
        var seenKinds = new HashSet<AudioRouteKind>();
        foreach (var device in _audio.AvailableCommunicationDevices)
        {
            if (KindOf(device.Type) is not { } kind) continue;
            // One entry for the built-in earpiece/speaker; every Bluetooth/wired device is listed separately.
            if (kind is AudioRouteKind.Earpiece or AudioRouteKind.Speaker && !seenKinds.Add(kind)) continue;
            routes.Add(new AudioRoute(kind, NameOf(kind, device.ProductName), current?.Id == device.Id, device.Id.ToString()));
        }
        return routes;
    }

    private IReadOnlyList<AudioRoute> LegacyRoutes()
    {
        if (_audio is null) return [];
        var outputs = _audio.GetDevices(GetDevicesTargets.Outputs) ?? [];
        var hasEarpiece = Android.App.Application.Context.PackageManager?.HasSystemFeature(PackageManager.FeatureTelephony) == true;
        var wired = outputs.FirstOrDefault(d => KindOf(d.Type) == AudioRouteKind.Wired);
        var bluetooth = outputs.FirstOrDefault(d => d.Type == AudioDeviceType.BluetoothSco);
#pragma warning disable CA1422
        var speakerOn = _audio.SpeakerphoneOn;
        var scoOn = _audio.BluetoothScoOn;
#pragma warning restore CA1422
        var routes = new List<AudioRoute>();
        if (hasEarpiece && wired is null) routes.Add(new AudioRoute(AudioRouteKind.Earpiece, NameOf(AudioRouteKind.Earpiece, null), !speakerOn && !scoOn, "earpiece"));
        routes.Add(new AudioRoute(AudioRouteKind.Speaker, NameOf(AudioRouteKind.Speaker, null), speakerOn, "speaker"));
        if (wired is not null) routes.Add(new AudioRoute(AudioRouteKind.Wired, NameOf(AudioRouteKind.Wired, wired.ProductName), !speakerOn && !scoOn, "wired"));
        if (bluetooth is not null) routes.Add(new AudioRoute(AudioRouteKind.Bluetooth, NameOf(AudioRouteKind.Bluetooth, bluetooth.ProductName), scoOn, "bluetooth"));
        return routes;
    }

    private AudioRouteKind? KindOfRouteId(string routeId)
    {
        switch (routeId)
        {
            case "earpiece": return AudioRouteKind.Earpiece;
            case "speaker": return AudioRouteKind.Speaker;
            case "wired": return AudioRouteKind.Wired;
            case "bluetooth": return AudioRouteKind.Bluetooth;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(31) && _audio is not null)
            return _audio.AvailableCommunicationDevices.FirstOrDefault(d => d.Id.ToString() == routeId) is { } device ? KindOf(device.Type) : null;
        return null;
    }

#pragma warning disable CA1416 // Comparing enum values is safe on every API level (newer types simply never occur).
    private static AudioRouteKind? KindOf(AudioDeviceType type) => type switch
    {
        AudioDeviceType.BuiltinEarpiece => AudioRouteKind.Earpiece,
        AudioDeviceType.BuiltinSpeaker or AudioDeviceType.BuiltinSpeakerSafe => AudioRouteKind.Speaker,
        AudioDeviceType.BluetoothSco or AudioDeviceType.BleHeadset or AudioDeviceType.BleSpeaker or AudioDeviceType.HearingAid
            => AudioRouteKind.Bluetooth,
        AudioDeviceType.WiredHeadset or AudioDeviceType.WiredHeadphones or AudioDeviceType.UsbHeadset or AudioDeviceType.UsbDevice
            => AudioRouteKind.Wired,
        _ => null,
    };
#pragma warning restore CA1416

    private string NameOf(AudioRouteKind kind, string? productName)
    {
        if (kind is AudioRouteKind.Bluetooth or AudioRouteKind.Wired && !string.IsNullOrWhiteSpace(productName)
            && !string.Equals(productName, Build.Model, StringComparison.OrdinalIgnoreCase))
            return productName.Trim();
        return kind switch
        {
            AudioRouteKind.Earpiece => _strings["platform.audio.earpiece"],
            AudioRouteKind.Speaker => _strings["platform.audio.speaker"],
            AudioRouteKind.Bluetooth => _strings["platform.audio.bluetooth"],
            _ => _strings["platform.audio.wired"],
        };
    }

    private void OnDevicesChanged()
    {
        Apply();
        RaiseChanged();
    }

    /// <summary>A headset connected during a call takes the audio over, as in a regular phone call.</summary>
    private void OnDevicesAdded(AudioDeviceInfo[]? added)
    {
        if ((_preferredKind is not null || _preferredId is not null) && added is not null)
        {
            var headset = added.FirstOrDefault(d => d.IsSink && KindOf(d.Type) is AudioRouteKind.Bluetooth or AudioRouteKind.Wired);
            if (headset is not null)
            {
                _preferredKind = KindOf(headset.Type);
                _preferredId = OperatingSystem.IsAndroidVersionAtLeast(31) ? headset.Id.ToString() : null;
            }
        }
        OnDevicesChanged();
    }

    private void RaiseChanged()
    {
        Action? handlers;
        lock (_gate) handlers = _handlers;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio route handler failed");
            }
        }
    }

    private sealed class DeviceCallback(AndroidAudioRouteService owner) : AudioDeviceCallback
    {
        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) => owner.OnDevicesAdded(addedDevices);

        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) => owner.OnDevicesChanged();
    }

    private sealed class CommunicationListener(AndroidAudioRouteService owner) : Java.Lang.Object, AudioManager.IOnCommunicationDeviceChangedListener
    {
        public void OnCommunicationDeviceChanged(AudioDeviceInfo? device) => owner.RaiseChanged();
    }

    private sealed class ModeListener(AndroidAudioRouteService owner) : Java.Lang.Object, AudioManager.IOnModeChangedListener
    {
        public void OnModeChanged(int mode)
        {
            try
            {
                owner.ModeChanged?.Invoke();
            }
            catch (Exception ex)
            {
                owner._logger.LogDebug(ex, "Audio mode handler failed");
            }
        }
    }
}
