// Sticker and GIF panels: one delegated long-press for every cell (a panel holds hundreds of them, so no per-cell
// binding). A cell is [data-hold="<key>"]. Holding it ~450 ms (moving less than 10 px) or right-clicking it reports
// OnHold(key) once and swallows the click that ends the hold wherever it lands (the preview that just opened under the
// finger must not get it). Scrolling cancels a hold. The pack strip's wheel scrolls it sideways.

const HOLD_MS = 450;
const SLOP_PX = 10;
// Android fires contextmenu for the same touch at its own long-press timeout (400–500 ms, longer with the accessibility
// "touch & hold delay"): within this long after our hold fired (or while the finger is still down) it is that hold.
const NATIVE_GRACE_MS = 900;
// The click that ends a hold comes with the release or soon after it.
const SWALLOW_MS = 600;

function noop() { }

function safeInvoke(ref, method, ...args) {
  if (!ref) return Promise.resolve();
  try {
    return ref.invokeMethodAsync(method, ...args).catch(noop);
  } catch (e) {
    return Promise.resolve();
  }
}

/**
 * The hold of attachHold without the DOM (tools/ui-check/sticker-hold.mjs drives it with a fake clock): pointer events
 * call down(key, x, y, id) / move(x, y, id) / up() / cancel(), a contextmenu on a cell calls context(key), and every
 * click asks click() whether to swallow it. `clock` = { now(), set(fn, ms) → id, clear(id) }; `fire(key)` reports a hold.
 * One touch fires at most once: our timer or Android's own long-press (contextmenu), whichever comes first.
 */
export function holdGesture(clock, fire) {
  let timer = null;
  let start = null;
  let firedAt = 0;
  let releasedAt = 0;
  let held = false;

  const stopTimer = () => {
    if (timer !== null) clock.clear(timer);
    timer = null;
  };
  const doFire = (key) => {
    stopTimer();
    start = null;
    firedAt = clock.now();
    releasedAt = held ? 0 : firedAt;
    fire(key);
  };

  return {
    down(key, x, y, id) {
      stopTimer();
      held = true;
      firedAt = 0;
      releasedAt = 0;
      start = { key, x, y, id };
      timer = clock.set(() => {
        timer = null;
        if (start) doFire(start.key);
      }, HOLD_MS);
    },
    move(x, y, id) {
      if (timer === null || !start || id !== start.id) return;
      if (Math.abs(x - start.x) > SLOP_PX || Math.abs(y - start.y) > SLOP_PX) {
        stopTimer();
        start = null;
      }
    },
    up() {
      if (held && firedAt && !releasedAt) releasedAt = clock.now();
      held = false;
      stopTimer();
      start = null;
    },
    cancel() {
      held = false;
      stopTimer();
      start = null;
    },
    context(key) {
      // The hold already fired for this touch (Android's long-press after our timer): the same gesture.
      if (firedAt && (held || clock.now() - firedAt < NATIVE_GRACE_MS)) return;
      // Android's long-press came first: it is this touch's hold, fired now (once).
      if (timer !== null && start) {
        doFire(start.key);
        return;
      }
      // A right-click.
      doFire(key);
    },
    click() {
      if (!firedAt) return false;
      const swallow = held || !releasedAt || clock.now() - releasedAt <= SWALLOW_MS;
      firedAt = 0;
      return swallow;
    },
  };
}

/** Long-press / right-click on [data-hold] cells inside `root` → ref.OnHold(key). */
export function attachHold(root, ref) {
  if (!root) return { dispose: noop };
  const clock = {
    now: () => performance.now(),
    set: (fn, ms) => setTimeout(fn, ms),
    clear: (id) => clearTimeout(id),
  };
  const gesture = holdGesture(clock, (key) => safeInvoke(ref, 'OnHold', key));
  const cellOf = (target) => (target && target.closest ? target.closest('[data-hold]') : null);

  const onDown = (e) => {
    if (!e.isPrimary || (e.pointerType === 'mouse' && e.button !== 0)) return;
    const cell = cellOf(e.target);
    if (!cell || !root.contains(cell)) return;
    gesture.down(cell.getAttribute('data-hold'), e.clientX, e.clientY, e.pointerId);
  };
  const onMove = (e) => gesture.move(e.clientX, e.clientY, e.pointerId);
  const onUp = () => gesture.up();
  const onCancel = () => gesture.cancel();
  // Wherever the click lands: the scrim of the dialog the hold just opened, too.
  const onClick = (e) => {
    if (!gesture.click()) return;
    e.preventDefault();
    e.stopPropagation();
  };
  const onContext = (e) => {
    const cell = cellOf(e.target);
    if (!cell || !root.contains(cell)) return;
    e.preventDefault();
    gesture.context(cell.getAttribute('data-hold'));
  };

  root.addEventListener('pointerdown', onDown, { passive: true });
  // The finger may leave the cell (or a dialog may open under it) before it lifts.
  window.addEventListener('pointermove', onMove, { passive: true });
  window.addEventListener('pointerup', onUp, { passive: true, capture: true });
  window.addEventListener('pointercancel', onCancel, { passive: true, capture: true });
  root.addEventListener('scroll', onCancel, { passive: true, capture: true });
  window.addEventListener('click', onClick, true);
  root.addEventListener('contextmenu', onContext);

  return {
    dispose() {
      gesture.cancel();
      root.removeEventListener('pointerdown', onDown);
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp, true);
      window.removeEventListener('pointercancel', onCancel, true);
      root.removeEventListener('scroll', onCancel, true);
      window.removeEventListener('click', onClick, true);
      root.removeEventListener('contextmenu', onContext);
    }
  };
}

/** A vertical mouse wheel scrolls a horizontal strip (the panel's packs) while it overflows. */
export function attachStripWheel(strip) {
  if (!strip) return { dispose: noop };
  const onWheel = (e) => {
    if (Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return;
    if (strip.scrollWidth <= strip.clientWidth) return;
    strip.scrollLeft += e.deltaY;
    e.preventDefault();
  };
  strip.addEventListener('wheel', onWheel, { passive: false });
  return { dispose() { strip.removeEventListener('wheel', onWheel); } };
}

/** Keeps the strip's item [data-strip-key=key] in view without moving the page. */
export function revealInStrip(strip, key) {
  if (!strip || !key) return;
  const item = strip.querySelector(`[data-strip-key="${CSS.escape(String(key))}"]`);
  if (!item) return;
  const left = item.offsetLeft - 8;
  const right = item.offsetLeft + item.offsetWidth + 8;
  if (left < strip.scrollLeft) strip.scrollTo({ left, behavior: 'smooth' });
  else if (right > strip.scrollLeft + strip.clientWidth) strip.scrollTo({ left: right - strip.clientWidth, behavior: 'smooth' });
}

/** Scrolls `scroller` so that `section` starts at its top (no page scroll, unlike scrollIntoView in some WebViews). */
export function scrollToSection(scroller, section) {
  if (!scroller || !section) return;
  const top = section.getBoundingClientRect().top - scroller.getBoundingClientRect().top + scroller.scrollTop;
  scroller.scrollTo({ top: Math.max(0, top - 2), behavior: 'auto' });
}

/** Reduced motion or the lite mode: GIFs then wait for a tap instead of playing by themselves. */
export function calmMotion() {
  const root = document.documentElement;
  return root.dataset.reduceMotion === 'true'
    || root.dataset.fx === 'lite'
    || (typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches);
}

/**
 * A picked image for the new-sticker dialog: an object URL of its bytes (transparency shows as it is) and its size in
 * pixels (0 × 0 when the browser cannot decode it). Revoke the URL with revokeImage once the dialog is gone.
 */
export async function readImage(streamRef, type) {
  try {
    const buffer = await streamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: type || 'image/png' }));
    const size = await new Promise((resolve) => {
      const img = new Image();
      img.onload = () => resolve({ width: img.naturalWidth, height: img.naturalHeight });
      img.onerror = () => resolve({ width: 0, height: 0 });
      img.src = url;
    });
    return { url, width: size.width, height: size.height };
  } catch (e) {
    return null;
  }
}

export function revokeImage(url) {
  try {
    if (url) URL.revokeObjectURL(url);
  } catch (e) { /* ignore */ }
}
