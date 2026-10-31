// SkyMessage tones: renders the three sound packs and writes every sound file the apps play.
//
//   dotnet run --project tools/tones                      regenerate everything in the repository (needs ffmpeg + libvorbis)
//   dotnet run --project tools/tones -- --out DIR         write the same tree under DIR instead, to compare
//   dotnet run --project tools/tones -- --masters DIR     also keep the 44.1 kHz float WAV masters (DIR/<pack>/<key>.wav)
//                                                         and the strike times (DIR/onsets.json)
//   python3 tools/tones/analyze.py                        independent QA: renders into a temp dir, measures what the apps
//                                                         decode, and checks the repository holds exactly those files
//
// Every sound is our own synthesis from first principles (Packs/*.cs): no samples, no third-party sound assets,
// nothing modelled on another phone's or messenger's sounds. ffmpeg only encodes.
//
// Per sound: render (44.1 kHz mono) -> set its loudness: the BS.1770 K-weighted level of its loudest 200 ms goes to the
// target of its group, the same in every pack, with the true peak kept at or under -1.5 dBTP -> resample to 48 kHz
// (windowed sinc; a loop is resampled as a loop, so its seam stays seamless) -> Ogg Vorbis, the same file twice:
//   src/Client/SkyMessage.App/Resources/Raw/tone_<pack>_<key>.ogg          Android res/raw, notification channels
//   src/Client/SkyMessage.Client.UI/wwwroot/sounds/<pack>/<key>.ogg        in-app playback (js/sounds.js)
// plus src/Client/SkyMessage.App/Resources/Raw/tone_<pack>_<key>.caf (IMA4, iOS/Mac notification sounds) and
// wwwroot/sounds/sounds.json (lengths, loops, content hashes). Then the checks: decoded length and peak, loudness after encoding,
// clicks away from strikes, loop seams, energy above 6 kHz. Any failure exits with code 1.

using System.Globalization;
using System.Text.Json;
using SkyMessage.Tones.Packs;
using SkyMessage.Tones.Pipeline;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const int RenderRate = 44100;
const int PlayRate = 48000;
const double CeilingDbtp = -1.5;

string? outRoot = null, masters = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outRoot = Path.GetFullPath(args[++i]); break;
        case "--masters": masters = Path.GetFullPath(args[++i]); break;
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}. Usage: dotnet run --project tools/tones [-- --out DIR] [--masters DIR]");
            return 2;
    }
}

var repo = FindRepoRoot();
var root = outRoot ?? repo;
var rawDir = Path.Combine(root, "src", "Client", "SkyMessage.App", "Resources", "Raw");
var webDir = Path.Combine(root, "src", "Client", "SkyMessage.Client.UI", "wwwroot", "sounds");
var temp = Directory.CreateTempSubdirectory("sky-tones-");
Directory.CreateDirectory(rawDir);
Directory.CreateDirectory(webDir);
Ffmpeg.EnsureAvailable();

var packs = new (string Name, Func<IReadOnlyList<RenderedTone>> Render)[]
{
    (WoodPack.Name, WoodPack.Render),
    (BellsPack.Name, () => BellsPack.Render(Console.Out)),
    (PianoPack.Name, PianoPack.Render),
};

var failures = new List<string>();
var manifest = new SortedDictionary<string, SortedDictionary<string, object>>(StringComparer.Ordinal);
var written = new HashSet<string>(StringComparer.Ordinal);
var loudness = new Dictionary<(string Pack, string Key), double>();
var onsets = new SortedDictionary<string, SortedDictionary<string, IReadOnlyList<double>>>(StringComparer.Ordinal);

Console.WriteLine($"{"sound",-16} {"len",6} {"loop",4} {"gain",6} {"tpeak",6} {"S200",6} {"M400",6} {"int",6} {">6k%",7} {"ogg KB",6} {"caf KB",6} {"oggPk",6}  checks");
foreach (var (pack, render) in packs)
{
    var tones = render();
    var keys = tones.Select(t => t.Key).ToArray();
    if (!keys.SequenceEqual(ToneKeys.All))
        failures.Add($"{pack}: renders [{string.Join(", ", keys)}], the catalog expects [{string.Join(", ", ToneKeys.All)}]");
    var entries = new SortedDictionary<string, object>(StringComparer.Ordinal);
    onsets[pack] = new SortedDictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
    foreach (var tone in tones)
    {
        var name = $"{pack}/{tone.Key}";
        onsets[pack][tone.Key] = tone.Onsets;
        var problems = new List<string>();

        // Loudness: one target per group, true peak under the ceiling.
        var target = TargetOf(tone.Key);
        var measured = Audio.MaxLoudness(tone.Samples, RenderRate, tone.Loop, 200);
        var gainDb = target - measured;
        var truePeak = Audio.Db(Audio.TruePeak(tone.Samples, RenderRate, tone.Loop)) + gainDb;
        if (truePeak > CeilingDbtp)
        {
            // Within 0.1 LU is as good as on target; more means the arrangement must change (longer notes, a firmer touch).
            if (truePeak - CeilingDbtp > 0.1) problems.Add($"misses its loudness target by {truePeak - CeilingDbtp:0.00} LU (true peak)");
            gainDb -= truePeak - CeilingDbtp;
            truePeak = CeilingDbtp;
        }
        var gain = Math.Pow(10, gainDb / 20);
        var master = tone.Samples.Select(s => s * gain).ToArray();

        // Technical checks on the master.
        var clicks = Audio.Clicks(master, RenderRate, tone.Loop, tone.Onsets);
        if (clicks.Count > 0) problems.Add($"HF bursts away from strikes at {string.Join(", ", clicks.Take(4).Select(t => $"{t:0.000}s"))}");
        var above6k = Audio.EnergyAbove(master, RenderRate, 6000);
        if (above6k > 0.001) problems.Add($"{above6k:P2} of the energy above 6 kHz");
        if (tone.Loop)
        {
            var (step, before, after) = Audio.Seam(master, RenderRate);
            if (step > 1e-3 || Math.Max(before, after) > -45) problems.Add($"loop seam: step {step:0.00000}, {before:0} / {after:0} dBFS");
        }
        else if (Math.Abs(master[0]) > 1e-4 || Math.Abs(master[^1]) > 1e-4)
        {
            problems.Add("does not start and end in silence");
        }

        // Files.
        var play = Audio.Resample(master, RenderRate, PlayRate, circular: tone.Loop);
        var wav48 = Path.Combine(temp.FullName, $"{pack}_{tone.Key}_48k.wav");
        var wav44 = Path.Combine(temp.FullName, $"{pack}_{tone.Key}_44k.wav");
        Ffmpeg.WriteFloatWav(wav48, play, PlayRate);
        Ffmpeg.WriteFloatWav(wav44, master, RenderRate);
        if (masters is not null)
        {
            Directory.CreateDirectory(Path.Combine(masters, pack));
            File.Copy(wav44, Path.Combine(masters, pack, $"{tone.Key}.wav"), overwrite: true);
        }
        var ogg = Path.Combine(temp.FullName, $"{pack}_{tone.Key}.ogg");
        Ffmpeg.EncodeVorbis(wav48, ogg, tone.Loop);
        var raw = Path.Combine(rawDir, $"tone_{pack}_{tone.Key}.ogg");
        var web = Path.Combine(webDir, pack, $"{tone.Key}.ogg");
        var caf = Path.Combine(rawDir, $"tone_{pack}_{tone.Key}.caf");
        Directory.CreateDirectory(Path.GetDirectoryName(web)!);
        File.Copy(ogg, raw, overwrite: true);
        File.Copy(ogg, web, overwrite: true);
        Ffmpeg.EncodeCaf(wav44, caf);
        written.UnionWith([raw, web, caf]);

        // What a player gets back. A loop must decode to exactly one cycle. Vorbis's end trimming may leave a one-shot
        // a block longer or shorter; that is fine while the difference is silence (the end is faded out).
        var decoded = Ffmpeg.Decode(ogg);
        if (tone.Loop && decoded.Length != play.Length) problems.Add($"decodes to {decoded.Length} samples, not {play.Length}");
        if (!tone.Loop && LengthProblem(decoded, play) is { } lengthProblem) problems.Add(lengthProblem);
        var decodedPeak = Audio.Db(Audio.Peak(decoded));
        if (decodedPeak > -0.5) problems.Add($"decoded peak {decodedPeak:0.00} dBFS");
        var level = measured + gainDb;
        var decodedLoudness = Audio.MaxLoudness(decoded, PlayRate, tone.Loop, 200);
        if (Math.Abs(decodedLoudness - level) > 0.35) problems.Add($"loudness after encoding {decodedLoudness:0.00} LUFS, not {level:0.00}");
        if (tone.Loop && decoded.Length == play.Length && Math.Abs(decoded[0] - decoded[^1]) > 2e-3)
            problems.Add($"decoded loop seam step {Math.Abs(decoded[0] - decoded[^1]):0.0000}");
        loudness[(pack, tone.Key)] = level;
        // The hash versions the file's URL (sounds.js fetches <key>.ogg?v=<hash>): names stay fixed, caches stay right.
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ogg)))[..12];
        entries[tone.Key] = tone.Loop ? new { samples = play.Length, loop = true, hash } : new { samples = play.Length, hash };
        Console.WriteLine($"{name,-16} {master.Length / (double)RenderRate,6:0.00} {(tone.Loop ? "yes" : ""),4} {gainDb,6:+0.00;-0.00;0.00} {truePeak,6:0.00} {level,6:0.00} " +
                          $"{Audio.MaxLoudness(master, RenderRate, tone.Loop, 400),6:0.00} {Audio.IntegratedLoudness(master, RenderRate, tone.Loop),6:0.00} " +
                          $"{above6k * 100,7:0.0000} {new FileInfo(raw).Length / 1024.0,6:0.0} {new FileInfo(caf).Length / 1024.0,6:0.0} {decodedPeak,6:0.00}  " +
                          (problems.Count == 0 ? "ok" : string.Join("; ", problems)));
        failures.AddRange(problems.Select(p => $"{name}: {p}"));
    }
    manifest[pack] = entries;
}

// Loudness agreement: one key across the packs, and each group within a pack (busy sits 1 LU under the group by design).
var perKey = ToneKeys.All.Max(k => packs.Max(p => loudness[(p.Name, k)]) - packs.Min(p => loudness[(p.Name, k)]));
Console.WriteLine($"loudness spread of one key across packs: at most {perKey:0.00} LU");
foreach (var group in new[] { ToneKeys.MessageTones, ToneKeys.Ringtones, ToneKeys.CallProgress })
{
    var values = packs.SelectMany(p => group.Select(k => loudness[(p.Name, k)] - TargetOf(k))).ToArray();
    Console.WriteLine($"  {string.Join('/', group),-22} off target by at most {values.Select(Math.Abs).Max():0.00} LU");
}

var manifestPath = Path.Combine(webDir, "sounds.json");
File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { version = 1, rate = PlayRate, packs = manifest },
    new JsonSerializerOptions { WriteIndented = true }) + "\n");
written.Add(manifestPath);
// The strike times, for analyze.py's click check (it tells a mallet's attack from a defect by them).
if (masters is not null)
    File.WriteAllText(Path.Combine(masters, "onsets.json"), JsonSerializer.Serialize(new { rate = RenderRate, onsets }) + "\n");

// Files of sounds that no longer exist, and the single-pack WAVs of earlier versions.
foreach (var stale in Directory.EnumerateFiles(rawDir, "tone_*").Concat(Directory.EnumerateFiles(webDir, "*", SearchOption.AllDirectories)))
{
    if (written.Contains(stale)) continue;
    File.Delete(stale);
    Console.WriteLine($"removed {Path.GetRelativePath(root, stale)}");
}
foreach (var dir in Directory.EnumerateDirectories(webDir).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
    Directory.Delete(dir);
temp.Delete(recursive: true);

if (failures.Count == 0)
{
    Console.WriteLine($"All {packs.Length * ToneKeys.All.Length} sounds written and checked.");
    return 0;
}
Console.Error.WriteLine($"{failures.Count} problem(s):");
foreach (var failure in failures) Console.Error.WriteLine("  " + failure);
return 1;

// Loudness targets (LUFS, K-weighted level of the loudest 200 ms). Every pack meets the same level per group, so
// switching a tone or a pack never jumps in volume. Ringtones sit a little under the message tones (they repeat);
// the busy signal is four short strokes, which the 200 ms window measures one at a time.
static double TargetOf(string key) => key switch
{
    "chime" or "pop" or "glass" => -11.0,
    "classic" or "soft" or "bright" => -11.2,
    "busy" => -12.0,
    _ => -11.0,
};

// A one-shot may decode up to 2048 samples (43 ms) longer or shorter than it was encoded, as long as what is added or
// lost is silence (under -60 dBFS).
static string? LengthProblem(double[] decoded, double[] encoded)
{
    var difference = decoded.Length - encoded.Length;
    if (difference == 0) return null;
    if (Math.Abs(difference) > 2048) return $"decodes to {decoded.Length} samples, not {encoded.Length}";
    var rest = difference > 0 ? decoded[encoded.Length..] : encoded[decoded.Length..];
    var level = Audio.Db(Audio.Peak(rest));
    return level > -60 ? $"decodes {difference:+0;-0} samples off, and that part is not silent ({level:0} dBFS)" : null;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkyMessage.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the SkyMessage repository.");
}
