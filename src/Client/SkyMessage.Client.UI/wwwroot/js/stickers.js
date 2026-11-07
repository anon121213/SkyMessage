// Sticker and GIF panels: one delegated long-press for every cell (a panel holds hundreds of them, so no per-cell
// binding). A cell is [data-hold="<key>"]. Holding it ~450 ms (moving less than 10 px) or right-clicking it reports
// OnHold(key) and swallows the click that follows, so a hold never also sends. Scrolling cancels a hold. The pack strip's
// wheel scrolls it sideways.

const HOLD_MS = 450;
const SLOP_PX = 10;

function noop() { }

function safeInvoke(ref, method, ...args) {
  if (!ref) return Promise.resolve();
  try {
    return ref.invokeMethodAsync(method, ...args).catch(noop);
  } catch (e) {
    return Promise.resolve();
  }
}

/** Long-press / right-click on [data-hold] cells inside `root` → ref.OnHold(key). */
export function attachHold(root, ref) {
  if (!root) return { dispose: noop };
  let timer = 0;
  let start = null;
  let swallowUntil = 0;

  const cellOf = (target) => (target && target.closest ? target.closest('[data-hold]') : null);

  const cancel = () => {
    if (timer) clearTimeout(timer);
    timer = 0;
    start = null;
  };

  const onDown = (e) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    const cell = cellOf(e.target);
    if (!cell || !root.contains(cell)) return;
    cancel();
    start = { x: e.clientX, y: e.clientY, cell, id: e.pointerId };
    timer = setTimeout(() => {
      timer = 0;
      const held = start;
      start = null;
      if (!held || !held.cell.isConnected) return;
      swallowUntil = performance.now() + 700;
      safeInvoke(ref, 'OnHold', held.cell.getAttribute('data-hold'));
    }, HOLD_MS);
  };

  const onMove = (e) => {
    if (!start || e.pointerId !== start.id) return;
    if (Math.abs(e.clientX - start.x) > SLOP_PX || Math.abs(e.clientY - start.y) > SLOP_PX) cancel();
  };

  const onUp = () => { if (timer) cancel(); };

  const onClick = (e) => {
    if (performance.now() < swallowUntil && cellOf(e.target)) {
      e.preventDefault();
      e.stopPropagation();
      swallowUntil = 0;
    }
  };

  const onContext = (e) => {
    const cell = cellOf(e.target);
    if (!cell || !root.contains(cell)) return;
    e.preventDefault();
    cancel();
    safeInvoke(ref, 'OnHold', cell.getAttribute('data-hold'));
  };

  root.addEventListener('pointerdown', onDown, { passive: true });
  root.addEventListener('pointermove', onMove, { passive: true });
  root.addEventListener('pointerup', onUp, { passive: true });
  root.addEventListener('pointercancel', cancel, { passive: true });
  root.addEventListener('scroll', cancel, { passive: true, capture: true });
  root.addEventListener('click', onClick, true);
  root.addEventListener('contextmenu', onContext);

  return {
    dispose() {
      cancel();
      root.removeEventListener('pointerdown', onDown);
      root.removeEventListener('pointermove', onMove);
      root.removeEventListener('pointerup', onUp);
      root.removeEventListener('pointercancel', cancel);
      root.removeEventListener('scroll', cancel, true);
      root.removeEventListener('click', onClick, true);
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
