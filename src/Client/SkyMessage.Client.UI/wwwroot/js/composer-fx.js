// SkyMessage composer motion (ES module; Features/Chat/Composer.razor attaches it, js/recorder.js plays its exits with
// ghost()/play()). See docs/MOTION.md. Blazor removes an element at once, so an exit plays on a copy: ghost(el) clones
// the element into the composer's own [data-fx-layer] (Blazor never renders into it) right over the original, hides the
// original and returns the copy, which play(..., remove) drops when its animation ends. attach(root) adds the rest:
// new tray items pop in one after another, a removed tray item shrinks away while the others slide into its place
// (FLIP), the round-video preview grows out of the record button, and launch() sends a message off from the orb (it
// lifts a little and its arrow flies up). All of it is skipped under reduceMotion().
import { reduceMotion } from './app.js';

function noop() { }

const EASE = 'cubic-bezier(0.2, 0.8, 0.2, 1)';
const EASE_OUT = 'cubic-bezier(0.16, 1, 0.3, 1)';
const EASE_IN = 'cubic-bezier(0.4, 0, 1, 1)';
// Attributes that make an element findable by the recorder or by attach(): a ghost must not answer to them.
const MARKERS = /^(_bl_|data-rec-|data-fx-(item|remove|grow|send)$|id$)/;

function layerOf(el) {
  const root = el && el.closest ? el.closest('[data-composer]') : null;
  return root ? root.querySelector(':scope > [data-fx-layer]') : null;
}

function scrub(node) {
  for (const el of [node, ...node.querySelectorAll('*')]) {
    for (const name of el.getAttributeNames()) {
      if (MARKERS.test(name)) el.removeAttribute(name);
    }
  }
}

function copyCanvases(from, to) {
  const a = from.querySelectorAll('canvas');
  const b = to.querySelectorAll('canvas');
  for (let i = 0; i < a.length && i < b.length; i++) {
    try {
      b[i].width = a[i].width;
      b[i].height = a[i].height;
      const ctx = b[i].getContext('2d');
      if (ctx && a[i].width && a[i].height) ctx.drawImage(a[i], 0, 0);
    } catch (e) { /* the copy stays blank */ }
  }
}

/**
 * Puts a copy of `el` into the composer's ghost layer exactly over it and hides `el` (Blazor removes it next).
 * `options.fixed`: `el` is position: fixed and keeps its own placement. `options.vars`: custom properties `el`
 * inherits (finger position) frozen onto the copy. `options.clip`: an element whose box clips the copy (a scroller).
 * Returns the copy (parts are found by [data-fx-part]), or null when motion is off or `el` is not in a composer.
 */
export function ghost(el, options) {
  const opts = options || {};
  if (!el || !el.isConnected || reduceMotion()) return null;
  const layer = layerOf(el);
  if (!layer) return null;
  const computed = getComputedStyle(el);
  const copy = el.cloneNode(true);
  scrub(copy);
  copyCanvases(el, copy);
  copy.setAttribute('aria-hidden', 'true');
  copy.inert = true;
  const style = copy.style;
  if (!opts.fixed) {
    const r = el.getBoundingClientRect();
    const base = layer.getBoundingClientRect();
    style.position = 'absolute';
    style.left = `${r.left - base.left}px`;
    style.top = `${r.top - base.top}px`;
    style.right = 'auto';
    style.bottom = 'auto';
    style.width = `${r.width}px`;
    style.height = `${r.height}px`;
    style.margin = '0';
    style.transform = 'none';
    if (opts.clip) {
      const c = opts.clip.getBoundingClientRect();
      const top = Math.max(0, c.top - r.top);
      const right = Math.max(0, r.right - c.right);
      const bottom = Math.max(0, r.bottom - c.bottom);
      const left = Math.max(0, c.left - r.left);
      if (top || right || bottom || left) style.clipPath = `inset(${top}px ${right}px ${bottom}px ${left}px)`;
    }
  }
  for (const name of opts.vars || []) {
    const value = computed.getPropertyValue(name);
    if (value) style.setProperty(name, value.trim());
  }
  style.animation = 'none';
  style.transition = 'none';
  style.pointerEvents = 'none';
  layer.appendChild(copy);
  el.style.visibility = 'hidden';
  return copy;
}

/** Runs `keyframes` on `el` (Web Animations); with `remove`, `el` is removed when it ends. Returns the Animation or null. */
export function play(el, keyframes, timing, remove) {
  if (!el) return null;
  let done = false;
  const finish = () => {
    if (done) return;
    done = true;
    if (remove) el.remove();
  };
  let animation = null;
  try {
    animation = typeof el.animate === 'function' ? el.animate(keyframes, timing) : null;
  } catch (e) {
    animation = null;
  }
  if (!animation) {
    finish();
    return null;
  }
  if (remove) {
    animation.onfinish = finish;
    animation.oncancel = finish;
    // Should a finish event never come (an engine quirk), the copy still goes.
    setTimeout(finish, (timing.duration || 0) + (timing.delay || 0) + 2000);
  }
  return animation;
}

/** Part of a ghost (or of any element) marked with data-fx-part. */
export const part = (el, name) => (el ? el.querySelector(`[data-fx-part="${name}"]`) : null);

/**
 * Composer-wide motion. Returns { dispose() }.
 */
export function attach(root) {
  if (!root) return { launch: noop, dispose: noop };
  let disposed = false;
  const layer = root.querySelector(':scope > [data-fx-layer]');
  const inLayer = (node) => !!layer && layer.contains(node);

  // ---- Tray: a removed item shrinks away, the ones after it slide into its place ----

  const flip = (list, removed, before) => {
    let timer = 0;
    const observer = new MutationObserver(() => {
      if (removed.parentNode === list) return;
      stop();
      for (const [child, left] of before) {
        if (!child.isConnected) continue;
        const dx = left - child.getBoundingClientRect().left;
        if (Math.abs(dx) > 0.5) play(child, [{ transform: `translateX(${dx}px)` }, { transform: 'none' }], { duration: 220, easing: EASE });
      }
    });
    const stop = () => {
      observer.disconnect();
      clearTimeout(timer);
    };
    observer.observe(list, { childList: true });
    // The last item stays in the closing tray (it keeps its content while it collapses): nothing to slide then.
    timer = setTimeout(stop, 1500);
  };

  const onClick = (e) => {
    if (disposed || reduceMotion()) return;
    const button = e.target && e.target.closest ? e.target.closest('[data-fx-remove]') : null;
    if (!button || !root.contains(button)) return;
    const item = button.closest('[data-fx-item]');
    const list = item ? item.parentNode : null;
    if (!list) return;
    const before = new Map();
    for (const child of list.children) if (child !== item) before.set(child, child.getBoundingClientRect().left);
    const copy = ghost(item, { clip: list });
    if (copy) {
      play(copy, [
        { transform: 'none', opacity: 1 },
        { transform: 'scale(0.5)', opacity: 0 }
      ], { duration: 160, easing: EASE_IN, fill: 'forwards' }, true);
    }
    flip(list, item, before);
  };

  // ---- The round-video preview grows out of the record button ----

  const grow = (overlay) => {
    const stage = part(overlay, 'stage');
    const button = root.querySelector('[data-rec-button]');
    if (!stage || !button) return;
    const s = stage.getBoundingClientRect();
    const b = button.getBoundingClientRect();
    if (!s.width || !b.width) return;
    const dx = b.left + b.width / 2 - (s.left + s.width / 2);
    const dy = b.top + b.height / 2 - (s.top + s.height / 2);
    play(stage, [
      { transform: `translate(${dx}px, ${dy}px) scale(${(b.width / s.width).toFixed(3)})`, opacity: 0.5 },
      { transform: 'none', opacity: 1 }
    ], { duration: 320, easing: EASE_OUT });
  };

  // ---- New elements: tray items pop in one after another; the video overlay grows ----

  const observer = new MutationObserver((records) => {
    if (disposed || reduceMotion()) return;
    let n = 0;
    for (const record of records) {
      for (const node of record.addedNodes) {
        if (node.nodeType !== 1 || inLayer(node)) continue;
        if (node.hasAttribute('data-fx-item')) {
          if (n > 0) node.style.animationDelay = `${Math.min(n, 6) * 45}ms`;
          n++;
        } else if (node.hasAttribute('data-fx-grow')) {
          grow(node);
        }
      }
    }
  });
  observer.observe(root, { childList: true, subtree: true });
  root.addEventListener('click', onClick, true);

  // ---- Launch: what goes out leaves from the orb ----

  /**
   * Called by .NET right before the field clears. kind: 'send' (the arrow flies up out of the orb) or 'save' (an edit:
   * the orb only lifts). The orb itself is never copied: whichever glyph shows next rides the lift.
   */
  const launch = (kind) => {
    if (disposed || reduceMotion()) return;
    const action = root.querySelector('[data-rec-action]');
    if (!action) return;
    play(action, [
      { transform: 'none' },
      { transform: 'translateY(-5px) scale(1.04)', offset: 0.3 },
      { transform: 'none' }
    ], { duration: 420, easing: EASE });
    const glyph = kind === 'send' ? root.querySelector('[data-fx-send] svg') : null;
    if (glyph && layer) {
      // Starts under the orb (the ghost layer paints below it) and rises out of it in the accent.
      const r = glyph.getBoundingClientRect();
      const base = layer.getBoundingClientRect();
      const copy = glyph.cloneNode(true);
      copy.removeAttribute('class');
      Object.assign(copy.style, {
        position: 'absolute', left: `${r.left - base.left}px`, top: `${r.top - base.top}px`, width: `${r.width}px`,
        height: `${r.height}px`, color: 'var(--sm-accent)', overflow: 'visible', pointerEvents: 'none'
      });
      layer.appendChild(copy);
      play(copy, [
        { transform: 'translateY(0) scale(1)', opacity: 1 },
        { transform: 'translateY(-36px) scale(0.95)', opacity: 1, offset: 0.45 },
        { transform: 'translateY(-62px) scale(0.7)', opacity: 0 }
      ], { duration: 480, easing: 'cubic-bezier(0.3, 0.7, 0.4, 1)' }, true);
    }
  };

  return {
    launch,
    dispose() {
      disposed = true;
      observer.disconnect();
      root.removeEventListener('click', onClick, true);
      if (layer) layer.textContent = '';
    }
  };
}

export { EASE, EASE_OUT, EASE_IN };
