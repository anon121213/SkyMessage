#!/usr/bin/env python3
"""Checks the rendered SkyMessage tones and builds the review material.

    python3 analyze.py            (reads wav/tone_<key>.wav next to this file)

Writes analysis.json (per tone: length, peak, RMS, loudness, spectral centroid, energy above 4/6/8/10 kHz, click
detector, edge levels, the span within 40 dB of the loudest 10 ms, loop-seam check; levels floor at -120 dBFS, which
means digital silence), spectrograms.png (all tones on one time scale, labeled) and demo.wav
(the three message tones, each ringtone looped twice, ringback once, busy, end; 0.7 s gaps).
"""
import json
import os
import wave

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.abspath(__file__))
WAV_DIR = os.path.join(ROOT, 'wav')
FS = 44100
FULL = 32768.0
KEYS = ['chime', 'pop', 'glass', 'classic', 'soft', 'bright', 'ringback', 'busy', 'end']
GROUP = {'chime': 'message', 'pop': 'message', 'glass': 'message',
         'classic': 'ringtone', 'soft': 'ringtone', 'bright': 'ringtone',
         'ringback': 'call', 'busy': 'call', 'end': 'call'}
LOOPS = {'classic', 'soft', 'bright', 'ringback'}
SCORE = {
    'chime': 'felt piano · D♭5 → G♭5+B♭5 (rolled): rising sixth into a warm third',
    'pop': 'felt piano · one short D♭6 (+ ghost D♭5)',
    'glass': 'e-piano · E♭5 · B♭5 · E♭6 rolled upward, open fifth',
    'classic': 'e-piano tremolo B♭5↔G♭5, 14/s, "ring-ring" over felt G♭4+D♭5',
    'soft': 'felt lullaby 6/8: D♭5 G♭5 B♭5— | A♭5 G♭5 D♭5— over G♭4, D♭4',
    'bright': 'e-piano riff ♩=120: G♭5 B♭5 D♭6— B♭5 E♭6— D♭6 B♭5 D♭6— over felt fifths',
    'ringback': 'mellow e-piano tremolo B♭4↔D♭5 swelling ~1 s, then quiet',
    'busy': 'felt piano · four short low fifths E♭4+B♭4, 0.32 s apart',
    'end': 'felt piano · D♭5 → B♭4 → G♭4, a soft falling triad',
}


def read(path):
    with wave.open(path, 'rb') as w:
        assert (w.getframerate(), w.getnchannels(), w.getsampwidth()) == (FS, 1, 2), path
        raw = w.readframes(w.getnframes())
    return np.frombuffer(raw, dtype='<i2').astype(np.float64)


def write(path, x):
    pcm = np.clip(np.round(x), -32768, 32767).astype('<i2')
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(FS)
        w.writeframes(pcm.tobytes())


def db(v):
    """dBFS, floored at -120 (= digital silence)."""
    return float(20 * np.log10(max(float(v), 1e-6)))


def rms(x):
    return float(np.sqrt(np.mean(np.square(x)))) if len(x) else 0.0


def frames_rms(x, win, hop):
    n = 1 + max(0, (len(x) - win) // hop)
    return np.array([rms(x[i * hop:i * hop + win]) for i in range(n)])


def band_power(x):
    spec = np.abs(np.fft.rfft(x * np.hanning(len(x)), n=1 << int(np.ceil(np.log2(len(x) * 2))))) ** 2
    freqs = np.fft.rfftfreq((len(spec) - 1) * 2, 1 / FS)
    return freqs, spec


def highpass_fft(x, hz):
    spec = np.fft.rfft(x)
    freqs = np.fft.rfftfreq(len(x), 1 / FS)
    spec[freqs < hz] = 0
    return np.fft.irfft(spec, n=len(x))


def k_weight(x):
    """ITU-R BS.1770 K-weighting (high shelf + RLB high-pass), coefficients derived for 44.1 kHz."""
    def shelf():
        g, q, fc = 3.999843853973347, 0.7071752369554196, 1681.974450955533
        a = 10 ** (g / 40); w = 2 * np.pi * fc / FS; al = np.sin(w) / (2 * q); c = np.cos(w); sa = 2 * np.sqrt(a) * al
        b = [a * ((a + 1) + (a - 1) * c + sa), -2 * a * ((a - 1) + (a + 1) * c), a * ((a + 1) + (a - 1) * c - sa)]
        d = [(a + 1) - (a - 1) * c + sa, 2 * ((a - 1) - (a + 1) * c), (a + 1) - (a - 1) * c - sa]
        return [v / d[0] for v in b], [v / d[0] for v in d]
    def highpass():
        q, fc = 0.5003270373238773, 38.13547087602444
        w = 2 * np.pi * fc / FS; al = np.sin(w) / (2 * q); c = np.cos(w)
        b = [(1 + c) / 2, -(1 + c), (1 + c) / 2]; d = [1 + al, -2 * c, 1 - al]
        return [v / d[0] for v in b], [v / d[0] for v in d]
    y = x
    for b, a in (shelf(), highpass()):
        out = np.zeros_like(y); x1 = x2 = y1 = y2 = 0.0
        for i, v in enumerate(y):
            o = b[0] * v + b[1] * x1 + b[2] * x2 - a[1] * y1 - a[2] * y2
            x2, x1, y2, y1 = x1, v, y1, o
            out[i] = o
        y = out
    return y


def lufs(y):
    return float(-0.691 + 10 * np.log10(max(np.mean(np.square(y)), 1e-20)))


def analyze(key, x):
    s = x / FULL
    freqs, p = band_power(s)
    total = p.sum()
    centroid = float((freqs * p).sum() / total)
    above = {hz: float(p[freqs >= hz].sum() / total * 100) for hz in (4000, 6000, 8000, 10000)}
    loud = frames_rms(s, int(0.4 * FS), int(0.01 * FS))
    kw_sig = k_weight(s)
    kloud = frames_rms(kw_sig, int(0.4 * FS), int(0.01 * FS))
    # Click detector: anything above 10 kHz, in 5 ms frames (the instruments generate nothing up there).
    hf = frames_rms(highpass_fft(s, 10000), int(0.005 * FS), int(0.0025 * FS))
    # Envelope in 10 ms frames, for the "where does it fall silent" checks.
    env = frames_rms(s, int(0.01 * FS), int(0.01 * FS))
    peak_env = env.max()
    active = np.nonzero(env > peak_env * 10 ** (-40 / 20))[0]
    result = {
        'group': GROUP[key],
        'seconds': round(len(x) / FS, 4),
        'peakDbfs': round(db(np.abs(s).max()), 2),
        'rmsDb': round(db(rms(s)), 2),
        'loudnessMax400msDb': round(db(loud.max()), 2),
        'loudnessLufsUngated': round(lufs(kw_sig), 2),
        'momentaryMaxLufs': round(float(-0.691 + 20 * np.log10(max(kloud.max(), 1e-10))), 2),
        'crestDb': round(db(np.abs(s).max()) - db(rms(s)), 2),
        'spectralCentroidHz': round(centroid, 1),
        'energyAbovePct': {f'{hz // 1000}k': round(v, 4) for hz, v in above.items()},
        'above10kHzMaxDbfs': round(db(hf.max()), 1),
        'edges': {
            'firstSampleLsb': int(x[0]), 'lastSampleLsb': int(x[-1]),
            'first2msMaxDbfs': round(db(np.abs(s[:int(0.002 * FS)]).max()), 1),
            'last2msMaxDbfs': round(db(np.abs(s[-int(0.002 * FS):]).max()), 1),
        },
        'audibleSpanSeconds': [round(float(active[0]) * 0.01, 2), round(float(active[-1] + 1) * 0.01, 2)] if len(active) else None,
    }
    if key in LOOPS:
        # The first note starts 20 ms in, so the first 10 ms hold only the wrapped room tail: compare 10 ms each side.
        n = int(0.01 * FS)
        tail, head = s[-n:], s[:n]
        joined = np.concatenate([s[-n:], s[:n]])
        d2_seam = np.abs(np.diff(joined, 2)).max()
        d2_body = np.percentile(np.abs(np.diff(s, 2)), 99.9)
        jump = abs(int(x[0]) - int(x[-1]))
        quiet = db(rms(tail)) < -60 and db(rms(head)) < -60
        level_jump = abs(db(rms(tail)) - db(rms(head))) if rms(tail) > 0 and rms(head) > 0 else 0.0
        result['loop'] = {
            'cycleSeconds': round(len(x) / FS, 4),
            'seamJumpLsb': jump,
            'largestStepElsewhereLsb': int(np.abs(np.diff(x)).max()),
            'last10msRmsDbfs': round(db(rms(tail)), 1),
            'first10msRmsDbfs': round(db(rms(head)), 1),
            'levelJumpDb': round(level_jump, 2),
            'seamCurvatureVsBody': round(float(d2_seam / max(d2_body, 1e-12)), 3),
            'ok': bool(jump <= 2 and (quiet or level_jump < 1.0) and d2_seam <= d2_body),
        }
    return result


# ---- Spectrogram sheet ------------------------------------------------------------------------------

def colormap(v):
    """v in 0..1 -> RGB, a dark-blue -> violet -> peach -> cream ramp (perceptually ordered)."""
    stops = np.array([
        (0.00, 10, 8, 24), (0.25, 44, 24, 92), (0.50, 128, 48, 140),
        (0.72, 222, 98, 110), (0.88, 250, 178, 112), (1.00, 255, 246, 214)])
    out = np.zeros(v.shape + (3,))
    for c in range(3):
        out[..., c] = np.interp(v, stops[:, 0], stops[:, c + 1])
    return out.astype(np.uint8)


def spectrogram(s, fmax, height, px_per_s):
    nfft, hop = 2048, int(FS / px_per_s)
    pad = np.concatenate([np.zeros(nfft // 2), s, np.zeros(nfft // 2)])
    win = np.hanning(nfft)
    cols = 1 + (len(pad) - nfft) // hop
    frames = np.stack([pad[i * hop:i * hop + nfft] * win for i in range(cols)], axis=1)
    mag = np.abs(np.fft.rfft(frames, axis=0)) / (win.sum() / 2)
    freqs = np.fft.rfftfreq(nfft, 1 / FS)
    rows = np.linspace(fmax, 0, height)
    img = np.empty((height, cols))
    for j in range(cols):
        img[:, j] = np.interp(rows, freqs, mag[:, j])
    dbv = 20 * np.log10(np.maximum(img, 1e-9))
    lo, hi = -100.0, -6.0
    return colormap(np.clip((dbv - lo) / (hi - lo), 0, 1))


def font(size, bold=False):
    for path in (['/System/Library/Fonts/Supplemental/Arial Bold.ttf'] if bold else []) + [
            '/System/Library/Fonts/Supplemental/Arial Unicode.ttf', '/System/Library/Fonts/Supplemental/Arial.ttf',
            '/System/Library/Fonts/Helvetica.ttc']:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def sheet(signals, stats, path):
    px_per_s, fmax, h = 300, 11025, 190
    left, top, gap = 230, 92, 50
    width = left + int(max(len(s) for s in signals.values()) / FS * px_per_s) + 60
    height = top + len(signals) * (h + gap) + 40
    im = Image.new('RGB', (width, height), (250, 248, 252))
    d = ImageDraw.Draw(im)
    d.text((20, 18), 'SkyMessage tones · palette "Мягкое пианино" (felt piano + FM e-piano) · 0-11 kHz linear, '
           '-100…-6 dBFS · dashed lines: 2 / 6 / 8 kHz', fill=(40, 30, 60), font=font(17, True))
    y = top
    for key, s in signals.items():
        st = stats[key]
        spec = spectrogram(s / FULL, fmax, h, px_per_s)
        im.paste(Image.fromarray(spec), (left, y))
        w = spec.shape[1]
        for hz, col in ((2000, (255, 255, 255)), (6000, (255, 210, 120)), (8000, (255, 140, 140))):
            yy = y + int((1 - hz / fmax) * h)
            for xx in range(left, left + w, 8):
                d.line([(xx, yy), (xx + 3, yy)], fill=col, width=1)
        # envelope (10 ms RMS, dB) drawn over the spectrogram: -80 dBFS at the bottom, 0 at the top
        env = frames_rms(s / FULL, int(0.01 * FS), int(FS / px_per_s))
        pts = [(left + i, y + int(np.clip(-db(v) / 80, 0, 1) * (h - 1))) for i, v in enumerate(env)]
        if len(pts) > 1:
            d.line(pts, fill=(140, 255, 200), width=1)
        for sec in range(0, int(len(s) / FS) + 1):
            xx = left + sec * px_per_s
            d.line([(xx, y + h), (xx, y + h + 5)], fill=(60, 50, 80), width=1)
            d.text((xx + 2, y + h + 4), f'{sec} s', fill=(90, 80, 110), font=font(11))
        d.text((20, y + 6), key, fill=(30, 20, 50), font=font(24, True))
        d.text((left, y - 21), SCORE[key], fill=(60, 50, 90), font=font(15))
        lines = [f"{st['group']}{' · loop' if 'loop' in st else ''}",
                 f"{st['seconds']:.2f} s",
                 f"peak {st['peakDbfs']:.1f} dBFS",
                 f"RMS {st['rmsDb']:.1f} dB",
                 f"centroid {st['spectralCentroidHz'] / 1000:.2f} kHz",
                 f">6 kHz {st['energyAbovePct']['6k']:.3f} %"]
        if 'loop' in st:
            lines.append(f"seam {'ok' if st['loop']['ok'] else 'CHECK'} ({st['loop']['seamJumpLsb']} LSB)")
        for i, line in enumerate(lines):
            d.text((20, y + 40 + i * 19), line, fill=(70, 60, 95), font=font(14))
        y += h + gap
    im.save(path)


def main():
    signals = {k: read(os.path.join(WAV_DIR, f'tone_{k}.wav')) for k in KEYS}
    stats = {k: analyze(k, x) for k, x in signals.items()}
    groups = {}
    for g in ('message', 'ringtone'):
        vals = [stats[k]['rmsDb'] for k in KEYS if GROUP[k] == g]
        loud = [stats[k]['loudnessMax400msDb'] for k in KEYS if GROUP[k] == g]
        lu = [stats[k]['loudnessLufsUngated'] for k in KEYS if GROUP[k] == g]
        mom = [stats[k]['momentaryMaxLufs'] for k in KEYS if GROUP[k] == g]
        groups[g] = {'rmsSpreadDb': round(max(vals) - min(vals), 2), 'loudness400msSpreadDb': round(max(loud) - min(loud), 2),
                     'lufsSpread': round(max(lu) - min(lu), 2), 'momentaryLufsSpread': round(max(mom) - min(mom), 2)}
    for k in KEYS:
        stats[k]['score'] = SCORE[k]
    out = {'palette': 'piano', 'sampleRate': FS, 'bits': 16, 'channels': 1, 'key': 'G♭ major pentatonic',
           'groups': groups, 'tones': stats}
    with open(os.path.join(ROOT, 'analysis.json'), 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False, indent=2)

    gap = np.zeros(int(0.7 * FS))
    parts = []
    for k in ['chime', 'pop', 'glass']:
        parts += [signals[k], gap]
    for k in ['classic', 'soft', 'bright']:
        parts += [signals[k], signals[k], gap]
    parts += [signals['ringback'], gap, signals['busy'], gap, signals['end']]
    write(os.path.join(ROOT, 'demo.wav'), np.concatenate(parts))

    sheet(signals, stats, os.path.join(ROOT, 'spectrograms.png'))

    for k in KEYS:
        st = stats[k]
        loop = st.get('loop')
        print(f"{k:9s} {st['seconds']:5.2f}s peak {st['peakDbfs']:6.2f} rms {st['rmsDb']:6.2f} loud {st['loudnessMax400msDb']:6.2f} "
              f"lufs {st['loudnessLufsUngated']:6.2f} mom {st['momentaryMaxLufs']:6.2f} cent {st['spectralCentroidHz']:7.1f} >6k {st['energyAbovePct']['6k']:.3f}% >8k {st['energyAbovePct']['8k']:.4f}% "
              f"hf {st['above10kHzMaxDbfs']:6.1f} edges {st['edges']['first2msMaxDbfs']:6.1f}/{st['edges']['last2msMaxDbfs']:6.1f} "
              f"span {st['audibleSpanSeconds']}"
              + (f" | seam jump {loop['seamJumpLsb']} tail {loop['last10msRmsDbfs']} head {loop['first10msRmsDbfs']} "
                 f"curv {loop['seamCurvatureVsBody']} ok {loop['ok']}" if loop else ''))
    print(json.dumps(groups))


if __name__ == '__main__':
    main()
