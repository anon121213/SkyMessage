using System.Diagnostics;
using System.Text;

namespace SkyMessage.Tones.Pipeline;

/// <summary>
/// The encoders: ffmpeg as an external process (never linked or shipped). Output is bit-exact for the same input
/// (no version strings, fixed stream serials), so re-running the renderer changes no file whose sound did not change.
/// </summary>
internal static class Ffmpeg
{
    private const string Executable = "ffmpeg";

    /// <summary>Fails early, with a hint, when ffmpeg or its libvorbis or libmp3lame encoder is missing.</summary>
    public static void EnsureAvailable()
    {
        string encoders;
        try
        {
            encoders = Run(["-hide_banner", "-encoders"]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("ffmpeg is not on PATH (macOS: brew install ffmpeg; Linux: apt install ffmpeg).", ex);
        }
        foreach (var (encoder, flag) in new[] { ("libvorbis", "--enable-libvorbis"), ("libmp3lame", "--enable-libmp3lame") })
        {
            if (!encoders.Contains(encoder, StringComparison.Ordinal))
                throw new InvalidOperationException($"This ffmpeg has no {encoder} encoder; install a build with {flag}.");
        }
    }

    /// <summary>
    /// Android's copy (res/raw, notification channels): Ogg Vorbis, mono, VBR quality 6 (about 40 kbps on these mostly
    /// quiet sounds; transparent for mallets and bells). Every Android version's notification player decodes Ogg
    /// Vorbis; Ogg Opus is not guaranteed on the older versions the app supports (Android 8). A loop gets the
    /// ANDROID_LOOP tag, which makes Android's player loop it in the extractor, without a gap at the seam.
    /// </summary>
    public static void EncodeVorbis(string wav, string ogg, bool loop) =>
        Run(["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", wav,
            "-c:a", "libvorbis", "-q:a", "6", "-map_metadata", "-1", .. (loop ? new[] { "-metadata", "ANDROID_LOOP=true" } : []),
            "-fflags", "+bitexact", "-flags:a", "+bitexact", ogg]);

    /// <summary>
    /// The in-app copy (js/sounds.js in every WebView and browser): MP3, mono, LAME VBR quality 4 (smaller than the
    /// Ogg and, measured against the masters, closer to them). Not Ogg: WebKit (the iOS/Mac app's WebView, Safari)
    /// decodes Ogg only from 18.4, the app supports iOS 15, and every engine decodes MP3. The Xing/LAME header carries
    /// the encoder delay and padding, so Chromium and ffmpeg decode exactly the encoded samples; a decoder that ignored
    /// it would start a loop 1105 samples (23 ms) late, inside its silent seam, with the cycle still exact. No ID3 tags.
    /// </summary>
    public static void EncodeMp3(string wav, string mp3) =>
        Run(["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", wav,
            "-c:a", "libmp3lame", "-q:a", "4", "-map_metadata", "-1", "-id3v2_version", "0", "-write_id3v1", "0",
            "-fflags", "+bitexact", "-flags:a", "+bitexact", mp3]);

    /// <summary>
    /// Apple notification sounds: IMA4 ADPCM in CAF at 22.05 kHz (iOS plays only PCM, IMA4, µ-law and a-law
    /// notification sounds; everything here lies below 10 kHz).
    /// </summary>
    public static void EncodeCaf(string wav, string caf) =>
        Run(["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", wav,
            "-ar", "22050", "-c:a", "adpcm_ima_qt", "-f", "caf", "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact", caf]);

    /// <summary>Decodes a file the way an app would (to 48 kHz mono float), for the checks.</summary>
    public static double[] Decode(string path)
    {
        var raw = RunBinary(["-nostdin", "-hide_banner", "-loglevel", "error", "-i", path, "-f", "f32le", "-ac", "1", "-ar", "48000", "-"]);
        var samples = new double[raw.Length / 4];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToSingle(raw, i * 4);
        return samples;
    }

    /// <summary>A 32-bit float mono WAV: the lossless hand-over to the encoders.</summary>
    public static void WriteFloatWav(string path, double[] samples, int rate)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream, Encoding.ASCII);
        var dataBytes = samples.Length * 4;
        w.Write("RIFF"u8);
        w.Write(4 + 26 + 12 + 8 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(18);              // WAVEFORMATEX with cbSize
        w.Write((short)3);        // IEEE float
        w.Write((short)1);        // mono
        w.Write(rate);
        w.Write(rate * 4);        // byte rate
        w.Write((short)4);        // block align
        w.Write((short)32);       // bits per sample
        w.Write((short)0);        // cbSize
        w.Write("fact"u8);
        w.Write(4);
        w.Write(samples.Length);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples)
            w.Write((float)s);
    }

    private static string Run(IReadOnlyList<string> args) => Encoding.UTF8.GetString(RunBinary(args));

    private static byte[] RunBinary(IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg {string.Join(' ', args)} failed: {error.Result.Trim()}");
        return output.ToArray();
    }
}
