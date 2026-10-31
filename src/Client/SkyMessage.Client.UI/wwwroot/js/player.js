// SkyMessage media playback (ES module, imported by Features/Chat and Features/Media). One sound at a time:
// voice messages and audio files share a single <audio>, round video notes play with sound in place. Muted
// autoplay of round notes / short videos while they are visible. Driven by data attributes rendered by .NET:
//   [data-voice=id][data-src][data-duration=ms][data-unplayed] > [data-voice-play] [data-voice-scrub] [data-voice-pos]
//   ([data-voice-speed] on voice messages: they play at the voice speed setting, audio files always at 1x)
//   [data-vnote=id] > video[data-src][data-autoplay]          video[data-src][data-autoplay] (inline videos)
// Progress is written straight into the DOM (--p, data-state, the position text) without .NET round trips, every frame
// from a smoothed clock (some WebViews move currentTime in coarse steps); .NET hears OnPlayed(mediaId) once per
// playback start and OnPlaybackError(mediaId). [data-voice-speed-chip] gets a small bump when tapped.

import { reduceMotion } from './app.js';

function noop() { }

function safeInvoke(ref, method, ...args) {
  if (!ref) return Promise.resolve();
  try {
    return ref.invokeMethodAsync(method, ...args).catch(noop);
  } catch (e) {
    return Promise.resolve();
  }
}

const listeners = new Set();
let installed = false;
let audio = null;
let rate = 1;
// { kind: 'voice' | 'note', id, scope, speed, video }
let current = null;
let frame = 0;
let scrub = null;
// Last observed playback position of the playing element: { el, media (s), at (ms), shown (s) }.
let clock = null;

function notify(method, id) {
  for (const ref of listeners) safeInvoke(ref, method, id);
}

function fmt(seconds) {
  const s = Math.max(0, Math.floor(seconds || 0));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const r = s % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`;
}

function voiceBoxes(id) {
  return document.querySelectorAll(`[data-voice="${CSS.escape(id)}"]`);
}

function durationOf(box) {
  const ms = Number(box.getAttribute('data-duration')) || 0;
  return ms / 1000;
}

function inSelection(el) { return !!(el && el.closest('[data-selecting]')); }

// ---- Voice / audio -----------------------------------------------------------------------------------------

function ensureAudio() {
  if (audio) return audio;
  audio = new Audio();
  audio.preload = 'auto';
  audio.addEventListener('play', () => { paint(); loop(); });
  audio.addEventListener('pause', paint);
  audio.addEventListener('ended', onVoiceEnded);
  audio.addEventListener('error', () => {
    if (!current || current.kind !== 'voice' || !audio.getAttribute('src')) return;
    const id = current.id;
    clearVoice(id);
    current = null;
    notify('OnPlaybackError', id);
  });
  return audio;
}

function applyRate() {
  if (!audio) return;
  // Music and other audio files have no speed button: they never take the voice message speed.
  audio.playbackRate = current && current.kind === 'voice' && current.speed ? rate : 1;
  try { audio.preservesPitch = true; } catch (e) { /* older engines */ }
}

function clearVoice(id) {
  for (const box of voiceBoxes(id)) {
    box.removeAttribute('data-state');
    const pos = box.querySelector('[data-voice-pos]');
    if (pos) pos.textContent = '';
    // The played colour fades out first (CSS); the progress resets once it is gone.
    const stamp = (box.__smClear = (box.__smClear || 0) + 1);
    setTimeout(() => {
      if (box.__smClear === stamp && !box.hasAttribute('data-state')) box.style.removeProperty('--p');
    }, 260);
  }
}

/**
 * Playback position to show now: between currentTime updates it moves on at the playback rate (never more than a
 * moment ahead, never backwards), so progress glides instead of stepping. Seeks and pauses take the real position.
 */
function smoothTime(media) {
  const actual = media.currentTime || 0;
  const now = performance.now();
  if (media.paused || media.seeking || !clock || clock.el !== media || actual < clock.media - 0.05 || actual > clock.media + 0.75) {
    clock = { el: media, media: actual, at: now, shown: actual };
    return actual;
  }
  if (actual !== clock.media) {
    clock.media = actual;
    clock.at = now;
  }
  const predicted = clock.media + ((now - clock.at) / 1000) * (media.playbackRate || 1);
  clock.shown = Math.max(clock.shown, Math.min(predicted, clock.media + 0.3));
  return clock.shown;
}

/** A tapped speed chip gives a little bump (the new speed arrives from .NET meanwhile). */
function onChipClick(e) {
  const chip = e.target && e.target.closest ? e.target.closest('[data-voice-speed-chip]') : null;
  if (!chip || reduceMotion() || typeof chip.animate !== 'function') return;
  chip.animate([{ transform: 'none' }, { transform: 'scale(1.2)', offset: 0.4 }, { transform: 'none' }], { duration: 220, easing: 'ease-out' });
}

function playVoice(box, fraction) {
  const id = box.getAttribute('data-voice');
  const src = box.getAttribute('data-src');
  if (!id || !src) return;
  stopCurrent();
  const a = ensureAudio();
  current = { kind: 'voice', id, scope: box.closest('[data-voice-scope]') || document.body, speed: box.hasAttribute('data-voice-speed') };
  a.src = src;
  applyRate();
  const total = durationOf(box);
  const start = () => {
    if (fraction > 0) {
      const d = isFinite(a.duration) && a.duration > 0 ? a.duration : total;
      if (d > 0) a.currentTime = Math.min(d - 0.05, fraction * d);
    }
    const p = a.play();
    if (p && p.catch) p.catch(() => { /* autoplay policies: the user can tap again */ });
  };
  if (fraction > 0 && a.readyState < 1) a.addEventListener('loadedmetadata', start, { once: true });
  else start();
  if (box.hasAttribute('data-unplayed')) notify('OnPlayed', id);
  paint();
}

function toggleVoice(box) {
  const id = box.getAttribute('data-voice');
  if (current && current.kind === 'voice' && current.id === id && audio) {
    if (audio.paused) {
      const p = audio.play();
      if (p && p.catch) p.catch(noop);
    } else audio.pause();
    return;
  }
  playVoice(box, 0);
}

function onVoiceEnded() {
  if (!current || current.kind !== 'voice') return;
  const id = current.id;
  const scope = current.scope;
  clearVoice(id);
  current = null;
  // Continue with the next unheard voice message below in the same list, while that list is still on screen: a
  // detached one belongs to a closed chat, where nothing could stop the playback or mark the next one played.
  if (!scope || !scope.isConnected) return;
  const all = Array.from(scope.querySelectorAll('[data-voice]'));
  const index = all.findIndex((b) => b.getAttribute('data-voice') === id);
  if (index < 0) return;
  for (let i = index + 1; i < all.length; i++) {
    const next = all[i];
    if (next.hasAttribute('data-unplayed') && next.hasAttribute('data-auto-next')) {
      playVoice(next, 0);
      return;
    }
  }
}

// ---- Round video notes with sound ------------------------------------------------------------------------------

function noteVideo(note) { return note.querySelector('video'); }

function sourceOf(video) {
  if (!video.getAttribute('src')) {
    const src = video.getAttribute('data-src');
    if (src) video.src = src;
  }
}

function toggleNote(note) {
  const video = noteVideo(note);
  if (!video) return;
  if (current && current.kind === 'note' && current.video === video) {
    if (video.paused) {
      const p = video.play();
      if (p && p.catch) p.catch(noop);
    } else video.pause();
    return;
  }
  stopCurrent();
  const id = note.getAttribute('data-vnote');
  current = { kind: 'note', id, video, note };
  sourceOf(video);
  video.loop = false;
  video.muted = false;
  try { video.currentTime = 0; } catch (e) { /* not loaded yet */ }
  note.setAttribute('data-state', 'playing');
  const p = video.play();
  if (p && p.catch) {
    p.catch(() => {
      // Sound playback was refused (no user gesture): fall back to silent.
      video.muted = true;
      const again = video.play();
      if (again && again.catch) again.catch(noop);
    });
  }
  if (!video.__smNoteBound) {
    video.__smNoteBound = true;
    video.addEventListener('ended', () => { if (current && current.video === video) onNoteEnded(); });
    video.addEventListener('pause', () => { if (current && current.video === video) paint(); });
    video.addEventListener('play', () => {
      if (current && current.video === video) {
        paint();
        loop();
      }
    });
  }
  if (note.hasAttribute('data-unplayed')) notify('OnPlayed', id);
  loop();
}

function onNoteEnded() {
  if (!current || current.kind !== 'note') return;
  const { video, note } = current;
  resetNote(video, note);
  current = null;
}

function resetNote(video, note) {
  note.removeAttribute('data-state');
  // The ring fades out (CSS) before its progress resets.
  const stamp = (note.__smClear = (note.__smClear || 0) + 1);
  setTimeout(() => {
    if (note.__smClear === stamp && !note.hasAttribute('data-state')) note.style.removeProperty('--p');
  }, 260);
  video.muted = true;
  video.loop = true;
  if (video.hasAttribute('data-autoplay') && video.hasAttribute('data-visible') && autoplayAllowed(video)) {
    const p = video.play();
    if (p && p.catch) p.catch(noop);
  } else {
    video.pause();
    try { video.currentTime = 0; } catch (e) { /* ignore */ }
  }
}

// ---- Shared ----------------------------------------------------------------------------------------------------

function stopCurrent() {
  if (!current) return;
  if (current.kind === 'voice') {
    if (audio) {
      audio.pause();
      audio.removeAttribute('src');
      try { audio.load(); } catch (e) { /* ignore */ }
    }
    clearVoice(current.id);
  } else if (current.kind === 'note') {
    resetNote(current.video, current.note);
  }
  current = null;
}

/** Did the sound playing now (voice, audio file, round note with sound) start inside `root`? */
function playsIn(root) {
  if (!current || !root) return false;
  const el = current.kind === 'voice' ? current.scope : current.note;
  return !!el && root.contains(el);
}

function paint() {
  if (!current) return;
  if (current.kind === 'voice' && audio) {
    const d = isFinite(audio.duration) && audio.duration > 0 ? audio.duration : 0;
    const t = smoothTime(audio);
    for (const box of voiceBoxes(current.id)) {
      const total = d || durationOf(box);
      box.setAttribute('data-state', audio.paused ? 'paused' : 'playing');
      box.style.setProperty('--p', total > 0 ? String(Math.min(1, t / total)) : '0');
      const pos = box.querySelector('[data-voice-pos]');
      const text = fmt(t);
      if (pos && pos.textContent !== text) pos.textContent = text;
    }
  } else if (current.kind === 'note') {
    const v = current.video;
    const d = isFinite(v.duration) && v.duration > 0 ? v.duration : 0;
    current.note.setAttribute('data-state', v.paused ? 'paused' : 'playing');
    current.note.style.setProperty('--p', d > 0 ? String(Math.min(1, smoothTime(v) / d)) : '0');
  }
}

function loop() {
  if (frame) return;
  const tick = () => {
    frame = 0;
    if (!current) return;
    paint();
    const playing = current.kind === 'voice' ? audio && !audio.paused : current.video && !current.video.paused;
    if (playing || scrub) frame = requestAnimationFrame(tick);
  };
  frame = requestAnimationFrame(tick);
}

function onClick(e) {
  const target = e.target;
  if (!target || !target.closest) return;
  const play = target.closest('[data-voice-play]');
  if (play) {
    const box = play.closest('[data-voice]');
    if (box && !inSelection(box)) {
      e.preventDefault();
      toggleVoice(box);
    }
    return;
  }
  const note = target.closest('[data-vnote]');
  if (note && !inSelection(note) && !target.closest('button, a, [data-no-play]')) {
    e.preventDefault();
    toggleNote(note);
  }
}

function fractionAt(el, x) {
  const r = el.getBoundingClientRect();
  if (r.width <= 0) return 0;
  return Math.min(1, Math.max(0, (x - r.left) / r.width));
}

function onPointerDown(e) {
  if (e.button !== undefined && e.button !== 0) return;
  const target = e.target;
  if (!target || !target.closest) return;
  const area = target.closest('[data-voice-scrub]');
  if (!area) return;
  const box = area.closest('[data-voice]');
  if (!box || inSelection(box)) return;
  const id = box.getAttribute('data-voice');
  const fraction = fractionAt(area, e.clientX);
  if (!current || current.kind !== 'voice' || current.id !== id) {
    playVoice(box, fraction);
    return;
  }
  scrub = { area, id: e.pointerId };
  try { area.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
  seekTo(fraction);
  e.preventDefault();
}

function seekTo(fraction) {
  if (!audio) return;
  const d = isFinite(audio.duration) && audio.duration > 0 ? audio.duration : 0;
  if (d > 0) audio.currentTime = Math.min(d - 0.05, fraction * d);
  paint();
}

function onPointerMove(e) {
  if (!scrub || e.pointerId !== scrub.id) return;
  seekTo(fractionAt(scrub.area, e.clientX));
}

function onPointerUp(e) {
  if (!scrub || e.pointerId !== scrub.id) return;
  scrub = null;
}

function install() {
  if (installed) return;
  installed = true;
  document.addEventListener('click', onClick);
  // Capture: Blazor handles the chip's own click (and stops it there).
  document.addEventListener('click', onChipClick, true);
  document.addEventListener('pointerdown', onPointerDown);
  document.addEventListener('pointermove', onPointerMove, { passive: true });
  document.addEventListener('pointerup', onPointerUp, { passive: true });
  document.addEventListener('pointercancel', onPointerUp, { passive: true });
}

// ---- Muted autoplay while visible ----------------------------------------------------------------------------------

const roots = new Set();

function autoplayAllowed(video) {
  for (const r of roots) {
    if (r.enabled && r.root.contains(video)) return true;
  }
  return false;
}

/**
 * Plays video[data-autoplay] inside `root` muted while at least 60% visible; pauses it otherwise. Disposing it means
 * the screen is gone: sound playback that started inside `root` stops too.
 */
export function observe(root, enabled) {
  install();
  if (!root || !('IntersectionObserver' in window)) return { setEnabled: noop, dispose: noop };
  const entry = { root, enabled: !!enabled };
  roots.add(entry);
  const watched = new Set();

  const startMuted = (video) => {
    if (current && current.video === video) return;
    sourceOf(video);
    video.muted = true;
    video.loop = true;
    video.playsInline = true;
    const p = video.play();
    if (p && p.catch) p.catch(noop);
  };

  const io = new IntersectionObserver((entries) => {
    for (const item of entries) {
      const video = item.target;
      const visible = item.isIntersecting && item.intersectionRatio >= 0.6;
      if (visible) video.setAttribute('data-visible', '');
      else video.removeAttribute('data-visible');
      if (current && current.video === video) {
        if (!item.isIntersecting) video.pause();
        continue;
      }
      // data-autoplay is re-checked: .NET drops it when autoplay stops being allowed (settings, data policy).
      if (visible && entry.enabled && video.hasAttribute('data-autoplay')) startMuted(video);
      else if (!video.paused) video.pause();
    }
  }, { threshold: [0, 0.6] });

  let scanFrame = 0;
  const scan = () => {
    scanFrame = 0;
    for (const video of Array.from(watched)) {
      if (!video.isConnected) {
        io.unobserve(video);
        watched.delete(video);
      }
    }
    for (const video of root.querySelectorAll('video[data-autoplay]')) {
      if (watched.has(video)) continue;
      watched.add(video);
      io.observe(video);
    }
  };
  const mo = new MutationObserver(() => { if (!scanFrame) scanFrame = requestAnimationFrame(scan); });
  mo.observe(root, { childList: true, subtree: true });
  scan();

  return {
    setEnabled(value) {
      entry.enabled = !!value;
      for (const video of watched) {
        if (current && current.video === video) continue;
        if (entry.enabled && video.hasAttribute('data-visible') && video.hasAttribute('data-autoplay')) startMuted(video);
        else if (!video.paused) video.pause();
      }
    },
    dispose() {
      roots.delete(entry);
      mo.disconnect();
      io.disconnect();
      if (scanFrame) cancelAnimationFrame(scanFrame);
      // Nothing left on screen could stop that sound (or would hear what it marks played).
      if (playsIn(root)) stopCurrent();
      for (const video of watched) {
        if (!(current && current.video === video)) video.pause();
      }
      watched.clear();
    }
  };
}

// ---- API -------------------------------------------------------------------------------------------------------------

/** Registers a .NET listener (OnPlayed, OnPlaybackError) and installs the document listeners once. */
export function init(ref) {
  install();
  if (ref) listeners.add(ref);
}

export function release(ref) {
  listeners.delete(ref);
}

export function setRate(value) {
  const next = Number(value);
  rate = isFinite(next) && next > 0 ? Math.min(3, Math.max(0.5, next)) : 1;
  if (current && current.kind === 'voice') applyRate();
}

/** Stops any sound playback (voice, audio, round note with sound). */
export function stop() {
  stopCurrent();
}

export function pause() {
  if (!current) return;
  if (current.kind === 'voice' && audio) audio.pause();
  else if (current.kind === 'note' && current.video) current.video.pause();
}

export function playingId() {
  return current ? current.id : null;
}
