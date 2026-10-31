namespace SkyMessage.Client.Core.Platform;

/// <summary>
/// Where a phone plays call audio, as a rule the call screen applies (kept here, platform-free, so it is unit-tested):
/// after the microphone is published, and once when an audio call starts showing a picture.
/// </summary>
public static class CallRoutePolicy
{
    /// <summary>
    /// The route call audio belongs on: the one the user picked in this call (that device, else that kind), else a
    /// connected headset (the kind from settings first, then Bluetooth, then wired), else the route from settings when
    /// it is there, else the automatic one: the loudspeaker for a call that shows a picture
    /// (<paramref name="showsPicture"/>: a video call, my camera, someone's camera or shared screen), the earpiece
    /// otherwise. Null when none of them is available, so the route is left as it is.
    /// </summary>
    public static AudioRoute? Pick(IReadOnlyList<AudioRoute> routes, string? chosenId, AudioRouteKind? chosenKind,
        AudioRouteKind? preferred, bool showsPicture)
    {
        if (routes.Count == 0) return null;
        if (chosenId is not null && routes.FirstOrDefault(r => r.Id == chosenId) is { } device) return device;
        if (chosenKind is not null && routes.FirstOrDefault(r => r.Kind == chosenKind) is { } chosen) return chosen;

        var headset = (preferred is AudioRouteKind.Bluetooth or AudioRouteKind.Wired ? routes.FirstOrDefault(r => r.Kind == preferred) : null)
                      ?? routes.FirstOrDefault(r => r.Kind == AudioRouteKind.Bluetooth)
                      ?? routes.FirstOrDefault(r => r.Kind == AudioRouteKind.Wired);
        if (headset is not null) return headset;

        if (preferred is not null && routes.FirstOrDefault(r => r.Kind == preferred) is { } fromSettings) return fromSettings;
        var automatic = showsPicture ? AudioRouteKind.Speaker : AudioRouteKind.Earpiece;
        return routes.FirstOrDefault(r => r.Kind == automatic);
    }
}
