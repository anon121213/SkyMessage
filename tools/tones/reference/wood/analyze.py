#!/usr/bin/env python3
"""Analysis for the SkyMessage "warm wood" tone set.

Reads wav/tone_<key>.wav, writes analysis.json, spectrograms.png and demo.wav next to this script.
  python3 analyze.py [--quiet]
"""
import json
import os
import sys
import wave

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
WAV = os.path.join(HERE, "wav")
KEYS = ["chime", "pop", "glass", "classic", "soft", "bright", "ringback", "busy", "end"]
GROUP = {"chime": "message", "pop": "message", "glass": "message",
         "classic": "ringtone", "soft": "ringtone", "bright": "ringtone",
         "ringback": "call", "busy": "call", "end": "call"}
LOOPS = {"classic", "soft", "bright", "ringback"}
DESCRIPTION = {
    "chime": "Marimba, three soft notes: D5, up a fifth to A5, settling on F#5 (do-sol-mi); a warm D-major chord rings and is damped.",
    "pop": "One round, hollow wooden 'bloop' on A5 whose pitch lifts about a semitone in its first 25 ms; the most minimal.",
    "glass": "Two kalimba tines plucked together a fifth apart (A5 + E6): a clear 'tink' and a pure, slowly shimmering ring.",
    "classic": "Ring-ring made of wood: two 0.42 s bursts of a fast marimba trill A5/F#5 (~15 strikes/s) over a soft D5, then a pause; 3.0 s loop.",
    "soft": "Kalimba lullaby in 3/4, one bar per second: A5-F#5 | A5-F#5-E5 | D5+A4 rings | rest (sol-mi, sol-mi-re, do); soft thumbs; 4.0 s loop.",
    "bright": "Cheerful marimba phrase at 150 bpm with a syncopated 3+3+2 start: D6 A5 B5 D6 E6 D6 B5 A5, soft wood block on 2 and 4, a bar of rest; 3.2 s loop.",
    "ringback": "One soft marimba roll on an open fifth A4/E5 (~13 strikes/s) swelling and fading over ~1 s, then ~2.5 s of quiet; 4.0 s loop.",
    "busy": "Four short low marimba strokes, A4 over a softer D4 (a slight flam), every 0.36 s, each a little softer: wooden 'short beeps'.",
    "end": "Soft falling marimba cadence A5 - E5 (short passing note) - D5 (sol-re-do), ending on a clean fifth.",
}
FS = 44100


def load(path):
    with wave.open(path, "rb") as w:
        assert w.getframerate() == FS, path
        assert w.getnchannels() == 1 and w.getsampwidth() == 2, path
        data = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2")
    return data.astype(np.float64) / 32768.0


def db(v):
    """dBFS; -120 stands for digital silence."""
    return float(20 * np.log10(max(float(v), 1e-6)))


def rms(x):
    return float(np.sqrt(np.mean(x * x))) if len(x) else 0.0


def spectrum(x):
    n = 1 << int(np.ceil(np.log2(max(len(x), 2))))
    p = np.abs(np.fft.rfft(x, n)) ** 2
    f = np.fft.rfftfreq(n, 1 / FS)
    return f, p


def hp_fft(x, cutoff):
    f, _ = spectrum(x)
    n = 2 * (len(f) - 1)
    X = np.fft.rfft(x, n)
    X[f < cutoff] = 0
    return np.fft.irfft(X, n)[: len(x)]


def short_term_max(x, win_s=0.3):
    w = int(win_s * FS)
    if len(x) <= w:
        return rms(x)
    c = np.cumsum(np.concatenate([[0.0], x * x]))
    e = (c[w:] - c[:-w]) / w
    return float(np.sqrt(e.max()))


def analyze(key, x):
    f, p = spectrum(x)
    total = p.sum()
    out = {
        "description": DESCRIPTION[key],
        "seconds": round(len(x) / FS, 4),
        "peakDb": round(db(np.abs(x).max()), 2),
        "rmsDb": round(db(rms(x)), 2),
        "shortTermMaxRmsDb": round(db(short_term_max(x)), 2),
        "centroidHz": round(float((f * p).sum() / total), 1),
        "energyAbove6kPct": round(float(p[f >= 6000].sum() / total * 100), 3),
        "energyAbove8kPct": round(float(p[f >= 8000].sum() / total * 100), 4),
        "dcOffset": round(float(x.mean()), 6),
        "firstSample": int(round(x[0] * 32768)),
        "lastSample": int(round(x[-1] * 32768)),
        "first5msDb": round(db(rms(x[: int(0.005 * FS)])), 1),
        "last5msDb": round(db(rms(x[-int(0.005 * FS):])), 1),
    }
    # Clicks: energy above 12 kHz in 2 ms windows (a click is a broadband burst; our partials stay low).
    h = hp_fft(x, 12000)
    w = int(0.002 * FS)
    if len(h) > w:
        c = np.cumsum(np.concatenate([[0.0], h * h]))
        out["maxHfBurstDb"] = round(db(np.sqrt(((c[w:] - c[:-w]) / w).max())), 1)
    if key in LOOPS:
        out["loop"] = seam(x)
    return out


def seam(x):
    """Plays the file twice back to back and inspects the join."""
    n = len(x)
    y = np.concatenate([x, x, x])
    w = int(0.02 * FS)
    before = rms(y[2 * n - w: 2 * n])
    after = rms(y[2 * n: 2 * n + w])
    d = np.abs(np.diff(y))
    seam_jump = float(np.abs(y[2 * n] - y[2 * n - 1]))
    # Click check around the seam: HF energy near the join vs the quietest part of the file.
    h = hp_fft(y, 8000)
    k = int(0.005 * FS)
    near = rms(h[2 * n - k: 2 * n + k])
    level = db(max(before, after))
    ok = seam_jump * 32768 <= 8 and (level < -45 or abs(db(after) - db(before)) < 1.0) and db(near) < -70
    return {
        "seamJumpLsb": round(seam_jump * 32768, 2),
        "maxStepElsewhereLsb": round(float(d.max()) * 32768, 1),
        "rms20msBeforeSeamDb": round(db(before), 1),
        "rms20msAfterSeamDb": round(db(after), 1),
        "levelStepDb": round(abs(db(after) - db(before)), 2),
        "hfNearSeamDb": round(db(near), 1),
        "seamless": bool(ok),
    }


# ---- Spectrogram picture ------------------------------------------------------------------------------

def stft_db(x, nfft=2048, hop=96):
    pad = np.concatenate([np.zeros(nfft // 2), x, np.zeros(nfft // 2)])
    k = np.arange(nfft) / nfft
    win = 0.35875 - 0.48829 * np.cos(2 * np.pi * k) + 0.14128 * np.cos(4 * np.pi * k) - 0.01168 * np.cos(6 * np.pi * k)
    frames = 1 + (len(pad) - nfft) // hop
    idx = np.arange(nfft)[None, :] + hop * np.arange(frames)[:, None]
    S = np.abs(np.fft.rfft(pad[idx] * win, axis=1)) ** 2
    S = 10 * np.log10(S + 1e-14)
    return S.T  # freq x time


def colormap(v):
    """v in 0..1 -> RGB (dark navy -> violet -> rose -> warm cream), a hand-made ramp."""
    stops = np.array([
        [0.00, 10, 8, 24],
        [0.25, 44, 22, 84],
        [0.50, 126, 40, 120],
        [0.70, 214, 84, 98],
        [0.86, 248, 160, 90],
        [1.00, 255, 244, 214],
    ])
    out = np.zeros(v.shape + (3,))
    for c in range(3):
        out[..., c] = np.interp(v, stops[:, 0], stops[:, c + 1])
    return out.astype(np.uint8)


def font(size, bold=False):
    for path in (["/System/Library/Fonts/Supplemental/Arial Bold.ttf"] if bold else []) + [
            "/System/Library/Fonts/Supplemental/Arial.ttf", "/System/Library/Fonts/Helvetica.ttc"]:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def spectrogram_sheet(signals, stats, path):
    pw, ph = 560, 250          # plot area per panel
    ml, mt, mr, mb = 50, 46, 14, 28
    cw, chh = pw + ml + mr, ph + mt + mb
    cols, rows = 3, 3
    title_h = 54
    img = Image.new("RGB", (cols * cw + 20, rows * chh + title_h + 10), (250, 248, 252))
    d = ImageDraw.Draw(img)
    d.text((16, 12), "SkyMessage tones \u2014 warm wood (marimba \u00b7 kalimba \u00b7 soft wood block)", fill=(30, 26, 48), font=font(20, True))
    d.text((16, 36), "Spectrogram, log frequency 150 Hz\u201311 kHz, 2048-pt Blackman-Harris; colour = dB below each tone's loudest bin (85 dB range). "
                     "Rows: message tones \u00b7 ringtones (one loop cycle) \u00b7 call progress. Dashed: 6 and 8 kHz.",
           fill=(90, 84, 110), font=font(12))
    fmin, fmax = 150.0, 11025.0
    lmin, lmax = np.log(fmin), np.log(fmax)
    for i, key in enumerate(KEYS):
        x = signals[key]
        r, c = divmod(i, cols)
        ox, oy = 10 + c * cw, title_h + r * chh
        S = stft_db(x)
        fb = np.linspace(0, FS / 2, S.shape[0])
        # resample rows onto a log-frequency grid (top row = fmax)
        rows_f = np.exp(np.linspace(lmax, lmin, ph * 2))
        Sl = np.empty((len(rows_f), S.shape[1]))
        for j in range(S.shape[1]):
            Sl[:, j] = np.interp(rows_f, fb, S[:, j])
        top = S[(fb >= fmin) & (fb <= fmax)].max()
        v = np.clip((Sl - (top - 85)) / 85, 0, 1) ** 0.9
        pic = Image.fromarray(colormap(v)).resize((pw, ph), Image.BILINEAR)
        img.paste(pic, (ox + ml, oy + mt))
        st = stats[key]
        d.text((ox + ml, oy + 6), key, fill=(30, 26, 48), font=font(16, True))
        info = f"{st['seconds']:.2f} s \u00b7 peak {st['peakDb']:.1f} dBFS \u00b7 RMS {st['rmsDb']:.1f} dB \u00b7 centroid {st['centroidHz']:.0f} Hz"
        if "loop" in st:
            info += " \u00b7 loop " + ("seamless" if st["loop"]["seamless"] else "SEAM!")
        d.text((ox + ml, oy + 27), info, fill=(90, 84, 110), font=font(11))

        def fy(hz):
            return oy + mt + int((lmax - np.log(hz)) / (lmax - lmin) * ph)
        for hz, lab in ((250, "250"), (500, "500"), (1000, "1k"), (2000, "2k"), (4000, "4k"), (6000, "6k"), (8000, "8k")):
            yy = fy(hz)
            d.line([(ox + ml - 4, yy), (ox + ml, yy)], fill=(120, 110, 140))
            d.text((ox + 8, yy - 7), lab, fill=(90, 84, 110), font=font(10))
            if hz in (6000, 8000):
                for xx in range(ox + ml, ox + ml + pw, 8):
                    d.line([(xx, yy), (xx + 3, yy)], fill=(235, 235, 245))
        dur = len(x) / FS
        step = 0.1 if dur <= 1.0 else 0.5
        k = 0
        while k * step <= dur + 1e-9:
            t = k * step
            xx = ox + ml + int(t / dur * (pw - 1))
            d.line([(xx, oy + mt + ph), (xx, oy + mt + ph + 4)], fill=(120, 110, 140))
            if t < dur - step * 0.6:
                d.text((xx - 8, oy + mt + ph + 7), f"{t:.1f}", fill=(90, 84, 110), font=font(10))
            k += 1
        d.text((ox + ml + pw - 34, oy + mt + ph + 7), f"{dur:.2f} s", fill=(90, 84, 110), font=font(10))
        d.rectangle([ox + ml - 1, oy + mt - 1, ox + ml + pw, oy + mt + ph], outline=(160, 150, 180))
    img.save(path)


# ---- Demo ---------------------------------------------------------------------------------------------

def write_wav(path, x):
    y = np.clip(np.round(x * 32767), -32768, 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(FS)
        w.writeframes(y.tobytes())


def demo(signals, path):
    gap = np.zeros(int(0.7 * FS))
    parts = []
    order = [("chime", 1), ("pop", 1), ("glass", 1), ("classic", 2), ("soft", 2), ("bright", 2),
             ("ringback", 1), ("busy", 1), ("end", 1)]
    for i, (key, times) in enumerate(order):
        if i:
            parts.append(gap)
        parts.append(np.tile(signals[key], times))
    y = np.concatenate(parts)
    write_wav(path, y)
    return len(y) / FS


def main():
    quiet = "--quiet" in sys.argv
    signals = {k: load(os.path.join(WAV, f"tone_{k}.wav")) for k in KEYS}
    stats = {k: analyze(k, signals[k]) for k in KEYS}
    groups = {}
    for k in KEYS:
        groups.setdefault(GROUP[k], []).append(stats[k]["rmsDb"])
    summary = {
        "message": {"keys": ["chime", "pop", "glass"], "rmsSpreadDb": round(max(groups["message"]) - min(groups["message"]), 2), "limitDb": 1.0},
        "ringtone": {"keys": ["classic", "soft", "bright"], "rmsSpreadDb": round(max(groups["ringtone"]) - min(groups["ringtone"]), 2), "limitDb": 1.0},
        "callProgress": {"keys": ["ringback", "busy", "end"], "note": "not loudness-matched: different jobs (ringback is mostly silence)"},
    }
    checks = {
        "allPeaksAt-1.5dBFS": all(abs(stats[k]["peakDb"] + 1.5) < 0.05 for k in KEYS),
        "messageTonesAtMost0.9s": all(stats[k]["seconds"] <= 0.9 for k in ("chime", "pop", "glass")),
        "ringtonesLoop2.5to4s": all(2.5 <= stats[k]["seconds"] <= 4.0 for k in ("classic", "soft", "bright")),
        "endAtMost0.8s": stats["end"]["seconds"] <= 0.8,
        "messageRmsWithin1dB": summary["message"]["rmsSpreadDb"] <= 1.0,
        "ringtoneRmsWithin1dB": summary["ringtone"]["rmsSpreadDb"] <= 1.0,
        "energyAbove8kUnder0.1pct": all(stats[k]["energyAbove8kPct"] < 0.1 for k in KEYS),
        "loopsSeamless": all(stats[k]["loop"]["seamless"] for k in LOOPS),
        "noClicks(HF bursts < -80 dBFS)": all(stats[k]["maxHfBurstDb"] < -80 for k in KEYS),
        "filesStartAndEndAtZero": all(stats[k]["firstSample"] == 0 and stats[k]["lastSample"] == 0 for k in KEYS),
    }
    report = {
        "palette": "Тёплое дерево — marimba, kalimba, soft wood blocks (modal synthesis)",
        "format": "44.1 kHz, 16-bit PCM, mono",
        "rmsDefinition": "whole-file RMS in dBFS (full-scale square = 0 dB); for loops = one cycle. -120 dB = digital silence. "
                         "shortTermMaxRmsDb = loudest 300 ms window.",
        "loopCheck": "the file is played three times back to back; seamJumpLsb = sample step across the join, "
                     "rms20ms* = level either side of the join, hfNearSeamDb = >8 kHz energy within 5 ms of the join",
        "tones": stats,
        "groups": summary,
        "checks": checks,
    }
    with open(os.path.join(HERE, "analysis.json"), "w") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)
    spectrogram_sheet(signals, stats, os.path.join(HERE, "spectrograms.png"))
    secs = demo(signals, os.path.join(HERE, "demo.wav"))
    if not quiet:
        for k in KEYS:
            s = stats[k]
            line = (f"{k:9s} {s['seconds']:5.2f}s peak {s['peakDb']:6.2f} rms {s['rmsDb']:6.2f} st {s['shortTermMaxRmsDb']:6.2f} "
                    f"cent {s['centroidHz']:6.0f} >6k {s['energyAbove6kPct']:6.3f}% >8k {s['energyAbove8kPct']:7.4f}% "
                    f"hf {s.get('maxHfBurstDb', 0):6.1f} edges {s['first5msDb']:6.1f}/{s['last5msDb']:6.1f}")
            if "loop" in s:
                L = s["loop"]
                line += (f" | seam jump {L['seamJumpLsb']} lsb, {L['rms20msBeforeSeamDb']}/{L['rms20msAfterSeamDb']} dB,"
                         f" hf {L['hfNearSeamDb']} {'OK' if L['seamless'] else 'SEAM'}")
            print(line)
        print("groups", {g: v.get("rmsSpreadDb") for g, v in summary.items()}, f"demo {secs:.1f}s")
        print("checks", checks)


if __name__ == "__main__":
    main()
