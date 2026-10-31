namespace SkyMessage.Tones.Pipeline;

/// <summary>
/// Measurements and conversions shared by every pack: BS.1770 loudness, true peak, band-limited resampling, and the
/// technical checks (clicks, loop seams, high-frequency energy).
/// </summary>
internal static class Audio
{
    public static double Db(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-12));

    public static double Peak(double[] x) => x.Length == 0 ? 0 : x.Max(Math.Abs);

    // ---- Loudness (ITU-R BS.1770) -----------------------------------------------------------------------------------

    /// <summary>
    /// K-weighted loudness of the loudest <paramref name="windowMs"/> of the sound (LUFS). Loops are measured in their
    /// steady state (windows may run across the seam into the next cycle); one-shots as if silence followed them.
    /// 400 ms is the EBU "momentary" loudness; 200 ms is closer to how long the ear integrates a short tone, so it does
    /// not penalize a quick "pop" against a longer chime.
    /// </summary>
    public static double MaxLoudness(double[] x, int rate, bool loop, double windowMs)
    {
        var window = (int)(rate * windowMs / 1000);
        var signal = loop ? Concat(x, x, x) : Concat(x, new double[window]);
        var k = KWeight(signal, rate);
        var first = loop ? x.Length : 0;
        var last = loop ? 2 * x.Length : x.Length;   // window starts
        var prefix = new double[k.Length + 1];
        for (var i = 0; i < k.Length; i++) prefix[i + 1] = prefix[i] + k[i] * k[i];
        double best = 0;
        for (var s = first; s < last && s + window <= k.Length; s++)
            best = Math.Max(best, (prefix[s + window] - prefix[s]) / window);
        return -0.691 + 10 * Math.Log10(Math.Max(best, 1e-24));
    }

    /// <summary>Gated integrated loudness (LUFS) over 400 ms blocks with 75 % overlap (loops: one steady-state cycle).</summary>
    public static double IntegratedLoudness(double[] x, int rate, bool loop)
    {
        var block = (int)(0.4 * rate);
        var hop = block / 4;
        var signal = loop ? Concat(x, x, x) : Concat(x, new double[block]);
        var k = KWeight(signal, rate);
        var first = loop ? x.Length : 0;
        var last = loop ? 2 * x.Length : x.Length;
        var blocks = new List<double>();
        for (var s = first; s < last && s + block <= k.Length; s += hop)
        {
            double sum = 0;
            for (var i = s; i < s + block; i++) sum += k[i] * k[i];
            blocks.Add(sum / block);
        }
        static double Lufs(double ms) => -0.691 + 10 * Math.Log10(Math.Max(ms, 1e-24));
        var gated = blocks.Where(b => Lufs(b) > -70).ToList();
        if (gated.Count == 0) return -120;
        var relative = Lufs(gated.Average()) - 10;
        var kept = gated.Where(b => Lufs(b) > relative).ToList();
        return Lufs(kept.Average());
    }

    /// <summary>The BS.1770 pre-filter (high shelf) and RLB high-pass, derived for any sample rate.</summary>
    public static double[] KWeight(double[] x, int rate)
    {
        const double ShelfHz = 1681.974450955533, ShelfDb = 3.999843853973347, ShelfQ = 0.7071752369554196;
        const double HighPassHz = 38.13547087602444, HighPassQ = 0.5003270373238773;
        var k = Math.Tan(Math.PI * ShelfHz / rate);
        var vh = Math.Pow(10, ShelfDb / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / ShelfQ + k * k;
        var y = Biquad(x, (vh + vb * k / ShelfQ + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / ShelfQ + k * k) / a0,
            2 * (k * k - 1) / a0, (1 - k / ShelfQ + k * k) / a0);
        k = Math.Tan(Math.PI * HighPassHz / rate);
        a0 = 1 + k / HighPassQ + k * k;
        return Biquad(y, 1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / HighPassQ + k * k) / a0);
    }

    private static double[] Biquad(double[] x, double b0, double b1, double b2, double a1, double a2)
    {
        var y = new double[x.Length];
        double z1 = 0, z2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var o = b0 * x[i] + z1;
            z1 = b1 * x[i] - a1 * o + z2;
            z2 = b2 * x[i] - a2 * o;
            y[i] = o;
        }
        return y;
    }

    /// <summary>True peak: the sample peak of a 4x band-limited oversampling (what a DAC or a lossy decoder can reach).</summary>
    public static double TruePeak(double[] x, int rate, bool loop) => Peak(Resample(x, rate, rate * 4, loop));

    // ---- Resampling ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Band-limited rational resampling with a Kaiser-windowed sinc (cutoff at 92 % of the lower Nyquist frequency,
    /// stopband about -90 dB). <paramref name="circular"/> treats the input as one period of a loop, so the output is
    /// exactly one period too and its seam stays seamless; otherwise the input is zero outside its ends.
    /// </summary>
    public static double[] Resample(double[] x, int fromRate, int toRate, bool circular)
    {
        if (fromRate == toRate) return (double[])x.Clone();
        var g = Gcd(fromRate, toRate);
        int up = toRate / g, down = fromRate / g;
        if ((long)x.Length * up % down != 0)
            throw new InvalidOperationException($"{x.Length} samples at {fromRate} Hz are not a whole number of samples at {toRate} Hz.");
        var length = (int)((long)x.Length * up / down);
        var cutoff = 0.92 * Math.Min(1.0, (double)toRate / fromRate);   // in units of the input Nyquist frequency
        const double Beta = 9.0;
        var half = (int)Math.Ceiling(24 / cutoff);                        // taps each side, in input samples
        var table = new double[up, 2 * half];
        var i0Beta = BesselI0(Beta);
        for (var p = 0; p < up; p++)
        {
            var frac = p / (double)up;
            for (var j = 0; j < 2 * half; j++)
            {
                var t = j - half + 1 - frac;   // distance from the output position to input sample (i0 + j - half + 1)
                var u = t / half;
                var window = Math.Abs(u) >= 1 ? 0 : BesselI0(Beta * Math.Sqrt(1 - u * u)) / i0Beta;
                table[p, j] = cutoff * Sinc(cutoff * t) * window;
            }
        }
        var y = new double[length];
        var n = x.Length;
        for (var o = 0; o < length; o++)
        {
            var position = (long)o * down;
            var i0 = (int)(position / up);
            var p = (int)(position % up);
            double sum = 0;
            for (var j = 0; j < 2 * half; j++)
            {
                var index = i0 + j - half + 1;
                double v;
                if (circular) v = x[((index % n) + n) % n];
                else if (index < 0 || index >= n) continue;
                else v = x[index];
                sum += v * table[p, j];
            }
            y[o] = sum;
        }
        return y;
    }

    private static double Sinc(double t) => Math.Abs(t) < 1e-12 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (var k = 1; k < 64; k++)
        {
            term *= half / k * (half / k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    // ---- Checks ----------------------------------------------------------------------------------------------------

    /// <summary>Share of the energy above <paramref name="hz"/> (0..1).</summary>
    public static double EnergyAbove(double[] x, int rate, double hz)
    {
        var (re, im) = Spectrum(x);
        double total = 0, above = 0;
        for (var k = 0; k <= re.Length / 2; k++)
        {
            var e = re[k] * re[k] + im[k] * im[k];
            total += e;
            if (k * (double)rate / re.Length >= hz) above += e;
        }
        return total <= 0 ? 0 : above / total;
    }

    /// <summary>
    /// Clicks: a sudden rise of the energy above 7 kHz (1 ms frames, 10 dB over the three frames before, above
    /// -92 dBFS) that no strike explains. Everything synthesized here is band-limited and every envelope is smooth, so
    /// high-frequency energy only appears with a strike's attack (mallet knock, hammer felt, tine) and then decays; a
    /// jump anywhere else is a discontinuity (a step, a corner, a cut). Returns the times (s) of the jumps.
    /// </summary>
    public static IReadOnlyList<double> Clicks(double[] x, int rate, bool loop, IReadOnlyList<double> onsets)
    {
        var signal = loop ? Concat(x, x) : Concat(new double[rate / 100], x, new double[rate / 10]);
        var offset = loop ? 0 : rate / 100;
        var hf = HighPass(signal, rate, 7000);
        var frame = rate / 1000;
        var count = signal.Length / frame;
        var level = new double[count];
        for (var f = 0; f < count; f++)
        {
            double sum = 0;
            for (var i = f * frame; i < (f + 1) * frame; i++) sum += hf[i] * hf[i];
            level[f] = 10 * Math.Log10(Math.Max(sum / frame, 1e-24));
        }
        var cycle = x.Length / (double)rate;
        var strikes = loop ? onsets.Concat(onsets.Select(t => t + cycle)).ToArray() : onsets.ToArray();
        var found = new List<double>();
        for (var f = 3; f < count; f++)
        {
            if (level[f] < -92 || level[f] < Math.Max(level[f - 1], Math.Max(level[f - 2], level[f - 3])) + 10) continue;
            var t = (f * frame - offset) / (double)rate;
            if (strikes.Any(s => t >= s - 0.003 && t <= s + 0.012)) continue;
            found.Add(loop ? t % cycle : t);
        }
        return found;
    }

    /// <summary>Loop seam: the step from the last sample to the first, and the level (dBFS) of 10 ms on either side.</summary>
    public static (double Step, double Before, double After) Seam(double[] x, int rate)
    {
        var w = rate / 100;
        double before = 0, after = 0;
        for (var i = 0; i < w; i++)
        {
            before += x[x.Length - 1 - i] * x[x.Length - 1 - i];
            after += x[i] * x[i];
        }
        return (Math.Abs(x[0] - x[^1]), Db(Math.Sqrt(before / w)), Db(Math.Sqrt(after / w)));
    }

    private static double[] HighPass(double[] x, int rate, double hz)
    {
        var (re, im) = Spectrum(x);
        var n = re.Length;
        for (var k = 0; k < n; k++)
        {
            var f = Math.Min(k, n - k) * (double)rate / n;
            var w = Math.Clamp((f - hz * 0.85) / (hz * 0.15), 0, 1);
            re[k] *= w;
            im[k] *= w;
        }
        Fft(re, im, inverse: true);
        return re[..x.Length];
    }

    private static (double[] Re, double[] Im) Spectrum(double[] x)
    {
        var size = 1;
        while (size < x.Length) size <<= 1;
        var re = new double[size];
        var im = new double[size];
        Array.Copy(x, re, x.Length);
        Fft(re, im, inverse: false);
        return (re, im);
    }

    private static void Fft(double[] re, double[] im, bool inverse)
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
        if (!inverse) return;
        for (var i = 0; i < n; i++)
        {
            re[i] /= n;
            im[i] /= n;
        }
    }

    private static double[] Concat(params double[][] parts)
    {
        var y = new double[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, y, offset, p.Length);
            offset += p.Length;
        }
        return y;
    }
}
