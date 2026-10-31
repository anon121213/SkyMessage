// SkyMessage sounds through WebAudio. The packs (wood, bells, piano) are our own synthesis from first principles —
// no samples, no third-party audio — rendered by tools/tones to ../sounds/<pack>/<key>.ogg (the same files Android's
// notification channels use). The user's own sounds come in as bytes (setCustom). Every file is fetched and decoded
// once, then played from memory; a ringtone or the ringback file is exactly one cycle and loops without a seam.
//
// API: play(key, pack) · playOnce(key, pack, limit) · startLoop(key, pack) · stop() · unlock()
//      preload(keys, pack) · setCustom(slot, id, bytes) → seconds | null · hasCustom(slot, id) · clearCustom(slot)
//      measure(bytes) → seconds | null (decodes without keeping anything)
//   pack keys: message tones chime, pop, glass · ringtones classic, soft, bright · call progress ringback, busy, end
//   the user's own sounds: "custom:message", "custom:ringtone" · pack: "wood" (default), "bells", "piano"

const SOUNDS = new URL('../sounds/', import.meta.url);
const DEFAULT_PACK = 'wood';
// The packs sit at about -11 LUFS with peaks under -1.5 dBTP; in-app sounds play a little under that.
const MASTER = 0.8;
// The user's own files can be mastered far louder than our tones: their loudest 200 ms is brought down to about
// this RMS level (dBFS), never up.
const CUSTOM_TARGET_DB = -14;

let ctx = null;
let master = null;
let manifest = null;           // Promise<{ rate, packs: { [pack]: { [key]: { samples, loop?, hash } } } } | null>
const buffers = new Map();     // "pack/key" -> Promise<AudioBuffer | null>
const custom = new Map();      // slot -> { id, buffer, gain }
let loop = null;               // { source, gain } of the playing loop
let loopId = null;             // what startLoop() was asked for last (also while it loads)
let once = null;               // gain node of the last one-shot that replaces others (previews, busy, call ended)
let generation = 0;            // bumped by stop(): a loop or preview still loading when it changes never starts

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
    master.gain.value = MASTER;
    master.connect(ctx.destination);
  }
  if (ctx.state === 'suspended') ctx.resume().catch(() => { });
  return ctx;
}

// Browsers only start audio after a user gesture: resume on the first interaction.
['pointerdown', 'keydown', 'touchend'].forEach((type) =>
  window.addEventListener(type, () => { if (ctx && ctx.state === 'suspended') ctx.resume().catch(() => { }); }, { capture: true, passive: true }));

export function unlock() { ensure(); }

// ---- Loading ------------------------------------------------------------------------------------------------------

function loadManifest() {
  if (!manifest) {
    manifest = fetch(new URL('sounds.json', SOUNDS))
      .then((r) => (r.ok ? r.json() : null))
      .catch(() => null)
      .then((m) => {
        if (!m) manifest = null;   // try again next time (an offline web client)
        return m;
      });
  }
  return manifest;
}

function decode(arrayBuffer) {
  // Older WebKit has only the callback form.
  return new Promise((resolve, reject) => {
    const p = ctx.decodeAudioData(arrayBuffer, resolve, reject);
    if (p && p.then) p.then(resolve, reject);
  });
}

function entryOf(m, pack, key) {
  return m && m.packs && m.packs[pack] ? m.packs[pack][key] || null : null;
}

// Promise<AudioBuffer | null>; failures are not kept, so a later call tries again.
function load(pack, key) {
  const id = `${pack}/${key}`;
  let pending = buffers.get(id);
  if (!pending) {
    pending = loadManifest().then(async (m) => {
      const entry = entryOf(m, pack, key);
      if (!entry) return null;
      // The hash versions the URL: the file names stay fixed, so caches must not serve an older sound.
      const response = await fetch(new URL(`${pack}/${key}.ogg?v=${entry.hash || ''}`, SOUNDS));
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return decode(await response.arrayBuffer());
    }).catch((e) => {
      buffers.delete(id);
      console.warn('[sounds] cannot load', id, e);
      return null;
    });
    buffers.set(id, pending);
  }
  return pending;
}

function packOf(pack) {
  return typeof pack === 'string' && /^[a-z]+$/.test(pack) ? pack : DEFAULT_PACK;
}

// { buffer, gain, loopEnd } for a key, or null.
async function resolve(key, pack) {
  if (typeof key !== 'string') return null;
  if (key.startsWith('custom:')) {
    const item = custom.get(key.substring(7));
    return item ? { buffer: item.buffer, gain: item.gain, loopEnd: item.buffer.duration } : null;
  }
  const p = packOf(pack);
  const [m, buffer] = await Promise.all([loadManifest(), load(p, key)]);
  if (!buffer) return null;
  const entry = entryOf(m, p, key);
  // A loop file is exactly one cycle; the manifest has its length, in case a decoder pads the end.
  const cycle = entry && entry.loop ? entry.samples / m.rate : buffer.duration;
  return { buffer, gain: 1, loopEnd: Math.min(cycle, buffer.duration) };
}

// Fetches and decodes sounds ahead of their first use (the selected message tone and ringtone).
export function preload(keys, pack) {
  if (!ensure()) return;
  (keys || []).forEach((key) => { if (typeof key === 'string' && !key.startsWith('custom:')) load(packOf(pack), key); });
}

// ---- The user's own sounds -------------------------------------------------------------------------------------------

// RMS level (dBFS) of the loudest 200 ms, all channels mixed.
function loudestDb(buffer) {
  const n = buffer.length;
  const channels = [];
  for (let c = 0; c < buffer.numberOfChannels; c++) channels.push(buffer.getChannelData(c));
  const w = Math.max(1, Math.min(n, Math.round(buffer.sampleRate * 0.2)));
  const power = (i) => {
    let s = 0;
    for (const data of channels) s += data[i];
    s /= channels.length;
    return s * s;
  };
  let sum = 0;
  let best = 0;
  for (let i = 0; i < n; i++) {
    sum += power(i);
    if (i >= w) sum -= power(i - w);
    if (i >= w - 1 && sum > best) best = sum;
  }
  return 10 * Math.log10(Math.max(best / w, 1e-12));
}

async function decodeBytes(bytes) {
  if (!ensure() || !bytes || !bytes.byteLength) return null;
  try {
    // decodeAudioData takes (and detaches) an ArrayBuffer of its own.
    const buffer = await decode(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
    return buffer && buffer.duration > 0 ? buffer : null;
  } catch (e) {
    return null;
  }
}

export async function measure(bytes) {
  const buffer = await decodeBytes(bytes);
  return buffer ? buffer.duration : null;
}

// Keeps the user's sound for a slot ("message" | "ringtone"); returns its length in seconds, or null when it cannot
// be decoded (the slot is then empty, and the caller plays the pack's sound instead).
export async function setCustom(slot, id, bytes) {
  const buffer = await decodeBytes(bytes);
  if (!buffer) {
    custom.delete(slot);
    return null;
  }
  const gain = Math.min(1, Math.pow(10, (CUSTOM_TARGET_DB - loudestDb(buffer)) / 20));
  custom.set(slot, { id, buffer, gain: Math.max(gain, 0.1) });
  return buffer.duration;
}

export function hasCustom(slot, id) {
  const item = custom.get(slot);
  return !!item && item.id === id;
}

export function clearCustom(slot) {
  custom.delete(slot);
}

// ---- Playback ------------------------------------------------------------------------------------------------------

function freshGain(level) {
  const g = ctx.createGain();
  g.gain.value = level;
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

function startSource(sound, out, looping) {
  const source = ctx.createBufferSource();
  source.buffer = sound.buffer;
  if (looping) {
    source.loop = true;
    source.loopStart = 0;
    source.loopEnd = sound.loopEnd;
  }
  source.connect(out);
  source.start(ctx.currentTime + 0.01);
  return source;
}

// The calls below return at once and load in the background (a slow fetch never holds back a later stop()); stop()
// cancels a loop or preview that is still loading.

// Whether the context runs (a browser keeps it suspended until the page has had a user gesture; the app's WebView
// does not need one).
function running() {
  if (ctx.state === 'running') return Promise.resolve(true);
  return Promise.race([
    ctx.resume().then(() => ctx.state === 'running', () => false),
    new Promise((done) => setTimeout(() => done(false), 300)),
  ]);
}

// One message tone (overlapping calls are fine; they are short). Dropped while audio cannot start yet: a tone that
// came out on the first tap, long after its message, would only confuse.
export function play(key, pack) {
  if (!ensure()) return;
  Promise.all([resolve(key, pack), running()]).then(([sound, ok]) => {
    if (!sound || !ok) return;
    const out = freshGain(sound.gain);
    const source = startSource(sound, out, false);
    source.onended = () => { try { out.disconnect(); } catch (e) { /* gone */ } };
  });
}

// One-shot that replaces any loop or previous preview (busy, call ended, settings previews: a ringtone plays one cycle).
// `limit` (seconds) fades a long file out early: a preview of the user's own ringtone need not run for a minute.
export function playOnce(key, pack, limit) {
  if (!ensure()) return;
  stop();
  const started = generation;
  resolve(key, pack).then((sound) => {
    if (!sound || started !== generation) return;
    const out = freshGain(sound.gain);
    once = out;
    const source = startSource(sound, out, false);
    if (limit > 0 && sound.buffer.duration > limit) {
      const end = ctx.currentTime + 0.01 + limit;
      out.gain.setValueAtTime(sound.gain, end - 0.6);
      out.gain.linearRampToValueAtTime(0, end);
      source.stop(end + 0.05);
    }
  });
}

// Loops a ringtone or the ringback until stop(); asking again for the loop that already plays changes nothing.
export function startLoop(key, pack) {
  if (!ensure() || typeof key !== 'string') return;
  const id = key.startsWith('custom:') ? `${key}/${(custom.get(key.substring(7)) || {}).id}` : `${packOf(pack)}/${key}`;
  if (loopId === id) return;
  stop();
  loopId = id;
  const started = generation;
  resolve(key, pack).then((sound) => {
    if (!sound || started !== generation) return;
    const gain = freshGain(sound.gain);
    loop = { gain, source: startSource(sound, gain, true) };
  });
}

export function stop() {
  generation++;
  loopId = null;
  if (loop) {
    silence(loop.gain);
    try { loop.source.stop(ctx.currentTime + 0.05); } catch (e) { /* not started */ }
    loop = null;
  }
  if (once) {
    silence(once);
    once = null;
  }
}
