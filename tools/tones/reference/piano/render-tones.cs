// Renders SkyMessage's notification and call tones to 44.1 kHz 16-bit mono WAV files.
// Palette "Мягкое пианино" (soft piano): a felt piano and a mellow FM tine electric piano in a small, warm room.
//
// Every sound is our own synthesis from first principles. No samples, no third-party sounds, no NuGet packages:
//   - felt piano: additive strings. Stretched (inharmonic) partials, two unison strings a fraction of a cent apart,
//     a felt-covered hammer (dark spectrum, 4-10 ms attack, a breath of felt noise), a two-stage decay and a damper
//     that takes the sound off smoothly;
//   - e-piano: two FM operator pairs (1:1 body, 1:N tine) with decaying indices. It is band-limited by construction:
//     every sideband is summed explicitly from its Bessel coefficient and nothing above 8 kHz is generated;
//   - room: an 8-line feedback delay network with early reflections and a short (~0.5 s), dark tail.
// Output goes to the MAUI asset folder (Android also packs the files as res/raw so notification channels can use them).
//
//   dotnet run tools/tones/render-tones.cs                 (writes into src/Client/SkyMessage.App/Resources/Raw)
//   dotnet run tools/tones/render-tones.cs -- <outputDir>
//   (from a folder that holds a .csproj, say `dotnet run --file tools/tones/render-tones.cs`)
//
// Keys match the UI tone catalog: message tones chime/pop/glass, ringtones classic/soft/bright, call progress
// ringback/busy/end. Every file peaks at -1.5 dBFS; the message tones are within 1 dB RMS of each other, the ringtones
// too. Ringtones and the ringback are exactly one loop cycle long. They fall silent before the loop point, and the
// last whisper of room tail is wrapped onto the start, so a looping player (WebAudio, Android FLAG_INSISTENT) joins
// them without a click or a level jump.
//
// The score. Everything is in G♭ major pentatonic (G♭ A♭ B♭ D♭ E♭, the piano's black keys), so any two notes are
// consonant and the nine sounds read as one family. Times are seconds from the start of the file.
//
//   chime    felt piano, the "hello": a pickup, then a rolled third; the top line rises a major sixth.
//              0.010 D♭5 · 0.148 G♭5 · 0.172 B♭5 (the third rolled 24 ms), lifted at ~0.6 s. 0.88 s.
//   pop      felt piano, the most minimal: one short, round note.
//              0.008 D♭6 with a ghost D♭5 an octave below, staccato (lifted after 0.1 s). 0.5 s.
//   glass    e-piano, an open fifth rolled upward like a fingertip along glass; no third, so it stays clear.
//              0.010 E♭5 · 0.030 B♭5 · 0.050 E♭6, held together and lifted at ~0.6 s. 0.88 s.
//   classic  e-piano tremolo, unmistakably a phone ringing: B♭5↔G♭5 at 14 notes/s in two 0.57 s bursts
//              ("ring-ring") at 0.02 and 0.82, each over a soft felt G♭4+D♭5, then quiet. Cycle 3.0 s.
//   soft     felt piano lullaby in 6/8, ♩. = 60 (eighths 0.333 s), quiet and dark:
//              | D♭5 G♭5 B♭5—— | A♭5 G♭5 D♭5—— | over G♭4 (I), then D♭4 (V), which resolves to I on the repeat;
//              the long note of each phrase lands 15 ms late, like a breath. Cycle 4.0 s (two bars).
//   bright   e-piano riff at ♩ = 120 with two anticipations, felt fifths underneath, then a breath:
//              beat 1 G♭5 · 1½ B♭5 · 1¾ D♭6— · 2½ B♭5 · 2¾ E♭6— · 3½ D♭6 · 4 B♭5 · 4½ D♭6—
//              felt: G♭4+D♭5 on 1, A♭4+E♭5 on 3, G♭4+B♭4 on 4½. Cycle 3.0 s (six beats).
//   ringback e-piano, mellow: a B♭4↔D♭5 tremolo (11 notes, 87 ms apart) that swells and fades over ~1 s, then
//              3 s of quiet. Cycle 4.0 s.
//   busy     felt piano: four short, low fifths E♭4+B♭4, 0.32 s apart, each a little softer; dry. 1.5 s.
//   end      felt piano: D♭5 → B♭4 → G♭4, a soft falling triad (the chime turned into a goodbye). 0.8 s.

using System.Globalization;
using System.Text;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var outputDir = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(FindRepoRoot(), "src", "Client", "SkyMessage.App", "Resources", "Raw"));
Directory.CreateDirectory(outputDir);

var tones = new (string Key, Tone Tone)[]
{
    ("chime", Chime()),
    ("pop", Pop()),
    ("glass", Glass()),
    ("classic", Classic()),
    ("soft", Soft()),
    ("bright", Bright()),
    ("ringback", Ringback()),
    ("busy", Busy()),
    ("end", End()),
};

foreach (var (key, tone) in tones)
{
    var samples = Mixer.Render(tone);
    var path = Path.Combine(outputDir, $"tone_{key}.wav");
    Wav.Write(path, samples);
    Console.WriteLine($"{Path.GetFileName(path),-18} {samples.Length / (double)Dsp.Rate,5:0.00} s  peak {Dsp.Db(Dsp.Peak(samples)),5:0.0} dBFS" +
                      $"  rms {Dsp.Db(Dsp.Rms(samples)),6:0.0} dB{(tone.Loop ? "  loop" : "")}");
}

// ---- Message tones -----------------------------------------------------------------------------

// Felt piano, the "hello": a pickup D♭5, then G♭5+B♭5 rolled. The top line rises a major sixth into a warm third.
static Tone Chime() => new(0.88, Loop: false, Play: s =>
{
    s.Felt(0.010, "Db5", 0.56, hold: 0.50);
    s.Felt(0.148, "Gb5", 0.48, hold: 0.46);
    s.Felt(0.172, "Bb5", 0.74, hold: 0.46);
});

// Felt piano, the most minimal: one short, round D♭6 with a ghost of its lower octave for body.
static Tone Pop() => new(0.50, Loop: false, Wet: 0.22, FadeOut: 0.14, Play: s =>
{
    s.Felt(0.008, "Db6", 0.70, hold: 0.10, release: 0.07);
    s.Felt(0.008, "Db5", 0.30, hold: 0.10, release: 0.07);
});

// E-piano, an open fifth rolled upward (E♭5 · B♭5 · E♭6, 20 ms apart) and held: clear, no third.
static Tone Glass() => new(0.88, Loop: false, Wet: 0.30, Play: s =>
{
    s.EPiano(0.010, "Eb5", 0.46, hold: 0.56, ElectricPiano.Glass);
    s.EPiano(0.030, "Bb5", 0.56, hold: 0.56, ElectricPiano.Glass);
    s.EPiano(0.050, "Eb6", 0.72, hold: 0.56, ElectricPiano.Glass);
});

// ---- Ringtones (one loop cycle per file) ------------------------------------------------------

// "Ring-ring": two bursts of a B♭5↔G♭5 e-piano tremolo (14 notes/s) over a soft felt G♭4+D♭5, then quiet.
static Tone Classic() => new(3.0, Loop: true, Play: s =>
{
    double[] swell = [0.50, 0.46, 0.56, 0.52, 0.62, 0.56, 0.60, 0.54];
    foreach (var burst in new[] { 0.02, 0.82 })
    {
        s.Felt(burst, "Gb4", 0.42, hold: 0.60);
        s.Felt(burst + 0.012, "Db5", 0.36, hold: 0.58);
        for (var i = 0; i < swell.Length; i++)
            s.EPiano(burst + i * 0.071, i % 2 == 0 ? "Bb5" : "Gb5", swell[i], hold: i == swell.Length - 1 ? 0.12 : 0.085, ElectricPiano.Bell, release: 0.06);
    }
});

// A felt-piano lullaby in 6/8: | D♭5 G♭5 B♭5—— | A♭5 G♭5 D♭5—— | over G♭4, then D♭4 (V → I on the repeat).
static Tone Soft() => new(4.0, Loop: true, Wet: 0.30, Rt60: 0.6, Play: s =>
{
    const double E = 1 / 3.0, Breath = 0.015; // one eighth; the long note of each phrase arrives a touch late
    s.Felt(0.020, "Gb4", 0.36, hold: 1.85, brightness: 0.85);
    s.Felt(0.020, "Db5", 0.44, hold: 0.50, brightness: 0.85);
    s.Felt(0.020 + E, "Gb5", 0.46, hold: 0.45, brightness: 0.85);
    s.Felt(0.020 + 2 * E + Breath, "Bb5", 0.52, hold: 1.10, brightness: 0.85);
    s.Felt(2.020, "Db4", 0.34, hold: 1.25, brightness: 0.85);
    s.Felt(2.020, "Ab5", 0.48, hold: 0.45, brightness: 0.85);
    s.Felt(2.020 + E, "Gb5", 0.44, hold: 0.45, brightness: 0.85);
    s.Felt(2.020 + 2 * E + Breath, "Db5", 0.42, hold: 0.62, release: 0.09, brightness: 0.85);
});

// An e-piano riff at ♩ = 120 with two anticipations over felt fifths, then a breath (six beats).
static Tone Bright() => new(3.0, Loop: true, Play: s =>
{
    const double T = 0.02; // downbeat
    (double Beat, string Note, double Vel, double Hold)[] riff =
    [
        (0.0, "Gb5", 0.66, 0.20), (0.5, "Bb5", 0.52, 0.10), (0.75, "Db6", 0.72, 0.32),
        (1.5, "Bb5", 0.52, 0.10), (1.75, "Eb6", 0.80, 0.33), (2.5, "Db6", 0.62, 0.20),
        (3.0, "Bb5", 0.58, 0.20), (3.5, "Db6", 0.70, 0.36),
    ];
    foreach (var (beat, note, vel, hold) in riff)
        s.EPiano(T + beat * 0.5, note, vel, hold, ElectricPiano.Warm);
    s.Felt(T, "Gb4", 0.45, hold: 0.90);
    s.Felt(T, "Db5", 0.40, hold: 0.90);
    s.Felt(T + 1.0, "Ab4", 0.45, hold: 0.70);
    s.Felt(T + 1.0, "Eb5", 0.40, hold: 0.70);
    s.Felt(T + 1.75, "Gb4", 0.45, hold: 0.42);
    s.Felt(T + 1.75, "Bb4", 0.40, hold: 0.42);
});

// ---- Call progress ----------------------------------------------------------------------------

// A gentle "trrr": mellow e-piano tremolo B♭4↔D♭5 that swells and fades over ~1 s, then quiet (one 4 s cycle).
static Tone Ringback() => new(4.0, Loop: true, Play: s =>
{
    const int Count = 11;
    for (var i = 0; i < Count; i++)
    {
        var x = i / (double)(Count - 1);
        var vel = 0.34 + 0.22 * Math.Sin(Math.PI * Math.Pow(x, 0.8));
        s.EPiano(0.02 + i * 0.087, i % 2 == 0 ? "Bb4" : "Db5", vel, hold: i == Count - 1 ? 0.15 : 0.10, ElectricPiano.Mellow, release: 0.07);
    }
});

// Four short, low felt fifths E♭4+B♭4, each a little softer.
static Tone Busy() => new(1.5, Loop: false, Wet: 0.18, FadeOut: 0.2, Play: s =>
{
    for (var i = 0; i < 4; i++)
    {
        double t = 0.012 + i * 0.32, vel = 0.62 - 0.05 * i;
        s.Felt(t, "Eb4", vel, hold: 0.10, release: 0.045);
        s.Felt(t + 0.006, "Bb4", vel * 0.85, hold: 0.10, release: 0.045);
    }
});

// A soft falling triad D♭5 → B♭4 → G♭4: the chime turned into a goodbye.
static Tone End() => new(0.80, Loop: false, FadeOut: 0.18, Play: s =>
{
    s.Felt(0.010, "Db5", 0.50, hold: 0.30);
    s.Felt(0.125, "Bb4", 0.46, hold: 0.30);
    s.Felt(0.240, "Gb4", 0.52, hold: 0.36);
});

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkyMessage.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the SkyMessage repository or pass an output directory.");
}

// ---- Rendering ----------------------------------------------------------------------------------

// One sound: its length (for a loop, exactly one cycle), how much room, and how long the final fade of a one-shot is.
sealed record Tone(double Seconds, bool Loop, Action<Score> Play, double Wet = 0.26, double Rt60 = 0.5, double FadeOut = 0.16);

sealed class Score(double[] buffer)
{
    public void Felt(double at, string note, double vel, double hold, double release = 0.10, double brightness = 1.0) =>
        FeltPiano.Strike(buffer, at, Pitch.Midi(note), vel, hold, release, brightness);

    public void EPiano(double at, string note, double vel, double hold, ElectricPiano.Voice voice, double release = 0.10) =>
        ElectricPiano.Strike(buffer, at, Pitch.Midi(note), vel, hold, release, voice);
}

static class Mixer
{
    public static double[] Render(Tone tone)
    {
        var n = (int)Math.Round(tone.Seconds * Dsp.Rate);
        var buf = new double[n + 3 * Dsp.Rate]; // room for every tail; loops fold it back onto the start
        tone.Play(new Score(buf));
        Room.Apply(buf, tone.Rt60, tone.Wet);
        Biquad.HighPass(110, 0.707).Process(buf);  // no rumble under the instruments
        Biquad.LowPass(7000, 0.6).Process(buf);    // a gentle top: little energy above 6-8 kHz

        var output = new double[n];
        if (tone.Loop)
        {
            for (var i = 0; i < buf.Length; i++) output[i % n] += buf[i];
        }
        else
        {
            Array.Copy(buf, output, n);
        }

        Dsp.Fade(output, 0.002, tone.Loop ? 0.002 : tone.FadeOut);
        Dsp.NormalizePeak(output, -1.5);
        return output;
    }
}

// ---- Instruments ---------------------------------------------------------------------------------

static class FeltPiano
{
    const double MaxPartialHz = 7500;

    public static void Strike(double[] buf, double at, int midi, double vel, double hold, double release, double brightness)
    {
        const int Fs = Dsp.Rate;
        var f0 = Pitch.Hz(midi);
        var rng = new Random(midi * 7919 + (int)Math.Round(at * 1000));
        var stiffness = 0.00038 * Math.Pow(2, (midi - 60) / 13.0); // inharmonicity B: upper partials run sharp
        var corner = (850 + 2300 * Math.Pow(vel, 1.3)) * brightness; // the felt's low-pass, opening with velocity
        var tau = 1.15 * Math.Pow(440 / f0, 0.7);                   // prompt decay of the fundamental, s
        var attack = 0.0105 - 0.0065 * vel;                         // the felt slows the hammer: 4-10 ms
        var gain = 0.42 * Math.Pow(vel, 1.4);
        var start = (int)Math.Round(at * Fs);
        var length = Math.Min(buf.Length - start, (int)((hold + 9 * release + 0.02) * Fs));
        if (length <= 0) return;

        var rise = new double[length];
        var lifted = new double[length];
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)Fs;
            rise[i] = Dsp.Rise(t, attack);
            lifted[i] = Dsp.Since(t, hold);
        }

        for (var n = 1; ; n++)
        {
            var fn = n * f0 * Math.Sqrt(1 + stiffness * n * n);
            if (fn > MaxPartialHz) break;
            var an = gain * Math.Pow(n, -1.05)
                / Math.Sqrt(1 + Math.Pow(fn / corner, 4))
                * (0.6 + 0.4 * Math.Abs(Math.Sin(Math.PI * n / 8.5))); // hammer at ~1/8.5 of the string
            var tau1 = tau / (1 + 0.42 * (n - 1));
            var tau2 = tau1 * 3.5;
            var damperTau = release / (1 + 0.25 * (n - 1));          // the damper silences the top first
            var strikePhase = rng.NextDouble() * 2 * Math.PI;
            for (var s = 0; s < 2; s++)                              // two unison strings, slightly apart
            {
                var cents = (s == 0 ? -1 : 1) * (0.15 + 0.25 * rng.NextDouble());
                var w = 2 * Math.PI * fn * Math.Pow(2, cents / 1200) / Fs;
                var a = an * (s == 0 ? 0.56 : 0.44);
                var phase = strikePhase;                             // one hammer: both strings start together
                double k1 = Math.Exp(-1 / (tau1 * Fs)), k2 = Math.Exp(-1 / (tau2 * Fs)), e1 = 0.78, e2 = 0.22;
                for (var i = 0; i < length; i++)
                {
                    buf[start + i] += a * rise[i] * (e1 + e2) * Math.Exp(-lifted[i] / damperTau) * Math.Sin(phase);
                    phase += w;
                    e1 *= k1;
                    e2 *= k2;
                }
            }
        }

        // The felt itself: a short, soft breath of band-passed noise at the strike.
        var felt = Biquad.BandPass(Math.Clamp(1.8 * f0, 600, 2200), 0.7);
        var noise = gain * 0.22;
        var noiseLength = Math.Min(length, (int)(0.05 * Fs));
        for (var i = 0; i < noiseLength; i++)
        {
            var t = i / (double)Fs;
            buf[start + i] += noise * Dsp.Rise(t, 0.0015) * Math.Exp(-t / 0.010) * felt.Next(2 * rng.NextDouble() - 1);
        }
    }
}

static class ElectricPiano
{
    // FM indices of the 1:1 body and the 1:TineRatio tine, the tine's level, the per-partial low-pass corner (Hz),
    // an optional second body a few cents sharp (chorus), and a decay scale.
    public sealed record Voice(double Body, double Tine, double TineLevel, double TineRatio, double Corner, double Chorus = 0, double Decay = 1);

    public static readonly Voice Glass = new(Body: 1.05, Tine: 1.40, TineLevel: 0.50, TineRatio: 3.5, Corner: 5600, Chorus: 0.22);
    public static readonly Voice Bell = new(Body: 1.30, Tine: 1.30, TineLevel: 0.45, TineRatio: 7, Corner: 5200);
    public static readonly Voice Warm = new(Body: 1.40, Tine: 1.10, TineLevel: 0.38, TineRatio: 7, Corner: 5000, Chorus: 0.20);
    public static readonly Voice Mellow = new(Body: 1.00, Tine: 0.70, TineLevel: 0.24, TineRatio: 7, Corner: 3600);

    const int Block = 16;
    const double MaxPartialHz = 8000;
    static readonly int[] BodyK = Enumerable.Range(-6, 13).ToArray(); // sideband k sits at (1 + k)·f
    static readonly int[] TineK = Enumerable.Range(-3, 7).ToArray();  // sideband k sits at (1 + k·ratio)·f

    public static void Strike(double[] buf, double at, int midi, double vel, double hold, double release, Voice v)
    {
        const int Fs = Dsp.Rate;
        var f = Pitch.Hz(midi);
        var ratio = v.TineRatio;
        while (ratio > 3 && (1 + ratio) * f > 6200) ratio -= 1;    // short tines in the top register
        var tauA = 0.30 * Math.Pow(440 / f, 0.4) * v.Decay;        // the quick first drop
        var tauB = 2.2 * Math.Pow(440 / f, 0.6) * v.Decay;         // the long singing part
        var gain = 0.34 * Math.Pow(vel, 1.3);
        var chorusHz = f * Math.Pow(2, 2.5 / 1200);
        var start = (int)Math.Round(at * Fs);
        var length = Math.Min(buf.Length - start, (int)((hold + 9 * release + 0.02) * Fs));
        if (length <= 0) return;

        var bodyW = BodyK.Select(k => Weight((1 + k) * f, v.Corner)).ToArray();
        var chorusW = BodyK.Select(k => Weight((1 + k) * chorusHz, v.Corner)).ToArray();
        var tineW = TineK.Select(k => Weight((1 + k * ratio) * f, v.Corner)).ToArray();
        double[] jb0 = new double[BodyK.Length], jb1 = new double[BodyK.Length];
        double[] jt0 = new double[TineK.Length], jt1 = new double[TineK.Length];

        Coefficients(0, jb0, jt0);
        for (var b = 0; b < length; b += Block)
        {
            Coefficients((b + Block) / (double)Fs, jb1, jt1);
            var end = Math.Min(b + Block, length);
            for (var i = b; i < end; i++)
            {
                double t = i / (double)Fs, u = (i - b) / (double)Block;
                var damper = Math.Exp(-Dsp.Since(t, hold) / release);
                var env = Dsp.Rise(t, 0.0035) * (0.55 * Math.Exp(-t / tauA) + 0.45 * Math.Exp(-t / tauB)) * damper;
                var tineEnv = Dsp.Rise(t, 0.0025) * Math.Exp(-t / 0.20) * damper;
                double ph = 2 * Math.PI * f * t, phc = 2 * Math.PI * chorusHz * t;
                double body = 0, chorus = 0, tine = 0;
                for (var k = 0; k < BodyK.Length; k++)
                {
                    var j = jb0[k] + (jb1[k] - jb0[k]) * u;
                    if (bodyW[k] > 0) body += j * bodyW[k] * Math.Sin((1 + BodyK[k]) * ph);
                    if (v.Chorus > 0 && chorusW[k] > 0) chorus += j * chorusW[k] * Math.Sin((1 + BodyK[k]) * phc);
                }
                for (var k = 0; k < TineK.Length; k++)
                {
                    var j = jt0[k] + (jt1[k] - jt0[k]) * u;
                    if (tineW[k] > 0) tine += j * tineW[k] * Math.Sin((1 + TineK[k] * ratio) * ph);
                }
                buf[start + i] += gain * (env * (body + v.Chorus * chorus) + v.TineLevel * tineEnv * tine);
            }
            (jb0, jb1) = (jb1, jb0);
            (jt0, jt1) = (jt1, jt0);
        }

        void Coefficients(double t, double[] jb, double[] jt)
        {
            var body = v.Body * (0.45 + 0.55 * vel) * (0.30 + 0.70 * Math.Exp(-t / 0.22));
            var tine = v.Tine * Math.Pow(vel, 1.1) * Math.Exp(-t / 0.07);
            for (var k = 0; k < BodyK.Length; k++) jb[k] = Bessel.J(BodyK[k], body);
            for (var k = 0; k < TineK.Length; k++) jt[k] = Bessel.J(TineK[k], tine);
        }
    }

    // A zero-phase low-pass applied per partial, and a hard ceiling: nothing above 8 kHz is generated.
    static double Weight(double hz, double corner)
    {
        hz = Math.Abs(hz);
        if (hz < 1 || hz > MaxPartialHz) return 0;
        return 1 / Math.Sqrt(1 + Math.Pow(hz / corner, 4));
    }
}

static class Bessel
{
    // J_n(x) from its power series; exact to double precision for the small FM indices used here (x < 4).
    public static double J(int n, double x)
    {
        if (n < 0) return (n & 1) == 0 ? J(-n, x) : -J(-n, x);
        var half = x / 2;
        var term = 1.0;
        for (var i = 1; i <= n; i++) term *= half / i;
        var sum = term;
        for (var m = 1; m < 40 && Math.Abs(term) > 1e-18; m++)
        {
            term *= -half * half / (m * (double)(m + n));
            sum += term;
        }
        return sum;
    }
}

// ---- Room ------------------------------------------------------------------------------------------

// A small room: 10 ms pre-delay, seven early reflections, and an 8-line feedback delay network (Hadamard mixing,
// a one-pole low-pass in every line so the tail darkens as it fades). The wet signal is normalized to unit impulse
// energy, so `wet` is simply the reverb-to-dry amplitude ratio.
static class Room
{
    static readonly int[] Lines = [557, 619, 701, 787, 887, 983, 1093, 1201]; // 12.6-27.2 ms, coprime
    static readonly (double Ms, double Gain)[] Early =
        [(4.1, 0.35), (7.3, -0.27), (10.9, 0.24), (14.2, -0.20), (18.8, 0.16), (23.5, -0.12), (29.0, 0.09)];

    public static void Apply(double[] buf, double rt60, double wet)
    {
        if (wet <= 0) return;
        var tail = Wet(buf, rt60);
        var impulse = new double[(int)(Math.Max(1.0, 3 * rt60) * Dsp.Rate)];
        impulse[0] = 1;
        var scale = wet / Math.Sqrt(Wet(impulse, rt60).Sum(x => x * x));
        for (var i = 0; i < buf.Length; i++) buf[i] += scale * tail[i];
    }

    static double[] Wet(double[] x, double rt60)
    {
        const int Fs = Dsp.Rate;
        const double Damping = 0.30;
        var pre = (int)(0.010 * Fs);
        var lines = Lines.Select(n => new double[n]).ToArray();
        var gains = Lines.Select(n => Math.Pow(10, -3.0 * n / (rt60 * Fs))).ToArray();
        var pos = new int[8];
        var lowpass = new double[8];
        var o = new double[8];
        Allpass[] diffusers = [new(211, 0.6), new(337, 0.55), new(463, 0.5)];
        var taps = Early.Select(e => (Delay: pre + (int)(e.Ms * Fs / 1000), e.Gain)).ToArray();
        var highpass = Biquad.HighPass(180, 0.6);
        var y = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            var input = i >= pre ? x[i - pre] : 0;
            var early = 0.0;
            foreach (var (delay, g) in taps)
                if (i >= delay) early += g * x[i - delay];
            var diffused = input;
            foreach (var ap in diffusers) diffused = ap.Next(diffused);
            var late = 0.0;
            for (var j = 0; j < 8; j++)
            {
                lowpass[j] = (1 - Damping) * lines[j][pos[j]] + Damping * lowpass[j];
                o[j] = lowpass[j] * gains[j];
                late += (j & 1) == 0 ? o[j] : -o[j];
            }
            Hadamard(o);
            for (var j = 0; j < 8; j++)
            {
                lines[j][pos[j]] = o[j] + ((j & 2) == 0 ? 0.5 : -0.5) * diffused;
                if (++pos[j] == lines[j].Length) pos[j] = 0;
            }
            y[i] = highpass.Next(0.6 * early + late);
        }
        return y;
    }

    static void Hadamard(double[] v)
    {
        for (var h = 1; h < v.Length; h *= 2)
            for (var i = 0; i < v.Length; i += 2 * h)
                for (var j = i; j < i + h; j++)
                    (v[j], v[j + h]) = (v[j] + v[j + h], v[j] - v[j + h]);
        var scale = 1 / Math.Sqrt(v.Length);
        for (var j = 0; j < v.Length; j++) v[j] *= scale;
    }
}

sealed class Allpass(int length, double g)
{
    readonly double[] _line = new double[length];
    int _pos;

    public double Next(double x)
    {
        var delayed = _line[_pos];
        var y = -g * x + delayed;
        _line[_pos] = x + g * y;
        if (++_pos == _line.Length) _pos = 0;
        return y;
    }
}

// ---- Small DSP helpers --------------------------------------------------------------------------

sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
{
    double _x1, _x2, _y1, _y2;

    public double Next(double x)
    {
        var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
        (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
        return y;
    }

    public void Process(double[] buf)
    {
        for (var i = 0; i < buf.Length; i++) buf[i] = Next(buf[i]);
    }

    // RBJ cookbook filters.
    public static Biquad LowPass(double hz, double q) => Make(hz, q, (c, _) => ((1 - c) / 2, 1 - c, (1 - c) / 2));
    public static Biquad HighPass(double hz, double q) => Make(hz, q, (c, _) => ((1 + c) / 2, -(1 + c), (1 + c) / 2));
    public static Biquad BandPass(double hz, double q) => Make(hz, q, (_, alpha) => (alpha, 0, -alpha));

    static Biquad Make(double hz, double q, Func<double, double, (double, double, double)> numerator)
    {
        var w = 2 * Math.PI * hz / Dsp.Rate;
        double c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
        var (b0, b1, b2) = numerator(c, alpha);
        return new Biquad(b0 / a0, b1 / a0, b2 / a0, -2 * c / a0, (1 - alpha) / a0);
    }
}

static class Pitch
{
    // "Gb5", "Db6", "Bb4": letter, optional b or #, octave (C4 = 60, A4 = 440 Hz).
    public static int Midi(string name)
    {
        int[] fromA = [9, 11, 0, 2, 4, 5, 7];
        var semitone = fromA[char.ToUpperInvariant(name[0]) - 'A'];
        var i = 1;
        if (name[i] == 'b') { semitone--; i++; }
        else if (name[i] == '#') { semitone++; i++; }
        return 12 * (int.Parse(name[i..]) + 1) + semitone;
    }

    public static double Hz(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);
}

static class Dsp
{
    public const int Rate = 44100;

    public static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-12));
    public static double Peak(double[] x) => x.Length == 0 ? 0 : x.Max(Math.Abs);
    public static double Rms(double[] x) => x.Length == 0 ? 0 : Math.Sqrt(x.Sum(v => v * v) / x.Length);

    // sin² rise from 0 to 1 over `time` seconds: no corner, so no click.
    public static double Rise(double t, double time) => t >= time ? 1 : Math.Pow(Math.Sin(Math.PI / 2 * t / time), 2);

    // Time since `at`, smoothed over a 12 ms knee (0 before, t - at after): a damper lands without a corner.
    public static double Since(double t, double at, double knee = 0.012)
    {
        var x = (t - at) / knee;
        return (x > 30 ? x : Math.Log(1 + Math.Exp(x))) * knee;
    }

    // Raised-cosine fades at both ends.
    public static void Fade(double[] x, double inSeconds, double outSeconds)
    {
        int fadeIn = Math.Min(x.Length / 2, (int)(inSeconds * Rate)), fadeOut = Math.Min(x.Length / 2, (int)(outSeconds * Rate));
        for (var i = 0; i < fadeIn; i++) x[i] *= 0.5 - 0.5 * Math.Cos(Math.PI * i / fadeIn);
        for (var i = 0; i < fadeOut; i++) x[^(i + 1)] *= 0.5 - 0.5 * Math.Cos(Math.PI * i / fadeOut);
    }

    public static void NormalizePeak(double[] x, double dbfs)
    {
        var peak = Peak(x);
        if (peak <= 0) return;
        var gain = Math.Pow(10, dbfs / 20) / peak;
        for (var i = 0; i < x.Length; i++) x[i] *= gain;
    }
}

static class Wav
{
    public static void Write(string path, double[] samples)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream, Encoding.ASCII);
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);              // PCM chunk size
        w.Write((short)1);        // PCM
        w.Write((short)1);        // mono
        w.Write(Dsp.Rate);
        w.Write(Dsp.Rate * 2);    // byte rate
        w.Write((short)2);        // block align
        w.Write((short)16);       // bits per sample
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples)
            w.Write((short)Math.Clamp(Math.Round(s * short.MaxValue), short.MinValue, short.MaxValue));
    }
}
