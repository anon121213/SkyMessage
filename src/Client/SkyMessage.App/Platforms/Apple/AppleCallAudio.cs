using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;
using UIKit;

namespace SkyMessage.App.Apple;

/// <summary>
/// Keeps a call alive on Apple platforms: the shared AVAudioSession in play-and-record / voice-chat mode
/// (UIBackgroundModes audio lets the call continue in background), screen kept awake during video. There is
/// no system call UI until CallKit/push (v2), so <see cref="CommandRequested"/> is never raised.
/// </summary>
public sealed class AppleCallPlatformService(IServiceProvider services, ILogger<AppleCallPlatformService> logger) : ICallPlatformService
{
    public event Action<CallCommand>? CommandRequested
    {
        add { }
        remove { }
    }

    public Task<bool> OnCallStartedAsync(CallDto call, string title, bool video) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                AppleAudioSession.ConfigureForCall(video);
                UIApplication.SharedApplication.IdleTimerDisabled = video;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Configuring the audio session for a call failed");
                return false;
            }
        });

    public Task<bool> OnCallMediaChangedAsync(bool cameraOn) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                AppleAudioSession.ConfigureForCall(cameraOn);
                UIApplication.SharedApplication.IdleTimerDisabled = cameraOn;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Updating the call audio session failed");
                return false;
            }
        });

    public Task OnCallEndedAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                UIApplication.SharedApplication.IdleTimerDisabled = false;
#if IOS
                (services.GetService(typeof(IAudioRouteService)) as AppleAudioRouteService)?.Reset();
#else
                _ = services;
#endif
                AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Releasing the call audio session failed");
            }
        });
}

internal static class AppleAudioSession
{
    public static void ConfigureForCall(bool video)
    {
        var session = AVAudioSession.SharedInstance();
        var options = AVAudioSessionCategoryOptions.AllowBluetooth | AVAudioSessionCategoryOptions.AllowBluetoothA2DP;
        if (video) options |= AVAudioSessionCategoryOptions.DefaultToSpeaker;
        session.SetCategory(AVAudioSessionCategory.PlayAndRecord, video ? AVAudioSessionMode.VideoChat : AVAudioSessionMode.VoiceChat, AVAudioSessionRouteSharingPolicy.Default, options, out _);
        session.SetActive(true, out _);
    }
}

#if IOS
/// <summary>
/// iPhone call output through AVAudioSession: earpiece (receiver), speaker (output override), Bluetooth and
/// wired headsets (preferred input). The preferred route is re-applied on every route change because the
/// WebView's media engine may reconfigure the session when capture restarts.
/// </summary>
public sealed class AppleAudioRouteService : IAudioRouteService
{
    private const string Receiver = "Receiver";
    private const string Speaker = "Speaker";
    private const string BuiltInMic = "MicrophoneBuiltIn";
    private static readonly string[] BluetoothPorts = ["BluetoothHFP", "BluetoothA2DPOutput", "BluetoothLE"];
    private static readonly string[] WiredPorts = ["Headphones", "MicrophoneWired", "USBAudio", "LineOut"];

    private readonly NativeStrings _strings;
    private readonly ILogger<AppleAudioRouteService> _logger;
    private readonly object _gate = new();
    private readonly NSObject _observer;
    private Action? _handlers;
    private AudioRouteKind? _preferredKind;
    private string? _preferredId;
    private bool _applying;

    public AppleAudioRouteService(NativeStrings strings, ILogger<AppleAudioRouteService> logger)
    {
        _strings = strings;
        _logger = logger;
        _observer = AVAudioSession.Notifications.ObserveRouteChange((_, args) => OnRouteChanged(args.Reason));
    }

    public bool IsSupported => UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;

    public event Action? RoutesChanged
    {
        add { lock (_gate) _handlers += value; }
        remove { lock (_gate) _handlers -= value; }
    }

    public Task<IReadOnlyList<AudioRoute>> GetRoutesAsync()
    {
        try
        {
            var session = AVAudioSession.SharedInstance();
            var current = session.CurrentRoute.Outputs.FirstOrDefault()?.PortType?.ToString() ?? "";
            var currentUid = session.CurrentRoute.Outputs.FirstOrDefault()?.UID;
            var routes = new List<AudioRoute>
            {
                new(AudioRouteKind.Earpiece, _strings["platform.audio.earpiece"], current == Receiver, "earpiece"),
                new(AudioRouteKind.Speaker, _strings["platform.audio.speaker"], current == Speaker, "speaker"),
            };
            foreach (var input in session.AvailableInputs ?? [])
            {
                var type = input.PortType?.ToString() ?? "";
                if (BluetoothPorts.Contains(type))
                    routes.Add(new AudioRoute(AudioRouteKind.Bluetooth, Name(input.PortName, "platform.audio.bluetooth"),
                        BluetoothPorts.Contains(current) && (currentUid is null || currentUid == input.UID || current != "BluetoothHFP"), input.UID));
                else if (WiredPorts.Contains(type))
                    routes.Add(new AudioRoute(AudioRouteKind.Wired, Name(input.PortName, "platform.audio.wired"), WiredPorts.Contains(current), input.UID));
            }
            return Task.FromResult<IReadOnlyList<AudioRoute>>(routes);
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
        return MainThread.InvokeOnMainThreadAsync(Apply);
    }

    public Task SetRouteAsync(string routeId)
    {
        _preferredId = routeId;
        _preferredKind = routeId switch
        {
            "earpiece" => AudioRouteKind.Earpiece,
            "speaker" => AudioRouteKind.Speaker,
            _ => KindOfInput(routeId),
        };
        return MainThread.InvokeOnMainThreadAsync(Apply);
    }

    /// <summary>Call ended: forget the requested route and give the output back to the system.</summary>
    internal void Reset()
    {
        if (_preferredKind is null && _preferredId is null) return;
        _preferredKind = null;
        _preferredId = null;
        _applying = true;
        try
        {
            var session = AVAudioSession.SharedInstance();
            session.OverrideOutputAudioPort(AVAudioSessionPortOverride.None, out _);
            session.SetPreferredInput(null, out _);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Releasing the audio route failed");
        }
        finally
        {
            _applying = false;
        }
    }

    private void Apply()
    {
        if (_preferredKind is not { } kind) return;
        _applying = true;
        try
        {
            var session = AVAudioSession.SharedInstance();
            var inputs = session.AvailableInputs ?? [];
            switch (kind)
            {
                case AudioRouteKind.Speaker:
                    session.OverrideOutputAudioPort(AVAudioSessionPortOverride.Speaker, out _);
                    break;
                case AudioRouteKind.Earpiece:
                    session.OverrideOutputAudioPort(AVAudioSessionPortOverride.None, out _);
                    if (inputs.FirstOrDefault(i => i.PortType?.ToString() == BuiltInMic) is { } mic) session.SetPreferredInput(mic, out _);
                    break;
                case AudioRouteKind.Bluetooth:
                case AudioRouteKind.Wired:
                    session.OverrideOutputAudioPort(AVAudioSessionPortOverride.None, out _);
                    var ports = kind == AudioRouteKind.Bluetooth ? BluetoothPorts : WiredPorts;
                    var input = inputs.FirstOrDefault(i => _preferredId is not null && i.UID == _preferredId)
                                ?? inputs.FirstOrDefault(i => ports.Contains(i.PortType?.ToString() ?? ""));
                    if (input is not null) session.SetPreferredInput(input, out _);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Applying the audio route failed");
        }
        finally
        {
            _applying = false;
        }
    }

    private AudioRouteKind? KindOfInput(string uid)
    {
        var input = AVAudioSession.SharedInstance().AvailableInputs?.FirstOrDefault(i => i.UID == uid);
        var type = input?.PortType?.ToString() ?? "";
        if (BluetoothPorts.Contains(type)) return AudioRouteKind.Bluetooth;
        if (WiredPorts.Contains(type)) return AudioRouteKind.Wired;
        return null;
    }

    private void OnRouteChanged(AVAudioSessionRouteChangeReason reason)
    {
        if (reason == AVAudioSessionRouteChangeReason.NewDeviceAvailable && _preferredKind is not null)
        {
            // A headset connected during a call takes the audio over, as in a regular phone call.
            var output = AVAudioSession.SharedInstance().CurrentRoute.Outputs.FirstOrDefault();
            var type = output?.PortType?.ToString() ?? "";
            if (BluetoothPorts.Contains(type) || WiredPorts.Contains(type))
            {
                _preferredKind = BluetoothPorts.Contains(type) ? AudioRouteKind.Bluetooth : AudioRouteKind.Wired;
                _preferredId = null;
            }
        }
        if (!_applying) MainThread.BeginInvokeOnMainThread(Apply);
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

    private string Name(string? portName, string fallbackKey) => string.IsNullOrWhiteSpace(portName) ? _strings[fallbackKey] : portName.Trim();
}

public sealed class AppleHapticsService : IHapticsService
{
    public bool IsSupported => UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;

    public void Perform(HapticKind kind) => MainThread.BeginInvokeOnMainThread(() =>
    {
        try
        {
#pragma warning disable CA1422 // The generator classes still work on iOS 17.5+; GetFeedbackGenerator needs a view.
            switch (kind)
            {
                case HapticKind.Selection:
                    new UISelectionFeedbackGenerator().SelectionChanged();
                    break;
                case HapticKind.Success:
                    new UINotificationFeedbackGenerator().NotificationOccurred(UINotificationFeedbackType.Success);
                    break;
                case HapticKind.Warning:
                    new UINotificationFeedbackGenerator().NotificationOccurred(UINotificationFeedbackType.Warning);
                    break;
                default:
                    var style = kind switch
                    {
                        HapticKind.Heavy => UIImpactFeedbackStyle.Heavy,
                        HapticKind.Medium => UIImpactFeedbackStyle.Medium,
                        _ => UIImpactFeedbackStyle.Light,
                    };
                    new UIImpactFeedbackGenerator(style).ImpactOccurred();
                    break;
            }
#pragma warning restore CA1422
        }
        catch (Exception)
        {
            // Haptics are decoration.
        }
    });
}
#endif

/// <summary>Bar style follows the in-app theme: the window's interface style drives status bar and system controls.</summary>
public sealed class AppleSystemUiService(ILogger<AppleSystemUiService> logger) : ISystemUiService
{
    public Task SetSystemBarsStyleAsync(bool darkContent, string backgroundHex)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var background = ParseColor(backgroundHex);
                foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
                {
                    if (scene is not UIWindowScene windowScene) continue;
                    foreach (var window in windowScene.Windows)
                    {
                        window.OverrideUserInterfaceStyle = darkContent ? UIUserInterfaceStyle.Light : UIUserInterfaceStyle.Dark;
                        if (background is not null) window.BackgroundColor = background;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "System bar styling failed");
            }
        });
        return Task.CompletedTask;
    }

    private static UIColor? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var text = hex.TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return null;
        return UIColor.FromRGB((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }
}
