namespace SkyMessage.Client.Core.Platform;

/// <summary>
/// Rules for call audio on phones that the native hosts apply (kept here, platform-free, so they are unit-tested).
/// </summary>
public static class CallAudioPolicy
{
    /// <summary>
    /// The proximity screen-off lock is for a call held to the ear: voice on the earpiece and my camera off. On the
    /// loudspeaker, a Bluetooth or wired headset, or with my camera on, the phone is held in front of the face (or lies
    /// on the table), so the screen must stay on. An unknown route (no earpiece, nothing reported) keeps it on as well,
    /// and so does a call whose voice is not on the phone's call path (<paramref name="inCommunication"/> false: the
    /// WebView opened no microphone yet, e.g. a call started muted, so it plays the voice like music, on the
    /// loudspeaker), whatever route the system would give a call.
    /// </summary>
    public static bool HoldsProximityLock(AudioRouteKind? route, bool cameraOn, bool inCommunication) =>
        inCommunication && route == AudioRouteKind.Earpiece && !cameraOn;
}
