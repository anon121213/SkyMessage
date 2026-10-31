using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;

namespace SkyMessage.Client.Core.Services;

/// <summary>When other apps' audio pauses for us (<see cref="IAudioFocusCoordinator"/>), as a pure decision.</summary>
internal static class AudioFocusPolicy
{
    /// <summary>
    /// A call of mine wins (set up, in progress, or its end screen with the end tone: other apps play on once it is
    /// gone), then a ringing incoming call that actually rings (incoming calls enabled), then a recording. Nothing when
    /// the setting is off.
    /// </summary>
    public static AudioFocusKind Decide(bool enabled, CallSession? current, CallSession? incoming, bool callsRing, bool recording)
    {
        if (!enabled) return AudioFocusKind.None;
        if (current is not null) return AudioFocusKind.Call;
        if (incoming is not null && callsRing) return AudioFocusKind.Ringtone;
        return recording ? AudioFocusKind.Recording : AudioFocusKind.None;
    }
}
