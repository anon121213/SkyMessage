#!/usr/bin/env python3
"""Measures the rendered bells tones and builds analysis.json, spectrograms.png and demo.wav.

    python3 analyze.py <bells dir>      (reads <dir>/wav/tone_<key>.wav)
"""
import json
import sys
import wave
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

RATE = 44100
KEYS = ["chime", "pop", "glass", "classic", "soft", "bright", "ringback", "busy", "end"]
LOOPS = {"classic", "soft", "bright", "ringback"}
GROUPS = {"message": ["chime", "pop", "glass"], "ringtone": ["classic", "soft", "bright"], "call": ["ringback", "busy", "end"]}
MOTIFS = {
    "chime": "celesta A5 - D6 - B5 (0 / 100 / 230 ms) over a soft glass D5; A5 damped as B5 lands; rings out",
    "pop": "one glass drop on D6, pitch rising into place from a minor third below within 24 ms",
    "glass": "two glass bowls a fifth apart, A5 then E6 28 ms later, one shimmering chord",
    "classic": "celesta trill D5-F#5 x3 landing on A5, again landing A5-D6; rest (3.2 s cycle)",
    "soft": "music box 6/8 lullaby A5 F#5 D5 B4 | D5 F#5 A5 over glass D4 then G4; one beat rest (3.75 s cycle)",
    "bright": "celesta A5. B5 D6 A5 | E6. D6 B5 D6 (dotted, 150 bpm, legato) over glass D5/A4/D5; rest (3.0 s cycle)",
    "ringback": "glass-bell roll on A4+D5, 13 strikes 80 ms apart swelling over 1 s, then 3 s quiet (4.0 s cycle)",
    "busy": "four damped handbell A4 notes 0.37 s apart, each a little softer",
    "end": "celesta A5 - E5 - D5 (0 / 120 / 250 ms), a soft falling cadence; E5 damped as D5 lands",
}


def read(path, rate=RATE):
    with wave.open(str(path)) as w:
        assert w.getframerate() == rate and w.getsampwidth() == 2 and w.getnchannels() == 1, path
        raw = w.readframes(w.getnframes())
    return np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0


def read_any(path):
    with wave.open(str(path)) as w:
        rate, raw = w.getframerate(), w.readframes(w.getnframes())
        assert w.getsampwidth() == 2 and w.getnchannels() == 1, path
    return np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0, rate


def roughness(x, rate, circular=False):
    """Sensory roughness (the Plomp-Levelt curve in Sethares' form) of the spectral peaks in 93 ms frames: 0 for a pure
    tone, larger when partials less than a critical band apart beat against each other. Blackman-Harris frames
    (sidelobes -92 dB) so one partial never shows up as several; normalized by the frame's peak power and averaged
    with power weights, so it describes the sound, not its level. See ROUGHNESS_ANCHORS for a scale."""
    nfft = int(round(0.0929 * rate)) // 2 * 2
    hop = nfft // 4
    n_ = np.arange(nfft)
    win = (0.35875 - 0.48829 * np.cos(2 * np.pi * n_ / nfft) + 0.14128 * np.cos(4 * np.pi * n_ / nfft)
           - 0.01168 * np.cos(6 * np.pi * n_ / nfft))
    sig = np.concatenate([x, x[:nfft]]) if circular else np.concatenate([x, np.zeros(nfft)])
    per_frame = []
    for i in range(0, len(sig) - nfft, hop):
        spec = np.abs(np.fft.rfft(sig[i:i + nfft] * win)) / (win.sum() / 2)
        if spec.max() < 1e-5:
            continue
        floor = spec.max() * 10 ** (-45 / 20)
        k = np.where((spec[1:-1] > spec[:-2]) & (spec[1:-1] >= spec[2:]) & (spec[1:-1] > floor))[0] + 1
        if len(k) < 2:
            per_frame.append((0.0, float((spec ** 2).sum())))
            continue
        a, b, c = np.log(spec[k - 1] + 1e-20), np.log(spec[k] + 1e-20), np.log(spec[k + 1] + 1e-20)
        off = 0.5 * (a - c) / np.where(a - 2 * b + c == 0, -1e-12, a - 2 * b + c)
        f = (k + off) * rate / nfft
        amp = np.exp(b - 0.25 * (a - c) * off)
        d = 0.0
        for m in range(len(f)):
            for n in range(m + 1, len(f)):
                lo, diff = min(f[m], f[n]), abs(f[n] - f[m])
                sc = 0.24 / (0.0207 * lo + 18.96)
                d += amp[m] * amp[n] * (np.exp(-3.5 * sc * diff) - np.exp(-5.75 * sc * diff))
        per_frame.append((d / float((amp ** 2).sum()), float((amp ** 2).sum())))
    if not per_frame:
        return 0.0, 0.0
    r = np.array([p[0] for p in per_frame]); w = np.array([p[1] for p in per_frame])
    keep = w > w.max() * 1e-3
    return float((r * w).sum() / w.sum()), float(np.percentile(r[keep], 95))


def roughness_anchors():
    t = np.arange(int(0.9 * RATE)) / RATE
    env = np.exp(-t / 0.4) * np.minimum(1, t / 0.005)
    def tone(*fs):
        return env * sum(np.sin(2 * np.pi * f * t + i) for i, f in enumerate(fs)) / len(fs)
    refs = {
        "pure tone A5": tone(880.0),
        "fifth A5+E6": tone(880.0, 1318.51),
        "major sixth D5+B5": tone(587.33, 987.77),
        "major third D5+F#5": tone(587.33, 739.99),
        "minor third F#5+A5": tone(739.99, 880.0),
        "major second A5+B5": tone(880.0, 987.77),
        "minor second A5+A#5": tone(880.0, 932.33),
    }
    return {k: round(roughness(v, RATE)[0], 4) for k, v in refs.items()}


def compare_with(old_dir, stats):
    """Same measures for the tones currently in the app (read-only), for a before/after view."""
    out = {}
    for key in KEYS:
        path = Path(old_dir) / f"tone_{key}.wav"
        if not path.exists():
            continue
        x, rate = read_any(path)
        freqs, power = np.fft.rfftfreq(len(x), 1 / rate), np.abs(np.fft.rfft(x * np.hanning(len(x)))) ** 2
        total = power.sum()
        rough, rough95 = roughness(x, rate)
        out[key] = {
            "sampleRate": rate,
            "seconds": round(len(x) / rate, 3),
            "peakDbfs": round(20 * np.log10(np.abs(x).max()), 2),
            "rmsDb": round(10 * np.log10(np.mean(x * x)), 2),
            "spectralCentroidHz": round(float((freqs * power).sum() / total), 1),
            "energyAbove5kHzRelDb": round(db(power[freqs >= 5000].sum() / total), 1),
            "roughness": round(rough, 4),
            "new": {"spectralCentroidHz": stats[key]["spectralCentroidHz"], "energyAbove5kHzRelDb": stats[key]["energyRelDb"]["above5kHz"],
                    "roughness": stats[key]["roughness"]["mean"]},
        }
    return out


def db(x, floor=1e-12):
    return float(10 * np.log10(max(x, floor)))


def frame_rms(x, win, hop, circular=False):
    if circular:
        x = np.concatenate([x, x[:win]])
    n = 1 + max(0, (len(x) - win) // hop)
    out = np.empty(n)
    for i in range(n):
        seg = x[i * hop:i * hop + win]
        out[i] = np.mean(seg * seg)
    return out


def welch(x, nfft=4096):
    hop = nfft // 2
    win = np.hanning(nfft)
    if len(x) < nfft:
        x = np.pad(x, (0, nfft - len(x)))
    acc = np.zeros(nfft // 2 + 1)
    count = 0
    for i in range(0, len(x) - nfft + 1, hop):
        acc += np.abs(np.fft.rfft(x[i:i + nfft] * win)) ** 2
        count += 1
    freqs = np.fft.rfftfreq(nfft, 1 / RATE)
    return freqs, acc / max(count, 1)


def true_peak(x, circular):
    # 4x oversampling via FFT zero-padding (exact for a periodic signal; good enough for one-shots that start
    # and end at zero).
    n = len(x)
    spec = np.fft.rfft(x)
    up = np.fft.irfft(spec, n * 4) * 4
    return float(np.max(np.abs(up)))


def highpass_brickwall(x, hz, circular):
    sig = x if circular else np.pad(x, (4096, 4096))
    spec = np.fft.rfft(sig)
    freqs = np.fft.rfftfreq(len(sig), 1 / RATE)
    spec[freqs < hz] = 0
    y = np.fft.irfft(spec, len(sig))
    return y if circular else y[4096:-4096]


def analyze(key, x):
    loop = key in LOOPS
    n = len(x)
    peak = float(np.max(np.abs(x)))
    rms = float(np.sqrt(np.mean(x * x)))
    frames = frame_rms(x, 441, 441, circular=False)              # 10 ms frames
    gate = frames.max() * 10 ** (-40 / 10)
    active = float(np.sqrt(np.mean(frames[frames >= gate])))
    momentary = frame_rms(x, int(0.4 * RATE), 441, circular=loop)
    freqs, power = welch(x)
    total = power.sum()
    centroid = float((freqs * power).sum() / total)
    cum = np.cumsum(power) / total
    rolloff95 = float(freqs[np.searchsorted(cum, 0.95)])
    rolloff99 = float(freqs[np.searchsorted(cum, 0.99)])
    above = {f"above{k}kHz": db(power[freqs >= k * 1000].sum() / total) for k in (5, 6, 8, 12)}

    # Click detector: these tones have next to nothing above 10 kHz, so a step or a corner in the waveform (a click)
    # shows up as a spike in the >10 kHz band. Loops are measured as a circle, so the seam is included.
    hf = highpass_brickwall(x, 10000, circular=loop)
    hf_peak = float(np.max(np.abs(hf)))
    hf_where = float(np.argmax(np.abs(hf)) / RATE)

    lsb = 1 / 32768
    rough, rough95 = roughness(x, RATE, circular=loop)
    result = {
        "seconds": round(n / RATE, 4),
        "loop": loop,
        "motif": MOTIFS[key],
        "peakDbfs": round(20 * np.log10(peak), 2),
        "truePeakDbfs": round(20 * np.log10(true_peak(x, loop)), 2),
        "rmsDb": round(20 * np.log10(rms), 2),
        "activeRmsDb": round(10 * np.log10(active ** 2), 2),
        "maxMomentary400msDb": round(db(momentary.max()), 2),
        "crestDb": round(20 * np.log10(peak / rms), 2),
        "spectralCentroidHz": round(centroid, 1),
        "rolloff95Hz": round(rolloff95, 1),
        "rolloff99Hz": round(rolloff99, 1),
        "energyRelDb": {k: round(v, 1) for k, v in above.items()},
        "roughness": {"mean": round(rough, 4), "p95": round(rough95, 4)},
        "clickCheck": {"peakAbove10kHzDbfs": round(20 * np.log10(max(hf_peak, 1e-12)), 1), "atS": round(hf_where, 3),
                       "ok": bool(hf_peak < 10 ** (-70 / 20))},
        "firstSampleLsb": int(round(x[0] / lsb)),
        "lastSampleLsb": int(round(x[-1] / lsb)),
    }
    if loop:
        # The loop point sits in the rest after the motif: the wrapped tail there must already be near-silent
        # (>= 45 dB under the peak), both ends are faded to zero inside it (10 ms in, 30 ms out), and nothing
        # above 10 kHz may appear across the seam (the signal is analysed as a circle, i.e. as it plays looped).
        side = int(0.020 * RATE)
        before = x[-int(0.080 * RATE):-int(0.030 * RATE)]          # tail just before the fade-out
        tail_db = 10 * np.log10(np.mean(before ** 2) + 1e-20)
        hf_seam = float(np.max(np.abs(np.concatenate([hf[-side:], hf[:side]]))))
        silent = tail_db <= -199
        below = -200.0 if silent else float(tail_db - 20 * np.log10(peak))
        jump = abs(x[0] - x[-1])
        result["loopSeam"] = {
            "jumpLsb": round(float(jump / lsb), 2),
            "tailBeforeLoopPointDbfs": round(max(float(tail_db), -200.0), 1),
            "seamBelowPeakDb": round(below, 1),
            "seamIsDigitalSilence": bool(silent),
            "fadeInMs": 10, "fadeOutMs": 30,
            "peakAbove10kHzNearSeamDbfs": round(20 * np.log10(max(hf_seam, 1e-12)), 1),
            "ok": bool(jump <= 2 * lsb and abs(x[0]) <= 2 * lsb and abs(x[-1]) <= 2 * lsb and below < -45
                       and hf_seam < 10 ** (-70 / 20)),
        }
    else:
        result["edges"] = {
            "first5msDbfs": round(db(np.mean(x[:220] ** 2)), 1),
            "last20msDbfs": round(db(np.mean(x[-882:] ** 2)), 1),
            "ok": bool(abs(x[0]) <= 2 * lsb and abs(x[-1]) <= 2 * lsb),
        }
    return result


# ---- Spectrogram sheet ---------------------------------------------------------------------------------------------

ANCHORS = [  # dark violet night -> lavender -> peach -> warm white (our own ramp)
    (0.00, (14, 10, 30)), (0.25, (52, 28, 96)), (0.50, (128, 62, 150)),
    (0.72, (218, 112, 128)), (0.88, (250, 182, 120)), (1.00, (255, 246, 222)),
]


def colorize(v):
    v = np.clip(v, 0, 1)
    out = np.zeros(v.shape + (3,))
    for (p0, c0), (p1, c1) in zip(ANCHORS, ANCHORS[1:]):
        m = (v >= p0) & (v <= p1)
        u = ((v - p0) / (p1 - p0))[m][:, None]
        out[m] = np.array(c0) * (1 - u) + np.array(c1) * u
    return out.astype(np.uint8)


def spectrogram(x, loop, fmin=80, fmax=20000, height=190, px_per_s=240):
    nfft, hop = 4096, 128
    win = np.hanning(nfft)
    pad = nfft // 2
    sig = np.concatenate([x[-pad:], x, x[:pad]]) if loop else np.pad(x, (pad, pad))
    cols = int(np.ceil(len(x) / RATE * px_per_s))
    spec = np.zeros((nfft // 2 + 1, cols))
    for c in range(cols):
        center = int(c / px_per_s * RATE) + pad
        seg = sig[center - pad:center + pad]
        if len(seg) < nfft:
            seg = np.pad(seg, (0, nfft - len(seg)))
        spec[:, c] = np.abs(np.fft.rfft(seg * win)) ** 2
    ref = (np.sum(win) / 2) ** 2      # a full-scale sine reads 0 dB
    level = 10 * np.log10(spec / ref + 1e-14)
    freqs = np.fft.rfftfreq(nfft, 1 / RATE)
    rows = np.geomspace(fmax, fmin, height)
    edges = np.sqrt(rows[:-1] * rows[1:])
    edges = np.concatenate([[rows[0] * (rows[0] / rows[1]) ** 0.5], edges, [rows[-1] * (rows[-1] / rows[-2]) ** 0.5]])
    bin_hz = RATE / nfft
    img = np.zeros((height, cols))
    for r in range(height):
        hi, lo = edges[r], edges[r + 1]
        a, b = int(np.floor(lo / bin_hz)), int(np.ceil(hi / bin_hz))
        a, b = max(a, 0), min(max(b, a + 1), len(freqs))
        img[r] = level[a:b].max(axis=0)
    vmin, vmax = -100.0, -6.0
    return colorize((img - vmin) / (vmax - vmin)), rows


def font(size, bold=False):
    for path in ("/System/Library/Fonts/HelveticaNeue.ttc", "/System/Library/Fonts/Helvetica.ttc"):
        try:
            return ImageFont.truetype(path, size, index=1 if bold else 0)
        except OSError:
            continue
    return ImageFont.load_default()


def sheet(data, stats, out):
    px_per_s, spec_h, wave_h = 240, 190, 46
    label_w, gutter, right = 330, 18, 30
    max_s = max(len(x) / RATE for x in data.values())
    width = label_w + int(max_s * px_per_s) + right
    row_h = spec_h + wave_h + gutter + 8
    height = 70 + row_h * len(data) + 60
    canvas = Image.new("RGB", (width, height), (247, 244, 252))
    draw = ImageDraw.Draw(canvas)
    f_title, f_name, f_small = font(26, True), font(19, True), font(13)
    draw.text((label_w, 22), "SkyMessage tones · palette «Колокольчики» (bells)", fill=(40, 30, 70), font=f_title)
    draw.text((18, 30), "log frequency 80 Hz–20 kHz", fill=(110, 100, 140), font=f_small)
    y = 70
    for key, x in data.items():
        st = stats[key]
        img, rows = spectrogram(x, key in LOOPS, height=spec_h, px_per_s=px_per_s)
        canvas.paste(Image.fromarray(img), (label_w, y))
        w_px = img.shape[1]
        # frequency grid
        for f, name in ((250, "250"), (500, "500"), (1000, "1k"), (2000, "2k"), (5000, "5k"), (8000, "8k"), (16000, "16k")):
            r = int(np.interp(np.log(f), np.log(rows[::-1]), np.arange(len(rows))[::-1]))
            color = (255, 255, 255) if f in (5000, 8000) else (190, 180, 220)
            for xx in range(label_w, label_w + w_px, 6 if f in (5000, 8000) else 3):
                draw.point((xx, y + r), fill=color)
            draw.text((label_w - 30, y + r - 7), name, fill=(110, 100, 140), font=f_small)
        # time ticks every 0.5 s
        for t in np.arange(0, len(x) / RATE + 1e-9, 0.5):
            xx = label_w + int(t * px_per_s)
            draw.line((xx, y + spec_h, xx, y + spec_h + 4), fill=(120, 110, 150))
            draw.text((xx + 2, y + spec_h + wave_h + 1), f"{t:.1f}s", fill=(130, 120, 160), font=f_small)
        # waveform strip (min/max per pixel), with the loop point marked
        wy0 = y + spec_h + 4
        mid = wy0 + wave_h // 2
        draw.rectangle((label_w, wy0, label_w + w_px - 1, wy0 + wave_h), fill=(236, 231, 246))
        spp = RATE / px_per_s
        for c in range(w_px):
            seg = x[int(c * spp):int((c + 1) * spp)]
            if len(seg) == 0:
                continue
            lo, hi = seg.min(), seg.max()
            draw.line((label_w + c, mid - hi * wave_h / 2, label_w + c, mid - lo * wave_h / 2), fill=(106, 75, 255))
        if key in LOOPS:
            xx = label_w + w_px - 1
            draw.line((xx, y, xx, wy0 + wave_h), fill=(232, 120, 90), width=2)
            draw.text((xx - 70, y + 4), "loop point", fill=(255, 230, 210), font=f_small)
        # labels
        draw.text((18, y + 6), key, fill=(40, 30, 70), font=f_name)
        lines = [
            f"{st['seconds']:.2f} s · {'loop' if st['loop'] else 'one-shot'}",
            f"peak {st['peakDbfs']:.1f} dBFS (true {st['truePeakDbfs']:.1f})",
            f"RMS {st['rmsDb']:.2f} dB · crest {st['crestDb']:.1f}",
            f"centroid {st['spectralCentroidHz']:.0f} Hz · roughness {st['roughness']['mean']:.3f}",
            f">6 kHz {st['energyRelDb']['above6kHz']:.0f} dB · >8 kHz {st['energyRelDb']['above8kHz']:.0f} dB",
        ]
        if st["loop"]:
            s = st["loopSeam"]
            lines.append("loop point: digital silence" if s["seamIsDigitalSilence"] else f"loop point: tail {s['seamBelowPeakDb']:.0f} dB re peak, faded")
            lines.append(f"seam >10 kHz {s['peakAbove10kHzNearSeamDbfs']:.0f} dBFS · {'OK' if s['ok'] else 'CHECK'}")
        for i, line in enumerate(lines):
            draw.text((18, y + 34 + i * 18), line, fill=(80, 70, 110), font=f_small)
        y += row_h
    # color scale
    bar = colorize(np.linspace(0, 1, 300))[None, :, :].repeat(10, axis=0)
    canvas.paste(Image.fromarray(bar), (label_w, y + 6))
    draw.text((label_w + 310, y + 3), "-100 … -6 dBFS (per 4096-pt bin)", fill=(110, 100, 140), font=f_small)
    draw.text((label_w, y + 22), "Loops are drawn as a circle: the colour at a loop's right edge is the next cycle's first strike, "
              "12 ms after the loop point.", fill=(110, 100, 140), font=f_small)
    canvas.save(out)


def demo(data, out):
    gap = np.zeros(int(0.7 * RATE))
    parts = []
    for key in ["chime", "pop", "glass"]:
        parts += [data[key], gap]
    for key in ["classic", "soft", "bright"]:
        parts += [np.concatenate([data[key], data[key]]), gap]   # looped twice, exactly as a player loops it
    parts += [data["ringback"], gap, data["busy"], gap, data["end"]]
    y = np.concatenate(parts)
    pcm = np.clip(np.round(y * 32767), -32768, 32767).astype("<i2")
    with wave.open(str(out), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())
    return len(y) / RATE


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    data = {k: read(root / "wav" / f"tone_{k}.wav") for k in KEYS}
    stats = {k: analyze(k, x) for k, x in data.items()}
    groups = {}
    for g, keys in GROUPS.items():
        r = [stats[k]["rmsDb"] for k in keys]
        a = [stats[k]["activeRmsDb"] for k in keys]
        m = [stats[k]["maxMomentary400msDb"] for k in keys]
        groups[g] = {"rmsSpreadDb": round(max(r) - min(r), 2), "activeRmsSpreadDb": round(max(a) - min(a), 2),
                     "momentarySpreadDb": round(max(m) - min(m), 2)}
    sheet(data, stats, root / "spectrograms.png")
    seconds = demo(data, root / "demo.wav")
    old_dir = sys.argv[2] if len(sys.argv) > 2 else None
    anchors = roughness_anchors()
    report = {"format": "44.1 kHz, 16-bit PCM, mono", "palette": "bells", "groups": groups,
              "roughnessScale": {"note": "same measure on two decaying sines of equal level (0.9 s); a tone's mean "
                                         "roughness can be read against these", "anchors": anchors},
              "demo": {"file": "demo.wav", "seconds": round(seconds, 2),
                       "order": "chime, pop, glass, classic x2, soft x2, bright x2, ringback, busy, end (0.7 s gaps)"},
              "tones": stats}
    if old_dir:
        report["previousSetForComparison"] = {"source": str(old_dir), "tones": compare_with(old_dir, stats)}
    (root / "analysis.json").write_text(json.dumps(report, indent=2, ensure_ascii=False))
    for k in KEYS:
        s = stats[k]
        seam = s.get("loopSeam") or s.get("edges")
        print(f"{k:9s} {s['seconds']:5.2f}s pk {s['peakDbfs']:6.2f} tp {s['truePeakDbfs']:6.2f} rms {s['rmsDb']:7.2f} act {s['activeRmsDb']:7.2f} "
              f"mom {s['maxMomentary400msDb']:7.2f} cen {s['spectralCentroidHz']:6.0f} r99 {s['rolloff99Hz']:6.0f} "
              f">6k {s['energyRelDb']['above6kHz']:6.1f} >8k {s['energyRelDb']['above8kHz']:6.1f} hf {s['clickCheck']['peakAbove10kHzDbfs']:6.1f}@{s['clickCheck']['atS']:.3f} {seam}")
    print(json.dumps(groups))


if __name__ == "__main__":
    main()
