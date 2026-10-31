// SkyMessage UI helpers (ES module, imported by Services/AppJs.cs). Every function is safe to call after the
// element is gone; subscriptions return an object with dispose().

const root = document.documentElement;
const THEME_KEY = 'sm.theme.v1';
const LANG_KEY = 'sm.lang';
let initialized = false;

function noop() { }

/** True when motion should be skipped (Animations setting off, or the OS asks for reduced motion). See docs/MOTION.md. */
export function reduceMotion() {
  return root.dataset.reduceMotion === 'true'
    || (typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches);
}

function safeInvoke(ref, method, ...args) {
  if (!ref) return Promise.resolve();
  try {
    return ref.invokeMethodAsync(method, ...args).catch(noop);
  } catch (e) {
    return Promise.resolve();
  }
}

// ---- Startup -----------------------------------------------------------------------------------------

export function init() {
  if (initialized) return;
  initialized = true;

  // Escape closes the top overlay through history (same path as Android Back).
  window.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape' || e.defaultPrevented || e.isComposing) return;
    const h = window.__smHistory;
    if (h && h.depth() > 0) {
      e.preventDefault();
      h.back();
    }
  });

  // Visual viewport inset (mobile browsers that overlay the keyboard). Native hosts set --sm-kb-inset themselves.
  const vv = window.visualViewport;
  if (vv) {
    let frame = 0;
    const update = () => {
      frame = 0;
      const inset = Math.max(0, Math.round(window.innerHeight - vv.height - vv.offsetTop));
      root.style.setProperty('--sm-vv-inset', inset > 60 ? inset + 'px' : '0px');
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(update); };
    vv.addEventListener('resize', schedule);
    vv.addEventListener('scroll', schedule);
    update();
  }

  // Input modality for focus rings: keyboard users get visible outlines, pointer users do not.
  window.addEventListener('pointerdown', () => root.setAttribute('data-input', 'pointer'), { passive: true, capture: true });
  window.addEventListener('keydown', (e) => { if (e.key === 'Tab') root.setAttribute('data-input', 'keyboard'); }, { capture: true });

  // iOS: block pinch-zoom of the whole app shell (images have their own viewer).
  document.addEventListener('gesturestart', (e) => e.preventDefault(), { passive: false });
}

// ---- Overlay history (implemented in boot.js, which must load before Blazor) -------------------------

const fallbackHistory = (() => {
  const stack = [];
  let listener = null;
  window.addEventListener('popstate', (e) => {
    const here = (e.state && e.state.smOverlay) || null;
    if (!stack.length) return;
    const index = here ? stack.indexOf(here) : -1;
    const closed = stack.splice(index + 1).reverse();
    if (closed.length && listener) safeInvoke(listener, 'OnOverlaysPopped', closed);
  });
  return {
    push(id) {
      const next = Object.assign({}, history.state || {}, { smOverlay: id });
      history.pushState(next, '', location.href);
      stack.push(id);
    },
    close(id) {
      const index = stack.indexOf(id);
      if (index < 0) return Promise.resolve();
      const removed = stack.splice(index);
      return new Promise((resolve) => { history.go(-removed.length); setTimeout(resolve, 350); });
    },
    // Without boot.js the router would see (and render) every page on the way back: keep the history as it is.
    rewind() { return Promise.resolve(); },
    forget() { stack.length = 0; },
    depth() { return stack.length; },
    setListener(l) { listener = l; },
    back() { history.back(); }
  };
})();

function hist() { return window.__smHistory || fallbackHistory; }

export function historyAttach(dotnetRef) { hist().setListener(dotnetRef); }
export function historyPush(id) { return hist().push(id); }
export function historyClose(id) { return hist().close(id); }
export function historyRewind(pages) { return hist().rewind(pages); }
export function historyForget() { hist().forget(); }
export function historyBack() { history.back(); }
export function historyGo(delta) { if (delta) history.go(delta); }
export function historyDepth() { return hist().depth(); }

// ---- Focus & clipboard ---------------------------------------------------------------------------------

export function focusElement(el, selectText) {
  if (!el || !el.focus) return;
  el.focus({ preventScroll: true });
  if (selectText && typeof el.select === 'function') el.select();
}

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function focusFirst(container) {
  if (!container) return;
  const target = container.querySelector('[autofocus]') || container.querySelector(FOCUSABLE) || container;
  if (target === container && !container.hasAttribute('tabindex')) container.setAttribute('tabindex', '-1');
  target.focus({ preventScroll: true });
}

export function blurActive() {
  const el = document.activeElement;
  if (el && el !== document.body && el.blur) el.blur();
}

// Keeps Tab focus inside a dialog while it is open.
export function trapFocus(container) {
  if (!container) return { dispose: noop };
  const previous = document.activeElement;
  const onKey = (e) => {
    if (e.key !== 'Tab') return;
    const items = Array.from(container.querySelectorAll(FOCUSABLE)).filter((x) => x.offsetParent !== null);
    if (!items.length) { e.preventDefault(); return; }
    const first = items[0];
    const last = items[items.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  };
  container.addEventListener('keydown', onKey);
  return {
    dispose() {
      container.removeEventListener('keydown', onKey);
      if (previous && previous.focus && document.contains(previous)) {
        try { previous.focus({ preventScroll: true }); } catch (e) { /* ignore */ }
      }
    }
  };
}

export async function copyText(text) {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch (e) { /* fall back below */ }
  try {
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    area.style.left = '-9999px';
    document.body.appendChild(area);
    area.select();
    area.setSelectionRange(0, text.length);
    const ok = document.execCommand('copy');
    document.body.removeChild(area);
    return ok;
  } catch (e) {
    return false;
  }
}

// ---- Observers ------------------------------------------------------------------------------------------

export function observeVisibility(el, ref, method, rootMargin, threshold) {
  if (!el || !('IntersectionObserver' in window)) {
    safeInvoke(ref, method, true, 1);
    return { dispose: noop };
  }
  let last = null;
  const io = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      const visible = entry.isIntersecting;
      if (visible !== last) {
        last = visible;
        safeInvoke(ref, method, visible, entry.intersectionRatio);
      }
    }
  }, { rootMargin: rootMargin || '0px', threshold: threshold || 0 });
  io.observe(el);
  return { dispose() { io.disconnect(); } };
}

// One observer for many elements; changes are batched per animation frame as [{ key, visible, ratio }].
export function createIntersectionObserver(rootEl, ref, method, rootMargin, threshold) {
  const keys = new WeakMap();
  const pending = new Map();
  let frame = 0;
  const flush = () => {
    frame = 0;
    if (!pending.size) return;
    const changes = Array.from(pending.values());
    pending.clear();
    safeInvoke(ref, method, changes);
  };
  const io = 'IntersectionObserver' in window
    ? new IntersectionObserver((entries) => {
      for (const entry of entries) {
        const key = keys.get(entry.target);
        if (key === undefined) continue;
        pending.set(key, { key, visible: entry.isIntersecting, ratio: entry.intersectionRatio });
      }
      if (!frame) frame = requestAnimationFrame(flush);
    }, { root: rootEl || null, rootMargin: rootMargin || '0px', threshold: threshold || 0 })
    : null;
  return {
    observe(el, key) {
      if (!el) return;
      keys.set(el, String(key));
      if (io) io.observe(el);
      else { pending.set(String(key), { key: String(key), visible: true, ratio: 1 }); if (!frame) frame = requestAnimationFrame(flush); }
    },
    unobserve(el) { if (el && io) io.unobserve(el); },
    dispose() { if (io) io.disconnect(); if (frame) cancelAnimationFrame(frame); pending.clear(); }
  };
}

// ---- Gestures -------------------------------------------------------------------------------------------

const LONG_PRESS_MOVE_TOLERANCE = 10;

// After a long-press fired, the click that ends the touch (sent when the hold was shorter than the system long-press
// delay, which older users often lengthen) must not land on what just opened under the finger: a menu's backdrop
// would close the menu at once. Swallows the next click if it comes within 500 ms of the release.
function swallowClickAfterRelease() {
  let releasedAt = 0;
  const cleanup = () => {
    window.removeEventListener('pointerup', onUp, true);
    window.removeEventListener('click', onClick, true);
    clearTimeout(timer);
  };
  const onUp = () => { if (!releasedAt) releasedAt = Date.now(); };   // the release of the long-press itself
  const onClick = (e) => {
    if (!releasedAt || Date.now() - releasedAt <= 500) {
      e.preventDefault();
      e.stopPropagation();
    }
    cleanup();
  };
  window.addEventListener('pointerup', onUp, true);
  window.addEventListener('click', onClick, true);
  const timer = setTimeout(cleanup, 8000);
}

function longPress(el, onFire, ms) {
  let timer = 0;
  let startX = 0;
  let startY = 0;
  let firedAt = 0;
  const cancel = () => { if (timer) { clearTimeout(timer); timer = 0; } };
  const down = (e) => {
    if (e.pointerType === 'mouse' || !e.isPrimary) return;
    startX = e.clientX;
    startY = e.clientY;
    cancel();
    timer = setTimeout(() => {
      timer = 0;
      firedAt = Date.now();
      swallowClickAfterRelease();
      onFire(startX, startY);
    }, ms || 450);
  };
  const move = (e) => {
    if (!timer) return;
    if (Math.abs(e.clientX - startX) > LONG_PRESS_MOVE_TOLERANCE || Math.abs(e.clientY - startY) > LONG_PRESS_MOVE_TOLERANCE) cancel();
  };
  // The click that ends a long-press must not also activate the element.
  const click = (e) => {
    if (firedAt && Date.now() - firedAt < 900) {
      e.preventDefault();
      e.stopPropagation();
      firedAt = 0;
    }
  };
  el.addEventListener('pointerdown', down, { passive: true });
  el.addEventListener('pointermove', move, { passive: true });
  el.addEventListener('pointerup', cancel, { passive: true });
  el.addEventListener('pointercancel', cancel, { passive: true });
  el.addEventListener('pointerleave', cancel, { passive: true });
  el.addEventListener('scroll', cancel, { passive: true, capture: true });
  el.addEventListener('click', click, true);
  return {
    recentlyFired: () => firedAt && Date.now() - firedAt < 900,
    // A native long-press menu event won the race: drop our timer and swallow the trailing click.
    claim() { cancel(); firedAt = Date.now(); },
    dispose() {
      cancel();
      el.removeEventListener('pointerdown', down);
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', cancel);
      el.removeEventListener('pointercancel', cancel);
      el.removeEventListener('pointerleave', cancel);
      el.removeEventListener('scroll', cancel, true);
      el.removeEventListener('click', click, true);
    }
  };
}

// Right-click (desktop) and touch long-press (all platforms; iOS has no contextmenu event) → method(x, y).
export function attachContextMenu(el, ref, method, ms) {
  if (!el) return { dispose: noop };
  const lp = longPress(el, (x, y) => safeInvoke(ref, method, x, y), ms);
  const onContext = (e) => {
    e.preventDefault();
    if (lp.recentlyFired()) return; // Android also fires contextmenu after our long-press timer
    lp.claim();
    safeInvoke(ref, method, e.clientX, e.clientY);
  };
  el.addEventListener('contextmenu', onContext);
  return {
    dispose() {
      lp.dispose();
      el.removeEventListener('contextmenu', onContext);
    }
  };
}

export function attachLongPress(el, ref, method, ms) {
  if (!el) return { dispose: noop };
  const lp = longPress(el, (x, y) => safeInvoke(ref, method, x, y), ms);
  const onContext = (e) => { if (lp.recentlyFired()) e.preventDefault(); };
  el.addEventListener('contextmenu', onContext);
  return { dispose() { lp.dispose(); el.removeEventListener('contextmenu', onContext); } };
}

export function onClickOutside(el, ref, method) {
  if (!el) return { dispose: noop };
  const handler = (e) => { if (!el.contains(e.target)) safeInvoke(ref, method); };
  const id = setTimeout(() => document.addEventListener('pointerdown', handler, true), 0);
  return { dispose() { clearTimeout(id); document.removeEventListener('pointerdown', handler, true); } };
}

function canScrollToward(target, stop, direction) {
  for (let node = target; node && node !== stop.parentElement; node = node.parentElement) {
    if (!(node instanceof HTMLElement)) continue;
    const style = getComputedStyle(node);
    const scrollY = /(auto|scroll)/.test(style.overflowY) && node.scrollHeight > node.clientHeight;
    const scrollX = /(auto|scroll)/.test(style.overflowX) && node.scrollWidth > node.clientWidth;
    if (direction === 'down' && scrollY && node.scrollTop > 0) return true;
    if (direction === 'up' && scrollY && node.scrollTop + node.clientHeight < node.scrollHeight - 1) return true;
    if (direction === 'right' && scrollX && node.scrollLeft > 0) return true;
    if (direction === 'left' && scrollX && node.scrollLeft + node.clientWidth < node.scrollWidth - 1) return true;
    if (node === stop) break;
  }
  return false;
}

// Drag in `direction` ("down", "up", "left", "right", or "x" for either side); the element follows the finger and
// method() runs when released beyond `threshold` px (or flicked). Gestures that start inside a scrolled child are
// ignored. options.fling: on release the element flies out the way it was thrown before method(options.arg) runs
// (otherwise it springs back after a moment, for the caller to remove it). The axis is decided after 8 px.
export function attachSwipe(el, ref, method, direction, threshold, options) {
  if (!el) return { dispose: noop };
  const opts = options || {};
  const limit = threshold || 80;
  const either = direction === 'x';
  const vertical = direction === 'down' || direction === 'up';
  let sign = direction === 'down' || direction === 'right' ? 1 : -1;
  let active = false;
  let startX = 0, startY = 0, startT = 0, delta = 0, decided = false, id = null, flung = false;
  const call = () => (opts.arg == null ? safeInvoke(ref, method) : safeInvoke(ref, method, opts.arg));
  const reset = () => {
    el.style.transition = 'transform 180ms cubic-bezier(.2,.8,.2,1)';
    el.style.transform = '';
    setTimeout(() => { el.style.transition = ''; }, 200);
  };
  const down = (e) => {
    if (flung || (e.pointerType === 'mouse' && e.button !== 0)) return;
    if (!either && canScrollToward(e.target, el, direction)) return;
    if (e.target.closest && e.target.closest('input, textarea, select, [data-no-swipe]')) return;
    active = true; decided = false; delta = 0; id = e.pointerId;
    startX = e.clientX; startY = e.clientY; startT = performance.now();
  };
  const move = (e) => {
    if (!active || e.pointerId !== id) return;
    const dx = e.clientX - startX;
    const dy = e.clientY - startY;
    if (!decided) {
      if (Math.abs(dx) < 8 && Math.abs(dy) < 8) return;
      const along = vertical ? Math.abs(dy) > Math.abs(dx) * 1.5 : Math.abs(dx) > Math.abs(dy) * 1.5;
      if (either && along) sign = dx > 0 ? 1 : -1;
      if (!along || (vertical ? dy : dx) * sign < 0) { active = false; return; }
      decided = true;
      try { el.setPointerCapture(id); } catch (err) { /* ignore */ }
    }
    delta = Math.max(0, (vertical ? dy : dx) * sign);
    el.style.transition = 'none';
    el.style.transform = vertical ? `translateY(${delta * sign}px)` : `translateX(${delta * sign}px)`;
    if (opts.fling && !vertical) el.style.opacity = String(Math.max(0.3, 1 - delta / (el.offsetWidth || 300)));
  };
  const up = (e) => {
    if (!active || e.pointerId !== id) return;
    active = false;
    if (!decided) return;
    const velocity = delta / Math.max(1, performance.now() - startT);
    const commit = e.type === 'pointerup' && (delta > limit || velocity > 0.6);
    if (commit && opts.fling) {
      flung = true;
      if (reduceMotion()) { call(); return; }
      el.style.transition = 'transform 160ms cubic-bezier(.4,0,1,1), opacity 160ms linear';
      el.style.transform = vertical ? `translateY(${sign * 120}%)` : `translateX(${sign * 120}%)`;
      el.style.opacity = '0';
      setTimeout(call, 150);
    } else if (commit) {
      call();
      setTimeout(reset, 250);
    } else {
      el.style.opacity = '';
      reset();
    }
  };
  el.addEventListener('pointerdown', down, { passive: true });
  el.addEventListener('pointermove', move, { passive: true });
  el.addEventListener('pointerup', up, { passive: true });
  el.addEventListener('pointercancel', up, { passive: true });
  return {
    dispose() {
      el.removeEventListener('pointerdown', down);
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', up);
      el.removeEventListener('pointercancel', up);
    }
  };
}

// ---- Swipe back (phone page stack) ------------------------------------------------------------------------
// MainLayout marks the page that can be swiped away with [data-swipe-back] and, when it is mounted, the screen Back
// returns to with [data-swipe-under] (a .shell__scrim sits between them). A rightward pan that starts >= 24 px from
// the left edge drags the page; releasing past 35% of the width or with a flick commits it: the page slides out,
// ref.OnSwipeBackCommit(revealed) runs and the page's own back button ([data-shell-back]) is clicked, so the navigation
// takes exactly the path of that button (NavigationService, overlay history). A tap on that button plays the same exit.
// Elements that own horizontal drags carry [data-no-swipe-back]. See docs/MOTION.md §2.

const SWIPE_BACK_EDGE = 24;
const SWIPE_BACK_SLOP = 8;
const SWIPE_BACK_COMMIT = 0.35;
const SWIPE_BACK_FLICK = 0.5;
const SWIPE_BACK_PARALLAX = 0.28;
const SWIPE_BACK_SCRIM = 0.2;
const SWIPE_BACK_SETTLE_MS = 1500;

// An overlay is open (history entries, or one still on screen); a closing one playing its exit does not count.
function overlaysOpen() {
  return hist().depth() > 0 || !!document.querySelector('.sm-overlay:not(.sm-overlay--leaving), .sm-menu-layer:not(.sm-menu-layer--leaving)');
}

// A horizontally scrollable element between target and stop (tabs, chips): its own pans win.
function inSideScroller(target, stop) {
  for (let node = target; node && node !== stop; node = node.parentElement) {
    if (!(node instanceof HTMLElement) || node.scrollWidth <= node.clientWidth + 1) continue;
    if (/(auto|scroll)/.test(getComputedStyle(node).overflowX)) return true;
  }
  return false;
}

// Completes CSS animations still running on the element (an enter slide) so inline transforms take over without
// the animation restarting later.
function finishAnimations(el) {
  if (!el || typeof el.getAnimations !== 'function') return;
  for (const a of el.getAnimations()) {
    try { a.finish(); } catch (e) { /* infinite animation */ }
  }
}

function afterTransition(el, ms, done) {
  let finished = false;
  const finish = () => {
    if (finished) return;
    finished = true;
    el.removeEventListener('transitionend', onEnd);
    clearTimeout(timer);
    done();
  };
  const onEnd = (e) => { if (e.target === el && e.propertyName === 'transform') finish(); };
  el.addEventListener('transitionend', onEnd);
  const timer = setTimeout(finish, ms + 60);
}

function cssMs(name, fallback) {
  const value = getComputedStyle(root).getPropertyValue(name).trim();
  const number = parseFloat(value);
  if (!Number.isFinite(number)) return fallback;
  return value.endsWith('ms') ? number : value.endsWith('s') ? number * 1000 : number;
}

export function attachSwipeBack(container, ref) {
  if (!container) return { dispose: noop };
  let g = null;          // pan in progress
  let settling = null;   // committed: waiting for the shell to remove the page
  let frame = 0;
  let lastPointer = '';
  let ignoreClicksUntil = 0;
  let clicking = false;

  const topLayer = () => {
    const marked = container.querySelectorAll(':scope > [data-swipe-back]');
    return marked.length ? marked[marked.length - 1] : null;
  };

  const backButton = (top) => {
    const button = top ? top.querySelector('[data-shell-back]') : null;
    return button && !button.disabled ? button : null;
  };

  const render = () => {
    frame = 0;
    if (!g || !g.claimed) return;
    const dx = Math.max(0, g.dx);
    const p = Math.min(1, dx / g.width);
    g.top.style.transform = `translate3d(${dx}px,0,0)`;
    if (g.under && !g.reduce) g.under.style.transform = `translate3d(${(-(1 - p) * g.width * SWIPE_BACK_PARALLAX).toFixed(1)}px,0,0)`;
    if (g.scrim) g.scrim.style.opacity = ((1 - p) * SWIPE_BACK_SCRIM).toFixed(3);
  };

  // Reveals the screen underneath (or the bare background) and lifts the page for dragging.
  const prepare = (s) => {
    s.under = container.querySelector(':scope > [data-swipe-under]');
    s.scrim = container.querySelector(':scope > .shell__scrim');
    s.width = container.clientWidth || window.innerWidth;
    s.reduce = reduceMotion();
    finishAnimations(s.top);
    s.top.style.transition = 'none';
    s.top.style.willChange = 'transform';
    s.top.setAttribute('data-swiping', '');
    if (s.under) {
      finishAnimations(s.under);
      s.under.style.transition = 'none';
      s.under.style.visibility = 'visible';
      s.under.style.willChange = 'transform';
      if (!s.reduce) s.under.style.transform = `translate3d(${(-s.width * SWIPE_BACK_PARALLAX).toFixed(1)}px,0,0)`;
    }
    if (s.scrim) {
      s.scrim.style.zIndex = getComputedStyle(s.top).zIndex;
      s.scrim.style.transition = 'none';
      s.scrim.style.visibility = 'visible';
      s.scrim.style.opacity = String(SWIPE_BACK_SCRIM);
    }
  };

  const clean = (s, resetTop) => {
    if (resetTop) {
      s.top.style.transition = '';
      s.top.style.transform = '';
      s.top.style.willChange = '';
      s.top.removeAttribute('data-swiping');
    }
    if (s.under) {
      s.under.style.transition = '';
      s.under.style.transform = '';
      s.under.style.visibility = '';
      s.under.style.willChange = '';
    }
    if (s.scrim) {
      s.scrim.style.transition = '';
      s.scrim.style.opacity = '';
      s.scrim.style.visibility = '';
      s.scrim.style.zIndex = '';
    }
    if (s.observer) s.observer.disconnect();
    clearTimeout(s.timer);
    if (settling === s) settling = null;
  };

  // Not committed: the page springs back over the screen underneath.
  const restore = (s) => {
    if (s.reduce || !s.top.isConnected) { clean(s, true); return; }
    const t = 'transform var(--sm-dur) var(--sm-ease-out)';
    s.top.style.transition = t;
    s.top.style.transform = 'translate3d(0,0,0)';
    if (s.under) {
      s.under.style.transition = t;
      s.under.style.transform = `translate3d(${(-s.width * SWIPE_BACK_PARALLAX).toFixed(1)}px,0,0)`;
    }
    if (s.scrim) {
      s.scrim.style.transition = 'opacity var(--sm-dur) var(--sm-ease-out)';
      s.scrim.style.opacity = String(SWIPE_BACK_SCRIM);
    }
    // A new pan on the same page that already took over keeps its own styles.
    afterTransition(s.top, cssMs('--sm-dur', 220), () => { if (!g || g.top !== s.top || !g.claimed) clean(s, true); });
  };

  // Committed: the page leaves, then the back button runs. The inline styles stay until the shell has removed the
  // page or covered it (or it is put back if it is still on top after a while: the back action did not navigate).
  const leave = (s, ms) => {
    settling = s;
    const gone = () => !s.top.isConnected || !s.top.hasAttribute('data-swipe-back');
    const navigate = async () => {
      try { await ref.invokeMethodAsync('OnSwipeBackCommit', !!s.under); } catch (e) { /* page shutting down */ }
      if (settling !== s) return;
      if (gone()) { clean(s, s.top.isConnected); return; }
      s.observer = new MutationObserver(() => { if (settling === s && gone()) clean(s, s.top.isConnected); });
      s.observer.observe(container, { childList: true });
      s.observer.observe(s.top, { attributes: true, attributeFilter: ['data-swipe-back'] });
      s.timer = setTimeout(() => { if (settling === s) restore(s); }, SWIPE_BACK_SETTLE_MS);
      if (s.back.isConnected) {
        clicking = true;
        try { s.back.click(); } finally { clicking = false; }
      }
    };
    if (s.reduce) {
      s.top.style.transform = `translate3d(${s.width}px,0,0)`;
      if (s.under) s.under.style.transform = '';
      if (s.scrim) s.scrim.style.opacity = '0';
      navigate();
      return;
    }
    const t = `transform ${ms}ms var(--sm-ease-in)`;
    s.top.style.transition = t;
    s.top.style.transform = `translate3d(${s.width}px,0,0)`;
    if (s.under) {
      s.under.style.transition = t;
      s.under.style.transform = 'translate3d(0,0,0)';
    }
    if (s.scrim) {
      s.scrim.style.transition = `opacity ${ms}ms linear`;
      s.scrim.style.opacity = '0';
    }
    afterTransition(s.top, ms, navigate);
  };

  const velocity = (s) => {
    const now = performance.now();
    let first = null;
    for (const sample of s.samples) {
      if (now - sample.t <= 100) { first = sample; break; }
    }
    const last = s.samples[s.samples.length - 1];
    if (!first || !last || last.t - first.t < 8) return 0;
    return (last.x - first.x) / (last.t - first.t);
  };

  const down = (e) => {
    lastPointer = e.pointerType;
    if (g || settling || !e.isPrimary) return;
    if (e.pointerType !== 'touch' && e.pointerType !== 'pen') return;
    if (e.clientX < SWIPE_BACK_EDGE) return;
    const top = topLayer();
    const target = e.target;
    if (!top || !(target instanceof Element) || !top.contains(target)) return;
    if (target.closest('[data-no-swipe-back], input, textarea, select, [contenteditable="true"]')) return;
    if (overlaysOpen() || inSideScroller(target, top)) return;
    const back = backButton(top);
    if (!back) return;
    g = { id: e.pointerId, x0: e.clientX, y0: e.clientY, dx: 0, top, back, claimed: false, over: false, samples: [] };
  };

  const move = (e) => {
    if (!g || e.pointerId !== g.id) return;
    if (!g.top.isConnected) { cancel(); return; }
    const dx = e.clientX - g.x0;
    const dy = e.clientY - g.y0;
    if (!g.claimed) {
      if (Math.abs(dx) < SWIPE_BACK_SLOP && Math.abs(dy) < SWIPE_BACK_SLOP) return;
      if (dx <= 0 || Math.abs(dx) <= Math.abs(dy) * 1.5) { g = null; return; }
      // No explicit pointer capture: touch is implicitly captured by the element it started on, so its own
      // recognizers keep seeing the moves (a message's long-press timer is cancelled by them).
      g.claimed = true;
      g.x0 = e.clientX;   // start following from here: no jump by the slop
      prepare(g);
    }
    g.dx = e.clientX - g.x0;
    const now = performance.now();
    g.samples.push({ x: e.clientX, t: now });
    while (g.samples.length > 2 && now - g.samples[0].t > 120) g.samples.shift();
    const over = g.dx > g.width * SWIPE_BACK_COMMIT;
    if (over !== g.over) {
      g.over = over;
      if (over) safeInvoke(ref, 'OnSwipeBackThreshold');
    }
    if (!frame) frame = requestAnimationFrame(render);
  };

  const cancel = () => {
    const s = g;
    g = null;
    if (s && s.claimed) clean(s, true);
  };

  const up = (e) => {
    if (!g || e.pointerId !== g.id) return;
    const s = g;
    g = null;
    if (!s.claimed) return;
    if (frame) { cancelAnimationFrame(frame); frame = 0; }
    ignoreClicksUntil = Date.now() + 400;
    if (!s.top.isConnected) { clean(s, true); return; }
    const flick = velocity(s);
    const commit = e.type === 'pointerup' && (s.dx > s.width * SWIPE_BACK_COMMIT || (flick > SWIPE_BACK_FLICK && s.dx > 16));
    if (!commit) { restore(s); return; }
    // The rest of the way at the finger's pace, within the short exit duration.
    const fast = cssMs('--sm-dur-fast', 140);
    const remaining = Math.max(0, s.width - s.dx);
    const ms = Math.round(Math.min(fast, Math.max(80, flick > 0 ? remaining / flick : fast)));
    leave(s, ms);
  };

  // A tap on the page's back button plays the same exit before it runs (touch only: mouse/keyboard stay instant).
  const click = (e) => {
    if (clicking) return;
    if (Date.now() < ignoreClicksUntil) {
      e.preventDefault();
      e.stopPropagation();
      return;
    }
    if (!e.isTrusted || settling || g || lastPointer !== 'touch' || reduceMotion()) return;
    const top = topLayer();
    const back = backButton(top);
    if (!back || !(e.target instanceof Element) || !back.contains(e.target) || overlaysOpen()) return;
    e.preventDefault();
    e.stopPropagation();
    const s = { top, back, dx: 0, claimed: true };
    prepare(s);
    settling = s;
    // Let the revealed state paint once before it starts moving.
    requestAnimationFrame(() => { if (settling === s) leave(s, cssMs('--sm-dur', 220)); });
  };

  container.addEventListener('pointerdown', down, { passive: true });
  container.addEventListener('pointermove', move, { passive: true });
  container.addEventListener('pointerup', up, { passive: true });
  container.addEventListener('pointercancel', up, { passive: true });
  container.addEventListener('click', click, true);
  return {
    dispose() {
      container.removeEventListener('pointerdown', down);
      container.removeEventListener('pointermove', move);
      container.removeEventListener('pointerup', up);
      container.removeEventListener('pointercancel', up);
      container.removeEventListener('click', click, true);
      if (frame) cancelAnimationFrame(frame);
      if (g && g.claimed) clean(g, true);
      if (settling) clean(settling, true);
      g = null;
    }
  };
}

// ---- Swipeable list rows (chat list) ----------------------------------------------------------------------
// Rows are wrappers [data-swipe-row="<key>"] around their content ([data-swipe-content]); their action buttons sit
// behind the content in [data-swipe-actions="start|end"] (left / right side; [data-swipe-start|end="false"] turns a side
// off) and are positioned by CSS from --row-x (signed offset) and --row-w (its size), set here on the wrapper. The
// wrapper gets [data-swipe] while it is moved or open, [data-full] past the full-swipe point. The last button of a side
// is its primary action ([data-row-action]): a full swipe runs it through ref.OnRowAction(key, action); a shorter swipe
// leaves the row open (one at a time; a tap or a scroll elsewhere closes it). ref.OnRowSwipeArm(key) asks .NET to
// render a row's buttons the first time it moves, ref.OnRowSwipeThreshold() marks the full-swipe point (haptic).

const ROW_SLOP = 8;
const ROW_ACTION_REM = 4.75;
const ROW_FULL = 0.6;

function remPx() { return parseFloat(getComputedStyle(root).fontSize) || 16; }

export function attachRowSwipe(list, scroller, ref) {
  if (!list) return { dispose: noop };
  let g = null;              // touch in progress
  let open = null;           // { wrap, offset }
  let frame = 0;
  let swallowUntil = 0;
  const armed = new Set();
  const timers = new Set();

  const later = (fn, ms) => {
    const id = setTimeout(() => { timers.delete(id); fn(); }, ms);
    timers.add(id);
  };

  const rowOf = (target) => {
    const wrap = target instanceof Element ? target.closest('[data-swipe-row]') : null;
    return wrap && list.contains(wrap) ? wrap : null;
  };

  const sideEnabled = (wrap, side) => wrap.getAttribute(`data-swipe-${side}`) !== 'false';

  const buttonsOf = (wrap, side) => Array.from(wrap.querySelectorAll(`:scope > [data-swipe-actions="${side}"] [data-row-action]`));

  // Without rendered buttons yet (they arrive after OnRowSwipeArm) a side counts as two buttons wide.
  const openWidth = (wrap, side) => {
    const count = buttonsOf(wrap, side).length || (sideEnabled(wrap, side) ? 2 : 0);
    return count * ROW_ACTION_REM * remPx();
  };

  const fullAt = (wrap, side) => Math.max(wrap.clientWidth * ROW_FULL, openWidth(wrap, side) + 56);

  const setOffset = (wrap, offset) => {
    wrap.style.setProperty('--row-x', `${offset.toFixed(1)}px`);
    wrap.style.setProperty('--row-w', `${Math.abs(offset).toFixed(1)}px`);
    const side = offset < 0 ? 'end' : offset > 0 ? 'start' : '';
    if (side && wrap.getAttribute('data-swipe') !== side) wrap.setAttribute('data-swipe', side);
  };

  const setFull = (wrap, full) => {
    if (wrap.hasAttribute('data-full') === full) return;
    wrap.toggleAttribute('data-full', full);
    // The primary button glides to (or from) the full width instead of jumping.
    wrap.setAttribute('data-full-anim', '');
    later(() => wrap.removeAttribute('data-full-anim'), 180);
    if (full) safeInvoke(ref, 'OnRowSwipeThreshold');
  };

  // Moves the row to `offset` with a transition of the registered --row-x/--row-w properties (instant without them).
  const snap = (wrap, offset, ms, easing, done) => {
    if (reduceMotion()) ms = 0;
    wrap.style.transition = ms ? `--row-x ${ms}ms ${easing}, --row-w ${ms}ms ${easing}` : '';
    setOffset(wrap, offset);
    later(() => {
      wrap.style.transition = '';
      if (done) done();
    }, ms + 30);
  };

  const close = (wrap, animate = true) => {
    if (!wrap) return;
    if (open && open.wrap === wrap) open = null;
    wrap.removeAttribute('data-full');
    const finish = () => {
      if (open && open.wrap === wrap) return;   // opened again meanwhile
      if (g && g.wrap === wrap) return;
      wrap.removeAttribute('data-swipe');
      wrap.style.removeProperty('--row-x');
      wrap.style.removeProperty('--row-w');
    };
    if (!animate || !wrap.isConnected) { wrap.style.transition = ''; finish(); return; }
    snap(wrap, 0, cssMs('--sm-dur', 220), 'var(--sm-ease-out)', finish);
  };

  const arm = (wrap) => {
    const key = wrap.getAttribute('data-swipe-row');
    if (!key || armed.has(key) || wrap.querySelector(':scope > [data-swipe-actions]')) return;
    armed.add(key);
    safeInvoke(ref, 'OnRowSwipeArm', key);
  };

  const layout = (s) => {
    const wrap = s.wrap;
    let offset = s.base + s.dx;
    if (offset > 0 && !sideEnabled(wrap, 'start')) offset = 0;
    if (offset < 0 && !sideEnabled(wrap, 'end')) offset = 0;
    const width = wrap.clientWidth;
    offset = Math.max(-width, Math.min(width, offset));
    s.offset = offset;
    setOffset(wrap, offset);
    const side = offset < 0 ? 'end' : 'start';
    setFull(wrap, offset !== 0 && Math.abs(offset) >= fullAt(wrap, side));
  };

  const render = () => {
    frame = 0;
    if (g && g.claimed) layout(g);
  };

  const down = (e) => {
    if (!e.isPrimary || (e.pointerType !== 'touch' && e.pointerType !== 'pen')) return;
    const wrap = rowOf(e.target);
    const onAction = !!(e.target instanceof Element && e.target.closest('[data-row-action]'));
    if (onAction) return;
    let reopen = false;
    if (open) {
      if (open.wrap === wrap) {
        reopen = true;          // drag the open row on, or a tap closes it (in up)
      } else {
        close(open.wrap);       // a touch on another row: a tap there only closes the open one
        swallowUntil = Date.now() + 600;
      }
    }
    if (!wrap) return;
    // A row still gliding (closing) continues from where it is.
    const base = wrap.hasAttribute('data-swipe') ? parseFloat(getComputedStyle(wrap).getPropertyValue('--row-x')) || 0 : 0;
    g = { id: e.pointerId, wrap, x0: e.clientX, y0: e.clientY, dx: 0, base, reopen, claimed: false, samples: [] };
  };

  const move = (e) => {
    if (!g || e.pointerId !== g.id) return;
    if (!g.wrap.isConnected) { g = null; return; }
    const dx = e.clientX - g.x0;
    const dy = e.clientY - g.y0;
    if (!g.claimed) {
      if (Math.abs(dx) < ROW_SLOP && Math.abs(dy) < ROW_SLOP) return;
      const side = g.base + dx < 0 ? 'end' : 'start';
      if (Math.abs(dx) <= Math.abs(dy) * 1.5 || (g.base === 0 && !sideEnabled(g.wrap, side))) { g = null; return; }
      // Touch stays implicitly captured by the row (its long-press timer sees the moves and gives up).
      g.claimed = true;
      g.x0 = e.clientX;
      g.wrap.style.transition = '';
      arm(g.wrap);
    }
    g.dx = e.clientX - g.x0;
    const now = performance.now();
    g.samples.push({ x: e.clientX, t: now });
    while (g.samples.length > 2 && now - g.samples[0].t > 120) g.samples.shift();
    if (!frame) frame = requestAnimationFrame(render);
  };

  const up = (e) => {
    if (!g || e.pointerId !== g.id) return;
    const s = g;
    g = null;
    if (!s.claimed) {
      // A tap on the open row closes it instead of opening the chat.
      if (s.reopen && e.type === 'pointerup') {
        close(s.wrap);
        swallowUntil = Date.now() + 400;
      }
      return;
    }
    if (frame) { cancelAnimationFrame(frame); frame = 0; layout(s); }
    swallowUntil = Date.now() + 400;
    const wrap = s.wrap;
    const offset = s.offset ?? s.base;
    if (!wrap.isConnected || offset === 0) { close(wrap, false); return; }
    const side = offset < 0 ? 'end' : 'start';
    const sign = offset < 0 ? -1 : 1;
    const first = s.samples.find((x) => performance.now() - x.t <= 100);
    const last = s.samples[s.samples.length - 1];
    const v = first && last && last.t - first.t >= 8 ? (last.x - first.x) / (last.t - first.t) : 0;
    const buttons = buttonsOf(wrap, side);
    if (e.type === 'pointerup' && wrap.hasAttribute('data-full') && buttons.length) {
      runPrimary(wrap, side, buttons[buttons.length - 1].getAttribute('data-row-action'));
      return;
    }
    const width = openWidth(wrap, side);
    const opening = Math.abs(offset) > width / 2 ? v * sign > -0.3 : v * sign > 0.3 && Math.abs(offset) > 24;
    wrap.removeAttribute('data-full');
    if (e.type === 'pointerup' && opening) {
      open = { wrap };
      snap(wrap, sign * width, cssMs('--sm-dur', 220), 'var(--sm-ease-out)');
    } else {
      close(wrap);
    }
  };

  // Full swipe: archive-like actions take the row away (it leaves the list); others put it back first.
  const runPrimary = (wrap, side, action) => {
    if (open && open.wrap === wrap) open = null;
    const key = wrap.getAttribute('data-swipe-row');
    const leaves = side === 'end';
    if (leaves) {
      snap(wrap, -wrap.clientWidth, cssMs('--sm-dur-fast', 140), 'var(--sm-ease-in)', () => {
        safeInvoke(ref, 'OnRowAction', key, action);
        // Still listed after a while (the action failed): bring the row back.
        later(() => { if (wrap.isConnected && !g) close(wrap); }, 1500);
      });
    } else {
      close(wrap);
      safeInvoke(ref, 'OnRowAction', key, action);
    }
  };

  const click = (e) => {
    if (Date.now() < swallowUntil && !(e.target instanceof Element && e.target.closest('[data-row-action]'))) {
      e.preventDefault();
      e.stopPropagation();
      swallowUntil = 0;
      return;
    }
    // A tapped action closes its row once the press has shown.
    const action = e.target instanceof Element ? e.target.closest('[data-row-action]') : null;
    if (action && open && open.wrap.contains(action)) {
      const wrap = open.wrap;
      later(() => close(wrap), 120);
    }
  };

  const onScroll = () => { if (open && !g) close(open.wrap); };
  const onOutside = (e) => { if (open && !list.contains(e.target)) close(open.wrap); };

  list.addEventListener('pointerdown', down, { passive: true });
  list.addEventListener('pointermove', move, { passive: true });
  list.addEventListener('pointerup', up, { passive: true });
  list.addEventListener('pointercancel', up, { passive: true });
  list.addEventListener('click', click, true);
  if (scroller) scroller.addEventListener('scroll', onScroll, { passive: true });
  document.addEventListener('pointerdown', onOutside, { passive: true, capture: true });
  return {
    /** Closes the open row (e.g. after an action ran from .NET). */
    closeAll() { if (open) close(open.wrap); },
    dispose() {
      list.removeEventListener('pointerdown', down);
      list.removeEventListener('pointermove', move);
      list.removeEventListener('pointerup', up);
      list.removeEventListener('pointercancel', up);
      list.removeEventListener('click', click, true);
      if (scroller) scroller.removeEventListener('scroll', onScroll);
      document.removeEventListener('pointerdown', onOutside, true);
      if (frame) cancelAnimationFrame(frame);
      for (const id of timers) clearTimeout(id);
      timers.clear();
      g = null;
      open = null;
    }
  };
}

// ---- List motion -------------------------------------------------------------------------------------------
// FLIP for keyed children of `container` (attribute `keyAttr`): when Blazor reorders, adds or removes them, moved items
// glide from where they were (transform only) and new ones slide in. Nothing animates on the first render, while the
// list is covered ([inert]) or hidden, under reduced motion, or for big changes (reload, resync, paging); suppress()
// makes the next change appear instantly too (switching to another list). options.enter = false leaves new items to
// their own CSS entrance; options.viewport measures on screen (a bottom-anchored stack such as the toasts) instead
// of inside the container (a scrolled list).

const FLIP_MAX_CHANGES = 3;

export function attachFlipList(container, keyAttr, options) {
  if (!container) return { dispose: noop, suppress: noop };
  const opts = options || {};
  const enter = opts.enter !== false;
  const running = new Map();   // element -> Animation
  let suppressed = false;

  const measure = () => {
    const tops = new Map();
    const order = [];
    for (const el of container.children) {
      const key = el.getAttribute(keyAttr);
      if (!key) continue;
      tops.set(key, opts.viewport ? el.getBoundingClientRect().top : el.offsetTop);
      order.push(key);
    }
    return { tops, order };
  };

  let last = measure();

  // Where an element currently shows, including a FLIP still in flight.
  const shownTop = (el, layoutTop) => {
    const anim = running.get(el);
    if (!anim) return layoutTop;
    const m = /matrix(?:3d)?\(([^)]+)\)/.exec(getComputedStyle(el).transform || '');
    anim.cancel();
    running.delete(el);
    if (!m) return layoutTop;
    const parts = m[1].split(',').map(parseFloat);
    return layoutTop + (parts.length === 16 ? parts[13] : parts[5]);
  };

  const play = (el, keyframes, ms, easing) => {
    const anim = el.animate(keyframes, { duration: ms, easing });
    running.set(el, anim);
    const clear = () => { if (running.get(el) === anim) { running.delete(el); el.style.zIndex = ''; } };
    anim.onfinish = clear;
    anim.oncancel = clear;
  };

  const observer = new MutationObserver(() => {
    const previous = last;
    const next = measure();
    last = next;
    if (suppressed) { suppressed = false; return; }
    if (!container.isConnected || document.hidden || reduceMotion() || typeof container.animate !== 'function') return;
    if (container.closest('[inert]') || container.offsetParent === null) return;

    const before = new Map(previous.order.map((key, i) => [key, i]));
    let added = 0;
    let jumps = 0;
    next.order.forEach((key, i) => {
      if (!before.has(key)) added++;
      else if (Math.abs(before.get(key) - i) > 1) jumps++;
    });
    const removed = previous.order.filter((key) => !next.tops.has(key)).length;
    if (added > FLIP_MAX_CHANGES || removed > FLIP_MAX_CHANGES || jumps > FLIP_MAX_CHANGES) return;

    const dur = cssMs('--sm-dur', 220);
    const slow = cssMs('--sm-dur-slow', 320);
    const ease = 'cubic-bezier(0.2, 0.8, 0.2, 1)';
    for (const el of container.children) {
      const key = el.getAttribute(keyAttr);
      if (!key) continue;
      const top = next.tops.get(key);
      if (!before.has(key)) {
        if (enter) play(el, [{ opacity: 0, transform: 'translateY(-10px) scale(0.98)' }, { opacity: 1, transform: 'none' }], dur, 'cubic-bezier(0.16, 1, 0.3, 1)');
        continue;
      }
      const from = shownTop(el, previous.tops.get(key));
      const dy = from - top;
      if (Math.abs(dy) < 1) continue;
      const jumped = Math.abs(before.get(key) - next.order.indexOf(key)) > 1;
      // The item that jumped travels over the others.
      if (jumped) el.style.zIndex = '2';
      play(el, [{ transform: `translateY(${dy}px)` }, { transform: 'none' }], jumped ? slow : dur, ease);
    }
  });
  observer.observe(container, { childList: true });
  return {
    suppress() { suppressed = true; },
    dispose() {
      observer.disconnect();
      for (const anim of running.values()) { try { anim.cancel(); } catch (e) { /* gone */ } }
      running.clear();
    }
  };
}

// A status strip at the top of the shell slides in (show) or out (hide: it is already out of the flow) while the
// content under it follows instead of jumping by its height. Transform/opacity only; nothing under reduced motion.
export function animateStrip(el, show) {
  if (!el || !el.isConnected || reduceMotion() || typeof el.animate !== 'function') return;
  const height = el.getBoundingClientRect().height;
  const ms = cssMs('--sm-dur', 220);
  const easing = show ? 'cubic-bezier(0.16, 1, 0.3, 1)' : 'cubic-bezier(0.4, 0, 1, 1)';
  const strip = [{ transform: 'translateY(-100%)', opacity: 0 }, { transform: 'none', opacity: 1 }];
  el.animate(show ? strip : strip.slice().reverse(), { duration: ms, easing, fill: show ? 'none' : 'forwards' });
  for (let next = el.nextElementSibling; next; next = next.nextElementSibling) {
    const position = getComputedStyle(next).position;
    if (position === 'fixed' || position === 'absolute') continue;
    next.animate([{ transform: `translateY(${show ? -height : height}px)` }, { transform: 'none' }], { duration: ms, easing: 'cubic-bezier(0.2, 0.8, 0.2, 1)' });
  }
}

// Sets `attr` on `target` while the user scrolls `scroller` down (a floating button steps aside) and removes it when
// they scroll back up or reach the top.
export function attachScrollHide(scroller, target, attr) {
  if (!scroller || !target) return { dispose: noop };
  let last = scroller.scrollTop;
  let travel = 0;
  let frame = 0;
  const set = (hidden) => { if (target.hasAttribute(attr) !== hidden) target.toggleAttribute(attr, hidden); };
  const update = () => {
    frame = 0;
    const y = scroller.scrollTop;
    const delta = y - last;
    last = y;
    if (y < 56) { travel = 0; set(false); return; }
    if (delta === 0) return;
    if ((delta > 0) !== (travel > 0)) travel = 0;
    travel += delta;
    if (travel > 24) set(true);
    else if (travel < -12) set(false);
  };
  const onScroll = () => { if (!frame) frame = requestAnimationFrame(update); };
  scroller.addEventListener('scroll', onScroll, { passive: true });
  return {
    dispose() {
      scroller.removeEventListener('scroll', onScroll);
      if (frame) cancelAnimationFrame(frame);
      target.removeAttribute(attr);
    }
  };
}

// Sets `attr` on `target` while `scroller` is scrolled past `threshold` px and removes it back at the top: a screen
// header turns into a floating glass bar (AppBar, data-scrolled). One attribute flip per crossing, read once a frame.
export function attachScrollState(scroller, target, attr, threshold) {
  if (!scroller || !target) return { dispose: noop };
  const limit = Number.isFinite(threshold) ? threshold : 6;
  let frame = 0;
  const update = () => {
    frame = 0;
    const on = scroller.scrollTop > limit;
    if (target.hasAttribute(attr) !== on) target.toggleAttribute(attr, on);
  };
  const onScroll = () => { if (!frame) frame = requestAnimationFrame(update); };
  scroller.addEventListener('scroll', onScroll, { passive: true });
  update();
  return {
    dispose() {
      scroller.removeEventListener('scroll', onScroll);
      if (frame) cancelAnimationFrame(frame);
      target.removeAttribute(attr);
    }
  };
}

// ---- Viewport ---------------------------------------------------------------------------------------------

function cssInset(name) {
  const value = getComputedStyle(root).getPropertyValue(name).trim();
  const number = parseFloat(value);
  return Number.isFinite(number) ? number : 0;
}

export function getViewport() {
  const coarse = window.matchMedia ? window.matchMedia('(pointer: coarse)').matches : false;
  const keyboard = Math.max(cssInset('--sm-kb-inset'), cssInset('--sm-vv-inset'));
  // The screen's short side does not change with rotation or the keyboard: it tells a phone from a tablet.
  const s = window.screen;
  const screenShortSide = s && s.width > 0 && s.height > 0 ? Math.min(s.width, s.height) : 0;
  return { width: window.innerWidth, height: window.innerHeight, keyboardInset: keyboard, coarsePointer: coarse, screenShortSide };
}

export function observeViewport(ref, method) {
  let frame = 0;
  let last = '';
  const report = () => {
    frame = 0;
    const info = getViewport();
    const signature = `${info.width}|${info.height}|${info.keyboardInset}|${info.coarsePointer}|${info.screenShortSide}`;
    if (signature === last) return;
    last = signature;
    safeInvoke(ref, method, info);
  };
  const schedule = () => { if (!frame) frame = requestAnimationFrame(report); };
  window.addEventListener('resize', schedule);
  if (window.visualViewport) window.visualViewport.addEventListener('resize', schedule);
  // Native hosts change --sm-kb-inset on <html>: watch the style attribute.
  const mo = new MutationObserver(schedule);
  mo.observe(root, { attributes: true, attributeFilter: ['style'] });
  const coarse = window.matchMedia ? window.matchMedia('(pointer: coarse)') : null;
  if (coarse && coarse.addEventListener) coarse.addEventListener('change', schedule);
  schedule();
  return {
    dispose() {
      window.removeEventListener('resize', schedule);
      if (window.visualViewport) window.visualViewport.removeEventListener('resize', schedule);
      mo.disconnect();
      if (coarse && coarse.removeEventListener) coarse.removeEventListener('change', schedule);
      if (frame) cancelAnimationFrame(frame);
    }
  };
}

// ---- Scrolling & layout -------------------------------------------------------------------------------------

function behavior(smooth) {
  if (!smooth) return 'auto';
  return root.getAttribute('data-reduce-motion') === 'true' ? 'auto' : 'smooth';
}

export function scrollToTop(el, smooth) { if (el) el.scrollTo({ top: 0, behavior: behavior(smooth) }); }
export function scrollToBottom(el, smooth) { if (el) el.scrollTo({ top: el.scrollHeight, behavior: behavior(smooth) }); }
export function scrollIntoView(el, block, smooth) { if (el) el.scrollIntoView({ block: block || 'nearest', behavior: behavior(smooth) }); }
export function getScroll(el) { return el ? { top: el.scrollTop, height: el.scrollHeight, clientHeight: el.clientHeight } : null; }

export function elementRect(el) {
  if (!el) return null;
  const r = el.getBoundingClientRect();
  return { x: r.left, y: r.top, width: r.width, height: r.height };
}

// Places a fixed-position element at (x, y), flipped/clamped to stay inside the viewport (8 px margin).
export function positionPopover(el, x, y) {
  if (!el) return;
  const margin = 8;
  el.style.left = '0px';
  el.style.top = '0px';
  const rect = el.getBoundingClientRect();
  const vw = window.innerWidth;
  const vh = window.innerHeight - cssInset('--sm-kb-inset');
  let left = x;
  let top = y;
  if (left + rect.width > vw - margin) left = Math.max(margin, x - rect.width);
  if (top + rect.height > vh - margin) top = Math.max(margin, y - rect.height);
  left = Math.min(Math.max(margin, left), Math.max(margin, vw - rect.width - margin));
  top = Math.min(Math.max(margin, top), Math.max(margin, vh - rect.height - margin));
  el.style.left = `${Math.round(left)}px`;
  el.style.top = `${Math.round(top)}px`;
  el.style.transformOrigin = `${x < left + rect.width / 2 ? 'left' : 'right'} ${y < top + rect.height / 2 ? 'top' : 'bottom'}`;
}

// Grows a textarea with its content up to maxRows lines.
export function autoGrow(textarea, maxRows) {
  if (!textarea) return { dispose: noop, refresh: noop };
  const resize = () => {
    const style = getComputedStyle(textarea);
    const line = parseFloat(style.lineHeight) || 20;
    const padding = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom);
    const border = parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
    const max = line * (maxRows || 6) + padding + border;
    textarea.style.height = 'auto';
    const next = Math.min(textarea.scrollHeight + border, max);
    textarea.style.height = `${next}px`;
    textarea.style.overflowY = textarea.scrollHeight + border > max ? 'auto' : 'hidden';
  };
  textarea.addEventListener('input', resize);
  requestAnimationFrame(resize);
  return {
    refresh: resize,
    dispose() { textarea.removeEventListener('input', resize); }
  };
}

// Drag handle setting a CSS variable on <html> (clamped), remembered in localStorage.
export function initResizer(handle, cssVar, min, max, storageKey) {
  if (!handle) return { dispose: noop };
  const apply = (px) => {
    const clamped = Math.round(Math.min(max, Math.max(min, px)));
    root.style.setProperty(cssVar, `${clamped}px`);
    return clamped;
  };
  const stored = parseFloat(localGet(storageKey) || '');
  if (Number.isFinite(stored)) apply(stored);
  let start = 0, startWidth = 0, dragging = false, id = null;
  const down = (e) => {
    if (e.button !== 0) return;
    dragging = true; id = e.pointerId;
    start = e.clientX;
    startWidth = parseFloat(getComputedStyle(root).getPropertyValue(cssVar)) || min;
    handle.setPointerCapture(id);
    root.classList.add('sm-resizing');
    e.preventDefault();
  };
  const move = (e) => { if (dragging && e.pointerId === id) apply(startWidth + e.clientX - start); };
  const up = (e) => {
    if (!dragging || e.pointerId !== id) return;
    dragging = false;
    root.classList.remove('sm-resizing');
    const width = apply(startWidth + e.clientX - start);
    localSet(storageKey, String(width));
  };
  const dbl = () => { root.style.removeProperty(cssVar); localSet(storageKey, null); };
  handle.addEventListener('pointerdown', down);
  handle.addEventListener('pointermove', move);
  handle.addEventListener('pointerup', up);
  handle.addEventListener('pointercancel', up);
  handle.addEventListener('dblclick', dbl);
  return {
    dispose() {
      handle.removeEventListener('pointerdown', down);
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', up);
      handle.removeEventListener('pointercancel', up);
      handle.removeEventListener('dblclick', dbl);
    }
  };
}

// ---- Per-device UI preferences ------------------------------------------------------------------------------

export function localGet(key) {
  try { return window.localStorage.getItem(key); } catch (e) { return null; }
}

export function localSet(key, value) {
  try {
    if (value === null || value === undefined) window.localStorage.removeItem(key);
    else window.localStorage.setItem(key, value);
  } catch (e) { /* storage full or disabled */ }
}

// ---- Theme ------------------------------------------------------------------------------------------------------

export function applyTheme(state) {
  localSet(THEME_KEY, JSON.stringify(state));
  if (window.__smBoot) return window.__smBoot.applyTheme(state);
  const dark = state.mode === 'dark' || (state.mode !== 'light' && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
  root.setAttribute('data-theme-mode', state.mode);
  root.setAttribute('data-theme', dark ? 'dark' : 'light');
  root.setAttribute('data-reduce-motion', state.reduceMotion ? 'true' : 'false');
  root.setAttribute('data-compact', state.compact ? 'true' : 'false');
  let style = document.getElementById('sm-theme');
  if (!style) { style = document.createElement('style'); style.id = 'sm-theme'; document.head.appendChild(style); }
  style.textContent = state.css || '';
  return dark;
}

export function observeSystemTheme(ref, method) {
  const media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  if (!media) return { dispose: noop };
  const handler = (e) => safeInvoke(ref, method, e.matches);
  if (media.addEventListener) media.addEventListener('change', handler);
  else if (media.addListener) media.addListener(handler);
  return {
    dispose() {
      if (media.removeEventListener) media.removeEventListener('change', handler);
      else if (media.removeListener) media.removeListener(handler);
    }
  };
}

export function setTitle(title) { if (typeof title === 'string') document.title = title; }

export function setLang(lang) {
  if (!lang) return;
  root.lang = lang;
  localSet(LANG_KEY, lang);
}
