// Renders SkyMessage's own notification and call tones to small 44.1 kHz 16-bit mono WAV files.
// Palette "bells": celesta, music box and soft glass bells.
//
// Every sound is synthesized here from first principles: no samples, no third-party sound assets, and no motif
// borrowed from another phone or messenger. Each note is a handful of exponentially decaying sine partials
// ("modes") with a soft attack, using the mode ratios of the real instruments (a struck steel bar, a plucked comb
// tooth, a glass bowl, a small handbell); a small room is added by convolving with an impulse response that is
// itself generated here. Output goes to the MAUI asset folder (Android also packs them as res/raw so
// notification channels can reference them).
//
//   dotnet run tools/tones/render-tones.cs                 (writes into src/Client/SkyMessage.App/Resources/Raw)
//   dotnet run tools/tones/render-tones.cs -- <outputDir>
//
// Keys match the UI tone catalog: message tones chime/pop/glass, ringtones classic/soft/bright,
// call progress ringback/busy/end.
//
// Rules this file keeps, so the set stays pleasant and consistent:
//   - one key for the whole set: D major pentatonic (D E F# A B; G appears once, as a soft bass note);
//     consonant intervals only; every fundamental lies between 290 and 1320 Hz;
//   - attacks of 3-6 ms (raised cosine, never a click) and natural exponential decays;
//   - every partial is weighted by a gentle 4th-order low-pass at 4.8 kHz and nothing above 10 kHz is
//     synthesized at all: nothing piercing, no aliasing;
//   - one short room for everything (RT60 0.65 s, 12-15 dB under the direct sound), checked to treat every pitch
//     of the set evenly (within +-0.8 dB);
//   - every file peaks at -1.5 dBFS; the three message tones, and the three ringtones, match in RMS within 1 dB,
//     balanced by the arrangement itself (velocities, ring lengths), not by a limiter;
//   - one-shots start at zero and end with a short fade over an already decayed tail;
//   - ringtones and the ringback hold exactly one loop cycle, rendered as a periodic steady state (whatever
//     rings past the end of the cycle is wrapped onto its start) and ending in a rest long enough for the sound
//     to die away (to 48 dB or more under the peak; the ringback to digital silence). Both ends fade inside that
//     near-silence (10 ms in before the first strike, 30 ms out), so the first play starts from true silence and
//     the loop point is seamless (Android FLAG_INSISTENT, CallKit and WebAudio loop the file as is).

using System.Globalization;
using System.Text;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const int Rate = 44100;
const double PeakDbfs = -1.5;
const double RoomSend = 0.25;   // room level against the direct sound (mid band); measured 12-15 dB under it

var outputDir = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(FindRepoRoot(), "src", "Client", "SkyMessage.App", "Resources", "Raw"));
Directory.CreateDirectory(outputDir);

var room = Dsp.RoomImpulse(rt60: 0.65, seconds: 1.1);

var tones = new (string Key, Func<Score> Compose)[]
{
    ("chime", Chime), ("pop", Pop), ("glass", Glass),
    ("classic", Classic), ("soft", Soft), ("bright", Bright),
    ("ringback", Ringback), ("busy", Busy), ("end", End),
};

// A fixed room colours near-pure tones: at some pitches its reflections add to the bell, at others they cancel it
// (the first draft of this room took 4.6 dB off A4 and added 2.3 dB to A5). The room below was chosen to be even
// across the pitches of this set; check it against every pitch the scores use, so an edit cannot land in a notch.
var pitches = tones.SelectMany(t => t.Compose().Notes).Select(n => n.Hz).Distinct().Order().ToArray();
var roomGains = pitches.Select(hz => Dsp.RoomGainDb(room, RoomSend, hz)).ToArray();
var meanGain = roomGains.Average();
Console.WriteLine("room gain per pitch: " + string.Join("  ", pitches.Select((hz, i) => $"{Pitch.Name(hz)} {roomGains[i] - meanGain:+0.0;-0.0}")));
if (roomGains.Any(g => Math.Abs(g - meanGain) > 1.25))
    Console.WriteLine("WARNING: the room colours some pitches by more than 1.25 dB; pick another room or adjust those notes.");

foreach (var (key, compose) in tones)
{
    var score = compose();
    var (samples, dryToWetDb) = score.Render(room, RoomSend, PeakDbfs);
    var path = Path.Combine(outputDir, $"tone_{key}.wav");
    Dsp.WriteWav(path, samples);
    Console.WriteLine($"{Path.GetFileName(path),-18} {samples.Length / (double)Rate,5:0.00}s {(score.Loop ? "loop" : "once")}"
        + $"  rms {Dsp.RmsDb(samples),6:0.00} dBFS  direct/room {dryToWetDb,5:0.0} dB  {new FileInfo(path).Length / 1024.0,6:0.0} KB");
}

// ---- Message tones (one-shots, at most 0.9 s) -------------------------------------------------------------------

// "Перезвон", the default. Three celesta strikes shaped like a friendly "при-ве-ет": a pickup, a lift, then a
// settle onto the sixth, with a soft glass D5 under the last note so the ending blooms into a warm chord
// (D5 + B5 + D6). The pickup A5 is damped as B5 enters, so no second rubs against the landing.
//   A5 (880 Hz) at 0 ms · D6 (1175) at 100 ms · B5 (988) + glass D5 (587) at 230 ms, ringing out to 0.88 s.
static Score Chime() => new Score(0.88) { FadeOut = 0.20 }
    .Strike(Voices.Celesta, 0.000, "A5", 0.70, damp: 0.222)
    .Strike(Voices.Celesta, 0.100, "D6", 0.92)
    .Strike(Voices.Celesta, 0.230, "B5", 0.84, ring: 1.3)
    .Strike(Voices.Glass, 0.226, "D5", 0.30, ring: 0.8);

// "Пузырёк", the most minimal: one round glass drop. A single D6 (1175 Hz) whose pitch rises into place from a
// whole tone below during the first 14 ms, under its soft attack, the way a water drop goes "plip"; it dies away
// within half a second. (A longer or deeper glide leaves a smear of in-between pitches in the room that rubs
// against the D6.)
static Score Pop() => new Score(0.65) { FadeOut = 0.20 }
    .Strike(Voices.Drop, 0.000, "D6", 1.00, glideCents: -200, glideMs: 14);

// "Стекло": two small glass bowls touched almost together, like a quiet toast: A5 (880 Hz) and, 28 ms later,
// E6 (1319 Hz) a fifth above. One airy chord that shimmers (each bowl beats slowly against its twin mode).
static Score Glass() => new Score(0.90) { FadeOut = 0.24 }
    .Strike(Voices.Glass, 0.000, "A5", 0.85)
    .Strike(Voices.Glass, 0.028, "E6", 0.72);

// ---- Ringtones (one seamless loop cycle each) -------------------------------------------------------------------

// "Классика": unmistakably a phone ringing, but on a celesta. Two bursts per 3.0 s cycle, "ring-ring":
//   burst 1 at 0.00 s: a trill D5-F#5-D5-F#5-D5-F#5 (75 ms apart, swelling), landing on A5 (0.45 s);
//   burst 2 at 0.75 s: the same trill, then A5 (1.20 s) stepping up to D6 (1.275 s), which rings out;
//   rest from about 1.5 s to 3.0 s. The trill is a major third, so its overlapping tails stay consonant; repeated
//   strikes of one bar add in phase, as a steady hand on a real instrument does.
static Score Classic()
{
    var score = new Score(3.0, loop: true);
    string[] trill = ["D5", "F#5", "D5", "F#5", "D5", "F#5"];
    foreach (var at in new[] { 0.0, 0.75 })
        for (var i = 0; i < trill.Length; i++)
            score.Strike(Voices.Celesta, at + i * 0.075, trill[i], 0.58 + 0.04 * i, ring: 0.45, coherent: true);
    score.Strike(Voices.Celesta, 0.450, "A5", 0.86, ring: 0.8);
    score.Strike(Voices.Celesta, 1.200, "A5", 0.72, ring: 0.5);
    score.Strike(Voices.Celesta, 1.275, "D6", 0.86, ring: 1.0);
    return score;
}

// "Мягкий": a music-box lullaby, gentle enough for the night. 6/8, an eighth = 0.25 s (dotted quarter = 80 bpm),
// one 3.75 s cycle = five dotted-quarter beats: two bars that rock down a chord and back up, then one beat of rest,
// so the pulse carries on across the loop. Every note belongs to the chord that is ringing, so the long music-box
// tails blend instead of rubbing:
//   bar 1, D6 chord over a soft glass D4:      A5 (quarter) F#5 (eighth) D5 (quarter) B4 (eighth);
//   bar 2, Gmaj9 colour over a soft glass G4:  D5 (quarter) F#5 (eighth) A5 (dotted quarter), which rings;
//   one beat of rest while it dies away; the loop starts again on the same A5.
static Score Soft() => new Score(3.75, loop: true)
    .Strike(Voices.Glass, 0.000, "D4", 0.30, ring: 1.0)
    .Strike(Voices.MusicBox, 0.006, "A5", 0.62, ring: 0.78)
    .Strike(Voices.MusicBox, 0.500, "F#5", 0.50, ring: 0.78)
    .Strike(Voices.MusicBox, 0.750, "D5", 0.58, ring: 0.78)
    .Strike(Voices.MusicBox, 1.250, "B4", 0.50, ring: 0.78)
    .Strike(Voices.Glass, 1.494, "G4", 0.24, ring: 0.7)
    .Strike(Voices.MusicBox, 1.500, "D5", 0.58, ring: 0.78)
    .Strike(Voices.MusicBox, 2.000, "F#5", 0.50, ring: 0.78)
    .Strike(Voices.MusicBox, 2.250, "A5", 0.58, ring: 0.65);

// "Яркий": cheerful and bouncy, celesta over soft glass bass notes. 4/4, an eighth = 0.2 s (150 bpm), skipping
// dotted rhythm, one 3.0 s cycle:
//   A5 (dotted eighth) B5 (sixteenth) D6 A5 | E6 (dotted eighth) D6 (sixteenth) B5, then D6 on the off-beat, which
//   rings over B5 (a sweet third); bass D5 / A4 / D5 under beats 1, 3 and the final D6; rest from 1.6 s to 3.0 s.
//   Played legato, like a real celesta: a note is damped as soon as a neighbour a step away enters.
static Score Bright() => new Score(3.0, loop: true)
    .Strike(Voices.Glass, 0.000, "D5", 0.34, ring: 0.5)
    .Strike(Voices.Celesta, 0.004, "A5", 0.80, damp: 0.292)
    .Strike(Voices.Celesta, 0.300, "B5", 0.66, damp: 0.292)
    .Strike(Voices.Celesta, 0.400, "D6", 0.86, damp: 0.392)
    .Strike(Voices.Celesta, 0.600, "A5", 0.72, damp: 0.592)
    .Strike(Voices.Glass, 0.796, "A4", 0.30, ring: 0.5)
    .Strike(Voices.Celesta, 0.800, "E6", 0.85, damp: 0.292)
    .Strike(Voices.Celesta, 1.100, "D6", 0.68, damp: 0.292)
    .Strike(Voices.Celesta, 1.200, "B5", 0.76)
    .Strike(Voices.Glass, 1.396, "D5", 0.34, ring: 0.6)
    .Strike(Voices.Celesta, 1.400, "D6", 0.95, ring: 1.1);

// ---- Call progress ------------------------------------------------------------------------------------------------

// Ringback (the caller waits): a soft glass-bell roll on the open fourth A4 + D5 (440 / 587 Hz, close to the
// familiar 425 Hz "гудок"), 13 strikes 80 ms apart that swell and fade over 1 s, then 3 s of quiet. 4.0 s loop.
// The strikes add in phase, so the roll is a smooth, gently trembling ring rather than a rattle.
static Score Ringback()
{
    var score = new Score(4.0, loop: true);
    for (var i = 0; i < 13; i++)
    {
        var t = i * 0.08;
        var swell = 0.55 + 0.45 * Math.Sin(Math.PI * t / 0.96);
        score.Strike(Voices.Glass, t, "A4", 0.90 * swell, ring: 0.30, coherent: true);
        score.Strike(Voices.Glass, t + 0.004, "D5", 0.70 * swell, ring: 0.30, coherent: true);
    }
    return score;
}

// Busy: four short, damped handbell notes on A4 (440 Hz), 0.37 s apart, each a little softer. About 1.5 s.
static Score Busy()
{
    var score = new Score(1.50) { FadeOut = 0.15 };
    for (var i = 0; i < 4; i++)
        score.Strike(Voices.Handbell, i * 0.37, "A4", 0.90 - 0.04 * i, ring: 0.6, damp: 0.13);
    return score;
}

// Call ended: a soft falling cadence on the celesta, A5 - E5 - D5 (880 / 659 / 587 Hz) at 0 / 120 / 250 ms:
// fifth, second, home ("до свидания"). E5 is damped as D5 lands, leaving the open fifth D5 + A5. 0.78 s.
static Score End() => new Score(0.78) { FadeOut = 0.22 }
    .Strike(Voices.Celesta, 0.000, "A5", 0.68)
    .Strike(Voices.Celesta, 0.120, "E5", 0.64, damp: 0.122)
    .Strike(Voices.Celesta, 0.250, "D5", 0.78, ring: 0.9);

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkyMessage.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the SkyMessage repository or pass an output directory.");
}

// ---- Instruments ---------------------------------------------------------------------------------------------------

/// <summary>
/// One partial of a voice: frequency ratio to the note, level at full velocity, decay relative to the note's base
/// decay, and an optional twin a little higher (Hz at 880 Hz, scaled with pitch) whose slow beating makes it shimmer.
/// </summary>
readonly record struct Mode(double Ratio, double Level, double Decay, double TwinHz = 0, double TwinLevel = 0);

/// <summary>
/// An instrument. <c>Tau880</c> is the fundamental's decay time constant (s) at 880 Hz; lower notes ring longer by
/// <c>(880 / f) ^ TauSlope</c>. <c>Prompt</c> is the share of the fundamental that dies 4.5x faster (the bright
/// first moment of a strike before the long aftersound). <c>Darken</c> makes softer strikes darker: an upper mode's
/// level is further multiplied by <c>velocity ^ (Darken * log2(ratio))</c>.
/// </summary>
sealed record Voice(string Name, Mode[] Modes, double AttackMs, double Tau880, double TauSlope, double Prompt, double Darken);

static class Voices
{
    // Celesta: a felt hammer on a steel bar over a tuned wooden resonator. The resonator favors the fundamental;
    // the bar's own inharmonic modes (2.76x and 5.40x of a free bar) give the short, sweet "ting" of the strike.
    public static readonly Voice Celesta = new("celesta",
        [new(1.0, 1.0, 1.0), new(2.0, 0.07, 0.50), new(2.756, 0.15, 0.24), new(5.404, 0.045, 0.10), new(8.933, 0.012, 0.06)],
        AttackMs: 4, Tau880: 0.32, TauSlope: 0.7, Prompt: 0.30, Darken: 0.8);

    // Music box: a plucked steel comb tooth (a clamped bar: modes 1x and 6.27x). Two teeth per note, a hair apart,
    // give the slow chorus of a real cylinder box.
    public static readonly Voice MusicBox = new("music box",
        [new(1.0, 1.0, 1.0, TwinHz: 0.8, TwinLevel: 0.22), new(2.0, 0.06, 0.45), new(6.267, 0.12, 0.10)],
        AttackMs: 3, Tau880: 0.40, TauSlope: 0.5, Prompt: 0.25, Darken: 0.5);

    // Soft glass bell: a small glass bowl touched with a soft beater. Bowl modes 1x, 2.79x, 5.32x; a slightly uneven
    // rim splits the lower modes into twins, so the ring shimmers slowly.
    public static readonly Voice Glass = new("glass",
        [new(1.0, 1.0, 1.0, TwinHz: 1.7, TwinLevel: 0.30), new(2.79, 0.12, 0.33, TwinHz: 2.9, TwinLevel: 0.40), new(5.32, 0.03, 0.14)],
        AttackMs: 6, Tau880: 0.42, TauSlope: 0.6, Prompt: 0.15, Darken: 0.7);

    // Water-drop bell for "pop": almost a pure sine with a whisper of octave and bar mode, short-lived.
    public static readonly Voice Drop = new("drop",
        [new(1.0, 1.0, 1.0), new(2.0, 0.05, 0.50), new(2.756, 0.04, 0.30)],
        AttackMs: 6, Tau880: 0.18, TauSlope: 0.5, Prompt: 0.0, Darken: 0.5);

    // Small handbell with a felt clapper, for call progress: near-harmonic modes 1x, 2x, 3x, 4.2x, warm and round.
    public static readonly Voice Handbell = new("handbell",
        [new(1.0, 1.0, 1.0), new(2.0, 0.22, 0.55), new(3.0, 0.09, 0.35), new(4.2, 0.03, 0.20)],
        AttackMs: 6, Tau880: 0.35, TauSlope: 0.6, Prompt: 0.20, Darken: 0.8);
}

// ---- Score and rendering -------------------------------------------------------------------------------------------

/// <summary>
/// One strike: <c>Ring</c> scales the decay, <c>Damp</c> (s after the strike) mutes it like a damper on the bar,
/// <c>GlideCents</c> starts the pitch that far off and settles it within <c>GlideMs</c>. <c>Coherent</c> strikes take
/// their phase from the clock, so repeated strikes of one note add up smoothly (a roll) instead of at random.
/// </summary>
sealed record Note(Voice Voice, double Time, double Hz, double Velocity, double Ring, double Damp, double GlideCents,
    double GlideMs, bool Coherent);

sealed class Score(double seconds, bool loop = false)
{
    const int Rate = 44100;

    public double Seconds { get; } = seconds;
    public bool Loop { get; } = loop;
    /// <summary>One-shots: raised-cosine fade over the last FadeOut seconds (the tail is already low there).</summary>
    public double FadeOut { get; init; } = 0.15;
    /// <summary>
    /// Loops: the cycle starts this long before the first strike, so the file opens (and ends) inside the quiet,
    /// wrapped tail of the previous cycle instead of on an attack.
    /// </summary>
    public double LoopLead { get; init; } = 0.012;
    public List<Note> Notes { get; } = [];

    public Score Strike(Voice voice, double time, string pitch, double velocity, double ring = 1.0,
        double damp = double.PositiveInfinity, double glideCents = 0, double glideMs = 0, bool coherent = false)
    {
        var at = Loop ? time + LoopLead : time;
        Notes.Add(new Note(voice, at, Pitch.Hz(pitch), velocity, ring, damp, glideCents, glideMs, coherent));
        return this;
    }

    /// <summary>Renders dry strikes plus room, peak-normalized; also returns the direct-to-room energy ratio (dB).</summary>
    public (float[] Samples, double DryToWetDb) Render(double[] room, double send, double peakDbfs)
    {
        var n = (int)Math.Round(Seconds * Rate);
        var dry = new double[n + 8 * Rate];   // room for every tail; loops fold it back, one-shots cut it
        foreach (var note in Notes)
            Synth.Strike(dry, note);

        double[] direct, wet;
        if (Loop)
        {
            // Periodic steady state: fold the tails onto the cycle, then a circular convolution with the room.
            direct = Dsp.Fold(dry, n);
            wet = Dsp.Fold(Dsp.Convolve(direct, room), n);
        }
        else
        {
            direct = dry[..n];
            wet = Dsp.Convolve(direct, room)[..n];
        }

        var mix = new double[n];
        double eDry = 0, eWet = 0;
        for (var i = 0; i < n; i++)
        {
            mix[i] = direct[i] + send * wet[i];
            eDry += direct[i] * direct[i];
            eWet += send * send * wet[i] * wet[i];
        }

        // Fades at both ends. One-shots: over the decayed tail. Loops: only inside the near-silent wrapped tail
        // (at least 50 dB under the peak), 10 ms in before the first strike and 30 ms out, so the very first play
        // starts from true silence and the loop point is a smooth, inaudible dip rather than a step.
        FadeIn(mix, (int)((Loop ? 0.010 : 0.001) * Rate));
        FadeOutEnd(mix, (int)((Loop ? 0.030 : FadeOut) * Rate));

        var peak = mix.Max(Math.Abs);
        var gain = Math.Pow(10, peakDbfs / 20) / peak;
        var output = new float[n];
        for (var i = 0; i < n; i++)
            output[i] = (float)(mix[i] * gain);
        return (output, 10 * Math.Log10(eDry / eWet));
    }

    static void FadeIn(double[] x, int length)
    {
        for (var i = 0; i < length; i++)
            x[i] *= 0.5 - 0.5 * Math.Cos(Math.PI * i / length);
    }

    static void FadeOutEnd(double[] x, int length)
    {
        for (var i = 0; i < length; i++)
            x[x.Length - 1 - i] *= 0.5 - 0.5 * Math.Cos(Math.PI * i / length);
    }
}

static class Synth
{
    const int Rate = 44100;
    const double MaxPartialHz = 10000;   // far below Nyquist (22050 Hz): no partial can alias
    const double DamperTau = 0.030;      // a felt damper: about -20 dB in 75 ms
    const double DamperEase = 0.012;     // ...settling onto the bar over 12 ms

    public static void Strike(double[] buf, Note note)
    {
        var voice = note.Voice;
        var tau = voice.Tau880 * Math.Pow(880.0 / note.Hz, voice.TauSlope) * note.Ring;
        var rng = new Rng((ulong)(note.Time * 1e6) * 1000003UL ^ (ulong)(note.Hz * 1000));
        foreach (var mode in voice.Modes)
        {
            var hz = note.Hz * mode.Ratio;
            Partial(buf, note, voice, mode, hz, 1.0, tau * mode.Decay, ref rng);
            if (mode.TwinLevel > 0)
                Partial(buf, note, voice, mode, hz + mode.TwinHz * note.Hz / 880.0, mode.TwinLevel, tau * mode.Decay * 0.9, ref rng);
        }
    }

    static void Partial(double[] buf, Note note, Voice voice, Mode mode, double hz, double share, double tau, ref Rng rng)
    {
        var phase = rng.NextDouble() * 2 * Math.PI;   // drawn first so a skipped partial does not shift the others
        if (note.Coherent)
            phase = 2 * Math.PI * (hz * note.Time % 1.0);
        if (hz >= MaxPartialHz) return;
        var v = note.Velocity;
        var level = v * mode.Level * share * Math.Pow(v, voice.Darken * Math.Log2(Math.Max(1.0, mode.Ratio))) * Dsp.Tame(hz);
        if (level < 1e-7) return;

        var prompt = mode.Ratio <= 2.01 ? voice.Prompt : 0.0;
        var attack = voice.AttackMs / 1000.0;
        var glide = note.GlideMs / 1000.0;
        var start = (int)Math.Round(note.Time * Rate);
        var length = Math.Min(buf.Length - start, (int)((attack + tau * 14) * Rate));   // down to about -120 dB
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)Rate;
            var env = (1 - prompt) * Math.Exp(-t / tau) + prompt * Math.Exp(-t * 4.5 / tau);
            if (t < attack) env *= 0.5 - 0.5 * Math.Cos(Math.PI * t / attack);
            if (t > note.Damp)
            {
                // The felt settles onto the bar over DamperEase seconds: the extra decay eases in (no corner, no click).
                var u = t - note.Damp;
                env *= Math.Exp(-(u < DamperEase ? u * u / (2 * DamperEase) : u - DamperEase / 2) / DamperTau);
            }
            var f = hz;
            if (t < glide)
            {
                var u = t / glide;
                f *= Math.Pow(2, note.GlideCents * (1 - u * u * (3 - 2 * u)) / 1200);
            }
            phase += 2 * Math.PI * f / Rate;
            buf[start + i] += level * env * Math.Sin(phase);
        }
    }
}

static class Pitch
{
    /// <summary>Equal temperament, A4 = 440 Hz: "A4", "F#5", "Bb3".</summary>
    public static double Hz(string name)
    {
        var semitone = "C D EF G A B".IndexOf(char.ToUpperInvariant(name[0]));
        var i = 1;
        if (name[i] == '#') { semitone++; i++; }
        else if (name[i] == 'b') { semitone--; i++; }
        var octave = int.Parse(name[i..]);
        var midi = 12 * (octave + 1) + semitone;
        return 440.0 * Math.Pow(2, (midi - 69) / 12.0);
    }

    public static string Name(double hz)
    {
        var midi = (int)Math.Round(69 + 12 * Math.Log2(hz / 440.0));
        return new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }[midi % 12] + (midi / 12 - 1);
    }
}

// ---- DSP helpers ---------------------------------------------------------------------------------------------------

static class Dsp
{
    const int Rate = 44100;

    /// <summary>Gentle 4th-order low-pass magnitude at 4.8 kHz, applied to every synthesized partial.</summary>
    public static double Tame(double hz) => 1.0 / Math.Sqrt(1.0 + Math.Pow(hz / 4800.0, 8));

    /// <summary>
    /// A small furnished room: seven early reflections (9-42 ms) and a diffuse tail of decaying noise that darkens as
    /// it decays (highs are absorbed first), high-passed at 140 Hz and low-passed at 6.5 kHz. Scaled so the mid band
    /// (300-3000 Hz) passes at unity power gain; the send level then sets how far the room sits under the sound.
    /// The reflection pattern and the noise seed were picked (from 150 candidates) as the room that treats the notes
    /// D4-E6 most evenly: every pitch of the set within +-0.8 dB, where a typical candidate is off by +-2.4 dB.
    /// </summary>
    public static double[] RoomImpulse(double rt60, double seconds)
    {
        var n = (int)(seconds * Rate);
        var early = new double[n];
        (double Ms, double Gain)[] taps = [(8.7, -0.370), (13.8, 0.334), (19.6, -0.299), (25.5, 0.209), (28.9, 0.213), (34.1, 0.145), (41.9, -0.114)];
        foreach (var (ms, g) in taps)
            early[(int)(ms / 1000 * Rate)] += g;

        var late = new double[n];
        var rng = new Rng(61);
        var lp = 0.0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            var cutoff = 1500 + 6500 * Math.Exp(-t / 0.16);
            var a = Math.Exp(-2 * Math.PI * cutoff / Rate);
            lp = (1 - a) * rng.NextGaussian() + a * lp;
            var onset = SmoothStep(0.006, 0.045, t);
            var end = 1 - SmoothStep(seconds - 0.12, seconds, t);
            late[i] = lp * onset * end * Math.Exp(-6.9078 * t / rt60);
        }

        // Balance: early reflections carry about a third of the room's energy.
        var eEarly = early.Sum(x => x * x);
        var eLate = late.Sum(x => x * x);
        var ir = new double[n];
        for (var i = 0; i < n; i++)
            ir[i] = early[i] * Math.Sqrt(0.35 / eEarly) + late[i] * Math.Sqrt(0.65 / eLate);

        HighPass1(ir, 140);
        LowPass2(ir, 6500);

        // Unity power gain in the mid band.
        var size = 1;
        while (size < n) size <<= 1;
        var re = new double[size];
        var im = new double[size];
        Array.Copy(ir, re, n);
        Fft(re, im, inverse: false);
        double sum = 0;
        var count = 0;
        for (var k = 0; k < size / 2; k++)
        {
            var hz = k * (double)Rate / size;
            if (hz < 300 || hz > 3000) continue;
            sum += re[k] * re[k] + im[k] * im[k];
            count++;
        }
        var scale = 1.0 / Math.Sqrt(sum / count);
        for (var i = 0; i < n; i++) ir[i] *= scale;
        return ir;
    }

    static double SmoothStep(double from, double to, double x)
    {
        var u = Math.Clamp((x - from) / (to - from), 0, 1);
        return u * u * (3 - 2 * u);
    }

    static void HighPass1(double[] x, double hz)
    {
        var a = Math.Exp(-2 * Math.PI * hz / Rate);
        double prevX = 0, prevY = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var y = a * (prevY + x[i] - prevX);
            prevX = x[i];
            prevY = y;
            x[i] = y;
        }
    }

    static void LowPass2(double[] x, double hz)
    {
        // RBJ biquad, Butterworth Q.
        var w0 = 2 * Math.PI * hz / Rate;
        var alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
        var cos = Math.Cos(w0);
        var a0 = 1 + alpha;
        double b0 = (1 - cos) / 2 / a0, b1 = (1 - cos) / a0, b2 = (1 - cos) / 2 / a0, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var y = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x[i];
            y2 = y1; y1 = y;
            x[i] = y;
        }
    }

    /// <summary>Linear convolution via FFT.</summary>
    public static double[] Convolve(double[] x, double[] h)
    {
        var n = x.Length + h.Length - 1;
        var size = 1;
        while (size < n) size <<= 1;
        double[] xr = new double[size], xi = new double[size], hr = new double[size], hi = new double[size];
        Array.Copy(x, xr, x.Length);
        Array.Copy(h, hr, h.Length);
        Fft(xr, xi, inverse: false);
        Fft(hr, hi, inverse: false);
        for (var k = 0; k < size; k++)
        {
            var re = xr[k] * hr[k] - xi[k] * hi[k];
            var im = xr[k] * hi[k] + xi[k] * hr[k];
            xr[k] = re;
            xi[k] = im;
        }
        Fft(xr, xi, inverse: true);
        return xr[..n];
    }

    /// <summary>Wraps a signal onto one period: the steady state of a loop whose tails ring into the next cycle.</summary>
    public static double[] Fold(double[] x, int period)
    {
        var y = new double[period];
        for (var i = 0; i < x.Length; i++)
            y[i % period] += x[i];
        return y;
    }

    static void Fft(double[] re, double[] im, bool inverse)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var half = len / 2;
            var step = (inverse ? 2 : -2) * Math.PI / len;
            for (var k = 0; k < half; k++)
            {
                double wr = Math.Cos(step * k), wi = Math.Sin(step * k);
                for (var i = k; i < n; i += len)
                {
                    var j = i + half;
                    var tr = re[j] * wr - im[j] * wi;
                    var ti = re[j] * wi + im[j] * wr;
                    re[j] = re[i] - tr;
                    im[j] = im[i] - ti;
                    re[i] += tr;
                    im[i] += ti;
                }
            }
        }
        if (inverse)
            for (var i = 0; i < n; i++)
            {
                re[i] /= n;
                im[i] /= n;
            }
    }

    public static double RmsDb(float[] x) => 10 * Math.Log10(x.Sum(s => (double)s * s) / x.Length);

    /// <summary>How much louder (dB) a decaying pure note at <paramref name="hz"/> comes out with the room added.</summary>
    public static double RoomGainDb(double[] room, double send, double hz)
    {
        var n = (int)(1.2 * Rate);
        var dry = new double[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            dry[i] = Math.Min(1, t / 0.004) * Math.Exp(-t / 0.3) * Math.Sin(2 * Math.PI * hz * t + 0.7);
        }
        var wet = Convolve(dry, room);
        double eDry = 0, eMix = 0;
        for (var i = 0; i < n; i++)
        {
            var m = dry[i] + send * wet[i];
            eDry += dry[i] * dry[i];
            eMix += m * m;
        }
        return 10 * Math.Log10(eMix / eDry);
    }

    public static void WriteWav(string path, float[] samples)
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
        w.Write(Rate);
        w.Write(Rate * 2);        // byte rate
        w.Write((short)2);        // block align
        w.Write((short)16);       // bits per sample
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples)
            w.Write((short)Math.Clamp(Math.Round(s * short.MaxValue), short.MinValue, short.MaxValue));
    }
}

/// <summary>SplitMix64: a tiny deterministic generator, so every render is bit-identical.</summary>
struct Rng(ulong seed)
{
    ulong _state = seed;

    public double NextDouble()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return ((z ^ (z >> 31)) >> 11) * (1.0 / (1UL << 53));
    }

    public double NextGaussian()
    {
        var u = 1.0 - NextDouble();
        var v = NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }
}
