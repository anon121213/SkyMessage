// Pack "wood" («Дерево», the default): marimba, kalimba and soft wood blocks in a small wooden room.
//
// Our own synthesis from first principles, by modal synthesis: a note is a handful of exponentially decaying sine
// partials at the mode ratios of a struck marimba bar, a plucked kalimba tine or a soft wood block, and the finished
// tone is placed in a small wooden room (the feedback-delay reverb below). No samples, no third-party sound assets,
// nothing modelled on another phone's or messenger's sounds.
//
// Keys match the app's tone catalog: message tones chime/pop/glass, ringtones classic/soft/bright, call progress
// ringback/busy/end. Files are 44.1 kHz mono and peak at -1.5 dBFS here (Program.cs then sets their loudness). A
// ringtone or ringback file holds exactly one cycle, rendered as the steady state of an endless loop (the previous
// cycle's tail is already in it and the bars are damped before the cycle ends), so a looping player joins it
// seamlessly.
//
// Music: the whole set is in D major pentatonic (D E F# A B; A4 = 440 Hz, equal temperament), so it sounds like one
// family of instruments. In the scores below times are seconds from the start of the file (or of the cycle) and
// dynamics are mallet velocities 0..1.

namespace SkyMessage.Tones.Packs;

internal static class WoodPack
{
    public const string Name = "wood";

    private const int SampleRate = 44100;
    private const double PeakDbfs = -1.5;
    private const double RoomLevel = 0.22;   // how much of the room is heard (wet gain: about -14 dB of the dry energy)

    public static IReadOnlyList<RenderedTone> Render() =>
        new[] { Chime(), Pop(), Glass(), Classic(), Soft(), Bright(), Ringback(), Busy(), End() }
            .Select(tone => new RenderedTone(tone.Key, Render(tone), tone.Loop, tone.Hits.Select(h => h.Time).ToArray()))
            .ToArray();

    // ---- Message tones (one-shots, at most 0.9 s) ------------------------------------------------------------

    // chime «Перезвон», the default. Marimba, three notes: up a fifth, then settle a minor third lower
    // (do-sol-mi), a little unhurried so each bar blooms before the next one sounds.
    //   0.010 D5 mf · 0.135 A5 f · 0.275 F#5 mf, rings out                                            0.90 s
    private static Tone Chime() => new("chime", 0.90,
    [
        Marimba(0.010, "D5", 0.70, ring: 0.7),
        Marimba(0.135, "A5", 0.86, ring: 0.7),
        Marimba(0.275, "F#5", 0.78, ring: 0.7),
    ]) { FadeOut = 0.24 };

    // pop «Пузырёк», the most minimal: one round, hollow wooden "bloop" whose pitch lifts by about a semitone
    // in its first 25 ms, like a bubble coming up.
    //   0.010 A5                                                                                     0.45 s
    private static Tone Pop() => new("pop", 0.45, [Bubble(0.010, "A5", 0.90)]) { FadeOut = 0.14 };

    // glass «Стекло»: two kalimba tines plucked together a fifth apart (the lower one 16 ms first): a clear
    // "tink" that leaves a pure, slowly shimmering tail.
    //   0.010 A5 f + 0.026 E6 mf                                                                     0.90 s
    private static Tone Glass() => new("glass", 0.90,
    [
        Kalimba(0.010, "A5", 0.86, decay: 1.6),
        Kalimba(0.026, "E6", 0.60, decay: 1.6),
    ]) { FadeOut = 0.28 };

    // ---- Ringtones (one seamless cycle each; the player loops the file) ------------------------------------

    // classic «Классика»: unmistakably a phone ringing, made of wood. Two bursts of a fast marimba trill on
    // A5/F#5 (a minor third, ~15 strikes a second, the old bell's "brrring") over a soft D5, then a pause:
    // ring 0.42 s, gap 0.18 s, ring 0.42 s, then the bars ring out (damped from 1.9 s) and the 3.0 s cycle rests.
    //   bursts at 0.020 and 0.620: A5 F#5 A5 F#5 A5 F#5 A5, one strike every 0.066 s, first one accented,
    //   slightly uneven like two hands; D5 p under the first strike                                   3.00 s
    private static Tone Classic()
    {
        var hits = new List<Hit>();
        double[] velocity = [0.92, 0.74, 0.85, 0.71, 0.82, 0.69, 0.76];
        foreach (var start in new[] { 0.020, 0.620 })
        {
            hits.Add(Marimba(start, "D5", 0.50, decay: 0.7, ring: 0.6));
            for (var i = 0; i < 7; i++)
                hits.Add(Marimba(start + i * 0.066, i % 2 == 0 ? "A5" : "F#5", velocity[i], decay: 0.7, hard: 0.5));
        }
        return new("classic", 3.0, Humanize(hits, seed: 3, ms: 2.0, velocity: 0.05)) { Loop = true, DampAt = 1.9, DampTime = 0.5 };
    }

    // soft «Мягкий», gentle enough for night: a kalimba calling like a voice from the next room, "sol-mi,
    // sol-mi-re, do", a lullaby in 3/4 at one bar per second; soft thumbs, long ringing tines, a bar of air.
    //   bar 1: A5 (half) F#5 | bar 2: A5 F#5 E5 | bar 3: D5+A4 (dotted half, rings) | bar 4: rest (damped)
    //   0.020 A5 · 0.687 F#5 · 1.020 A5 · 1.353 F#5 · 1.687 E5 · 2.020 D5 + 2.036 A4                 4.00 s
    private static Tone Soft() => new("soft", 4.0,
    [
        Kalimba(0.020, "A5", 0.70, decay: 0.9, hard: 0.15),
        Kalimba(0.687, "F#5", 0.58, decay: 0.9, hard: 0.15),
        Kalimba(1.020, "A5", 0.64, decay: 0.9, hard: 0.15),
        Kalimba(1.353, "F#5", 0.56, decay: 0.9, hard: 0.15),
        Kalimba(1.687, "E5", 0.52, decay: 0.7, hard: 0.15),
        Kalimba(2.020, "D5", 0.66, decay: 0.9, hard: 0.15),
        Kalimba(2.036, "A4", 0.44, decay: 0.9, hard: 0.15),
    ]) { Loop = true, DampAt = 3.30, DampTime = 0.45 };

    // bright «Яркий», cheerful: a bouncing marimba phrase with a syncopated start (3+3+2 sixteenths) that climbs
    // to E6 and skips back down, a soft wood block on the backbeat, then a breath.
    //   16ths of 0.10 s:  D6 . . A5 . . B5 . D6 . E6 . D6 . B5 . A5 ~~~   (D5 under the first and last notes)
    //   0.02 D6+D5 · 0.32 A5 · 0.62 B5 · 0.82 D6 · 1.02 E6 · 1.22 D6 · 1.42 B5 · 1.62 A5+D5;
    //   wood block (A5) on beats 2 and 4 (0.42, 1.22); two bars of 4/4 at 150 bpm, the second one rests 3.20 s
    private static Tone Bright() => new("bright", 3.2, Humanize(
    [
        Marimba(0.02, "D6", 0.92, hard: 0.6), Marimba(0.02, "D5", 0.50, hard: 0.6),
        Marimba(0.32, "A5", 0.70, hard: 0.6),
        Wood(0.42, "A5", 0.45),
        Marimba(0.62, "B5", 0.76, hard: 0.6),
        Marimba(0.82, "D6", 0.84, hard: 0.6),
        Marimba(1.02, "E6", 0.95, hard: 0.6),
        Marimba(1.22, "D6", 0.72, hard: 0.6), Wood(1.22, "A5", 0.40),
        Marimba(1.42, "B5", 0.74, hard: 0.6),
        Marimba(1.62, "A5", 0.86, hard: 0.6), Marimba(1.62, "D5", 0.48, hard: 0.6),
    ], seed: 5, ms: 1.5, velocity: 0.04)) { Loop = true, DampAt = 2.60 };

    // ---- Call progress -------------------------------------------------------------------------------------

    // ringback «Гудки вызова», what the caller hears: one soft marimba roll on an open fifth (A4/E5, alternating
    // mallets, ~13 strikes a second) that swells and fades over about a second, a long wooden "ring" near the
    // pitch of the familiar 425 Hz ringback, then about 2.5 s of quiet. One 4.0 s cycle.
    //   A4 E5 A4 E5 ... 13 strikes from 0.020, one every 0.078 s, p < mf > p                           4.00 s
    private static Tone Ringback()
    {
        var hits = new List<Hit>();
        for (var i = 0; i < 13; i++)
        {
            var swell = Math.Sin(Math.PI * (i + 0.5) / 13);
            hits.Add(Marimba(0.020 + i * 0.078, i % 2 == 0 ? "A4" : "E5", 0.42 + 0.38 * swell, decay: 0.65, hard: 0.25, ring: 0.5));
        }
        // The bars are damped from 1.15 s so the "ring" stays about a second long and the quiet that follows is real.
        return new("ringback", 4.0, Humanize(hits, seed: 7, ms: 2.0, velocity: 0.04)) { Loop = true, DampAt = 1.15, DampTime = 0.5 };
    }

    // busy «Занято»: the wooden version of the familiar "short beeps": four short, low marimba strokes on an
    // open fifth, A4 (the pitch of the usual busy tone) over a softer D4 that leads by 18 ms (a two-mallet
    // flam, warmer than a hard unison), one every 0.36 s, each a little softer.
    //   0.02 · 0.38 · 0.74 · 1.10: D4+A4  f, mf, mf, mp                                                1.50 s
    private static Tone Busy()
    {
        var hits = new List<Hit>();
        double[] velocity = [0.85, 0.80, 0.77, 0.73];
        for (var i = 0; i < 4; i++)
        {
            hits.Add(Marimba(0.020 + i * 0.36, "D4", velocity[i] * 0.72, decay: 0.75, ring: 0.6));
            hits.Add(Marimba(0.038 + i * 0.36, "A4", velocity[i], decay: 0.75, ring: 0.6));
        }
        return new("busy", 1.5, hits.ToArray()) { FadeOut = 0.16 };
    }

    // end «Конец звонка»: the call is over. A soft falling cadence, down a fourth and then a step home
    // (sol-re-do). The passing E5 is a short dead stroke so the last chord is a clean fifth (D5 under the
    // still-ringing A5), not a rubbing second.
    //   0.010 A5 mf · 0.128 E5 mp (short) · 0.262 D5 mp, rings out                                     0.80 s
    private static Tone End() => new("end", 0.80,
    [
        Marimba(0.010, "A5", 0.72),
        Marimba(0.128, "E5", 0.62, decay: 0.45, ring: 0.3),
        Marimba(0.262, "D5", 0.66),
    ]) { FadeOut = 0.22 };

    // ---- Instruments (modal models) ---------------------------------------------------------------------------

    private static Hit Marimba(double time, string note, double velocity, double decay = 1.0, double hard = 0.35, double ring = 1.0) =>
        new(time, Voice.Marimba, Hz(note), velocity) { Decay = decay, Hardness = hard, Ring = ring };

    private static Hit Kalimba(double time, string note, double velocity, double decay = 1.0, double hard = 0.35) =>
        new(time, Voice.Kalimba, Hz(note), velocity) { Decay = decay, Hardness = hard };

    private static Hit Wood(double time, string note, double velocity) => new(time, Voice.Wood, Hz(note), velocity);

    private static Hit Bubble(double time, string note, double velocity) => new(time, Voice.Bubble, Hz(note), velocity);

    // The partials of one strike. Mode amplitudes are relative to the fundamental; the sign is the phase the
    // strike gives each mode; the mallet's softness rolls off the upper modes (and less so when struck harder).
    private static Mode[] Modes(Hit hit)
    {
        var f = hit.Freq;
        var v = hit.Velocity;
        var bar = new Rng((ulong)BitConverter.DoubleToInt64Bits(f));   // each bar/tine is tuned a little differently
        switch (hit.Voice)
        {
            case Voice.Marimba:
            {
                // A tuned marimba bar: the first overtone is tuned two octaves up (~3.97 f), the next near 9.85 f;
                // a weak torsional mode (~2.9 f, the bar twisting when struck off-centre) colours the attack. The
                // resonator tube under the bar gives the fundamental a long, soft second stage of decay.
                var tau = 0.30 * hit.Decay * Math.Pow(523.25 / f, 0.55);
                var fc = MalletCutoff(hit.Hardness, v);
                var r2 = 3.97 * (1 + bar.Range(-0.002, 0.002));
                var r3 = 9.85 * (1 + bar.Range(-0.006, 0.006));
                var rt = 2.9 * (1 + bar.Range(-0.03, 0.03));
                return
                [
                    new(f, 1.0, tau * 0.55, tau * 1.6, 0.40 * hit.Ring, 0.004 + 0.004 * (1 - v)),
                    new(f * rt, 0.05 * Mallet(f * rt, fc), tau * 0.10, tau * 0.10, 0, 0.003),
                    new(f * r2, -0.40 * Mallet(f * r2, fc) * (0.55 + 0.45 * v), tau * 0.16, tau * 0.16, 0, 0.0025),
                    new(f * r3, 0.10 * Mallet(f * r3, fc) * v, tau * 0.05, tau * 0.05, 0, 0.002),
                ];
            }
            case Voice.Kalimba:
            {
                // A clamped steel tine: the fundamental rings long; the first bending overtone (~6.1 f, a cantilever's
                // 6.27 lowered by the tine's taper) is the short bright "tink". A slightly detuned copy of the
                // fundamental (the tine's other bending plane) makes the tail shimmer slowly; the bridge adds a
                // little octave.
                var tau = 0.42 * hit.Decay * Math.Pow(659.26 / f, 0.4);
                var fc = 3000 + 3000 * hit.Hardness * (0.5 + 0.5 * v);
                var twin = 1 + bar.Range(0.0010, 0.0022);
                var r2 = 6.1 * (1 + bar.Range(-0.02, 0.02));
                return
                [
                    new(f, 1.0, tau * 0.7, tau * 1.7, 0.5, 0.005),
                    new(f * twin, 0.20, tau * 0.7, tau * 1.7, 0.5, 0.006),
                    new(f * 2, 0.07 * (0.6 + 0.4 * v), tau * 0.35, tau * 0.35, 0, 0.004),
                    new(f * r2, -0.22 * Mallet(f * r2, fc) * v, 0.045, 0.045, 0, 0.0025),
                ];
            }
            case Voice.Wood:
            {
                // A soft wood block: one round hollow mode and three inharmonic ones that die out quickly
                // (plus the mallet's knock, added by Strike).
                var fc = MalletCutoff(hit.Hardness, v);
                return
                [
                    new(f, 1.0, 0.060 * hit.Decay, 0.15 * hit.Decay, 0.18, 0.0025),
                    new(f * 2.32, -0.30 * Mallet(f * 2.32, fc), 0.030 * hit.Decay, 0.030 * hit.Decay, 0, 0.0022),
                    new(f * 3.71, 0.12 * Mallet(f * 3.71, fc), 0.018 * hit.Decay, 0.018 * hit.Decay, 0, 0.002),
                    new(f * 5.19, -0.05 * Mallet(f * 5.19, fc), 0.011 * hit.Decay, 0.011 * hit.Decay, 0, 0.002),
                ];
            }
            default:
            {
                // Bubble: the wood block's rounder little sister, nearly harmonic, with the pitch lift set in Strike.
                var fc = MalletCutoff(hit.Hardness, v);
                return
                [
                    new(f, 1.0, 0.070, 0.18, 0.22, 0.005),
                    new(f * 2.02, -0.24 * Mallet(f * 2.02, fc), 0.030, 0.030, 0, 0.003),
                    new(f * 3.06, 0.08 * Mallet(f * 3.06, fc), 0.016, 0.016, 0, 0.002),
                ];
            }
        }
    }

    // Where a soft mallet stops exciting the bar: ~2.5 kHz for yarn played gently, higher when harder.
    private static double MalletCutoff(double hardness, double velocity) => 1800 + 3200 * hardness * (0.5 + 0.5 * velocity);

    // Second-order roll-off of the mallet's contact force spectrum.
    private static double Mallet(double freq, double cutoff) => 1 / Math.Sqrt(1 + Math.Pow(freq / cutoff, 4));

    // ---- Rendering -------------------------------------------------------------------------------------------

    private static double[] Render(Tone tone)
    {
        var n = (int)Math.Round(tone.Seconds * SampleRate);
        var cycles = tone.Loop ? 3 : 1;   // a loop is cut from the third of three cycles: its steady state
        var dry = new double[n * cycles];
        for (var c = 0; c < cycles; c++)
            for (var i = 0; i < tone.Hits.Length; i++)
            {
                var hit = tone.Hits[i];
                var start = hit.Time * SampleRate + (double)c * n;
                var dampAt = tone.Loop ? tone.DampAt * SampleRate + (double)c * n : double.PositiveInfinity;
                Strike(dry, hit, start, dampAt, tone.DampTime * SampleRate, seed: i);
            }

        var wet = Room(dry);
        var mix = new double[dry.Length];
        for (var i = 0; i < mix.Length; i++)
            mix[i] = dry[i] + RoomLevel * wet[i];
        HighPass(mix, 120);
        LowPass(mix, 7000);

        var output = tone.Loop ? mix[(2 * n)..(3 * n)] : mix;
        Normalize(output, Math.Pow(10, PeakDbfs / 20));
        if (tone.Loop)
            Fade(output, SampleRate * 3 / 1000, SampleRate * 3 / 1000);   // the seam is already quiet
        else
            Fade(output, SampleRate * 2 / 1000, (int)(tone.FadeOut * SampleRate));
        return output;
    }

    private static void Strike(double[] buf, Hit hit, double start, double dampAt, double dampLength, int seed)
    {
        var gain = Math.Pow(hit.Velocity, 1.2);
        var glide = hit.Voice == Voice.Bubble ? 0.06 : 0.0;
        foreach (var mode in Modes(hit))
            AddMode(buf, start, mode, gain, glide, dampAt, dampLength);
        switch (hit.Voice)
        {
            case Voice.Marimba: AddKnock(buf, start, gain * 0.03, 650, seed); break;    // the yarn mallet's soft thud
            case Voice.Wood: AddKnock(buf, start, gain * 0.07, 1900, seed); break;      // a knock on wood
            case Voice.Bubble: AddKnock(buf, start, gain * 0.03, 1500, seed); break;
        }
    }

    // One decaying partial: raised-cosine attack, two-stage exponential decay, optional upward pitch lift that
    // settles in ~25 ms (phase integrated in closed form), optional damping from dampAt (see Damper).
    private static void AddMode(double[] buf, double start, Mode mode, double gain, double glide, double dampAt, double dampLength)
    {
        if (mode.Freq >= 12000 || mode.Amp == 0) return;   // nothing near Nyquist (no aliasing), nothing shrill
        const double glideTau = 0.009;
        var w = 2 * Math.PI * mode.Freq;
        for (var s = Math.Max((int)Math.Ceiling(start), 0); s < buf.Length; s++)
        {
            var t = (s - start) / SampleRate;
            var env = (1 - mode.SlowMix) * Math.Exp(-t / mode.TauFast) + mode.SlowMix * Math.Exp(-t / mode.TauSlow);
            if (t > mode.Attack && env < 1e-6) break;
            if (t < mode.Attack) env *= 0.5 - 0.5 * Math.Cos(Math.PI * t / mode.Attack);
            if (s >= dampAt)
            {
                var u = (s - dampAt) / dampLength;
                if (u >= 1) break;
                env *= Damper(u);
            }
            var phase = glide == 0 ? w * t : w * (t - glide * glideTau * (1 - Math.Exp(-t / glideTau)));
            buf[s] += gain * mode.Amp * env * Math.Sin(phase);
        }
    }

    // A mallet's contact noise: 6 ms of band-passed noise under a Hann window (seeded, so renders repeat).
    private static void AddKnock(double[] buf, double start, double gain, double centerHz, int seed)
    {
        var length = (int)(0.006 * SampleRate);
        var rng = new Rng((ulong)seed * 7919 + 17);
        var noise = new double[length];
        for (var i = 0; i < length; i++) noise[i] = rng.Next() * 2 - 1;
        BandPass(noise, centerHz, 0.8);
        LowPass(noise, 3500);
        var first = (int)Math.Ceiling(start);
        for (var i = 0; i < length; i++)
        {
            var s = first + i;
            if (s >= 0 && s < buf.Length)
                buf[s] += gain * noise[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / length));
        }
    }

    // ---- The room ----------------------------------------------------------------------------------------------

    // A small wooden room: six early reflections, then an 8-line feedback delay network (Hadamard feedback,
    // prime delays of 17-38 ms, one-pole damping so high frequencies die first), fed through four allpass
    // diffusers. The lines are fed and tapped with irregular signs: an alternating pattern is itself a row of
    // the Hadamard matrix and would excite only a regular subset of the room's modes (a metallic ring).
    // RT60 is about 0.4 s; the wet signal is kept between 250 Hz and 4.5 kHz.
    private static double[] Room(double[] x)
    {
        const double rt60 = 0.42, damping = 0.30, input = 0.35, output = 0.35, diffusion = 0.5;
        const int predelay = 265;   // 6 ms
        int[] delays = [751, 887, 1009, 1153, 1291, 1433, 1597, 1693];
        double[] inSign = [1, -1, -1, 1, 1, 1, -1, -1], outSign = [1, 1, -1, 1, -1, -1, 1, -1];
        int[] diffusers = [142, 107, 379, 277];
        (int Delay, double Gain)[] early = [(190, 0.42), (313, -0.34), (481, 0.28), (673, -0.23), (877, 0.18), (1109, -0.14)];

        var lines = delays.Select(d => new double[d]).ToArray();
        var feedback = delays.Select(d => Math.Pow(10, -3.0 * d / (rt60 * SampleRate))).ToArray();
        var allpasses = diffusers.Select(d => new double[d]).ToArray();
        var pos = new int[delays.Length];
        var apPos = new int[diffusers.Length];
        var lp = new double[delays.Length];
        var v = new double[delays.Length];
        var y = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            var u = i >= predelay ? x[i - predelay] : 0;
            double reflections = 0;
            foreach (var (delay, gain) in early)
                if (i - predelay - delay >= 0) reflections += gain * x[i - predelay - delay];

            for (var a = 0; a < allpasses.Length; a++)
            {
                var line = allpasses[a];
                var delayed = line[apPos[a]];
                var o = -diffusion * u + delayed;
                line[apPos[a]] = u + diffusion * o;
                apPos[a] = (apPos[a] + 1) % line.Length;
                u = o;
            }

            double tail = 0;
            for (var k = 0; k < lines.Length; k++)
            {
                var s = lines[k][pos[k]];
                tail += outSign[k] * s;
                lp[k] = (1 - damping) * s + damping * lp[k];
                v[k] = lp[k] * feedback[k];
            }
            Hadamard8(v);
            for (var k = 0; k < lines.Length; k++)
            {
                lines[k][pos[k]] = v[k] + inSign[k] * u * input;
                pos[k] = (pos[k] + 1) % lines[k].Length;
            }
            y[i] = reflections + tail * output;
        }
        HighPass(y, 250);
        LowPass(y, 4500);
        return y;
    }

    private static void Hadamard8(double[] v)
    {
        for (var h = 1; h < 8; h <<= 1)
            for (var i = 0; i < 8; i += h << 1)
                for (var j = i; j < i + h; j++)
                    (v[j], v[j + h]) = (v[j] + v[j + h], v[j] - v[j + h]);
        for (var k = 0; k < 8; k++) v[k] *= 0.35355339059327373;   // 1/sqrt(8): lossless mixing
    }

    // ---- Filters, levels -------------------------------------------------------------------------------------

    private static void LowPass(double[] x, double f0, double q = 0.7071)
    {
        var (cos, alpha) = Coefficients(f0, q);
        Biquad(x, (1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    private static void HighPass(double[] x, double f0, double q = 0.7071)
    {
        var (cos, alpha) = Coefficients(f0, q);
        Biquad(x, (1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    private static void BandPass(double[] x, double f0, double q)
    {
        var (cos, alpha) = Coefficients(f0, q);
        Biquad(x, alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    private static (double Cos, double Alpha) Coefficients(double f0, double q)
    {
        var w0 = 2 * Math.PI * f0 / SampleRate;
        return (Math.Cos(w0), Math.Sin(w0) / (2 * q));
    }

    // RBJ biquad, transposed direct form II.
    private static void Biquad(double[] x, double b0, double b1, double b2, double a0, double a1, double a2)
    {
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
        double z1 = 0, z2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var input = x[i];
            var output = b0 * input + z1;
            z1 = b1 * input - a1 * output + z2;
            z2 = b2 * input - a2 * output;
            x[i] = output;
        }
    }

    private static void Normalize(double[] x, double peak)
    {
        var max = x.Length == 0 ? 0 : x.Max(Math.Abs);
        if (max <= 0) return;
        var gain = peak / max;
        for (var i = 0; i < x.Length; i++) x[i] *= gain;
    }

    // Raised-cosine fade-in; the fade-out is the damper (lengths in samples).
    private static void Fade(double[] x, int fadeIn, int fadeOut)
    {
        fadeIn = Math.Min(fadeIn, x.Length / 2);
        fadeOut = Math.Min(fadeOut, x.Length / 2);
        for (var i = 0; i < fadeIn; i++)
            x[i] *= 0.5 - 0.5 * Math.Cos(Math.PI * i / fadeIn);
        for (var i = 0; i < fadeOut; i++)
            x[x.Length - fadeOut + i] *= Damper((i + 1.0) / fadeOut);
    }

    // A hand settling on the bar: the decay steepens smoothly (the loss in dB grows with u², so the damper eases in
    // without a kink that would smear the spectrum) to -40 dB, and the last 15 % rounds off to exact silence.
    // u runs 0..1 over the damping time.
    private static double Damper(double u) =>
        Math.Pow(10, -2.0 * u * u) * (u < 0.85 ? 1 : 0.5 + 0.5 * Math.Cos(Math.PI * Math.Min(1, (u - 0.85) / 0.15)));

    // Note name ("F#5", "Bb4") to Hz: A4 = 440, equal temperament.
    private static double Hz(string note)
    {
        var semitone = "C D EF G A B".IndexOf(note[0]);
        var rest = note[1..];
        if (rest[0] == '#') { semitone++; rest = rest[1..]; }
        else if (rest[0] == 'b') { semitone--; rest = rest[1..]; }
        var midi = 12 * (int.Parse(rest) + 1) + semitone;
        return 440.0 * Math.Pow(2, (midi - 69) / 12.0);
    }

    // Small timing and velocity variations, as two human hands would play (seeded, so renders repeat).
    private static Hit[] Humanize(IEnumerable<Hit> hits, int seed, double ms, double velocity)
    {
        var rng = new Rng((ulong)seed);
        return hits.Select(h => h with
        {
            Time = Math.Max(0.008, h.Time + rng.Range(-ms, ms) / 1000),
            Velocity = Math.Clamp(h.Velocity * (1 + rng.Range(-velocity, velocity)), 0.05, 1),
        }).ToArray();
    }

    private enum Voice { Marimba, Kalimba, Wood, Bubble }

    private sealed record Hit(double Time, Voice Voice, double Freq, double Velocity)
    {
        public double Decay { get; init; } = 1.0;      // scales the decay times
        public double Hardness { get; init; } = 0.35;  // 0 = soft yarn mallet / thumb flesh, 1 = hard
        public double Ring { get; init; } = 1.0;       // marimba: weight of the resonator's long ring
    }

    private readonly record struct Mode(double Freq, double Amp, double TauFast, double TauSlow, double SlowMix, double Attack);

    private sealed record Tone(string Key, double Seconds, Hit[] Hits)
    {
        public bool Loop { get; init; }
        public double DampAt { get; init; } = double.PositiveInfinity;   // loops: the bars are damped here (cycle time)
        public double DampTime { get; init; } = 0.35;
        public double FadeOut { get; init; } = 0.15;                     // one-shots
    }

    // xorshift64*: tiny, deterministic.
    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL;

        public double Next()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return ((_state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53));
        }

        public double Range(double min, double max) => min + (max - min) * Next();
    }
}
