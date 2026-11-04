// SkyMessage avatar editor and emoji avatars (ES module, imported by Features/Profile/AvatarEditor and
// EmojiAvatarDialog). The editor frames a photo in a square crop shown through the squircle mask: drag to pan, pinch
// or the mouse wheel to zoom (1x = the photo just covers the square, up to 5x), rotate by quarter turns, flip. The
// photo can never leave a gap in the square. Done draws exactly what the square shows into a canvas of `size` px and
// hands .NET the JPEG bytes. Rotations and flips glide (transform only); gestures follow the finger 1:1.

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

const MAX_ZOOM = 5;
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

/**
 * Binds the editor's `stage` ([data-editor-img] is the photo, [data-editor-crop] the square). The photo bytes come
 * through `streamRef` (a .NET stream). Resolves to { setZoom(z), rotate(), flip(), reset(), exportJpeg(size, quality),
 * dispose() } — or null when the photo cannot be decoded. .NET hears OnZoom(z) after gestures (the slider follows).
 */
export async function attachEditor(stage, ref, streamRef, options) {
  if (!stage) return null;
  const img = stage.querySelector('[data-editor-img]');
  const crop = stage.querySelector('[data-editor-crop]');
  if (!img || !crop) return null;
  const opts = options || {};

  let url = null;
  try {
    const buffer = await streamRef.arrayBuffer();
    url = URL.createObjectURL(new Blob([buffer], { type: opts.type || 'image/jpeg' }));
    img.src = url;
    await img.decode();
  } catch (e) {
    if (url) URL.revokeObjectURL(url);
    return null;
  }

  const natW = img.naturalWidth || 1;
  const natH = img.naturalHeight || 1;
  img.style.width = natW + 'px';
  img.style.height = natH + 'px';
  img.style.left = `calc(50% - ${natW / 2}px)`;
  img.style.top = `calc(50% - ${natH / 2}px)`;

  let quarter = 0; // quarter turns, counter-clockwise
  let flipped = false;
  let zoom = 1;
  let tx = 0;
  let ty = 0;
  let disposed = false;
  const pointers = new Map();
  let gesture = null;
  let zoomTimer = 0;
  let glideTimer = 0;

  const side = () => crop.getBoundingClientRect().width || 1;
  const turnedW = () => (quarter % 2 ? natH : natW);
  const turnedH = () => (quarter % 2 ? natW : natH);
  // The scale at which the turned photo just covers the square.
  const baseScale = () => side() / Math.min(turnedW(), turnedH());

  function center() {
    const r = crop.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
  }

  function clampPan() {
    const s = baseScale() * zoom;
    const maxX = Math.max(0, (turnedW() * s - side()) / 2);
    const maxY = Math.max(0, (turnedH() * s - side()) / 2);
    tx = clamp(tx, -maxX, maxX);
    ty = clamp(ty, -maxY, maxY);
  }

  function apply(glide) {
    clampPan();
    const s = baseScale() * zoom;
    // Read right to left: scale, then turn, then flip on screen, then move.
    img.style.transition = glide && !reduceMotion() ? 'transform 240ms cubic-bezier(0.2, 0.8, 0.2, 1)' : 'none';
    img.style.transform = `translate3d(${tx.toFixed(2)}px, ${ty.toFixed(2)}px, 0) scaleX(${flipped ? -1 : 1}) rotate(${-90 * quarter}deg) scale(${s.toFixed(5)})`;
    if (glide) {
      clearTimeout(glideTimer);
      glideTimer = setTimeout(() => { img.style.transition = 'none'; }, 260);
    }
  }

  function reportZoom() {
    clearTimeout(zoomTimer);
    zoomTimer = setTimeout(() => safeInvoke(ref, 'OnZoom', zoom), 60);
  }

  // Zooms to `next` keeping the screen point (px, py) still.
  function zoomAt(next, px, py, glide) {
    const z = clamp(next, 1, MAX_ZOOM);
    const c = center();
    const k = z / zoom;
    tx = (px - c.x) * (1 - k) + tx * k;
    ty = (py - c.y) * (1 - k) + ty * k;
    zoom = z;
    apply(glide);
  }

  function onDown(e) {
    if (disposed || (e.pointerType === 'mouse' && e.button !== 0)) return;
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    try { stage.setPointerCapture(e.pointerId); } catch (_) { }
    begin();
    e.preventDefault();
  }

  function begin() {
    const list = [...pointers.values()];
    if (list.length >= 2) {
      const [a, b] = list;
      gesture = {
        kind: 'pinch',
        dist: Math.hypot(b.x - a.x, b.y - a.y) || 1,
        mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 },
        zoom,
        tx,
        ty,
      };
    } else if (list.length === 1) {
      gesture = { kind: 'pan', x: list[0].x, y: list[0].y, tx, ty };
    } else {
      gesture = null;
    }
    stage.toggleAttribute('data-dragging', !!gesture);
  }

  function onMove(e) {
    if (disposed || !pointers.has(e.pointerId)) return;
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (!gesture) return;
    const list = [...pointers.values()];
    if (gesture.kind === 'pan' && list.length === 1) {
      tx = gesture.tx + (list[0].x - gesture.x);
      ty = gesture.ty + (list[0].y - gesture.y);
      apply(false);
    } else if (gesture.kind === 'pinch' && list.length >= 2) {
      const [a, b] = list;
      const dist = Math.hypot(b.x - a.x, b.y - a.y) || 1;
      const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
      const c = center();
      const z = clamp(gesture.zoom * (dist / gesture.dist), 1, MAX_ZOOM);
      const k = z / gesture.zoom;
      // The point under the fingers stays under them, and the pair can also drag the photo.
      tx = (gesture.mid.x - c.x) * (1 - k) + gesture.tx * k + (mid.x - gesture.mid.x);
      ty = (gesture.mid.y - c.y) * (1 - k) + gesture.ty * k + (mid.y - gesture.mid.y);
      zoom = z;
      apply(false);
    }
    e.preventDefault();
  }

  function onUp(e) {
    if (!pointers.has(e.pointerId)) return;
    pointers.delete(e.pointerId);
    begin();
    if (!gesture) reportZoom();
  }

  function onWheel(e) {
    if (disposed) return;
    e.preventDefault();
    const factor = Math.exp(-(e.deltaY || 0) * (e.deltaMode === 1 ? 0.05 : 0.0018));
    zoomAt(zoom * factor, e.clientX, e.clientY, false);
    reportZoom();
  }

  function onKey(e) {
    if (disposed) return;
    // Keys meant for a control (the zoom slider moves with the arrows itself) do not also pan the photo.
    if (e.defaultPrevented || (e.target instanceof Element && e.target.closest('input, textarea, select, [contenteditable]'))) return;
    const step = 24;
    switch (e.key) {
      case 'ArrowLeft': tx += step; break;
      case 'ArrowRight': tx -= step; break;
      case 'ArrowUp': ty += step; break;
      case 'ArrowDown': ty -= step; break;
      case '+': case '=': { const c = center(); zoomAt(zoom * 1.2, c.x, c.y, true); reportZoom(); return; }
      case '-': { const c = center(); zoomAt(zoom / 1.2, c.x, c.y, true); reportZoom(); return; }
      default: return;
    }
    e.preventDefault();
    apply(true);
  }

  // The crop square follows the window (rotation of the phone, a resized desktop window).
  const resize = new ResizeObserver(() => apply(false));
  resize.observe(stage);

  stage.addEventListener('pointerdown', onDown);
  stage.addEventListener('pointermove', onMove);
  stage.addEventListener('pointerup', onUp);
  stage.addEventListener('pointercancel', onUp);
  stage.addEventListener('wheel', onWheel, { passive: false });
  window.addEventListener('keydown', onKey);
  apply(false);
  stage.setAttribute('data-ready', '');

  return {
    setZoom(z) {
      const c = center();
      zoomAt(Number(z) || 1, c.x, c.y, false);
    },
    rotate() {
      quarter = (quarter + 1) % 4;
      // The offset turns with the photo, so the same part stays in the square.
      [tx, ty] = [ty, -tx];
      apply(true);
    },
    flip() {
      flipped = !flipped;
      tx = -tx;
      apply(true);
    },
    reset() {
      quarter = 0;
      flipped = false;
      zoom = 1;
      tx = 0;
      ty = 0;
      apply(true);
      reportZoom();
    },
    /** What the square shows, as a `size` px JPEG (bytes). */
    async exportJpeg(size, quality) {
      const out = Math.max(64, Math.min(4096, Number(size) || 1024));
      const canvas = document.createElement('canvas');
      canvas.width = out;
      canvas.height = out;
      const g = canvas.getContext('2d');
      if (!g) return null;
      const f = out / side();
      const s = baseScale() * zoom * f;
      g.fillStyle = '#FFFFFF';
      g.fillRect(0, 0, out, out);
      g.imageSmoothingEnabled = true;
      g.imageSmoothingQuality = 'high';
      g.translate(out / 2 + tx * f, out / 2 + ty * f);
      g.scale(flipped ? -1 : 1, 1);
      g.rotate((-Math.PI / 2) * quarter);
      g.scale(s, s);
      g.drawImage(img, -natW / 2, -natH / 2);
      const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', Number(quality) || 0.9));
      return blob ? new Uint8Array(await blob.arrayBuffer()) : null;
    },
    dispose() {
      disposed = true;
      clearTimeout(zoomTimer);
      clearTimeout(glideTimer);
      resize.disconnect();
      stage.removeEventListener('pointerdown', onDown);
      stage.removeEventListener('pointermove', onMove);
      stage.removeEventListener('pointerup', onUp);
      stage.removeEventListener('pointercancel', onUp);
      stage.removeEventListener('wheel', onWheel);
      window.removeEventListener('keydown', onKey);
      if (url) URL.revokeObjectURL(url);
      url = null;
    },
  };
}

/**
 * An emoji on a two-color gradient (top-left light to bottom-right deep, like the placeholder avatars), `size` px,
 * as JPEG bytes. The emoji is drawn by the device's own emoji font.
 */
export async function renderEmoji(emoji, from, to, size, quality) {
  const out = Math.max(64, Math.min(2048, Number(size) || 1024));
  const canvas = document.createElement('canvas');
  canvas.width = out;
  canvas.height = out;
  const g = canvas.getContext('2d');
  if (!g) return null;
  const gradient = g.createLinearGradient(out * 0.1, 0, out * 0.9, out);
  gradient.addColorStop(0, from || '#9C8BFF');
  gradient.addColorStop(1, to || '#5B3FF0');
  g.fillStyle = gradient;
  g.fillRect(0, 0, out, out);
  g.font = `${Math.round(out * 0.54)}px "Apple Color Emoji", "Segoe UI Emoji", "Noto Color Emoji", "Android Emoji", sans-serif`;
  g.textAlign = 'center';
  g.textBaseline = 'alphabetic';
  const m = g.measureText(emoji);
  const ascent = m.actualBoundingBoxAscent || out * 0.4;
  const descent = m.actualBoundingBoxDescent || out * 0.1;
  // Optically centered: the glyph's own box sits in the middle.
  g.fillText(emoji, out / 2, out / 2 + (ascent - descent) / 2);
  const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', Number(quality) || 0.92));
  return blob ? new Uint8Array(await blob.arrayBuffer()) : null;
}
