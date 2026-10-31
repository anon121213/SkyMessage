// SkyMessage composer recording (ES module, imported by Features/Chat): voice messages and round video notes with
// getUserMedia + MediaRecorder. Gesture on the record button: hold = record, slide left = cancel, slide up = lock,
// release = send, a short tap = switch between voice and video note. The live UI is written straight into the DOM
// inside `root` ([data-rec-time] timer, [data-rec-wave] canvas, [data-rec-preview] canvas, [data-rec-ring] progress,
// [data-rec-slide] bar / overlay, [data-rec-lock] lock hint); the gesture state is mirrored on `root` for the CSS
// (data-rec="pressing|starting|recording|locked|stopping", --slide and --lift 0..1 for the finger) and the voice level
// on [data-rec-halo] (--level 0..1). .NET hears OnRecordTap(), OnRecordState(state, mode, info) and
// OnRecordDone(result | null, reason) with the blob kept in chat.js's registry (streamed to .NET on demand). When a
// recording ends, the bar or overlay leaves the way it went (sent, binned, too short) on a copy from composer-fx.js,
// since .NET removes the real one at once. The live wave is painted with the composer's colors: a gradient from the
// canvas's --wave-a (older bars, left) to --wave-b (newest, right), falling back to its CSS color.
import { keepBlob } from './chat.js';
import { pause as pausePlayback } from './player.js';
import { reduceMotion } from './app.js';
import { ghost, play, part, EASE_IN, EASE_OUT } from './composer-fx.js';

const HOLD_MS = 220;
const CANCEL_PX = 110;
const LOCK_PX = 80;
const MIN_MS = 700;
const NOTE_SIZE = 480;
const NOTE_MAX_MS = 60000;
const VOICE_MAX_MS = 3600000;
const WAVE_SAMPLES = 100;
const MAX_LEVELS = 24000;
// Live waveform: one bar per BAR_MS flows in from the right (CSS px).
const BAR_W = 3;
const BAR_GAP = 2;
const BAR_MS = 90;
const MAX_BARS = 240;

const VOICE_TYPES = ['audio/webm;codecs=opus', 'audio/ogg;codecs=opus', 'audio/mp4;codecs=mp4a.40.2', 'audio/mp4', 'audio/aac', 'audio/webm'];
const VIDEO_TYPES = ['video/mp4;codecs=avc1.42E01E,mp4a.40.2', 'video/mp4;codecs=avc1,mp4a', 'video/mp4', 'video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm'];

function noop() { }

function safeInvoke(ref, method, ...args) {
  if (!ref) return Promise.resolve();
  try {
    return ref.invokeMethodAsync(method, ...args).catch(noop);
  } catch (e) {
    return Promise.resolve();
  }
}

const clamp01 = (v) => Math.min(1, Math.max(0, v));

export function isSupported() {
  return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder);
}

function pickType(candidates) {
  if (!window.MediaRecorder || typeof MediaRecorder.isTypeSupported !== 'function') return '';
  for (const type of candidates) {
    try { if (MediaRecorder.isTypeSupported(type)) return type; } catch (e) { /* ignore */ }
  }
  return '';
}

function baseType(type, fallback) {
  const t = (type || '').split(';')[0].trim().toLowerCase();
  return t || fallback;
}

function extensionOf(type) {
  if (type.endsWith('/webm')) return 'webm';
  if (type.endsWith('/ogg')) return 'ogg';
  if (type === 'audio/mp4') return 'm4a';
  if (type === 'audio/aac') return 'aac';
  if (type === 'video/mp4') return 'mp4';
  return type.split('/')[1] || 'bin';
}

function stamp() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function fmt(ms) {
  const s = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const r = s % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`;
}

function stopTracks(stream) {
  if (!stream) return;
  for (const track of stream.getTracks()) {
    try { track.stop(); } catch (e) { /* ignore */ }
  }
}

function addBar(ctx, x, h, width, height) {
  const bh = Math.max(width, height);
  const y = (h - bh) / 2;
  if (typeof ctx.roundRect === 'function') ctx.roundRect(x, y, width, bh, width / 2);
  else ctx.rect(x, y, width, bh);
}

function waveformOf(levels) {
  const out = new Array(WAVE_SAMPLES).fill(0);
  const n = levels.length;
  if (!n) return out;
  let max = 0.02;
  for (let i = 0; i < n; i++) if (levels[i] > max) max = levels[i];
  for (let i = 0; i < WAVE_SAMPLES; i++) {
    const from = Math.floor((i * n) / WAVE_SAMPLES);
    const to = Math.max(from + 1, Math.floor(((i + 1) * n) / WAVE_SAMPLES));
    let peak = 0;
    for (let j = from; j < to && j < n; j++) if (levels[j] > peak) peak = levels[j];
    out[i] = Math.round(clamp01(peak / max) * 255);
  }
  return out;
}

/**
 * Binds the record button. `options`: { mode: 'voice' | 'video', microphoneId, cameraId, echoCancellation,
 * noiseSuppression, autoGainControl, mirror }. Returns { configure(options), stop(), cancel(), flip(), dispose() }.
 */
export function attach(button, root, ref, options) {
  if (!button) return { configure: noop, stop: noop, cancel: noop, flip: noop, dispose: noop };
  let opts = Object.assign({}, options || {});
  let mode = opts.mode === 'video' ? 'video' : 'voice';
  // idle | pressing | starting | recording | locked | stopping (see setState)
  let state = 'idle';
  let pointerId = null;
  let x0 = 0;
  let y0 = 0;
  let pressTimer = 0;
  let shakeTimer = 0;
  // Set while the devices are still opening: 'short' (released too early) or 'cancel'.
  let abortReason = null;
  let session = null;
  let audioCtx = null;
  let disposed = false;

  const find = (selector) => (root ? root.querySelector(selector) : null);
  const setVar = (selector, name, value) => {
    const el = find(selector);
    if (el) el.style.setProperty(name, String(value));
  };
  // `root` is rendered by Blazor without a style attribute, so its inline custom properties are ours.
  const setRootVar = (name, value) => {
    if (root) root.style.setProperty(name, String(value));
  };

  // The CSS follows the gesture without a round trip to .NET (the button grows while held, follows the finger).
  function setState(next) {
    state = next;
    if (!root) return;
    if (next === 'idle') {
      root.removeAttribute('data-rec');
      root.style.removeProperty('--slide');
      root.style.removeProperty('--lift');
      const halo = find('[data-rec-halo]');
      if (halo) halo.style.removeProperty('--level');
    } else {
      root.setAttribute('data-rec', next);
    }
  }

  function primeAudioContext() {
    // Created inside the gesture so iOS lets it run.
    try {
      if (!audioCtx) {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        if (Ctor) audioCtx = new Ctor();
      }
      if (audioCtx && audioCtx.state === 'suspended') audioCtx.resume().catch(noop);
    } catch (e) {
      audioCtx = null;
    }
  }

  function audioConstraints() {
    const a = {
      echoCancellation: opts.echoCancellation !== false,
      noiseSuppression: opts.noiseSuppression !== false,
      autoGainControl: opts.autoGainControl !== false,
      channelCount: 1,
    };
    if (opts.microphoneId) a.deviceId = { ideal: opts.microphoneId };
    return a;
  }

  function videoConstraints(deviceId, exact, facing) {
    const v = { width: { ideal: 720 }, height: { ideal: 720 }, aspectRatio: { ideal: 1 }, frameRate: { ideal: 30, max: 30 } };
    if (deviceId) v.deviceId = exact ? { exact: deviceId } : { ideal: deviceId };
    else v.facingMode = { ideal: facing || 'user' };
    return v;
  }

  function info() {
    return { canFlip: !!(session && session.canFlip), mode };
  }

  async function start() {
    if (!isSupported()) {
      setState('idle');
      safeInvoke(ref, 'OnRecordDone', null, 'error:NotSupportedError');
      return;
    }
    setState('starting');
    abortReason = null;
    // A voice message or round note playing now would end up in the recording (and talk over the user).
    pausePlayback();
    const recordingMode = mode;
    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia(recordingMode === 'video'
        ? { audio: audioConstraints(), video: videoConstraints(opts.cameraId, false, 'user') }
        : { audio: audioConstraints() });
    } catch (err) {
      setState('idle');
      // The refusal wins over 'short'/'cancel': the runtime permission dialog itself cancels the touch (pointercancel),
      // and after "Deny" the user must hear that access is missing, not that the button was released too early.
      if (!disposed) safeInvoke(ref, 'OnRecordDone', null, 'error:' + ((err && err.name) || 'Error'));
      return;
    }
    if (disposed || state !== 'starting' || abortReason) {
      stopTracks(stream);
      if (!disposed && abortReason) {
        setState('idle');
        safeInvoke(ref, 'OnRecordDone', null, abortReason);
      }
      return;
    }
    let created;
    try {
      created = recordingMode === 'video' ? await createVideoSession(stream) : createVoiceSession(stream);
    } catch (err) {
      stopTracks(stream);
      setState('idle');
      if (!disposed) safeInvoke(ref, 'OnRecordDone', null, 'error:' + ((err && err.name) || 'Error'));
      return;
    }
    if (disposed || abortReason) {
      cleanup(created);
      setState('idle');
      if (!disposed) safeInvoke(ref, 'OnRecordDone', null, abortReason);
      return;
    }
    session = created;
    setState('recording');
    session.startedAt = performance.now();
    try {
      session.recorder.start(1000);
    } catch (err) {
      cleanup(session);
      session = null;
      setState('idle');
      safeInvoke(ref, 'OnRecordDone', null, 'error:' + ((err && err.name) || 'Error'));
      return;
    }
    safeInvoke(ref, 'OnRecordState', 'recording', recordingMode, info());
    loop();
  }

  function makeRecorder(stream, types, bitrates) {
    const type = pickType(types);
    const config = Object.assign({}, bitrates);
    if (type) config.mimeType = type;
    let recorder;
    try {
      recorder = new MediaRecorder(stream, config);
    } catch (e) {
      recorder = new MediaRecorder(stream);
    }
    return { recorder, type: type || recorder.mimeType || '' };
  }

  function createVoiceSession(stream) {
    const s = { mode: 'voice', stream, chunks: [], levels: [], pausedTotal: 0, pausedAt: 0, canFlip: false };
    const made = makeRecorder(stream, VOICE_TYPES, { audioBitsPerSecond: 64000 });
    s.recorder = made.recorder;
    s.type = made.type;
    s.recorder.ondataavailable = (e) => { if (e.data && e.data.size) s.chunks.push(e.data); };
    s.recorder.onerror = () => finish(false, 'error:RecorderError');
    if (audioCtx) {
      try {
        s.source = audioCtx.createMediaStreamSource(stream);
        s.analyser = audioCtx.createAnalyser();
        s.analyser.fftSize = 1024;
        s.source.connect(s.analyser);
        s.buffer = new Uint8Array(s.analyser.fftSize);
      } catch (e) {
        s.analyser = null;
      }
    }
    for (const track of stream.getAudioTracks()) track.addEventListener('ended', () => finish(true));
    return s;
  }

  async function createVideoSession(stream) {
    const s = { mode: 'video', stream, chunks: [], levels: [], pausedTotal: 0, pausedAt: 0, mirror: false };
    s.source = document.createElement('video');
    s.source.muted = true;
    s.source.playsInline = true;
    s.source.setAttribute('playsinline', '');
    s.source.srcObject = new MediaStream(stream.getVideoTracks());
    try {
      await Promise.race([s.source.play(), new Promise((resolve) => setTimeout(resolve, 1500))]);
    } catch (e) { /* frames still arrive once metadata loads */ }
    s.canvas = document.createElement('canvas');
    s.canvas.width = NOTE_SIZE;
    s.canvas.height = NOTE_SIZE;
    s.ctx = s.canvas.getContext('2d');
    s.mirror = isFrontCamera(stream.getVideoTracks()[0], true) && opts.mirror !== false;
    let recordStream;
    if (typeof s.canvas.captureStream === 'function') {
      drawFrame(s);
      const canvasStream = s.canvas.captureStream(30);
      recordStream = new MediaStream([...canvasStream.getVideoTracks(), ...stream.getAudioTracks()]);
      s.canvasStream = canvasStream;
      s.canFlip = true;
    } else {
      // No canvas capture: record the camera directly (no flipping mid-recording).
      recordStream = stream;
      s.canFlip = false;
    }
    const made = makeRecorder(recordStream, VIDEO_TYPES, { videoBitsPerSecond: 1000000, audioBitsPerSecond: 64000 });
    s.recorder = made.recorder;
    s.type = made.type;
    s.recorder.ondataavailable = (e) => { if (e.data && e.data.size) s.chunks.push(e.data); };
    s.recorder.onerror = () => finish(false, 'error:RecorderError');
    for (const track of stream.getAudioTracks()) track.addEventListener('ended', () => finish(true));
    s.drawTimer = setInterval(() => drawFrame(s), 1000 / 30);
    return s;
  }

  function isFrontCamera(track, assume) {
    if (!track) return assume;
    try {
      const facing = track.getSettings ? track.getSettings().facingMode : undefined;
      if (facing) return facing === 'user';
    } catch (e) { /* ignore */ }
    return assume;
  }

  function drawFrame(s) {
    const v = s.source;
    if (!v || !s.ctx) return;
    const vw = v.videoWidth;
    const vh = v.videoHeight;
    if (!vw || !vh) return;
    const side = Math.min(vw, vh);
    const sx = (vw - side) / 2;
    const sy = (vh - side) / 2;
    s.ctx.save();
    if (s.mirror) s.ctx.setTransform(-1, 0, 0, 1, NOTE_SIZE, 0);
    s.ctx.drawImage(v, sx, sy, side, side, 0, 0, NOTE_SIZE, NOTE_SIZE);
    s.ctx.restore();
    const preview = find('canvas[data-rec-preview]');
    if (preview) {
      if (preview.width !== NOTE_SIZE) {
        preview.width = NOTE_SIZE;
        preview.height = NOTE_SIZE;
      }
      const pctx = preview.getContext('2d');
      if (pctx) pctx.drawImage(s.canvas, 0, 0);
    }
  }

  function elapsed(s) {
    if (!s || !s.startedAt) return 0;
    const now = s.pausedAt || performance.now();
    return Math.max(0, now - s.startedAt - s.pausedTotal);
  }

  function sampleLevel(s) {
    if (!s.analyser) return 0;
    s.analyser.getByteTimeDomainData(s.buffer);
    let peak = 0;
    for (let i = 0; i < s.buffer.length; i++) {
      const v = Math.abs(s.buffer[i] - 128);
      if (v > peak) peak = v;
    }
    return peak / 128;
  }

  // Bars of BAR_MS each flow in from the right edge: the one being filled slides in and grows while the older ones
  // move left at a steady pace and fade out at the left edge (one path, one fill per frame).
  function drawWave(s, ms) {
    const canvas = find('canvas[data-rec-wave]');
    if (!canvas) return;
    const ratio = window.devicePixelRatio || 1;
    const w = Math.max(1, Math.round(canvas.clientWidth * ratio));
    const h = Math.max(1, Math.round(canvas.clientHeight * ratio));
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
    }
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    // Colors come from CSS (theme tokens), re-read once a second so a theme switch mid-recording follows.
    if (!s.waveColor || ms - s.waveColorAt > 1000) {
      const style = getComputedStyle(canvas);
      const solid = style.color || '#6A4BFF';
      const from = style.getPropertyValue('--wave-a').trim() || solid;
      const to = style.getPropertyValue('--wave-b').trim() || solid;
      const key = `${from}|${to}|${w}`;
      if (key !== s.waveKey) {
        s.waveKey = key;
        try {
          const fill = ctx.createLinearGradient(0, 0, w, 0);
          fill.addColorStop(0, from);
          fill.addColorStop(1, to);
          s.waveColor = fill;
        } catch (e) {
          s.waveColor = solid;
        }
      }
      s.waveColorAt = ms;
    }
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = s.waveColor;
    const bar = BAR_W * ratio;
    const pitch = (BAR_W + BAR_GAP) * ratio;
    const bars = s.bars;
    let max = Math.max(0.08, s.barPeak);
    for (let i = Math.max(0, bars.length - 120); i < bars.length; i++) if (bars[i] > max) max = bars[i];
    const height = (level) => Math.sqrt(clamp01(level / max)) * h;
    const frac = s.reduce ? 1 : clamp01((ms - s.barAt) / BAR_MS);
    ctx.beginPath();
    let x = w - frac * pitch;
    addBar(ctx, x, h, bar, height(s.barPeak) * (0.35 + 0.65 * frac));
    for (let i = bars.length - 1; i >= 0 && x > -pitch; i--) {
      x -= pitch;
      addBar(ctx, x, h, bar, height(bars[i]));
    }
    ctx.fill();
    const fade = Math.min(w / 4, 20 * ratio);
    const gradient = ctx.createLinearGradient(0, 0, fade, 0);
    gradient.addColorStop(0, 'rgba(0, 0, 0, 1)');
    gradient.addColorStop(1, 'rgba(0, 0, 0, 0)');
    ctx.save();
    ctx.globalCompositeOperation = 'destination-out';
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, fade, h);
    ctx.restore();
  }

  function loop() {
    const s = session;
    if (!s) return;
    s.reduce = reduceMotion();
    s.bars = [];
    s.barPeak = 0;
    s.barAt = 0;
    s.glow = 0;
    s.glowShown = -1;
    // Only the halo uses the level: set it there, not on the root, so a frame restyles one element.
    s.halo = find('[data-rec-halo]');
    const tick = () => {
      if (session !== s || (state !== 'recording' && state !== 'locked')) return;
      const ms = elapsed(s);
      if (!s.pausedAt && s.mode === 'voice') {
        const level = sampleLevel(s);
        s.levels.push(level);
        if (s.levels.length > MAX_LEVELS) {
          const merged = [];
          for (let i = 0; i < s.levels.length; i += 2) merged.push(Math.max(s.levels[i], s.levels[i + 1] || 0));
          s.levels = merged;
        }
        s.barPeak = Math.max(s.barPeak, level);
        if (ms - s.barAt > BAR_MS * MAX_BARS) s.barAt = ms - BAR_MS * MAX_BARS;
        while (ms - s.barAt >= BAR_MS) {
          s.bars.push(s.barPeak);
          s.barPeak = level;
          s.barAt += BAR_MS;
        }
        if (s.bars.length > MAX_BARS) s.bars.splice(0, s.bars.length - MAX_BARS);
        drawWave(s, ms);
        // The halo around the button breathes with the voice (smoothed; static when motion is reduced).
        if (!s.reduce) {
          s.glow += (Math.min(1, level * 1.8) - s.glow) * 0.22;
          if (s.halo && Math.abs(s.glow - s.glowShown) > 0.02) {
            s.glowShown = s.glow;
            s.halo.style.setProperty('--level', s.glow.toFixed(3));
          }
        }
      }
      const time = find('[data-rec-time]');
      const text = fmt(ms);
      if (time && time.textContent !== text) time.textContent = text;
      if (s.mode === 'video') setVar('[data-rec-ring]', '--rec-p', clamp01(ms / NOTE_MAX_MS).toFixed(4));
      if (ms >= (s.mode === 'video' ? NOTE_MAX_MS : VOICE_MAX_MS)) {
        finish(true);
        return;
      }
      s.frame = requestAnimationFrame(tick);
    };
    s.frame = requestAnimationFrame(tick);
  }

  function cleanup(s) {
    if (!s) return;
    if (s.frame) cancelAnimationFrame(s.frame);
    if (s.drawTimer) clearInterval(s.drawTimer);
    try { if (s.source && s.source.disconnect) s.source.disconnect(); } catch (e) { /* ignore */ }
    if (s.source && s.source.tagName === 'VIDEO') {
      try { s.source.pause(); s.source.srcObject = null; } catch (e) { /* ignore */ }
    }
    stopTracks(s.canvasStream);
    stopTracks(s.stream);
  }

  async function finish(send, reason) {
    const s = session;
    if (!s || state === 'stopping') return;
    const duration = elapsed(s);
    leave(!send ? (reason === 'cancel' ? 'cancel' : 'error') : duration < MIN_MS ? 'short' : 'send', s.mode);
    setState('stopping');
    await new Promise((resolve) => {
      const timeout = setTimeout(resolve, 3000);
      s.recorder.addEventListener('stop', () => { clearTimeout(timeout); resolve(); }, { once: true });
      try {
        if (s.recorder.state !== 'inactive') s.recorder.stop();
        else { clearTimeout(timeout); resolve(); }
      } catch (e) {
        clearTimeout(timeout);
        resolve();
      }
    });
    cleanup(s);
    session = null;
    setState('idle');
    pointerId = null;
    if (disposed) return;
    safeInvoke(ref, 'OnRecordState', 'idle', s.mode, { canFlip: false, mode: s.mode });
    if (!send) {
      safeInvoke(ref, 'OnRecordDone', null, reason || 'cancel');
      return;
    }
    if (duration < MIN_MS) {
      safeInvoke(ref, 'OnRecordDone', null, 'short');
      return;
    }
    const type = baseType(s.type || s.recorder.mimeType, s.mode === 'video' ? 'video/webm' : 'audio/webm');
    const blob = new Blob(s.chunks, { type });
    if (!blob.size) {
      safeInvoke(ref, 'OnRecordDone', null, 'error:EmptyRecording');
      return;
    }
    const name = `${s.mode === 'video' ? 'video-note' : 'voice'}-${stamp()}.${extensionOf(type)}`;
    const kept = keepBlob(blob, name, type);
    safeInvoke(ref, 'OnRecordDone', {
      ref: kept.ref,
      name: kept.name,
      type: kept.type,
      size: kept.size,
      durationMs: Math.round(duration),
      waveform: s.mode === 'voice' ? waveformOf(s.levels) : null,
      width: s.mode === 'video' ? NOTE_SIZE : null,
      height: s.mode === 'video' ? NOTE_SIZE : null,
      mode: s.mode,
    }, 'ok');
  }

  function lock() {
    if (state !== 'recording') return;
    snapLock();
    setState('locked');
    if (pointerId !== null) {
      try { button.releasePointerCapture(pointerId); } catch (e) { /* ignore */ }
    }
    pointerId = null;
    setRootVar('--slide', 0);
    setRootVar('--lift', 0);
    safeInvoke(ref, 'OnRecordState', 'locked', session ? session.mode : mode, info());
  }

  // ---- Motion (docs/MOTION.md): the recording UI leaves the way the recording went ----

  /** Too short to record: the button shakes its head (no toast). */
  function shake() {
    const target = button.closest('[data-rec-action]') || button;
    clearTimeout(shakeTimer);
    target.removeAttribute('data-fx-shake');
    void target.offsetWidth;
    target.setAttribute('data-fx-shake', '');
    shakeTimer = setTimeout(() => target.removeAttribute('data-fx-shake'), 400);
  }

  /** Locked: the shackle snaps shut, the lock bumps and floats away. */
  function snapLock() {
    const el = find('[data-rec-lock]');
    const closed = part(el, 'lock-closed');
    const shut = closed ? parseFloat(getComputedStyle(closed).opacity) || 0 : 0;
    const copy = ghost(el, { vars: ['--lift'] });
    if (!copy) return;
    play(part(copy, 'lock-open'), [{ opacity: 1 - shut }, { opacity: 0 }], { duration: 90, easing: 'ease', fill: 'forwards' });
    play(part(copy, 'lock-closed'), [
      { opacity: shut, transform: 'translateY(-3px)' },
      { opacity: 1, transform: 'none' }
    ], { duration: 120, easing: EASE_OUT, fill: 'forwards' });
    play(copy, [
      { transform: 'none', opacity: 1 },
      { transform: 'scale(1.18)', opacity: 1, offset: 0.35 },
      { transform: 'none', opacity: 1, offset: 0.6 },
      { transform: 'translateY(-14px) scale(0.85)', opacity: 0 }
    ], { duration: 320, easing: 'ease-out' }, true);
  }

  /** kind: 'send' | 'cancel' | 'short' | 'error'. Runs before .NET hears about it (it removes the real elements). */
  function leave(kind, recordingMode) {
    if (kind === 'short') shake();
    if (reduceMotion()) return;
    const lockCopy = ghost(find('[data-rec-lock]'), { vars: ['--lift'] });
    if (lockCopy) play(lockCopy, [{ opacity: 1 }, { opacity: 0 }], { duration: 120, easing: 'ease' }, true);
    const bar = find('[data-rec-slide]');
    if (!bar) return;
    if (recordingMode === 'video') leaveVideo(bar, kind);
    else leaveVoice(bar, kind);
  }

  function leaveVoice(bar, kind) {
    // Start where the finger left things (faded content, half-shown bin), not from the resting look.
    const opacity = (name) => {
      const el = part(bar, name);
      const value = el ? parseFloat(getComputedStyle(el).opacity) : 1;
      return Number.isFinite(value) ? value : 1;
    };
    const binShown = opacity('bin');
    const contentShown = opacity('content');
    const tailShown = opacity('tail');
    const copy = ghost(bar, { vars: ['--slide'] });
    if (!copy) return;
    if (kind === 'send') {
      // Up into the chat, where the voice message appears.
      play(copy, [
        { transform: 'none', opacity: 1 },
        { transform: 'translateY(-14px) scale(0.97)', opacity: 0 }
      ], { duration: 200, easing: EASE_IN }, true);
      return;
    }
    if (kind !== 'cancel') {
      play(copy, [{ opacity: 1 }, { opacity: 0 }], { duration: 140, easing: 'ease' }, true);
      return;
    }
    // Binned: the recording is swept into a bin where the red dot was; the bin gulps it and drops away.
    play(part(copy, 'content'), [
      { transform: 'none', opacity: contentShown },
      { transform: 'translateX(-30px) scaleX(0.4)', opacity: 0 }
    ], { duration: 150, easing: EASE_IN, fill: 'forwards' });
    play(part(copy, 'tail'), [{ opacity: tailShown }, { opacity: 0 }], { duration: 100, easing: 'ease', fill: 'forwards' });
    play(part(copy, 'dot'), [
      { transform: 'none', opacity: 1 - binShown },
      { transform: 'scale(0)', opacity: 0 }
    ], { duration: 110, easing: EASE_IN, fill: 'forwards' });
    play(part(copy, 'bin'), [
      { transform: binShown > 0.5 ? 'none' : 'scale(0.4)', opacity: binShown, offset: 0 },
      { transform: 'scale(1.22)', opacity: 1, offset: 0.3 },
      { transform: 'rotate(-14deg)', opacity: 1, offset: 0.48 },
      { transform: 'rotate(10deg)', opacity: 1, offset: 0.64 },
      { transform: 'none', opacity: 1, offset: 0.76 },
      { transform: 'translateY(10px) scale(0.7)', opacity: 0, offset: 1 }
    ], { duration: 320, easing: 'linear', fill: 'forwards' });
    play(copy, [{ opacity: 1 }, { opacity: 1, offset: 0.7 }, { opacity: 0 }], { duration: 320, easing: 'ease' }, true);
  }

  function leaveVideo(overlay, kind) {
    const original = part(overlay, 'stage');
    const shown = original ? parseFloat(getComputedStyle(original).opacity) : 1;
    const o = Number.isFinite(shown) ? shown : 1;
    const copy = ghost(overlay, { fixed: true, vars: ['--slide'] });
    if (!copy) return;
    const stage = part(copy, 'stage');
    if (kind === 'send' && stage) {
      // The round note shrinks into the send button.
      const s = stage.getBoundingClientRect();
      const b = button.getBoundingClientRect();
      const dx = b.left + b.width / 2 - (s.left + s.width / 2);
      const dy = b.top + b.height / 2 - (s.top + s.height / 2);
      play(stage, [
        { transform: 'none', opacity: o },
        { transform: `translate(${dx}px, ${dy}px) scale(0.18)`, opacity: 0.3 * o }
      ], { duration: 280, easing: EASE_IN, fill: 'forwards' });
      play(copy, [{ opacity: 1 }, { opacity: 0 }], { duration: 280, easing: 'ease' }, true);
      return;
    }
    if (kind === 'cancel' && stage) {
      // Binned: it shakes its head while it shrinks away (keyframes carry their own easing).
      play(stage, [
        { transform: 'none', opacity: o, easing: EASE_OUT },
        { transform: 'translateX(-12px) scale(0.86)', opacity: o, offset: 0.25 },
        { transform: 'translateX(10px) scale(0.76)', opacity: o, offset: 0.5 },
        { transform: 'translateX(-4px) scale(0.68)', opacity: 0.7 * o, offset: 0.7, easing: EASE_IN },
        { transform: 'scale(0.5)', opacity: 0 }
      ], { duration: 320, easing: 'linear', fill: 'forwards' });
      play(copy, [{ opacity: 1 }, { opacity: 1, offset: 0.5 }, { opacity: 0 }], { duration: 320, easing: 'ease' }, true);
      return;
    }
    play(copy, [{ opacity: 1 }, { opacity: 0 }], { duration: 160, easing: 'ease' }, true);
  }

  async function flip() {
    const s = session;
    if (!s || s.mode !== 'video' || !s.canFlip) return;
    try {
      const devices = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === 'videoinput');
      const currentTrack = s.stream.getVideoTracks()[0];
      const currentId = currentTrack && currentTrack.getSettings ? currentTrack.getSettings().deviceId : undefined;
      let constraints;
      if (devices.length > 1 && devices.every((d) => d.deviceId)) {
        const index = devices.findIndex((d) => d.deviceId === currentId);
        const next = devices[(index + 1) % devices.length];
        constraints = videoConstraints(next.deviceId, true);
      } else {
        const facing = isFrontCamera(currentTrack, true) ? 'environment' : 'user';
        constraints = videoConstraints(null, false, facing);
      }
      const fresh = await navigator.mediaDevices.getUserMedia({ video: constraints });
      if (session !== s) {
        stopTracks(fresh);
        return;
      }
      const track = fresh.getVideoTracks()[0];
      if (currentTrack) {
        s.stream.removeTrack(currentTrack);
        try { currentTrack.stop(); } catch (e) { /* ignore */ }
      }
      s.stream.addTrack(track);
      s.source.srcObject = new MediaStream([track]);
      try { await s.source.play(); } catch (e) { /* ignore */ }
      s.mirror = isFrontCamera(track, false) && opts.mirror !== false;
    } catch (err) {
      safeInvoke(ref, 'OnRecordFlipFailed', (err && err.name) || 'Error');
    }
  }

  // ---- Gesture ----

  const onDown = (e) => {
    if (disposed || state !== 'idle' || button.disabled) return;
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    e.preventDefault();
    pointerId = e.pointerId;
    x0 = e.clientX;
    y0 = e.clientY;
    abortReason = null;
    try { button.setPointerCapture(pointerId); } catch (err) { /* ignore */ }
    if (mode === 'voice') primeAudioContext();
    setState('pressing');
    clearTimeout(pressTimer);
    pressTimer = setTimeout(() => { if (state === 'pressing') start(); }, HOLD_MS);
  };

  const onMove = (e) => {
    if (e.pointerId !== pointerId || (state !== 'recording' && state !== 'starting')) return;
    const dx = e.clientX - x0;
    const dy = e.clientY - y0;
    setRootVar('--slide', clamp01(-dx / CANCEL_PX).toFixed(3));
    setRootVar('--lift', clamp01(-dy / LOCK_PX).toFixed(3));
    if (state !== 'recording') return;
    if (dx < -CANCEL_PX) finish(false, 'cancel');
    else if (dy < -LOCK_PX && Math.abs(dy) > Math.abs(dx)) lock();
  };

  const onUp = (e) => {
    if (e.pointerId !== pointerId) return;
    pointerId = null;
    clearTimeout(pressTimer);
    if (state === 'pressing') {
      setState('idle');
      if (e.type === 'pointerup') safeInvoke(ref, 'OnRecordTap');
      return;
    }
    if (state === 'starting') {
      abortReason = abortReason || 'short';
      // Released before the microphone/camera opened: the button lets go now, not when the devices answer, and says
      // it was too short (a touch cancelled by the permission dialog is not the user's doing).
      if (root) root.setAttribute('data-rec', 'stopping');
      if (e.type === 'pointerup' && abortReason === 'short') shake();
      return;
    }
    if (state === 'recording') {
      const keep = e.type === 'pointerup' || elapsed(session) > 1500;
      finish(keep, keep ? undefined : 'cancel');
    }
  };

  // The release that started or ended a recording must not "click" the button; a keyboard "click" switches mode.
  const onClick = (e) => {
    e.preventDefault();
    e.stopPropagation();
    if (e.detail === 0 && state === 'idle' && !disposed) safeInvoke(ref, 'OnRecordTap');
  };
  const onContextMenu = (e) => e.preventDefault();

  // Leaving the app mid-recording: pause (the finger is gone, so lock), continue when back if the devices survived.
  const onVisibility = () => {
    const s = session;
    if (!s) return;
    if (document.visibilityState === 'hidden') {
      if (state === 'recording') lock();
      if (state === 'locked' && !s.pausedAt && s.recorder.state === 'recording') {
        try {
          s.recorder.pause();
          s.pausedAt = performance.now();
        } catch (e) { /* pause unsupported: keep recording */ }
      }
    } else if (s.pausedAt) {
      const alive = s.stream.getTracks().every((t) => t.readyState === 'live');
      s.pausedTotal += performance.now() - s.pausedAt;
      s.pausedAt = 0;
      if (!alive) {
        finish(true);
        return;
      }
      try { s.recorder.resume(); } catch (e) { /* ignore */ }
    }
  };

  button.addEventListener('pointerdown', onDown);
  button.addEventListener('pointermove', onMove);
  button.addEventListener('pointerup', onUp);
  button.addEventListener('pointercancel', onUp);
  button.addEventListener('click', onClick);
  button.addEventListener('contextmenu', onContextMenu);
  document.addEventListener('visibilitychange', onVisibility);

  return {
    configure(o) {
      opts = Object.assign({}, opts, o || {});
      if (o && o.mode && state === 'idle') mode = o.mode === 'video' ? 'video' : 'voice';
    },
    /** Locked mode: stop and send. */
    stop() { finish(true); },
    cancel() {
      if (state === 'pressing') {
        clearTimeout(pressTimer);
        setState('idle');
        return;
      }
      if (state === 'starting') {
        abortReason = 'cancel';
        return;
      }
      finish(false, 'cancel');
    },
    flip() { return flip(); },
    dispose() {
      disposed = true;
      clearTimeout(pressTimer);
      clearTimeout(shakeTimer);
      if (session) {
        const s = session;
        session = null;
        try { if (s.recorder.state !== 'inactive') s.recorder.stop(); } catch (e) { /* ignore */ }
        cleanup(s);
      }
      setState('idle');
      button.removeEventListener('pointerdown', onDown);
      button.removeEventListener('pointermove', onMove);
      button.removeEventListener('pointerup', onUp);
      button.removeEventListener('pointercancel', onUp);
      button.removeEventListener('click', onClick);
      button.removeEventListener('contextmenu', onContextMenu);
      document.removeEventListener('visibilitychange', onVisibility);
      if (audioCtx) {
        try { audioCtx.close(); } catch (e) { /* ignore */ }
        audioCtx = null;
      }
    }
  };
}
