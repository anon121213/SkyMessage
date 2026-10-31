// Renders SkyMessage's own notification/call tones to small 16-bit mono WAV files.
// Every sound is synthesized here from first principles (sine partials + envelopes): no samples,
// no third-party sound assets. Output goes to the MAUI asset folder (Android also packs them as
// res/raw so notification channels can reference them).
//
//   dotnet run tools/tones/render-tones.cs                 (writes into src/Client/SkyMessage.App/Resources/Raw)
//   dotnet run tools/tones/render-tones.cs -- <outputDir>
//
// Keys match the UI tone catalog: message tones chime/pop/glass, ringtones classic/soft/bright,
// call progress ringback/busy/end.

using System.Text;

const int SampleRate = 22050;

var outputDir = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(FindRepoRoot(), "src", "Client", "SkyMessage.App", "Resources", "Raw"));
Directory.CreateDirectory(outputDir);

var tones = new Dictionary<string, Func<float[]>>
{
    ["chime"] = Chime,
    ["pop"] = Pop,
    ["glass"] = Glass,
    ["classic"] = Classic,
    ["soft"] = Soft,
    ["bright"] = Bright,
    ["ringback"] = Ringback,
    ["busy"] = Busy,
    ["end"] = End,
};

foreach (var (key, render) in tones)
{
    var samples = render();
    Normalize(samples, peak: 0.70f);
    FadeEdges(samples, fadeMs: 4);
    var path = Path.Combine(outputDir, $"tone_{key}.wav");
    WriteWav(path, samples);
    Console.WriteLine($"{Path.GetFileName(path),-20} {samples.Length / (double)SampleRate,5:0.00}s {new FileInfo(path).Length / 1024.0,7:0.0} KB");
}

// ---- Message tones -------------------------------------------------------------------------

// Two soft bell strikes a fifth apart.
static float[] Chime()
{
    var buf = Buffer(0.95);
    Bell(buf, 0.00, 1046.5, 0.55, 0.9);   // C6
    Bell(buf, 0.13, 1568.0, 0.70, 0.75);  // G6
    return buf;
}

// A short rising "bubble" blip followed by a quieter echo.
static float[] Pop()
{
    var buf = Buffer(0.32);
    Sweep(buf, 0.00, 0.070, 420, 980, 0.9);
    Sweep(buf, 0.105, 0.060, 620, 1240, 0.45);
    return buf;
}

// A high, slightly detuned crystal ting with a long, airy tail.
static float[] Glass()
{
    var buf = Buffer(1.10);
    Partial(buf, 0.0, 2093.0, 0.90, amp: 0.55);   // C7
    Partial(buf, 0.0, 2097.5, 0.85, amp: 0.30);   // gentle beating
    Partial(buf, 0.0, 2093.0 * 2.76, 0.30, amp: 0.18);
    Partial(buf, 0.0, 2093.0 * 5.40, 0.12, amp: 0.08);
    Partial(buf, 0.09, 3136.0, 0.55, amp: 0.22);  // G7 shimmer
    return buf;
}

// ---- Ringtones (each file holds two full cycles; Android loops it with FLAG_INSISTENT) ---------

// Alternating two-tone trill: ring 1.4 s, pause 1.6 s.
static float[] Classic()
{
    var buf = Buffer(6.0);
    for (var cycle = 0; cycle < 2; cycle++)
    {
        var start = cycle * 3.0;
        for (var i = 0; i < 14; i++)
        {
            var f = i % 2 == 0 ? 932.3 : 740.0; // A#5 / F#5
            Tone(buf, start + i * 0.1, 0.095, f, 0.55, attack: 0.004, release: 0.02, harmonics: [1.0, 0.35, 0.12]);
        }
    }
    return buf;
}

// Warm mallet arpeggio, unhurried.
static float[] Soft()
{
    var buf = Buffer(6.0);
    double[] notes = [523.25, 659.25, 783.99, 659.25, 587.33, 783.99]; // C5 E5 G5 E5 D5 G5
    for (var cycle = 0; cycle < 2; cycle++)
    {
        var start = cycle * 3.0;
        for (var i = 0; i < notes.Length; i++)
            Mallet(buf, start + i * 0.22, notes[i], 0.9, 0.65);
    }
    return buf;
}

// Brighter, upbeat rising figure with a sparkle on top.
static float[] Bright()
{
    var buf = Buffer(6.0);
    double[] notes = [783.99, 987.77, 1174.66, 1567.98, 1174.66, 1567.98]; // G5 B5 D6 G6 D6 G6
    double[] times = [0.0, 0.14, 0.28, 0.42, 0.70, 0.84];
    for (var cycle = 0; cycle < 2; cycle++)
    {
        var start = cycle * 3.0;
        for (var i = 0; i < notes.Length; i++)
            Bell(buf, start + times[i], notes[i], 0.45, 0.7);
    }
    return buf;
}

// ---- Call progress ----------------------------------------------------------------------------

// 425 Hz, 1 s on / 3 s off (one cycle; played in a loop).
static float[] Ringback()
{
    var buf = Buffer(4.0);
    Tone(buf, 0.0, 1.0, 425.0, 0.6, attack: 0.02, release: 0.03, harmonics: [1.0]);
    return buf;
}

// 425 Hz, 0.35 s on / 0.35 s off, four times.
static float[] Busy()
{
    var buf = Buffer(2.8);
    for (var i = 0; i < 4; i++)
        Tone(buf, i * 0.7, 0.35, 425.0, 0.6, attack: 0.01, release: 0.02, harmonics: [1.0]);
    return buf;
}

// Two descending soft notes: "call ended".
static float[] End()
{
    var buf = Buffer(0.85);
    Mallet(buf, 0.00, 783.99, 0.45, 0.8);  // G5
    Mallet(buf, 0.18, 587.33, 0.60, 0.8);  // D5
    return buf;
}

// ---- Synthesis primitives -----------------------------------------------------------------------

static float[] Buffer(double seconds) => new float[(int)Math.Ceiling(seconds * SampleRate)];

// Struck bell: fundamental plus inharmonic partials that die out faster than the fundamental.
static void Bell(float[] buf, double start, double freq, double decay, double amp)
{
    Partial(buf, start, freq, decay, amp * 0.60);
    Partial(buf, start, freq * 2.0, decay * 0.55, amp * 0.22);
    Partial(buf, start, freq * 2.76, decay * 0.35, amp * 0.12);
    Partial(buf, start, freq * 5.40, decay * 0.18, amp * 0.06);
}

// Soft mallet (marimba-like): mostly fundamental, a little 4th harmonic, fast decay.
static void Mallet(float[] buf, double start, double freq, double decay, double amp)
{
    Partial(buf, start, freq, decay * 0.5, amp * 0.70, attack: 0.006);
    Partial(buf, start, freq * 4.0, decay * 0.12, amp * 0.12, attack: 0.003);
    Partial(buf, start, freq * 10.0, decay * 0.04, amp * 0.03, attack: 0.002);
}

// One exponentially decaying sine partial.
static void Partial(float[] buf, double start, double freq, double decay, double amp, double attack = 0.003)
{
    var first = (int)(start * SampleRate);
    var length = (int)(decay * 7 * SampleRate);
    for (var i = 0; i < length && first + i < buf.Length; i++)
    {
        var t = i / (double)SampleRate;
        var env = Math.Min(1.0, t / attack) * Math.Exp(-t / decay);
        buf[first + i] += (float)(amp * env * Math.Sin(2 * Math.PI * freq * t));
    }
}

// Sustained tone with linear attack/release and optional harmonics.
static void Tone(float[] buf, double start, double duration, double freq, double amp, double attack, double release, double[] harmonics)
{
    var first = (int)(start * SampleRate);
    var length = (int)(duration * SampleRate);
    for (var i = 0; i < length && first + i < buf.Length; i++)
    {
        var t = i / (double)SampleRate;
        var env = Math.Min(1.0, Math.Min(t / attack, (duration - t) / release));
        double v = 0;
        for (var h = 0; h < harmonics.Length; h++)
            v += harmonics[h] * Math.Sin(2 * Math.PI * freq * (h + 1) * t);
        buf[first + i] += (float)(amp * Math.Max(0, env) * v);
    }
}

// Exponential pitch sweep with a quick decay (bubble).
static void Sweep(float[] buf, double start, double duration, double fromHz, double toHz, double amp)
{
    var first = (int)(start * SampleRate);
    var length = (int)(duration * 2.2 * SampleRate);
    double phase = 0;
    for (var i = 0; i < length && first + i < buf.Length; i++)
    {
        var t = i / (double)SampleRate;
        var k = Math.Min(1.0, t / duration);
        var f = fromHz * Math.Pow(toHz / fromHz, k);
        phase += 2 * Math.PI * f / SampleRate;
        var env = Math.Min(1.0, t / 0.003) * Math.Exp(-t / (duration * 0.6));
        buf[first + i] += (float)(amp * env * Math.Sin(phase));
    }
}

static void Normalize(float[] samples, float peak)
{
    var max = samples.Select(Math.Abs).DefaultIfEmpty(0).Max();
    if (max <= 0) return;
    var gain = peak / max;
    for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
}

static void FadeEdges(float[] samples, int fadeMs)
{
    var n = Math.Min(samples.Length / 2, SampleRate * fadeMs / 1000);
    for (var i = 0; i < n; i++)
    {
        var g = i / (float)n;
        samples[i] *= g;
        samples[^(i + 1)] *= g;
    }
}

static void WriteWav(string path, float[] samples)
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
    w.Write(SampleRate);
    w.Write(SampleRate * 2);  // byte rate
    w.Write((short)2);        // block align
    w.Write((short)16);       // bits per sample
    w.Write("data"u8);
    w.Write(dataBytes);
    foreach (var s in samples)
        w.Write((short)Math.Clamp(Math.Round(s * short.MaxValue), short.MinValue, short.MaxValue));
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkyMessage.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the SkyMessage repository or pass an output directory.");
}
