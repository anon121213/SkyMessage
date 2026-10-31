// SkyMessage media viewer gestures and motion (ES module, imported by Features/Media/MediaViewer). Inside `stage`: the
// element that follows swipes is [data-track]; the shown item is a [data-frame] box with the item's proportions that
// holds the element that zooms ([data-zoom], photos) or the <video>. Pinch, wheel and double-tap zoom (1x–5x) with
// panning, horizontal swipe = previous/next (the next item settles with a spring), swipe down = close (the picture
// shrinks under the finger and the backdrop thins out with the drag), arrow keys and +/- on desktop.
// Opening grows the picture out of the thumbnail that was tapped and closing flies it back there while that thumbnail
// is still on screen (found by [data-viewer-thumb=id] or an <img>/<video> whose URL carries the media id, preferring
// the copy under the last press that track() saw); otherwise the viewer fades.
// Videos have no native controls: the viewer's own ([data-vplay] buttons, a [data-vbar] with [data-vscrub], [data-vcur]
// and [data-vdur]) are driven here — data-vstate on the viewer (loading | waiting | playing | paused | ended), the
// progress as --vp (0..1) on the bar, painted every frame while playing; drags on [data-vscrub] seek; Space plays or
// pauses, arrows on the focused scrubber seek by 5 s.
// .NET hears OnNavigate(delta), OnDismiss() and OnTap() (single tap toggles the viewer chrome, on photos and videos).
// Also the shared media page's small helpers (sharedMedia: thumbnails fading in, the sliding tab pill).

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

const MIN_SCALE = 1;
const MAX_SCALE = 5;
const SEEK_STEP_S = 5;
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

function isEditable(el) {
  if (!el || !el.tagName) return false;
  const tag = el.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || !!el.isContentEditable;
}

// ---- motion helpers (docs/MOTION.md) -------------------------------------------------------------------------------

/** cubic-bezier(x1, y1, x2, y2) as a function of progress 0..1 (for sampled keyframes). */
function bezier(x1, y1, x2, y2) {
  const at = (a1, a2, t) => 3 * a1 * t * (1 - t) * (1 - t) + 3 * a2 * t * t * (1 - t) + t * t * t;
  return (p) => {
    if (p <= 0) return 0;
    if (p >= 1) return 1;
    let lo = 0;
    let hi = 1;
    let t = p;
    for (let i = 0; i < 22; i++) {
      t = (lo + hi) / 2;
      if (at(x1, x2, t) < p) lo = t;
      else hi = t;
    }
    return at(y1, y2, t);
  };
}

const EASE_OUT = bezier(0.16, 1, 0.3, 1); // --sm-ease-out: arriving
const EASE = bezier(0.2, 0.8, 0.2, 1); // --sm-ease: moving into place

/**
 * Samples a damped spring (mass 1) from `from` to `to` (arrays) starting with `velocity` (units per ms per component)
 * at 60 fps. Settles in ~250 ms with a small overshoot; never longer than `maxMs`, the last frame is `to`.
 */
function springFrames(from, to, velocity, maxMs = 320) {
  const stiffness = 600;
  const damping = 32;
  const dt = 1 / 60;
  const x = from.map((value, i) => value - to[i]);
  const v = from.map((_, i) => ((velocity && velocity[i]) || 0) * 1000);
  const frames = [from.slice()];
  for (let t = dt; t * 1000 < maxMs; t += dt) {
    for (let i = 0; i < x.length; i++) {
      v[i] += (-stiffness * x[i] - damping * v[i]) * dt;
      x[i] += v[i] * dt;
    }
    frames.push(x.map((d, i) => to[i] + d));
    if (x.every((d) => Math.abs(d) < 0.25) && v.every((s) => Math.abs(s) < 15)) break;
  }
  frames.push(to.slice());
  return { frames, duration: Math.round(((frames.length - 1) * 1000) / 60) };
}

/** Pointer velocity (px/ms) over the last ~100 ms of { t, x, y } samples. */
function velocityOf(samples) {
  if (!samples || samples.length < 2) return { x: 0, y: 0 };
  const last = samples[samples.length - 1];
  let first = samples[0];
  for (const s of samples) {
    if (last.t - s.t <= 100) {
      first = s;
      break;
    }
  }
  const dt = last.t - first.t;
  return dt > 0 ? { x: (last.x - first.x) / dt, y: (last.y - first.y) / dt } : { x: 0, y: 0 };
}

/**
 * Keyframes that carry a [data-frame] box (laid out at `home`) from the on-screen rect `from` to `to` while its picture
 * keeps its proportions and covers the box, so a square-cropped thumbnail grows into the whole photo: the frame
 * scales unevenly and clips, the content is counter-scaled. Sampled, as a plain transition would squash the picture
 * mid-way.
 */
function flightFrames(home, from, to, ease, steps = 20) {
  const frame = [];
  const inner = [];
  const hx = home.left + home.width / 2;
  const hy = home.top + home.height / 2;
  for (let i = 0; i <= steps; i++) {
    const k = ease(i / steps);
    const w = from.width + (to.width - from.width) * k;
    const h = from.height + (to.height - from.height) * k;
    const cx = from.left + from.width / 2 + (to.left + to.width / 2 - from.left - from.width / 2) * k;
    const cy = from.top + from.height / 2 + (to.top + to.height / 2 - from.top - from.height / 2) * k;
    const sx = Math.max(0.001, w / home.width);
    const sy = Math.max(0.001, h / home.height);
    const s = Math.max(sx, sy);
    const offset = i / steps;
    frame.push({ offset, transform: `translate3d(${(cx - hx).toFixed(2)}px, ${(cy - hy).toFixed(2)}px, 0) scale(${sx.toFixed(4)}, ${sy.toFixed(4)})` });
    inner.push({ offset, transform: `scale(${(s / sx).toFixed(4)}, ${(s / sy).toFixed(4)})` });
  }
  return { frame, inner };
}

const ID_PATTERN = /^[0-9a-fA-F-]{8,64}$/;

// Where the last press went down (see track()): the copy of a thumbnail under it is the one that was tapped.
let lastPress = null;

/** Mostly inside the window and not covered by another page or sheet. */
function onScreen(el, r) {
  if (r.width < 12 || r.height < 12) return false;
  const seenW = Math.min(r.right, window.innerWidth) - Math.max(r.left, 0);
  const seenH = Math.min(r.bottom, window.innerHeight) - Math.max(r.top, 0);
  return seenW >= r.width * 0.5 && seenH >= r.height * 0.5 && uncovered(el, r);
}

/**
 * The thumbnail of `mediaId` on screen under the viewer, as { el, rect }: the copy under a press of the last couple of
 * seconds when there is one (the same photo may show twice, e.g. in the chat and in the shared-media column), else
 * the largest visible copy; null when there is none.
 */
function findThumb(mediaId) {
  if (!mediaId || !ID_PATTERN.test(mediaId)) return null;
  const list = document.querySelectorAll(`[data-viewer-thumb="${mediaId}"], img[src*="${mediaId}"], video[src*="${mediaId}"]`);
  const press = lastPress && performance.now() - lastPress.t < 2000 ? lastPress : null;
  let best = null;
  let bestArea = 0;
  for (const el of list) {
    if (el.closest('[data-viewer-open]')) continue;
    const r = el.getBoundingClientRect();
    if (!onScreen(el, r)) continue;
    if (press && press.x >= r.left && press.x <= r.right && press.y >= r.top && press.y <= r.bottom) return { el, rect: r };
    const area = r.width * r.height;
    if (area > bestArea) {
      best = { el, rect: r };
      bestArea = area;
    }
  }
  return best;
}

// What is on top at the thumbnail's centre (under the viewer) belongs to the thumbnail's tile: it is not hidden under
// another page layer (the chat stays mounted under its shared-media page on phones).
function uncovered(el, r) {
  if (typeof document.elementsFromPoint !== 'function') return true;
  const x = clamp(r.left + r.width / 2, 1, window.innerWidth - 1);
  const y = clamp(r.top + r.height / 2, 1, window.innerHeight - 1);
  const tile = el.parentElement || el;
  for (const hit of document.elementsFromPoint(x, y)) {
    if (hit.closest('[data-viewer-open]')) continue;
    return tile.contains(hit) || hit.contains(el);
  }
  return false;
}

// ---- the viewer ----------------------------------------------------------------------------------------------------

/**
 * Binds the viewer's `stage`. options: { mediaId, hasPrev, hasNext } of the item shown first; the opening animation
 * starts right away. Returns { show(delta, mediaId, hasPrev, hasNext) (delta 0: shown again without a transition),
 * close(mediaId) → Promise, zoom(f), dispose() }.
 */
export function attach(stage, ref, options) {
  if (!stage) return { show: noop, close: () => Promise.resolve(true), zoom: noop, dispose: noop };
  const root = stage.closest('[data-viewer-open]') || stage.parentElement;
  const backdrop = stage.querySelector('.viewer__backdrop');
  const opts = options || {};
  let currentId = opts.mediaId || null;
  let hasPrev = !!opts.hasPrev;
  let hasNext = !!opts.hasNext;
  let scale = 1;
  let tx = 0;
  let ty = 0;
  const pointers = new Map();
  let gesture = null;
  let lastTap = 0;
  let lastTapX = 0;
  let lastTapY = 0;
  let tapTimer = 0;
  let disposed = false;
  let trackAnim = null; // a swipe settling or sliding out
  let dismissAnims = []; // a dismiss drag springing back
  let closing = null; // promise of the closing animation (the viewer goes away right after)
  let enterVelocity = 0; // the finger's speed of the swipe that led to the next item
  let waitTimer = 0;
  let safety = 0;
  let origin = null; // { id, el }: the thumbnail the viewer opened from
  let flight = []; // the frame's (and its picture's) open/close animations

  const zoomTarget = () => stage.querySelector('[data-zoom]');
  const trackTarget = () => stage.querySelector('[data-track]');
  const frameTarget = () => stage.querySelector('[data-frame]');
  const innerOf = (frame) => frame && (frame.querySelector('[data-zoom]') || frame.querySelector('video'));
  const chrome = () => (root ? Array.from(root.querySelectorAll('.viewer__top, .viewer__bottom, .viewer__nav')) : []);
  const isBare = () => !!root && root.classList.contains('viewer--bare');
  const motion = () => !reduceMotion() && typeof stage.animate === 'function';

  function center() {
    const r = stage.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2, width: r.width, height: r.height };
  }

  function clampPan() {
    const el = zoomTarget();
    if (!el) return;
    const c = center();
    const maxX = Math.max(0, (el.offsetWidth * scale - c.width) / 2);
    const maxY = Math.max(0, (el.offsetHeight * scale - c.height) / 2);
    tx = clamp(tx, -maxX, maxX);
    ty = clamp(ty, -maxY, maxY);
  }

  function apply(animate) {
    const el = zoomTarget();
    if (!el) return;
    el.style.transition = animate ? 'transform 200ms cubic-bezier(.2,.8,.2,1)' : 'none';
    el.style.transform = scale === 1 && !tx && !ty ? '' : `translate3d(${tx}px, ${ty}px, 0) scale(${scale})`;
    stage.toggleAttribute('data-zoomed', scale > 1.01);
  }

  // Zooms to `next` keeping the stage point (px, py) still.
  function zoomAt(next, px, py, animate) {
    const s = clamp(next, MIN_SCALE, MAX_SCALE);
    const c = center();
    const k = s / scale;
    tx = (px - c.x) * (1 - k) + tx * k;
    ty = (py - c.y) * (1 - k) + ty * k;
    scale = s;
    if (scale <= 1.01) {
      scale = 1;
      tx = 0;
      ty = 0;
    }
    clampPan();
    apply(animate);
  }

  function setTrack(dx) {
    const el = trackTarget();
    if (el) el.style.transform = dx ? `translate3d(${dx}px, 0, 0)` : '';
  }

  function stopTrack() {
    if (trackAnim) {
      // A settle or slide-out still running: jump to its end (a slide-out then navigates as planned).
      trackAnim.finish();
      trackAnim = null;
    }
  }

  // ---- swipe down to close ----

  // Drag → look: the picture follows the finger and shrinks a little, the backdrop and the chrome thin out.
  function dismissPose(dx, dy) {
    const h = window.innerHeight || 800;
    const p = clamp(dy / (h * 0.45), 0, 1);
    return { dx, dy, k: 1 - 0.25 * p, dim: 1 - 0.85 * p, chrome: clamp(1 - p * 2.5, 0, 1) };
  }

  function showDismiss(pose) {
    const track = trackTarget();
    if (track) track.style.transform = `translate3d(${pose.dx.toFixed(1)}px, ${pose.dy.toFixed(1)}px, 0) scale(${pose.k.toFixed(4)})`;
    if (backdrop) backdrop.style.opacity = pose.dim.toFixed(3);
    if (isBare()) return;
    for (const el of chrome()) {
      el.style.transition = 'none';
      el.style.opacity = pose.chrome.toFixed(3);
    }
  }

  function clearDismiss() {
    const track = trackTarget();
    if (track) {
      track.style.transform = '';
      track.style.transformOrigin = '';
    }
    if (backdrop) backdrop.style.opacity = '';
    // Their CSS transition fades the chrome back in.
    for (const el of chrome()) {
      el.style.transition = '';
      el.style.opacity = '';
    }
  }

  function stopDismiss() {
    for (const a of dismissAnims) a.cancel();
    dismissAnims = [];
  }

  function settleDismiss(dx, dy, velocity) {
    const track = trackTarget();
    if (!track || !motion()) {
      clearDismiss();
      return;
    }
    const { frames, duration } = springFrames([dx, dy], [0, 0], [velocity.x, velocity.y]);
    const poses = frames.map(([x, y]) => dismissPose(x, y));
    // The chrome fades back by itself (CSS); the picture and the backdrop spring back.
    for (const el of chrome()) {
      el.style.transition = '';
      el.style.opacity = '';
    }
    track.style.transform = '';
    if (backdrop) backdrop.style.opacity = '';
    const anims = [track.animate(poses.map((p) => ({ transform: `translate3d(${p.dx.toFixed(1)}px, ${p.dy.toFixed(1)}px, 0) scale(${p.k.toFixed(4)})` })), { duration, easing: 'linear' })];
    if (backdrop) anims.push(backdrop.animate(poses.map((p) => ({ opacity: p.dim })), { duration, easing: 'linear' }));
    dismissAnims = anims;
    anims[0].onfinish = () => {
      if (dismissAnims !== anims) return;
      dismissAnims = [];
      track.style.transformOrigin = '';
    };
  }

  // ---- open / close ----

  // Runs the frame's flight from one on-screen rect to another (replacing a flight still under way, e.g. closing
  // right after opening); returns the frame's animation.
  function fly(frame, home, from, to, ease, duration, fadeOut) {
    const inner = innerOf(frame);
    const { frame: frameKeys, inner: innerKeys } = flightFrames(home, from, to, ease);
    if (fadeOut) {
      // Blend into the real thumbnail over the last stretch.
      frameKeys[0].opacity = 1;
      frameKeys[Math.round(frameKeys.length * 0.7)].opacity = 1;
      frameKeys[frameKeys.length - 1].opacity = 0;
    }
    for (const a of flight) a.cancel();
    frame.classList.add('is-flying');
    const timing = { duration, easing: 'linear', fill: fadeOut ? 'forwards' : 'none' };
    const animation = frame.animate(frameKeys, timing);
    flight = inner ? [animation, inner.animate(innerKeys, timing)] : [animation];
    if (!fadeOut) {
      animation.onfinish = () => {
        frame.classList.remove('is-flying');
        flight = [];
      };
    }
    return animation;
  }

  /** Opening: the picture grows out of the tapped thumbnail while the backdrop fades in (else a quick fade + scale). */
  function open(mediaId) {
    const frame = frameTarget();
    if (!frame) return;
    // The CSS fallback intro waits a moment for this; it is replaced either way.
    frame.style.animation = 'none';
    const thumb = findThumb(mediaId);
    // Closing goes back into this very element while it still shows this item.
    origin = thumb ? { id: mediaId, el: thumb.el } : null;
    if (!motion()) return;
    const home = frame.getBoundingClientRect();
    if (!thumb || home.width <= 1 || home.height <= 1) {
      frame.animate([{ opacity: 0, transform: 'scale(0.94)' }, { opacity: 1, transform: 'none' }], { duration: 220, easing: 'cubic-bezier(0.16, 1, 0.3, 1)' });
      return;
    }
    if (backdrop) {
      backdrop.style.animation = 'none';
      backdrop.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 320, easing: 'cubic-bezier(0.2, 0.8, 0.2, 1)' });
    }
    fly(frame, home, thumb.rect, home, EASE_OUT, 320, false);
  }

  /** The thumbnail to close into: the one it opened from while that is still there, else any visible copy. */
  function homeThumb(mediaId) {
    if (origin && origin.id === mediaId && origin.el.isConnected) {
      const rect = origin.el.getBoundingClientRect();
      if (onScreen(origin.el, rect)) return rect;
    }
    const thumb = findThumb(mediaId);
    return thumb ? thumb.rect : null;
  }

  /**
   * Closing: the picture flies back into its thumbnail when that is still on screen (and the picture is not zoomed),
   * else it shrinks a little and fades; the backdrop and the chrome fade with it. Resolves when the viewer can go.
   */
  function close(mediaId) {
    if (closing) return closing;
    clearTimeout(tapTimer);
    clearTimeout(waitTimer);
    gesture = null;
    pointers.clear();
    stopDismiss();
    if (trackAnim) {
      trackAnim.cancel();
      trackAnim = null;
    }
    const frame = frameTarget();
    const track = trackTarget();
    if (!frame || !motion()) {
      closing = Promise.resolve(true);
      return closing;
    }
    const shown = frame.getBoundingClientRect(); // where it is now, dragged or not
    const thumb = scale > 1.01 ? null : homeThumb(mediaId || currentId);
    if (track) {
      track.style.transform = '';
      track.style.transformOrigin = '';
    }
    const home = frame.getBoundingClientRect();
    let animation;
    if (thumb && home.width > 1 && home.height > 1) {
      animation = fly(frame, home, shown, thumb, EASE, 240, true);
    } else {
      for (const a of flight) a.cancel();
      flight = [];
      frame.classList.remove('is-flying');
      const k = home.width > 0 ? shown.width / home.width : 1;
      const dx = shown.left + shown.width / 2 - home.left - home.width / 2;
      const dy = shown.top + shown.height / 2 - home.top - home.height / 2;
      animation = frame.animate([
        { transform: `translate3d(${dx}px, ${dy}px, 0) scale(${k})`, opacity: 1 },
        { transform: `translate3d(${dx}px, ${dy}px, 0) scale(${k * 0.88})`, opacity: 0 },
      ], { duration: 200, easing: 'cubic-bezier(0.4, 0, 1, 1)', fill: 'forwards' });
    }
    if (backdrop) {
      const from = Number(getComputedStyle(backdrop).opacity);
      backdrop.style.animation = 'none';
      backdrop.animate([{ opacity: isFinite(from) ? from : 1 }, { opacity: 0 }], { duration: 220, easing: 'cubic-bezier(0.2, 0.8, 0.2, 1)', fill: 'forwards' });
    }
    for (const el of chrome()) {
      const from = Number(getComputedStyle(el).opacity);
      el.style.transition = 'none';
      el.animate([{ opacity: isFinite(from) ? from : 1 }, { opacity: 0 }], { duration: 120, easing: 'linear', fill: 'forwards' });
    }
    closing = new Promise((resolve) => {
      animation.onfinish = () => resolve(true);
      animation.oncancel = () => resolve(true);
      // Animations stall in a hidden page: never keep .NET waiting for this one.
      setTimeout(() => resolve(true), 450);
    });
    // .NET removes the viewer right after; should that not happen, make it usable again.
    safety = setTimeout(() => {
      if (disposed || !stage.isConnected) return;
      for (const a of root ? root.getAnimations({ subtree: true }) : []) a.cancel();
      flight = [];
      frame.classList.remove('is-flying');
      clearDismiss();
      closing = null;
    }, 1500);
    return closing;
  }

  // ---- previous / next ----

  /** A committed swipe: the item leaves to the side, then .NET shows the next one (show() brings it in). */
  function slideOut(dx, velocity) {
    const track = trackTarget();
    const delta = dx < 0 ? 1 : -1;
    const width = stage.clientWidth || window.innerWidth;
    const to = -delta * (width + 32);
    enterVelocity = clamp(velocity, -3, 3);
    if (!track || !motion()) {
      setTrack(0);
      safeInvoke(ref, 'OnNavigate', delta);
      return;
    }
    track.style.transform = `translate3d(${to}px, 0, 0)`;
    const duration = clamp(Math.abs(to - dx) / Math.max(Math.abs(velocity), 1.8), 120, 220);
    const animation = track.animate([{ transform: `translate3d(${dx}px, 0, 0)` }, { transform: `translate3d(${to}px, 0, 0)` }],
      { duration, easing: 'cubic-bezier(0.2, 0.8, 0.2, 1)' });
    trackAnim = animation;
    animation.onfinish = () => {
      if (trackAnim === animation) trackAnim = null;
      if (disposed) return;
      safeInvoke(ref, 'OnNavigate', delta);
      // .NET answers with show(); never leave the stage empty if it does not.
      clearTimeout(waitTimer);
      waitTimer = setTimeout(() => setTrack(0), 900);
    };
  }

  /** Spring back to the middle after a swipe that did not go far enough. */
  function settleTrack(dx, velocity) {
    const track = trackTarget();
    if (!track || !motion() || !dx) {
      setTrack(0);
      return;
    }
    const { frames, duration } = springFrames([dx], [0], [velocity]);
    track.style.transform = '';
    const animation = track.animate(frames.map(([x]) => ({ transform: `translate3d(${x.toFixed(1)}px, 0, 0)` })), { duration, easing: 'linear' });
    trackAnim = animation;
    animation.onfinish = () => {
      if (trackAnim === animation) trackAnim = null;
    };
  }

  // ---- the video's own controls (no native <video controls>) ----

  let vFrame = 0;
  let vScrub = null; // { id, el, rect, t }: a drag on the progress line
  let vReady = 0;
  const videoEl = () => stage.querySelector('[data-frame] video');
  const vBar = () => (root ? root.querySelector('[data-vbar]') : null);

  function fmtTime(seconds) {
    const total = Math.max(0, Math.floor(seconds || 0));
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const r = total % 60;
    return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`;
  }

  function videoState(v) {
    if (!v) return 'paused';
    if (v.ended) return 'ended';
    if (!v.paused) return v.readyState >= 3 || v.seeking ? 'playing' : 'waiting';
    return v.readyState >= 2 || v.error ? 'paused' : 'loading';
  }

  function setVState(state) {
    if (root && root.getAttribute('data-vstate') !== state) root.setAttribute('data-vstate', state);
  }

  function paintVideo() {
    const v = videoEl();
    const bar = vBar();
    if (!v || !bar) return;
    const d = isFinite(v.duration) && v.duration > 0 ? v.duration : 0;
    const t = vScrub ? vScrub.t : v.currentTime;
    const p = d ? clamp(t / d, 0, 1) : 0;
    bar.style.setProperty('--vp', p.toFixed(4));
    const cur = bar.querySelector('[data-vcur]');
    const curText = fmtTime(t);
    if (cur && cur.textContent !== curText) cur.textContent = curText;
    const dur = bar.querySelector('[data-vdur]');
    const durText = d ? fmtTime(d) : null;
    if (dur && durText && dur.textContent !== durText) dur.textContent = durText;
    const scrubber = bar.querySelector('[data-vscrub]');
    if (scrubber) scrubber.setAttribute('aria-valuenow', String(Math.round(p * 100)));
  }

  function loopVideo() {
    vFrame = 0;
    paintVideo();
    const v = videoEl();
    if (v && !v.paused && !v.ended && !disposed) vFrame = requestAnimationFrame(loopVideo);
  }

  function syncVideo() {
    const v = videoEl();
    if (!v) {
      if (root) root.removeAttribute('data-vstate');
      return;
    }
    setVState(videoState(v));
    paintVideo();
    if (!vFrame && !v.paused && !v.ended) vFrame = requestAnimationFrame(loopVideo);
  }

  function togglePlay() {
    const v = videoEl();
    if (!v) return;
    if (v.paused || v.ended) {
      const played = v.play();
      if (played && played.catch) played.catch(noop);
    } else {
      v.pause();
    }
  }

  function seekTo(seconds) {
    const v = videoEl();
    if (!v || !(v.duration > 0)) return;
    try {
      v.currentTime = clamp(seconds, 0, v.duration);
    } catch (err) {
      // Not seekable yet.
    }
    paintVideo();
  }

  function seekBy(delta) {
    const v = videoEl();
    if (v) seekTo(v.currentTime + delta);
  }

  const onMedia = (e) => {
    const v = e.target;
    if (!v || v.tagName !== 'VIDEO' || !stage.contains(v)) return;
    if (e.type === 'canplay' || e.type === 'loadeddata') {
      // An autoplay the browser refused never says so: show the play orb once it should have started.
      clearTimeout(vReady);
      vReady = setTimeout(syncVideo, 320);
    }
    if (e.type === 'waiting' && !v.paused) setVState('waiting');
    else syncVideo();
  };

  const onVideoClick = (e) => {
    const button = e.target && e.target.closest ? e.target.closest('[data-vplay]') : null;
    if (!button || !root.contains(button)) return;
    togglePlay();
  };

  function scrubTo(e) {
    const v = videoEl();
    if (!vScrub || !v || !(v.duration > 0)) return;
    const f = clamp((e.clientX - vScrub.rect.left) / Math.max(1, vScrub.rect.width), 0, 1);
    vScrub.t = f * v.duration;
    seekTo(vScrub.t);
  }

  const onScrubDown = (e) => {
    const el = e.target && e.target.closest ? e.target.closest('[data-vscrub]') : null;
    if (!el || vScrub || (e.pointerType === 'mouse' && e.button !== 0)) return;
    const v = videoEl();
    if (!v || !(v.duration > 0)) return;
    e.preventDefault();
    vScrub = { id: e.pointerId, el, rect: el.getBoundingClientRect(), t: v.currentTime };
    try { el.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
    const bar = el.closest('[data-vbar]');
    if (bar) bar.setAttribute('data-scrubbing', '');
    scrubTo(e);
  };

  const onScrubMove = (e) => {
    if (!vScrub || e.pointerId !== vScrub.id) return;
    e.preventDefault();
    scrubTo(e);
  };

  const onScrubEnd = (e) => {
    if (!vScrub || e.pointerId !== vScrub.id) return;
    const { el } = vScrub;
    vScrub = null;
    try { el.releasePointerCapture(e.pointerId); } catch (err) { /* ignore */ }
    const bar = el.closest('[data-vbar]');
    if (bar) bar.removeAttribute('data-scrubbing');
    paintVideo();
  };

  const MEDIA_EVENTS = ['loadstart', 'loadedmetadata', 'loadeddata', 'canplay', 'durationchange', 'play', 'playing', 'pause', 'waiting', 'seeked', 'timeupdate', 'ended', 'emptied', 'error'];

  // ---- pointers ----

  const onDown = (e) => {
    if (disposed || closing) return;
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    if (e.target.closest && e.target.closest('button, a, [data-no-gesture]')) return;
    const video = e.target.closest ? e.target.closest('video') : null;
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    const onVideo = !!video;
    if (pointers.size === 2 && !onVideo) {
      const [a, b] = Array.from(pointers.values());
      gesture = {
        kind: 'pinch',
        dist0: Math.hypot(a.x - b.x, a.y - b.y) || 1,
        mid0: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 },
        scale0: scale,
        tx0: tx,
        ty0: ty,
      };
      return;
    }
    if (pointers.size === 1) {
      stopTrack();
      if (dismissAnims.length) {
        stopDismiss();
        clearDismiss();
      }
      gesture = {
        kind: 'pending', id: e.pointerId, x0: e.clientX, y0: e.clientY, tx0: tx, ty0: ty, t0: performance.now(), onVideo,
        samples: [{ t: e.timeStamp || performance.now(), x: e.clientX, y: e.clientY }],
      };
      if (!onVideo) {
        try { stage.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
      }
    }
  };

  const onMove = (e) => {
    if (!pointers.has(e.pointerId) || !gesture) return;
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (gesture.kind === 'pinch') {
      if (pointers.size < 2) return;
      const [a, b] = Array.from(pointers.values());
      const dist = Math.hypot(a.x - b.x, a.y - b.y) || 1;
      const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
      const s = clamp(gesture.scale0 * (dist / gesture.dist0), MIN_SCALE * 0.8, MAX_SCALE);
      const c = center();
      const k = s / gesture.scale0;
      scale = s;
      tx = (gesture.mid0.x - c.x) * (1 - k) + gesture.tx0 * k + (mid.x - gesture.mid0.x);
      ty = (gesture.mid0.y - c.y) * (1 - k) + gesture.ty0 * k + (mid.y - gesture.mid0.y);
      apply(false);
      return;
    }
    if (e.pointerId !== gesture.id) return;
    gesture.samples.push({ t: e.timeStamp || performance.now(), x: e.clientX, y: e.clientY });
    if (gesture.samples.length > 8) gesture.samples.shift();
    const dx = e.clientX - gesture.x0;
    const dy = e.clientY - gesture.y0;
    if (gesture.kind === 'pending') {
      if (Math.abs(dx) < 8 && Math.abs(dy) < 8) return;
      if (scale > 1.01 && !gesture.onVideo) gesture.kind = 'pan';
      else if (Math.abs(dx) > Math.abs(dy)) gesture.kind = 'swipe';
      else if (dy > 0) {
        gesture.kind = 'dismiss';
        // Shrink around the finger, so the spot it holds stays under it.
        const track = trackTarget();
        if (track) {
          const r = track.getBoundingClientRect();
          track.style.transformOrigin = `${gesture.x0 - r.left}px ${gesture.y0 - r.top}px`;
        }
      } else {
        gesture = null;
        return;
      }
      clearTimeout(tapTimer);
    }
    if (gesture.kind === 'pan') {
      tx = gesture.tx0 + dx;
      ty = gesture.ty0 + dy;
      clampPan();
      apply(false);
    } else if (gesture.kind === 'swipe') {
      // Nothing that way: the picture only gives a little.
      const blocked = (dx > 0 && !hasPrev) || (dx < 0 && !hasNext);
      setTrack(blocked ? dx * 0.3 : dx);
    } else if (gesture.kind === 'dismiss') {
      showDismiss(dismissPose(dx, dy));
    }
  };

  const onUp = (e) => {
    if (!pointers.has(e.pointerId)) return;
    const point = pointers.get(e.pointerId);
    pointers.delete(e.pointerId);
    const g = gesture;
    if (!g) return;
    if (g.kind === 'pinch') {
      if (pointers.size < 2) {
        gesture = null;
        if (scale < 1.01) {
          scale = 1;
          tx = 0;
          ty = 0;
        }
        clampPan();
        apply(true);
      }
      return;
    }
    if (e.pointerId !== g.id) return;
    gesture = null;
    const dx = point.x - g.x0;
    const dy = point.y - g.y0;
    const released = e.type === 'pointerup';
    if (g.kind === 'swipe') {
      const v = released ? velocityOf(g.samples).x : 0;
      const allowed = dx < 0 ? hasNext : hasPrev;
      if (released && allowed && (Math.abs(dx) > 70 || (Math.abs(v) > 0.5 && Math.sign(v) === Math.sign(dx)))) {
        slideOut(dx, v);
      } else {
        const blocked = (dx > 0 && !hasPrev) || (dx < 0 && !hasNext);
        settleTrack(blocked ? dx * 0.3 : dx, v);
      }
      return;
    }
    if (g.kind === 'dismiss') {
      const v = released ? velocityOf(g.samples) : { x: 0, y: 0 };
      if (released && (dy > 110 || v.y > 0.6)) {
        close(currentId).then(() => safeInvoke(ref, 'OnDismiss'));
      } else {
        settleDismiss(dx, dy, v);
      }
      return;
    }
    if (g.kind !== 'pending' || !released) return;
    // A tap: two quick taps toggle zoom at that point (photos), a single one toggles the chrome (with a video's own
    // controls in it).
    const now = performance.now();
    if (now - lastTap < 300 && Math.abs(point.x - lastTapX) < 30 && Math.abs(point.y - lastTapY) < 30) {
      clearTimeout(tapTimer);
      lastTap = 0;
      if (zoomTarget()) zoomAt(scale > 1.01 ? 1 : 2.5, point.x, point.y, true);
      return;
    }
    lastTap = now;
    lastTapX = point.x;
    lastTapY = point.y;
    clearTimeout(tapTimer);
    tapTimer = setTimeout(() => safeInvoke(ref, 'OnTap'), 290);
  };

  const onWheel = (e) => {
    if (!zoomTarget() || closing) return;
    e.preventDefault();
    const factor = Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0022));
    zoomAt(scale * factor, e.clientX, e.clientY, false);
  };

  const onKey = (e) => {
    if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey || e.isComposing || closing) return;
    // Keys typed into a field or into a dialog / menu opened above the viewer (Forward) are not for the viewer:
    // arrows move the caret there and +/-/0 are text.
    if (isEditable(e.target) || document.querySelector('.sm-overlay, .sm-menu-layer')) return;
    const onScrubber = !!(e.target && e.target.closest && e.target.closest('[data-vscrub]'));
    if (onScrubber && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
      e.preventDefault();
      seekBy(e.key === 'ArrowLeft' ? -SEEK_STEP_S : SEEK_STEP_S);
      return;
    }
    if (e.key === ' ' && videoEl() && !(e.target && e.target.closest && e.target.closest('button, a'))) {
      e.preventDefault();
      togglePlay();
      return;
    }
    if (e.key === 'ArrowLeft') {
      e.preventDefault();
      enterVelocity = 0;
      safeInvoke(ref, 'OnNavigate', -1);
    } else if (e.key === 'ArrowRight') {
      e.preventDefault();
      enterVelocity = 0;
      safeInvoke(ref, 'OnNavigate', 1);
    } else if ((e.key === '+' || e.key === '=') && zoomTarget()) {
      const c = center();
      zoomAt(scale * 1.5, c.x, c.y, true);
    } else if (e.key === '-' && zoomTarget()) {
      const c = center();
      zoomAt(scale / 1.5, c.x, c.y, true);
    } else if (e.key === '0') {
      scale = 1;
      tx = 0;
      ty = 0;
      apply(true);
    }
  };

  stage.addEventListener('pointerdown', onDown);
  stage.addEventListener('pointermove', onMove);
  stage.addEventListener('pointerup', onUp);
  stage.addEventListener('pointercancel', onUp);
  stage.addEventListener('wheel', onWheel, { passive: false });
  window.addEventListener('keydown', onKey);
  if (root) {
    // Media events do not bubble, but they pass the viewer on their way down (capture).
    for (const type of MEDIA_EVENTS) root.addEventListener(type, onMedia, true);
    root.addEventListener('click', onVideoClick);
    root.addEventListener('pointerdown', onScrubDown);
    root.addEventListener('pointermove', onScrubMove);
    root.addEventListener('pointerup', onScrubEnd);
    root.addEventListener('pointercancel', onScrubEnd);
  }

  open(currentId);
  syncVideo();

  return {
    /**
     * Another item is shown (swipe, arrows, buttons): forget zoom and offsets; the new item comes in from the side it
     * was asked for (delta > 0: from the right) and settles with a spring.
     */
    show(delta, mediaId, prev, next) {
      if (disposed) return;
      currentId = mediaId || currentId;
      hasPrev = !!prev;
      hasNext = !!next;
      clearTimeout(waitTimer);
      scale = 1;
      tx = 0;
      ty = 0;
      gesture = null;
      pointers.clear();
      apply(false);
      stopDismiss();
      clearDismiss();
      if (trackAnim) {
        trackAnim.cancel();
        trackAnim = null;
      }
      const track = trackTarget();
      const frame = frameTarget();
      if (frame) frame.style.animation = 'none';
      setTrack(0);
      vScrub = null;
      syncVideo();
      const direction = Math.sign(Number(delta) || 0);
      if (!track || !direction || !motion()) return;
      const width = stage.clientWidth || window.innerWidth;
      const { frames, duration } = springFrames([direction * width * 0.3], [0], [enterVelocity]);
      enterVelocity = 0;
      const animation = track.animate(frames.map(([x]) => ({ transform: `translate3d(${x.toFixed(1)}px, 0, 0)` })), { duration, easing: 'linear' });
      trackAnim = animation;
      animation.onfinish = () => {
        if (trackAnim === animation) trackAnim = null;
      };
      if (frame) frame.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 160, easing: 'linear' });
    },
    close(mediaId) {
      if (disposed) return Promise.resolve(true);
      return close(mediaId);
    },
    zoom(factor) {
      const c = center();
      zoomAt(scale * (Number(factor) || 1), c.x, c.y, true);
    },
    dispose() {
      disposed = true;
      clearTimeout(tapTimer);
      clearTimeout(waitTimer);
      clearTimeout(safety);
      stopDismiss();
      if (trackAnim) trackAnim.cancel();
      trackAnim = null;
      stage.removeEventListener('pointerdown', onDown);
      stage.removeEventListener('pointermove', onMove);
      stage.removeEventListener('pointerup', onUp);
      stage.removeEventListener('pointercancel', onUp);
      stage.removeEventListener('wheel', onWheel);
      window.removeEventListener('keydown', onKey);
      if (root) {
        for (const type of MEDIA_EVENTS) root.removeEventListener(type, onMedia, true);
        root.removeEventListener('click', onVideoClick);
        root.removeEventListener('pointerdown', onScrubDown);
        root.removeEventListener('pointermove', onScrubMove);
        root.removeEventListener('pointerup', onScrubEnd);
        root.removeEventListener('pointercancel', onScrubEnd);
      }
      if (vFrame) cancelAnimationFrame(vFrame);
      vFrame = 0;
      clearTimeout(vReady);
      vScrub = null;
      origin = null;
      flight = [];
    }
  };
}

/**
 * Remembers where presses go down (capture, passive), so the viewer opens out of the copy of a thumbnail that was
 * actually tapped. Attached by each screen that has a viewer, from its first render (which also loads this module
 * early, so the first open starts right away); returns { dispose }.
 */
export function track() {
  const onPress = (e) => {
    if (e.isPrimary) lastPress = { x: e.clientX, y: e.clientY, t: performance.now() };
  };
  document.addEventListener('pointerdown', onPress, { capture: true, passive: true });
  return {
    dispose() {
      document.removeEventListener('pointerdown', onPress, { capture: true });
    }
  };
}

// ---- shared media page ---------------------------------------------------------------------------------------------

/**
 * Shared media page helpers: thumbnails (img[data-fade] inside `root`) fade in once they have loaded, instead of
 * popping in over their blurred placeholder (marked [data-loaded]; the CSS hides them only while `root` has
 * [data-fading], set here); and the tab pill (.smedia-tabs__ink inside `tabsBox`, a gradient capsule under the kit's
 * pill tabs) slides to the active tab and follows the tab strip when it scrolls. The pill takes the tab's width at once
 * and then moves by transform alone, starting stretched to where it was (so its round ends are only squeezed on the
 * way). Returns { dispose }.
 */
export function sharedMedia(root, tabsBox) {
  if (!root) return { dispose: noop };
  const onLoad = (e) => {
    const img = e.target;
    if (img && img.tagName === 'IMG' && img.hasAttribute('data-fade')) img.setAttribute('data-loaded', '');
  };
  root.addEventListener('load', onLoad, true);
  root.addEventListener('error', onLoad, true);
  for (const img of root.querySelectorAll('img[data-fade]')) {
    if (img.complete) img.setAttribute('data-loaded', '');
  }
  root.setAttribute('data-fading', '');

  const strip = tabsBox && tabsBox.querySelector('.sm-tabs');
  const ink = tabsBox && tabsBox.querySelector('.smedia-tabs__ink');
  let activeTab = null;
  let frame = 0;
  let observer = null;
  let resize = null;
  let glide = null;
  let ownScroll = false;

  // A tab that was picked half off the strip's edge scrolls fully into view (at once: the pill measures after it).
  function reveal(tab) {
    const sr = strip.getBoundingClientRect();
    const r = tab.getBoundingClientRect();
    const pad = 8;
    let delta = 0;
    if (r.left < sr.left + pad) delta = r.left - sr.left - pad;
    else if (r.right > sr.right - pad) delta = r.right - sr.right + pad;
    if (Math.abs(delta) < 1) return;
    const before = strip.scrollLeft;
    strip.scrollLeft = before + delta;
    if (strip.scrollLeft !== before) ownScroll = true;
  }

  // Puts the pill under the active tab; slides when the active tab changed, jumps otherwise (scroll, resize).
  function place() {
    frame = 0;
    if (!strip || !ink) return;
    const tab = strip.querySelector('.sm-tab--active');
    if (!tab) {
      tabsBox.removeAttribute('data-ink');
      return;
    }
    const changed = !!activeTab && activeTab !== tab && tabsBox.hasAttribute('data-ink');
    const slide = changed && !reduceMotion() && typeof ink.animate === 'function';
    // Where it shows now (possibly mid-slide), before anything changes.
    const was = slide ? ink.getBoundingClientRect() : null;
    if (changed) reveal(tab);
    const box = tabsBox.getBoundingClientRect();
    const r = tab.getBoundingClientRect();
    const x = r.left - box.left;
    const width = Math.max(1, r.width);
    if (glide) {
      glide.cancel();
      glide = null;
    }
    ink.style.width = `${width.toFixed(1)}px`;
    ink.style.transform = `translateX(${x.toFixed(1)}px)`;
    if (was && was.width > 0) {
      const from = `translateX(${(was.left - box.left).toFixed(1)}px) scaleX(${(was.width / width).toFixed(4)})`;
      glide = ink.animate([{ transform: from }, { transform: `translateX(${x.toFixed(1)}px)` }],
        { duration: 340, easing: 'cubic-bezier(0.22, 1.25, 0.36, 1)' });
      const current = glide;
      glide.onfinish = () => {
        if (glide === current) glide = null;
      };
    }
    activeTab = tab;
    tabsBox.setAttribute('data-ink', '');
  }

  const schedule = () => {
    if (!frame) frame = requestAnimationFrame(place);
  };
  // The strip scrolled under the finger: the pill follows at once (not after our own reveal, already accounted for).
  const onStripScroll = () => {
    if (ownScroll) {
      ownScroll = false;
      return;
    }
    place();
  };
  if (strip && ink) {
    observer = new MutationObserver(schedule);
    observer.observe(strip, { subtree: true, attributes: true, attributeFilter: ['class'], childList: true });
    strip.addEventListener('scroll', onStripScroll, { passive: true });
    if (typeof ResizeObserver === 'function') {
      resize = new ResizeObserver(schedule);
      resize.observe(strip);
    }
    place();
  }

  return {
    dispose() {
      root.removeEventListener('load', onLoad, true);
      root.removeEventListener('error', onLoad, true);
      if (frame) cancelAnimationFrame(frame);
      if (glide) glide.cancel();
      glide = null;
      if (observer) observer.disconnect();
      if (resize) resize.disconnect();
      if (strip) strip.removeEventListener('scroll', onStripScroll);
    }
  };
}
