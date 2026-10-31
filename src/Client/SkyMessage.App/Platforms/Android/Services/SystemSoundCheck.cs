using Android.Media;

namespace SkyMessage.App.Droid;

/// <summary>
/// Whether Android's own media framework, which plays a notification channel's sound in SystemUI, can play an audio
/// file: an extractor opens it and finds an audio track that a decoder on this device handles. The WebView decodes
/// more than that on some versions (Ogg Opus or 24-bit and float WAV are not guaranteed on older ones), so the user's
/// own sound is checked here before it is accepted and before a channel rings with it.
/// </summary>
internal static class SystemSoundCheck
{
    private static HashSet<string>? _decoderTypes;

    /// <summary>Never throws: a file the framework cannot open is not playable.</summary>
    public static bool CanPlay(string path)
    {
        using var extractor = new MediaExtractor();
        try
        {
            extractor.SetDataSource(path);
            for (var i = 0; i < extractor.TrackCount; i++)
            {
                using var format = extractor.GetTrackFormat(i);
                var mime = format.GetString(MediaFormat.KeyMime);
                if (mime is not null && mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && HasDecoder(mime)) return true;
            }
            return false;
        }
        catch (Exception)
        {
            // Java.IO.IOException: "Failed to instantiate extractor".
            return false;
        }
        finally
        {
            extractor.Release();
        }
    }

    private static bool HasDecoder(string mime)
    {
        var types = _decoderTypes ??= DecoderTypes();
        // A device that reports no codecs at all: the extractor's word is all there is.
        return types.Count == 0 || types.Contains(mime);
    }

    private static HashSet<string> DecoderTypes()
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var codec in new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos() ?? [])
            {
                if (codec.IsEncoder) continue;
                foreach (var type in codec.GetSupportedTypes() ?? []) types.Add(type);
            }
        }
        catch (Exception)
        {
            types.Clear();
        }
        return types;
    }
}
