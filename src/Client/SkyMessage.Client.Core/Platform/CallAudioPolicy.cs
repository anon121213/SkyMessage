namespace SkyMessage.Client.Core.Platform;

/// <summary>
/// Rules for call audio on phones that the native hosts apply (kept here, platform-free, so they are unit-tested).
/// </summary>
public static class CallAudioPolicy
{
    /// <summary>
    /// The proximity screen-off lock is for a call held to the ear: voice on the earpiece and my camera off. On the
    /// loudspeaker, a Bluetooth or wired headset, or with my camera on, the phone is held in front of the face (or lies
    /// on the table), so the screen must stay on. An unknown route (no earpiece, nothing reported) keeps it on as well.
    /// </summary>
    public static bool HoldsProximityLock(AudioRouteKind? route, bool cameraOn) => route == AudioRouteKind.Earpiece && !cameraOn;
}
