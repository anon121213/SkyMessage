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

    /// <summary>Fails early, with a hint, when ffmpeg or its libvorbis encoder is missing.</summary>
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
        if (!encoders.Contains("libvorbis", StringComparison.Ordinal))
            throw new InvalidOperationException("This ffmpeg has no libvorbis encoder; install a build with --enable-libvorbis.");
    }

    /// <summary>
    /// Ogg Vorbis, mono, VBR quality 6 (about 40 kbps on these mostly quiet sounds; transparent for mallets and bells).
    /// Vorbis, not Opus: Android plays Ogg Opus only from Android 10, the app supports Android 8, and every Android
    /// version's notification player and every browser engine the apps run in decode Ogg Vorbis. A loop gets the
    /// ANDROID_LOOP tag, which makes Android's player loop it in the extractor, without a gap at the seam.
    /// </summary>
    public static void EncodeVorbis(string wav, string ogg, bool loop) =>
        Run(["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", wav,
            "-c:a", "libvorbis", "-q:a", "6", "-map_metadata", "-1", .. (loop ? new[] { "-metadata", "ANDROID_LOOP=true" } : []),
            "-fflags", "+bitexact", "-flags:a", "+bitexact", ogg]);

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
