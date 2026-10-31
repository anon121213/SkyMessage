#!/usr/bin/env python3
"""Technical QA of SkyMessage's sound packs, independent of the renderer's own checks (numpy + ffmpeg only).

  python3 tools/tones/analyze.py                  render into a temp dir (dotnet run --project tools/tones), analyze
                                                  it, and check the repository holds exactly those files
  python3 tools/tones/analyze.py --tree DIR [--masters DIR]
                                                  analyze a tree written by "dotnet run --project tools/tones -- --out
                                                  DIR --masters DIR/masters" (or the repository itself, --tree .)

What it measures, per sound, on what a player decodes (Android's Ogg Vorbis, the in-app MP3, also the iOS CAF copies)
and on the 44.1 kHz masters:
  clipping   sample peak and true peak (4x band-limited oversampling): decoded <= -0.5 dBTP, masters <= -1.4 dBTP;
             no flat-topped peaks at any level
  DC         mean <= -80 dBFS and energy under 20 Hz <= -60 dB of the total
  clicks     a jump of the energy above 7 kHz (1 ms frames, +10 dB over the 3 frames before) that no strike explains
             (strike times come from the renderer's onsets.json; loops are checked across the seam; on decoded files
             a codec's pre-echo up to 13 ms before a strike belongs to that strike)
  seams      loops: the step from the last sample to the first, and the level 10 ms either side of it (<= -45 dBFS)
  > 6 kHz    share of the energy above 6 kHz <= 0.1 % (nothing shrill or harsh)
  aliasing   every renderer keeps its partials under 12 kHz, so any spectral line above 12.5 kHz (> -90 dB under the
             strongest one) is a folded partial; broadband noise up there (a felt hammer's) stays under -60 dB
  loudness   BS.1770 K-weighted level of the loudest 200 ms on target for its group in every pack (+-0.35 LU), and one
             key at most 0.5 LU apart across the packs
  files      the Ogg and the MP3 of a sound decode to the same signal; loops decode to exactly the cycle in
             sounds.json, one-shots within 2048 samples of it with only silence in the difference; message tones
             <= 1 s; sounds.json's hash is the MP3's
Exit code 1 on any failure.
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

import numpy as np

KEYS = ["chime", "pop", "glass", "classic", "soft", "bright", "ringback", "busy", "end"]
TARGET = {"chime": -11.0, "pop": -11.0, "glass": -11.0, "classic": -11.2, "soft": -11.2, "bright": -11.2,
          "ringback": -11.0, "busy": -12.0, "end": -11.0}
MESSAGE_TONES = {"chime", "pop", "glass"}
RAW = os.path.join("src", "Client", "SkyMessage.App", "Resources", "Raw")
WEB = os.path.join("src", "Client", "SkyMessage.Client.UI", "wwwroot", "sounds")
REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))


# ---- I/O ---------------------------------------------------------------------------------------------------------------

def decode(path, rate=None):
    """Mono float samples and the rate, decoded by ffmpeg the way a player would."""
    args = ["ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-i", path, "-f", "f32le", "-ac", "1"]
    if rate:
        args += ["-ar", str(rate)]
    raw = subprocess.run(args + ["-"], capture_output=True, check=True).stdout
    if rate is None:
        probe = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate",
                                "-of", "default=nw=1:nk=1", path], capture_output=True, check=True, text=True).stdout
        rate = int(probe.strip())
    return np.frombuffer(raw, dtype="<f4").astype(np.float64), rate


def db(v, floor=1e-12):
    return float(10 * np.log10(max(float(v), floor)))


def amp_db(v):
    return float(20 * np.log10(max(float(v), 1e-12)))


# ---- Measurements --------------------------------------------------------------------------------------------------------

def true_peak(x, loop):
    """Peak of a 4x band-limited oversampling (FFT zero-stuffing; one-shots padded so nothing wraps around)."""
    y = x if loop else np.concatenate([x, np.zeros(1024)])
    spectrum = np.fft.rfft(y)
    up = np.fft.irfft(spectrum, 4 * len(y)) * 4
    return float(np.abs(up).max())


def biquad_response(b, a, w):
    z = np.exp(-1j * w)
    return (b[0] + b[1] * z + b[2] * z * z) / (a[0] + a[1] * z + a[2] * z * z)


def k_weight(x, rate, loop):
    """BS.1770 K-weighting (pre-filter shelf + RLB high-pass, derived for any rate), applied in the frequency domain:
    circular for a loop (its steady state), on a zero-padded copy for a one-shot (the filters ring out in the pad)."""
    shelf_hz, shelf_db, shelf_q = 1681.974450955533, 3.999843853973347, 0.7071752369554196
    hp_hz, hp_q = 38.13547087602444, 0.5003270373238773
    k = np.tan(np.pi * shelf_hz / rate)
    vh = 10 ** (shelf_db / 20)
    vb = vh ** 0.4996667741545416
    a0 = 1 + k / shelf_q + k * k
    shelf = ([(vh + vb * k / shelf_q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / shelf_q + k * k) / a0],
             [1, 2 * (k * k - 1) / a0, (1 - k / shelf_q + k * k) / a0])
    k = np.tan(np.pi * hp_hz / rate)
    a0 = 1 + k / hp_q + k * k
    highpass = ([1, -2, 1], [1, 2 * (k * k - 1) / a0, (1 - k / hp_q + k * k) / a0])
    y = x if loop else np.concatenate([x, np.zeros(rate // 2)])
    spectrum = np.fft.rfft(y)
    w = 2 * np.pi * np.fft.rfftfreq(len(y))
    spectrum *= biquad_response(*shelf, w) * biquad_response(*highpass, w)
    return np.fft.irfft(spectrum, len(y))


def max_loudness(x, rate, loop, window_ms=200):
    """K-weighted level (LUFS) of the loudest window; a loop's windows run across the seam."""
    k = k_weight(x, rate, loop)
    w = int(rate * window_ms / 1000)
    if loop:
        k = np.concatenate([k, k[:w]])
    c = np.concatenate([[0.0], np.cumsum(k * k)])
    return -0.691 + db(((c[w:] - c[:-w]) / w).max(), 1e-24)


def band_share(x, rate, lo, hi=None):
    p = np.abs(np.fft.rfft(x)) ** 2
    f = np.fft.rfftfreq(len(x), 1 / rate)
    sel = (f >= lo) & (f < (hi if hi else np.inf))
    return float(p[sel].sum() / max(p.sum(), 1e-30))


def tonal_peak_above(x, rate, hz):
    """Strongest spectral line above hz, in dB under the strongest line overall (Hann window)."""
    p = np.abs(np.fft.rfft(x * np.hanning(len(x)))) ** 2
    f = np.fft.rfftfreq(len(x), 1 / rate)
    return db(p[f >= hz].max() / p.max(), 1e-30)


def highpass(x, rate, hz):
    spectrum = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / rate)
    spectrum *= np.clip((f - hz * 0.85) / (hz * 0.15), 0, 1)
    return np.fft.irfft(spectrum, len(x))


def clicks(x, rate, loop, onsets, floor_db, lead=0.003):
    """Times (s) where the energy above 7 kHz jumps by 10 dB within a millisecond away from every strike (from `lead`
    seconds before it to 12 ms after)."""
    pre = 0 if loop else rate // 100
    y = np.concatenate([x, x]) if loop else np.concatenate([np.zeros(pre), x, np.zeros(rate // 10)])
    hf = highpass(y, rate, 7000)
    frame = rate // 1000
    count = len(hf) // frame
    level = 10 * np.log10(np.maximum((hf[:count * frame] ** 2).reshape(count, frame).mean(1), 1e-24))
    cycle = len(x) / rate
    strikes = np.array(list(onsets) + ([t + cycle for t in onsets] if loop else []))
    found = []
    for f in range(3, count):
        if level[f] < floor_db or level[f] < level[f - 3:f].max() + 10:
            continue
        t = (f * frame - pre) / rate
        if len(strikes) and np.any((t >= strikes - lead) & (t <= strikes + 0.012)):
            continue
        found.append(t % cycle if loop else t)
    return found


def analyze(x, rate, loop, onsets, master):
    """Measurements of one sound and the problems found."""
    m = {"len": len(x) / rate, "peak": amp_db(np.abs(x).max()), "tpeak": amp_db(true_peak(x, loop)),
         "dc": amp_db(abs(float(np.mean(x)))), "sub20": db(band_share(x, rate, 0, 20), 1e-30),
         "above6k": band_share(x, rate, 6000), "above12k": db(band_share(x, rate, 12500), 1e-30),
         "tonal12k": tonal_peak_above(x, rate, 12500), "s200": max_loudness(x, rate, loop)}
    problems = []
    ceiling = -1.4 if master else -0.5
    if m["tpeak"] > ceiling:
        problems.append(f"true peak {m['tpeak']:.2f} dBTP over {ceiling}")
    # Hard clipping at any level: three or more samples in a row stuck at the peak.
    top = np.abs(x) >= np.abs(x).max() - 1e-6
    if np.any(top[:-2] & top[1:-1] & top[2:]):
        problems.append("flat-topped (clipped) peaks")
    if m["dc"] > -80 or m["sub20"] > -60:
        problems.append(f"DC: mean {m['dc']:.0f} dBFS, {m['sub20']:.0f} dB under 20 Hz")
    if m["above6k"] > 0.001:
        problems.append(f"{m['above6k'] * 100:.2f} % of the energy above 6 kHz")
    if m["tonal12k"] > -90:
        problems.append(f"a spectral line above 12.5 kHz {m['tonal12k']:.0f} dB under the strongest: a folded partial?")
    if m["above12k"] > -60:
        problems.append(f"noise above 12.5 kHz at {m['above12k']:.0f} dB of the total")
    if onsets is not None:
        # Masters are clean to the last bit; a decoder adds its own faint noise floor, so judge it from -85 dBFS, and
        # a transform codec smears a strike's attack up to one block (MP3: 576 samples, 12 ms) ahead of it (pre-echo,
        # masked by the strike that follows).
        found = clicks(x, rate, loop, onsets, -92 if master else -85, 0.003 if master else 0.013)
        if found:
            problems.append("clicks at " + ", ".join(f"{t:.3f}s" for t in found[:4]))
    if loop:
        w = rate // 100
        step = abs(x[0] - x[-1])
        before, after = amp_db(np.sqrt(np.mean(x[-w:] ** 2))), amp_db(np.sqrt(np.mean(x[:w] ** 2)))
        m["seam"] = max(before, after)
        if step > (1e-3 if master else 2e-3) or m["seam"] > -45:
            problems.append(f"loop seam: step {step:.4f}, {before:.0f} / {after:.0f} dBFS")
    return m, problems


# ---- Driver ----------------------------------------------------------------------------------------------------------------

def render(tmp):
    masters = os.path.join(tmp, "masters")
    print("rendering (dotnet run --project tools/tones) ...", flush=True)
    result = subprocess.run(["dotnet", "run", "--project", os.path.join(REPO, "tools", "tones"), "--", "--out", tmp,
                             "--masters", masters], cwd=REPO, capture_output=True, text=True)
    if result.returncode not in (0, 1):
        sys.exit(f"the renderer failed:\n{result.stdout}\n{result.stderr}")
    return masters


def same_files(tree, failures):
    """The repository must hold exactly what the renderer writes (the encoders are bit-exact for the same ffmpeg)."""
    for sub in (RAW, WEB):
        ours = os.path.join(tree, sub)
        repo = os.path.join(REPO, sub)
        mine = {os.path.relpath(os.path.join(d, f), ours) for d, _, fs in os.walk(ours) for f in fs}
        theirs = {os.path.relpath(os.path.join(d, f), repo) for d, _, fs in os.walk(repo) for f in fs}
        theirs = {f for f in theirs if sub == WEB or os.path.basename(f).startswith("tone_")}
        for missing in sorted(mine - theirs):
            failures.append(f"repository lacks {os.path.join(sub, missing)} (run dotnet run --project tools/tones)")
        for extra in sorted(theirs - mine):
            failures.append(f"repository has stale {os.path.join(sub, extra)}")
        for name in sorted(mine & theirs):
            a, b = os.path.join(ours, name), os.path.join(repo, name)
            with open(a, "rb") as fa, open(b, "rb") as fb:
                if fa.read() == fb.read():
                    continue
            if name.endswith(".json"):
                failures.append(f"{os.path.join(sub, name)} differs from a fresh render")
                continue
            # Another ffmpeg, libvorbis or LAME build encodes differently; the sound must still match.
            x, _ = decode(a, 48000)
            y, _ = decode(b, 48000)
            n = min(len(x), len(y))
            if abs(len(x) - len(y)) > 2048 or amp_db(np.abs(x[:n] - y[:n]).max()) > -40:
                failures.append(f"{os.path.join(sub, name)} sounds different from a fresh render")
            else:
                print(f"note: {os.path.join(sub, name)} is encoded differently (another ffmpeg) but decodes the same")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--tree", help="analyze this already written tree instead of rendering one")
    parser.add_argument("--masters", help="the masters folder of that tree (enables the master and click checks)")
    args = parser.parse_args()

    tmp = None
    if args.tree:
        tree = os.path.abspath(args.tree)
        masters = os.path.abspath(args.masters) if args.masters else None
    else:
        tmp = tempfile.mkdtemp(prefix="sky-tones-qa-")
        tree, masters = tmp, render(tmp)

    failures = []
    with open(os.path.join(tree, WEB, "sounds.json")) as f:
        manifest = json.load(f)
    onsets = None
    if masters and os.path.exists(os.path.join(masters, "onsets.json")):
        with open(os.path.join(masters, "onsets.json")) as f:
            onsets = json.load(f)["onsets"]
    rate = manifest["rate"]
    packs = list(manifest["packs"])
    loud = {}

    print(f"{'sound':16} {'len':>5} {'loop':>4} {'peak':>6} {'tpeak':>6} {'S200':>6} {'dc':>5} {'>6k%':>7} {'>12.5k':>6} "
          f"{'line':>5} {'seam':>5} {'ogg/mp3':>7} {'caf':>6}  problems")
    for pack in packs:
        entries = manifest["packs"][pack]
        if list(entries) != sorted(KEYS) and sorted(entries) != sorted(KEYS):
            failures.append(f"{pack}: keys {sorted(entries)} are not {sorted(KEYS)}")
        for key in KEYS:
            if key not in entries:
                continue
            entry = entries[key]
            loop = bool(entry.get("loop"))
            name = f"{pack}/{key}"
            strikes = onsets[pack][key] if onsets else None
            web = os.path.join(tree, WEB, pack, f"{key}.mp3")
            raw = os.path.join(tree, RAW, f"tone_{pack}_{key}.ogg")
            caf = os.path.join(tree, RAW, f"tone_{pack}_{key}.caf")
            problems = []
            with open(web, "rb") as f:
                if hashlib.sha256(f.read()).hexdigest()[:12] != entry.get("hash"):
                    problems.append("sounds.json's hash is not the MP3's")

            decoded = {}
            expected = entry["samples"]
            for fmt, path in (("ogg", raw), ("mp3", web)):
                x, r = decode(path)
                decoded[fmt] = x
                if r != rate:
                    problems.append(f"{fmt} decodes at {r} Hz, the manifest says {rate}")
                if loop and len(x) != expected:
                    problems.append(f"{fmt} loop decodes to {len(x)} samples, not {expected}")
                elif not loop and len(x) != expected:
                    extra = x[expected:] if len(x) > expected else np.zeros(0)
                    if abs(len(x) - expected) > 2048 or (len(extra) and amp_db(np.abs(extra).max()) > -60):
                        problems.append(f"{fmt} decodes to {len(x)} samples, not {expected}")
                if key in MESSAGE_TONES and len(x) / r > 1.0:
                    problems.append(f"{fmt}: {len(x) / r:.2f} s is long for a message tone")
                m, found = analyze(x, r, loop, strikes, master=False)
                problems += [f"{fmt}: {p}" for p in found]
                if abs(m["s200"] - TARGET[key]) > 0.35:
                    problems.append(f"{fmt} loudness {m['s200']:.2f} LUFS, target {TARGET[key]}")
                if fmt == "ogg":
                    shown = m
            m = shown
            loud[(pack, key)] = m["s200"]
            # Both encodings carry the same sound: the difference stays far under it.
            a, b = decoded["ogg"], decoded["mp3"]
            n = min(len(a), len(b))
            difference = amp_db(np.sqrt(np.mean((a[:n] - b[:n]) ** 2))) - amp_db(np.sqrt(np.mean(a[:n] ** 2)))
            if difference > -20:
                problems.append(f"the Ogg and the MP3 differ by {difference:.0f} dB")

            c, cr = decode(caf)
            caf_peak = amp_db(np.abs(c).max())
            if cr != 22050 or abs(len(c) / cr - len(x) / r) > 0.05 or caf_peak > -0.5:
                problems.append(f"CAF: {cr} Hz, {len(c) / cr:.2f} s, peak {caf_peak:.2f} dBFS")

            if masters:
                mx, mr = decode(os.path.join(masters, pack, f"{key}.wav"))
                _, found = analyze(mx, mr, loop, strikes, master=True)
                problems += [f"master: {p}" for p in found]

            print(f"{name:16} {m['len']:5.2f} {'yes' if loop else '':>4} {m['peak']:6.2f} {m['tpeak']:6.2f} {m['s200']:6.2f} "
                  f"{m['dc']:5.0f} {m['above6k'] * 100:7.4f} {m['above12k']:6.0f} {m['tonal12k']:5.0f} "
                  f"{m.get('seam', float('nan')):5.0f} {difference:6.0f}dB {os.path.getsize(caf) / 1024:5.1f}K  {'; '.join(problems) or 'ok'}")
            failures += [f"{name}: {p}" for p in problems]

    for key in KEYS:
        values = [loud[(p, key)] for p in packs if (p, key) in loud]
        if values and max(values) - min(values) > 0.5:
            failures.append(f"{key}: {max(values) - min(values):.2f} LU apart across the packs")
    spread = max(max(loud[(p, k)] for p in packs) - min(loud[(p, k)] for p in packs) for k in KEYS)
    print(f"loudness: one key at most {spread:.2f} LU apart across {len(packs)} packs; "
          f"furthest off its target {max(abs(v - TARGET[k]) for (p, k), v in loud.items()):.2f} LU")
    if onsets is None:
        print("note: no onsets.json (pass --masters), so the click check was skipped")

    if tmp:
        same_files(tree, failures)
        shutil.rmtree(tmp, ignore_errors=True)

    if failures:
        print(f"\n{len(failures)} problem(s):", file=sys.stderr)
        for failure in failures:
            print("  " + failure, file=sys.stderr)
        return 1
    print(f"All {len(loud)} sounds pass.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
