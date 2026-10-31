using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Services;

/// <summary>iOS/macOS expose no API for the device's own phone number; the user types it in.</summary>
public sealed class UnsupportedPhoneNumberService : IPhoneNumberService
{
    public bool IsSupported => false;

    public Task<PhoneDetectionResult> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PhoneDetectionResult.Of(PhoneDetectionStatus.Unsupported));
}

/// <summary>
/// Apple platforms do not allow a persistent background connection; until push lands the app receives
/// messages and calls only while it is open (the UI says so).
/// </summary>
public sealed class UnsupportedBackgroundConnectionService : IBackgroundConnectionService
{
    public bool IsSupported => false;

    public bool IsEnabled => false;

    public Task SetEnabledAsync(bool enabled) => Task.CompletedTask;

    public Task<bool> RequestBatteryExemptionAsync() => Task.FromResult(false);
}

/// <summary>Desktop: output selection belongs to the OS (no earpiece/speaker switching).</summary>
public sealed class UnsupportedAudioRouteService : IAudioRouteService
{
    public bool IsSupported => false;

    public Task<IReadOnlyList<AudioRoute>> GetRoutesAsync() => Task.FromResult<IReadOnlyList<AudioRoute>>([]);

    public Task SetRouteAsync(AudioRouteKind kind) => Task.CompletedTask;

    public Task SetRouteAsync(string routeId) => Task.CompletedTask;

    public event Action? RoutesChanged
    {
        add { }
        remove { }
    }
}

/// <summary>
/// Apple: the call's audio session (play-and-record, not mixable) already interrupts other audio, and there is no
/// focus to ask for around recordings; the "pause music" setting is hidden.
/// </summary>
public sealed class UnsupportedAudioFocusService : IAudioFocusService
{
    public bool IsSupported => false;

    public void SetFocus(AudioFocusKind kind)
    {
    }
}

public sealed class UnsupportedHapticsService : IHapticsService
{
    public bool IsSupported => false;

    public void Perform(HapticKind kind)
    {
    }
}
