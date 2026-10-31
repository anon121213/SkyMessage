// SkyMessage tones, synthesized with WebAudio from sine partials and envelopes (our own sounds — no samples,
// no third-party audio). The recipes match tools/tones/render-tones.cs, which renders the same tones to WAV
// files for OS notification channels.
//
// API: play(key) · playOnce(key) · startLoop(key) · stop() · unlock()
//   message tones: chime, pop, glass · ringtones: classic, soft, bright · call progress: ringback, busy, end

let ctx = null;
let master = null;
let loop = null;   // { key, gain, timer }
let once = null;   // gain node of the last one-shot preview (stopped by stop())

const CYCLES = { classic: 3.0, soft: 3.0, bright: 3.0, ringback: 4.0 };
// Rough per-tone normalization so every tone peaks at a similar loudness (the WAV renderer normalizes exactly).
const LEVEL = { chime: 0.48, pop: 0.78, glass: 0.62, classic: 0.85, soft: 0.95, bright: 0.62, ringback: 1.0, busy: 1.0, end: 0.9 };

function ensure() {
  if (!ctx) {
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    try {
      ctx = new AC();
    } catch (e) {
      return null;
    }
    master = ctx.createGain();
    master.gain.value = 0.7;
    master.connect(ctx.destination);
  }
  if (ctx.state === 'suspended') ctx.resume().catch(() => { });
  return ctx;
}

// Browsers only start audio after a user gesture: resume on the first interaction.
['pointerdown', 'keydown', 'touchend'].forEach((type) =>
  window.addEventListener(type, () => { if (ctx && ctx.state === 'suspended') ctx.resume().catch(() => { }); }, { capture: true, passive: true }));

export function unlock() { ensure(); }

// ---- Primitives (times in seconds relative to `t0`) -------------------------------------------------------

function partial(out, t0, start, freq, decay, amp, attack = 0.003) {
  const c = out.context;
  const at = t0 + start;
  const osc = c.createOscillator();
  const g = c.createGain();
  osc.type = 'sine';
  osc.frequency.setValueAtTime(freq, at);
  g.gain.setValueAtTime(0, at);
  g.gain.linearRampToValueAtTime(amp, at + attack);
  g.gain.setTargetAtTime(0, at + attack, decay);
  osc.connect(g);
  g.connect(out);
  osc.start(at);
  osc.stop(at + attack + decay * 7);
}

function bell(out, t0, start, freq, decay, amp) {
  partial(out, t0, start, freq, decay, amp * 0.60);
  partial(out, t0, start, freq * 2.0, decay * 0.55, amp * 0.22);
  partial(out, t0, start, freq * 2.76, decay * 0.35, amp * 0.12);
  partial(out, t0, start, freq * 5.40, decay * 0.18, amp * 0.06);
}

function mallet(out, t0, start, freq, decay, amp) {
  partial(out, t0, start, freq, decay * 0.5, amp * 0.70, 0.006);
  partial(out, t0, start, freq * 4.0, decay * 0.12, amp * 0.12, 0.003);
  partial(out, t0, start, freq * 10.0, decay * 0.04, amp * 0.03, 0.002);
}

function tone(out, t0, start, duration, freq, amp, attack, release, harmonics) {
  const c = out.context;
  const at = t0 + start;
  const g = c.createGain();
  g.gain.setValueAtTime(0, at);
  g.gain.linearRampToValueAtTime(amp, at + attack);
  g.gain.setValueAtTime(amp, at + Math.max(attack, duration - release));
  g.gain.linearRampToValueAtTime(0, at + duration);
  g.connect(out);
  harmonics.forEach((h, i) => {
    if (!h) return;
    const osc = c.createOscillator();
    const hg = c.createGain();
    osc.type = 'sine';
    osc.frequency.setValueAtTime(freq * (i + 1), at);
    hg.gain.value = h;
    osc.connect(hg);
    hg.connect(g);
    osc.start(at);
    osc.stop(at + duration + 0.02);
  });
}

function sweep(out, t0, start, duration, fromHz, toHz, amp) {
  const c = out.context;
  const at = t0 + start;
  const osc = c.createOscillator();
  const g = c.createGain();
  osc.type = 'sine';
  osc.frequency.setValueAtTime(fromHz, at);
  osc.frequency.exponentialRampToValueAtTime(toHz, at + duration);
  g.gain.setValueAtTime(0, at);
  g.gain.linearRampToValueAtTime(amp, at + 0.003);
  g.gain.setTargetAtTime(0, at + 0.003, duration * 0.6);
  osc.connect(g);
  g.connect(out);
  osc.start(at);
  osc.stop(at + duration * 2.2);
}

// ---- Recipes ------------------------------------------------------------------------------------------------

const RECIPES = {
  // Two soft bell strikes a fifth apart.
  chime(out, t) {
    bell(out, t, 0.00, 1046.5, 0.55, 0.9);
    bell(out, t, 0.13, 1568.0, 0.70, 0.75);
    return 0.95;
  },
  // A short rising bubble blip followed by a quieter echo.
  pop(out, t) {
    sweep(out, t, 0.000, 0.070, 420, 980, 0.9);
    sweep(out, t, 0.105, 0.060, 620, 1240, 0.45);
    return 0.32;
  },
  // A high, slightly detuned crystal ting with an airy tail.
  glass(out, t) {
    partial(out, t, 0.0, 2093.0, 0.90, 0.55);
    partial(out, t, 0.0, 2097.5, 0.85, 0.30);
    partial(out, t, 0.0, 2093.0 * 2.76, 0.30, 0.18);
    partial(out, t, 0.0, 2093.0 * 5.40, 0.12, 0.08);
    partial(out, t, 0.09, 3136.0, 0.55, 0.22);
    return 1.1;
  },
  // Alternating two-tone trill: ring 1.4 s, then a pause (one 3 s cycle).
  classic(out, t) {
    for (let i = 0; i < 14; i++) {
      const f = i % 2 === 0 ? 932.3 : 740.0;
      tone(out, t, i * 0.1, 0.095, f, 0.55, 0.004, 0.02, [1.0, 0.35, 0.12]);
    }
    return 3.0;
  },
  // Warm mallet arpeggio, unhurried.
  soft(out, t) {
    const notes = [523.25, 659.25, 783.99, 659.25, 587.33, 783.99];
    notes.forEach((f, i) => mallet(out, t, i * 0.22, f, 0.9, 0.65));
    return 3.0;
  },
  // Brighter, upbeat rising figure with a sparkle on top.
  bright(out, t) {
    const notes = [783.99, 987.77, 1174.66, 1567.98, 1174.66, 1567.98];
    const times = [0.0, 0.14, 0.28, 0.42, 0.70, 0.84];
    notes.forEach((f, i) => bell(out, t, times[i], f, 0.45, 0.7));
    return 3.0;
  },
  // 425 Hz, 1 s on / 3 s off.
  ringback(out, t) {
    tone(out, t, 0.0, 1.0, 425.0, 0.6, 0.02, 0.03, [1.0]);
    return 4.0;
  },
  // 425 Hz, 0.35 s on / 0.35 s off, four times.
  busy(out, t) {
    for (let i = 0; i < 4; i++) tone(out, t, i * 0.7, 0.35, 425.0, 0.6, 0.01, 0.02, [1.0]);
    return 2.8;
  },
  // Two descending soft notes: "call ended".
  end(out, t) {
    mallet(out, t, 0.00, 783.99, 0.45, 0.8);
    mallet(out, t, 0.18, 587.33, 0.60, 0.8);
    return 0.85;
  }
};

function render(key, gainNode, at) {
  const recipe = RECIPES[key];
  if (!recipe) return 0;
  const level = ctx.createGain();
  level.gain.value = LEVEL[key] || 0.8;
  level.connect(gainNode);
  return recipe(level, at);
}

function freshGain() {
  const g = ctx.createGain();
  g.gain.value = 1;
  g.connect(master);
  return g;
}

function silence(g) {
  if (!g) return;
  try {
    const now = ctx.currentTime;
    g.gain.cancelScheduledValues(now);
    g.gain.setValueAtTime(g.gain.value, now);
    g.gain.linearRampToValueAtTime(0, now + 0.04);
    setTimeout(() => { try { g.disconnect(); } catch (e) { /* already gone */ } }, 120);
  } catch (e) { /* context closed */ }
}

// One message tone (overlapping calls are fine; they are short).
export function play(key) {
  if (!ensure()) return 0;
  return render(key, master, ctx.currentTime + 0.01);
}

// One-shot that replaces any loop or previous preview (busy, call ended, settings previews).
export function playOnce(key) {
  if (!ensure()) return 0;
  stop();
  once = freshGain();
  return render(key, once, ctx.currentTime + 0.02);
}

// Loops a ringtone or the ringback until stop().
export function startLoop(key) {
  if (!ensure() || !RECIPES[key]) return;
  if (loop && loop.key === key) return;
  stop();
  const gain = freshGain();
  const cycle = CYCLES[key] || 3.0;
  let next = ctx.currentTime + 0.05;
  const state = { key, gain, timer: 0 };
  const schedule = () => {
    // Keep ~1.5 cycles scheduled ahead so timer throttling in background tabs does not create gaps.
    while (next < ctx.currentTime + cycle * 1.5) {
      render(key, gain, next);
      next += cycle;
    }
  };
  schedule();
  state.timer = setInterval(schedule, 500);
  loop = state;
}

export function stop() {
  if (loop) {
    clearInterval(loop.timer);
    silence(loop.gain);
    loop = null;
  }
  if (once) {
    silence(once);
    once = null;
  }
}
