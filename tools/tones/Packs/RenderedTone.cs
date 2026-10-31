namespace SkyMessage.Tones.Packs;

/// <summary>
/// One sound of a pack as its renderer made it: 44.1 kHz mono samples (peak-normalized by the renderer and faded at
/// both ends), whether the file is exactly one loop cycle, and the strike times in seconds from the start of the file
/// (or of the cycle), which the click check uses to tell a mallet's attack noise from a defect.
/// </summary>
internal sealed record RenderedTone(string Key, double[] Samples, bool Loop, IReadOnlyList<double> Onsets);

/// <summary>The keys every pack renders, in catalog order (they match the app's tone catalog).</summary>
internal static class ToneKeys
{
    public static readonly string[] MessageTones = ["chime", "pop", "glass"];
    public static readonly string[] Ringtones = ["classic", "soft", "bright"];
    public static readonly string[] CallProgress = ["ringback", "busy", "end"];
    public static readonly string[] All = [.. MessageTones, .. Ringtones, .. CallProgress];
}
