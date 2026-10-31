// SkyMessage chat screen helpers (ES module, imported by Features/Chat). Keeps the message list visually stable
// while rows are added, removed or resized, reports what is on screen (read tracking, paging), handles message
// gestures (long-press / right-click menus, swipe to reply, double tap to react), the composer's keys, input and
// paste, file drops, page shortcuts, and a small registry of picked/recorded blobs that .NET streams on demand.

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

function overlaysOpen() {
  const h = window.__smHistory;
  return !!(h && typeof h.depth === 'function' && h.depth() > 0);
}

// ---- Blob registry (pasted / dropped files, recordings) --------------------------------------------------

const blobs = new Map();
let blobSeq = 0;
const BLOB_MAX_AGE_MS = 60 * 60 * 1000;
const BLOB_MAX_BYTES = 768 * 1024 * 1024;

function evictBlobs() {
  const now = Date.now();
  let total = 0;
  for (const [id, entry] of blobs) {
    if (now - entry.at > BLOB_MAX_AGE_MS) blobs.delete(id);
    else total += entry.blob.size;
  }
  if (total <= BLOB_MAX_BYTES) return;
  const ordered = [...blobs.entries()].sort((a, b) => a[1].at - b[1].at);
  for (const [id, entry] of ordered) {
    if (total <= BLOB_MAX_BYTES) break;
    blobs.delete(id);
    total -= entry.blob.size;
  }
}

function stamp() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function defaultName(type) {
  const t = (type || '').toLowerCase();
  const sub = t.includes('/') ? t.split('/')[1].split(/[+;]/)[0] : '';
  if (t.startsWith('image/')) return `image-${stamp()}.${sub === 'jpeg' ? 'jpg' : (sub || 'png')}`;
  if (t.startsWith('video/')) return `video-${stamp()}.${sub || 'mp4'}`;
  if (t.startsWith('audio/')) return `audio-${stamp()}.${sub || 'm4a'}`;
  return `file-${stamp()}`;
}

/** Keeps a Blob/File for later streaming into .NET; returns { ref, name, type, size }. */
export function keepBlob(blob, name, type) {
  const ref = `c:${Date.now().toString(36)}${(++blobSeq).toString(36)}`;
  blobs.set(ref, { blob, at: Date.now() });
  evictBlobs();
  const kind = type || blob.type || 'application/octet-stream';
  return { ref, name: name || defaultName(kind), type: kind, size: blob.size };
}

/** Returned to .NET as an IJSStreamReference. */
export function openBlob(ref) {
  const entry = blobs.get(ref);
  if (!entry) throw new Error('blob not found');
  entry.at = Date.now();
  return entry.blob;
}

export function releaseBlob(ref) { blobs.delete(ref); }

function keepFiles(files) {
  const list = [];
  for (const file of files || []) {
    if (!file) continue;
    list.push(keepBlob(file, file.name || defaultName(file.type), file.type));
  }
  return list;
}

// ---- Message list -------------------------------------------------------------------------------------------

const LONG_PRESS_MS = 430;
const MOVE_TOLERANCE = 10;
// A drag picks its axis after this many px, horizontal when |dx| > AXIS_RATIO·|dy| (docs/MOTION.md).
const AXIS_SLOP = 8;
const AXIS_RATIO = 1.5;
const SWIPE_TRIGGER = 64;
const SWIPE_MAX = 104;
// Past the trigger the bubble still follows the finger, with resistance.
const SWIPE_RESIST = 0.35;
// Double tap: two short taps on the same bubble, close in time and place. Single taps are never delayed.
const TAP_MAX_MS = 300;
const DOUBLE_TAP_GAP_MS = 300;
const DOUBLE_TAP_SLOP = 32;
// A touch right after the list moved stops a fling: it is not a tap on a message.
const SCROLL_CALM_MS = 150;
const HIGHLIGHT_MS = 1400;
// The floating date hides this long after the user stops scrolling.
const FLOAT_IDLE_MS = 1500;
const SPRING = 'cubic-bezier(0.34, 1.56, 0.64, 1)';
// Parts of a bubble with a tap action of their own (media, links, players, buttons, spoilers) are not double-tapped.
const OWN_TAP = 'a, button, input, textarea, select, video, audio, [role="button"], [role="slider"], [data-voice], [data-no-dbltap]';
// Parts that own horizontal drags (the voice scrubber, code blocks scrolling sideways) never start a swipe to reply.
const OWN_DRAG = 'input, textarea, select, [contenteditable], [data-voice-scrub], [data-no-swipe-back]';
// Double-tap burst decoration: dot colours (theme tokens), and the reactions that also throw little hearts.
const DOT_COLORS = ['var(--sm-accent)', 'var(--sm-warm)', 'var(--sm-accent-2)', 'var(--sm-gold)'];
const HEART_EMOJI = new Set(['❤️', '❤', '♥️', '🧡', '💛', '💚', '💙', '💜', '🤍', '🖤', '🤎', '💖', '💗', '💓', '💞', '💕', '😍', '🥰', '😘']);

/**
 * Attaches the scroll keeper to a message list. `scroller` scrolls, `content` holds the rows (direct children).
 * Rows: message rows carry data-msg + data-key (+ data-seq when confirmed, data-reply when they can be replied
 * to, data-react when they take reactions); date chips carry data-date; the unread divider carries data-key. A
 * [data-float-date] child of `scroller` (outside `content`) is the floating date: it shows the topmost visible day
 * while that day's chip is scrolled away and the user scrolls. While the user is at the bottom new content keeps the
 * list at the bottom; otherwise the first visible row stays where it is (prepends, removals, image loads).
 * Gestures (touch and pen): long-press → menu, leftward swipe → reply (rightward pans are left to the shell's swipe
 * back), double tap on a bubble → quick reaction. Reports to .NET: OnViewport, OnMessageMenu(key, x, y, touch),
 * OnSwipeArmed (the swipe crossed its trigger), OnSwipeReply(key), OnDoubleTap(key, x, y).
 */
export function attachList(scroller, content, ref, options) {
  if (!scroller || !content) {
    return { scrollToBottom: noop, scrollToKey: () => false, refresh: noop, setActive: noop, isAtBottom: () => true, burst: noop, dispose: noop };
  }
  const opts = options || {};
  let stick = opts.stick !== false;
  let anchor = null;
  let active = true;
  let reportTimer = 0;
  let lastReport = '';
  let disposed = false;
  // The user's finger/wheel is on the list: never snap to the bottom under it.
  let interacting = false;
  let interactTimer = 0;
  // A smooth scroll to the bottom is running: keep "stick" until it settles.
  let smoothing = false;
  let smoothTimer = 0;
  // When the list last moved (a touch right after that stops a fling, it is not a tap).
  let lastScrollAt = 0;
  let floatTimer = 0;

  const viewTop = () => scroller.getBoundingClientRect().top;
  const fromBottom = () => Math.max(0, scroller.scrollHeight - scroller.clientHeight - scroller.scrollTop);

  // The chat's floating chrome (header stack, composer) covers the scroller's edges; CSS declares how much as the
  // scroller's scroll-padding (MessageList.razor.css). What lies under it is not "on screen" (reads, the floating
  // date, jump targets). Re-read on every restore (the chrome changes size → the content padding changes → restore).
  let padTop = 0;
  let padBottom = 0;
  const readInsets = () => {
    const style = getComputedStyle(scroller);
    padTop = parseFloat(style.scrollPaddingTop) || 0;
    padBottom = parseFloat(style.scrollPaddingBottom) || 0;
  };
  readInsets();

  // The first row whose bottom is below `y`. Rows are all in flow (nothing sticky), so their rects are monotonic.
  function indexBelow(y) {
    const items = content.children;
    let lo = 0;
    let hi = items.length - 1;
    let found = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (items[mid].getBoundingClientRect().bottom > y) { found = mid; hi = mid - 1; } else lo = mid + 1;
    }
    return found;
  }

  const firstVisibleIndex = () => indexBelow(viewTop() + 1);

  const floatRow = scroller.querySelector(':scope > [data-float-date]');
  const floatChip = floatRow ? floatRow.firstElementChild : null;

  // The floating date shows the day of the row under it, but only while that day's own chip is fully scrolled away
  // (so the two never overlap, and a chip arriving at the top hides it instead of sliding under it). It is on screen
  // only while the user scrolls (data-active) and fades out shortly after, so it does not cover messages for good.
  function updateFloat() {
    if (!floatChip) return;
    const items = content.children;
    let i = indexBelow(floatChip.getBoundingClientRect().bottom + 4);
    while (i >= 0 && !items[i].hasAttribute('data-date')) i--;
    const chip = i >= 0 ? items[i] : null;
    const show = !!chip && chip.getBoundingClientRect().bottom <= viewTop() + padTop;
    if (show && floatChip.textContent !== chip.textContent) floatChip.textContent = chip.textContent;
    floatRow.toggleAttribute('data-show', show);
  }

  function wakeFloat() {
    if (!floatRow) return;
    floatRow.setAttribute('data-active', '');
    clearTimeout(floatTimer);
    floatTimer = setTimeout(() => floatRow.removeAttribute('data-active'), FLOAT_IDLE_MS);
  }

  function capture() {
    const items = content.children;
    const first = firstVisibleIndex();
    anchor = null;
    if (first < 0) return;
    for (let i = first; i < items.length && i < first + 12; i++) {
      const el = items[i];
      if (!el.hasAttribute('data-key')) continue;
      anchor = { el, top: el.getBoundingClientRect().top - viewTop() };
      return;
    }
    const el = items[first];
    anchor = { el, top: el.getBoundingClientRect().top - viewTop() };
  }

  // Images that finished loading before this list was attached (cache hits) never fire "load" for us.
  function markLoaded() {
    for (const img of content.querySelectorAll('img[data-fade]:not([data-loaded])')) {
      if (img.complete && img.naturalWidth > 0) img.setAttribute('data-loaded', '');
    }
  }

  function restore() {
    if (disposed) return;
    readInsets();
    markLoaded();
    if (stick) {
      if (!interacting && fromBottom() > 1) scroller.scrollTop = scroller.scrollHeight;
    } else if (anchor && anchor.el.isConnected) {
      const now = anchor.el.getBoundingClientRect().top - viewTop();
      const delta = now - anchor.top;
      if (Math.abs(delta) > 0.5) scroller.scrollTop += delta;
    }
    capture();
    updateFloat();
    scheduleReport();
  }

  function computeReport() {
    const clientHeight = scroller.clientHeight;
    // The visible band between the floating chrome.
    const top = viewTop() + padTop;
    const bottom = viewTop() + clientHeight - padBottom;
    const items = content.children;
    let maxSeq = 0;
    let minSeq = 0;
    const first = indexBelow(top + 1);
    if (first >= 0) {
      for (let i = first; i < items.length; i++) {
        const el = items[i];
        const r = el.getBoundingClientRect();
        if (r.top >= bottom) break;
        const seq = Number(el.getAttribute('data-seq')) || 0;
        if (seq <= 0) continue;
        const visible = Math.min(r.bottom, bottom) - Math.max(r.top, top);
        if (visible < Math.min(r.height * 0.6, 40)) continue;
        if (seq > maxSeq) maxSeq = seq;
        if (!minSeq || seq < minSeq) minSeq = seq;
      }
    }
    const below = fromBottom();
    return {
      atBottom: below < 32,
      nearTop: scroller.scrollTop < Math.max(400, clientHeight * 1.2),
      nearBottom: below < Math.max(400, clientHeight * 1.2),
      fromBottom: Math.round(below),
      maxSeq,
      minSeq,
    };
  }

  function sendReport(force) {
    if (disposed || !active) return;
    const report = computeReport();
    const signature = `${report.atBottom}|${report.nearTop}|${report.nearBottom}|${report.maxSeq}|${report.minSeq}`;
    if (!force && signature === lastReport) return;
    lastReport = signature;
    safeInvoke(ref, 'OnViewport', report);
  }

  function scheduleReport() {
    if (reportTimer) return;
    reportTimer = setTimeout(() => { reportTimer = 0; sendReport(false); }, 120);
  }

  const markInteract = () => {
    interacting = true;
    clearTimeout(interactTimer);
    interactTimer = setTimeout(() => {
      interacting = false;
      if (stick) restore();
    }, 260);
  };

  const onScroll = () => {
    lastScrollAt = performance.now();
    // Only the user's own scrolling (finger, wheel, scrollbar, keys) shows the floating date, never the anchoring
    // above or the browser clamping the position after a resize.
    if (interacting) {
      markInteract();
      wakeFloat();
    }
    if (!smoothing) stick = fromBottom() < 32;
    capture();
    updateFloat();
    scheduleReport();
  };
  // A scrollbar drag or scrolling keys are the user's too.
  const onScrollerPointer = (e) => { if (e.pointerType === 'mouse' && e.target === scroller) markInteract(); };
  const onScrollerKey = (e) => { if (/^(Page(Up|Down)|Arrow(Up|Down)|Home|End| )$/.test(e.key) && !e.defaultPrevented) markInteract(); };

  const mutations = new MutationObserver(restore);
  mutations.observe(content, { childList: true, subtree: true, characterData: true });
  const resizes = 'ResizeObserver' in window ? new ResizeObserver(restore) : null;
  if (resizes) {
    // Border box: the content's padding is the room left for the floating chrome (attachChrome), so a composer that
    // grows (emoji panel, reply bar, more lines) must keep the list at the bottom like any other resize.
    resizes.observe(content, { box: 'border-box' });
    resizes.observe(scroller);
  }
  scroller.addEventListener('scroll', onScroll, { passive: true });
  scroller.addEventListener('wheel', markInteract, { passive: true });
  scroller.addEventListener('touchstart', markInteract, { passive: true });
  scroller.addEventListener('touchmove', markInteract, { passive: true });
  scroller.addEventListener('pointerdown', onScrollerPointer, { passive: true });
  scroller.addEventListener('keydown', onScrollerKey, { passive: true });

  // ---- Gestures: long-press / right-click menus, swipe to reply, double tap to react ----

  let press = null;
  let firedAt = 0;
  let swipe = null;
  // The touch in progress as a possible tap, and the last tap waiting for a second one.
  let touch = null;
  let lastTap = null;

  const rowOf = (target) => {
    if (!target || !target.closest) return null;
    const row = target.closest('[data-msg]');
    return row && content.contains(row) ? row : null;
  };
  const rowByKey = (key) => {
    for (const row of content.children) {
      if (row.getAttribute('data-key') === key) return row;
    }
    return null;
  };
  const selecting = () => !!scroller.closest('[data-selecting]');

  const cancelPress = () => {
    if (press) {
      clearTimeout(press.timer);
      press = null;
    }
  };

  // A double tap counts on the bubble itself, outside anything with a tap action of its own.
  const tappable = (row, target) =>
    row.hasAttribute('data-react') && !selecting() && !!target.closest('[data-swipe]') && !target.closest(OWN_TAP);

  const onPointerDown = (e) => {
    if (e.pointerType === 'mouse' || !e.isPrimary) return;
    const row = rowOf(e.target);
    if (!row) {
      lastTap = null;
      return;
    }
    cancelPress();
    const x = e.clientX;
    const y = e.clientY;
    const now = performance.now();
    press = {
      x, y, row,
      timer: setTimeout(() => {
        if (!press) return;
        firedAt = Date.now();
        const key = press.row.getAttribute('data-key');
        press = null;
        touch = null;
        lastTap = null;
        if (swipe && swipe.decided) return;
        swipe = null;
        safeInvoke(ref, 'OnMessageMenu', key, x, y, true);
      }, LONG_PRESS_MS)
    };
    touch = { id: e.pointerId, x, y, at: now, row, ok: now - lastScrollAt > SCROLL_CALM_MS && tappable(row, e.target) };
    if (opts.swipe !== false && !selecting() && row.hasAttribute('data-reply') && !e.target.closest(OWN_DRAG)) {
      const target = row.querySelector('[data-swipe]') || row;
      // The sender's avatar (groups) travels with the bubble.
      const moving = [target, ...row.querySelectorAll('[data-swipe-also]')];
      swipe = { row, target, moving, hint: row.querySelector('[data-reply-hint]'), id: e.pointerId, x0: x, y0: y, dx: 0, decided: false, armed: false };
    } else {
      swipe = null;
    }
  };

  // The reply arrow behind the bubble grows with the pull (0..1).
  const paintHint = (s, progress) => {
    if (!s.hint) return;
    s.hint.style.opacity = String(progress);
    s.hint.style.transform = `scale(${0.4 + progress * 0.6})`;
  };

  const onPointerMove = (e) => {
    const far = (p) => Math.abs(e.clientX - p.x) > MOVE_TOLERANCE || Math.abs(e.clientY - p.y) > MOVE_TOLERANCE;
    if (press && far(press)) cancelPress();
    if (touch && e.pointerId === touch.id && far(touch)) touch = null;
    if (!swipe || e.pointerId !== swipe.id) return;
    const dx = e.clientX - swipe.x0;
    const dy = e.clientY - swipe.y0;
    if (!swipe.decided) {
      if (Math.abs(dx) < AXIS_SLOP && Math.abs(dy) < AXIS_SLOP) return;
      // Leftward only: a rightward pan belongs to the shell's swipe back, a vertical one to scrolling.
      if (dx >= 0 || Math.abs(dx) <= Math.abs(dy) * AXIS_RATIO) {
        swipe = null;
        return;
      }
      swipe.decided = true;
      // The bubble starts from where the axis was decided, so it does not jump by the slop.
      swipe.x0 = e.clientX;
      cancelPress();
      touch = null;
      lastTap = null;
      try { swipe.target.setPointerCapture(e.pointerId); } catch (err) { /* already released */ }
      for (const el of swipe.moving) {
        el.style.transition = 'none';
        el.style.willChange = 'transform';
      }
      if (swipe.hint) swipe.hint.style.transition = 'none';
    }
    const raw = Math.min(0, e.clientX - swipe.x0);
    const pull = Math.max(-SWIPE_MAX, raw > -SWIPE_TRIGGER ? raw : -SWIPE_TRIGGER + (raw + SWIPE_TRIGGER) * SWIPE_RESIST);
    swipe.dx = pull;
    for (const el of swipe.moving) el.style.transform = `translateX(${pull}px)`;
    paintHint(swipe, Math.min(1, -pull / SWIPE_TRIGGER));
    const armed = -pull >= SWIPE_TRIGGER - 0.5;
    if (armed !== swipe.armed) {
      swipe.armed = armed;
      swipe.row.toggleAttribute('data-armed', armed);
      if (armed) safeInvoke(ref, 'OnSwipeArmed');
    }
  };

  // Springs the bubble back (a little overshoot) and fades the arrow; reduced motion jumps straight back.
  const endSwipe = (s, trigger) => {
    const calm = reduceMotion();
    for (const el of s.moving) {
      el.style.transition = calm ? 'none' : `transform 260ms ${SPRING}`;
      el.style.transform = '';
    }
    if (s.hint) {
      s.hint.style.transition = calm ? 'none' : 'opacity 140ms ease, transform 140ms ease';
      s.hint.style.opacity = '';
      s.hint.style.transform = '';
    }
    s.row.removeAttribute('data-armed');
    setTimeout(() => {
      for (const el of s.moving) {
        el.style.transition = '';
        el.style.willChange = '';
      }
      if (s.hint) s.hint.style.transition = '';
    }, 300);
    if (trigger) {
      firedAt = Date.now();
      safeInvoke(ref, 'OnSwipeReply', s.row.getAttribute('data-key'));
    }
  };

  const onTap = (t, e) => {
    const now = performance.now();
    if (!t.ok || now - t.at > TAP_MAX_MS) {
      lastTap = null;
      return;
    }
    const prev = lastTap;
    if (prev && prev.row === t.row && t.at - prev.at < DOUBLE_TAP_GAP_MS
      && Math.abs(e.clientX - prev.x) < DOUBLE_TAP_SLOP && Math.abs(e.clientY - prev.y) < DOUBLE_TAP_SLOP) {
      lastTap = null;
      // The second tap's click must not also act on the row.
      firedAt = Date.now();
      safeInvoke(ref, 'OnDoubleTap', t.row.getAttribute('data-key'), e.clientX, e.clientY);
      return;
    }
    lastTap = { row: t.row, at: now, x: e.clientX, y: e.clientY };
  };

  const onPointerUp = (e) => {
    cancelPress();
    if (touch && e.pointerId === touch.id) {
      const t = touch;
      touch = null;
      if (e.type === 'pointerup') onTap(t, e);
      else lastTap = null;
    }
    if (!swipe || e.pointerId !== swipe.id) return;
    const s = swipe;
    swipe = null;
    if (s.decided) endSwipe(s, e.type === 'pointerup' && s.armed);
  };

  const onContextMenu = (e) => {
    const row = rowOf(e.target);
    if (!row) return;
    // Keep the native menu for a text selection made inside the message (copy part of it).
    const selection = window.getSelection ? window.getSelection() : null;
    if (selection && !selection.isCollapsed && selection.toString().trim() && row.contains(selection.anchorNode)) return;
    e.preventDefault();
    if (firedAt && Date.now() - firedAt < 900) return;
    cancelPress();
    touch = null;
    lastTap = null;
    firedAt = Date.now();
    safeInvoke(ref, 'OnMessageMenu', row.getAttribute('data-key'), e.clientX, e.clientY, !!e.pointerType && e.pointerType !== 'mouse');
  };

  // The emoji of a double-tap reaction pops at the finger and flies into its chip (or fades when taken back).
  const burstTarget = (row, emoji, x, y) => {
    if (row) {
      for (const face of row.querySelectorAll('.reaction__emoji')) {
        if (face.textContent !== emoji) continue;
        const r = face.getBoundingClientRect();
        if (r.width > 0) return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
      }
      const bubble = row.querySelector('[data-swipe]');
      if (bubble) {
        const r = bubble.getBoundingClientRect();
        return { x: r.left + 24, y: r.bottom - 14 };
      }
    }
    return { x, y: y + 40 };
  };

  // Decoration around a double-tap reaction: a handful of small round dots in the theme's colours (and little copies
  // of the emoji for a heart reaction) scatter from the finger and fade. Fixed-position spans animated with Web
  // Animations (transform/opacity only), removed when done; fewer of them on slow devices (data-fx="lite").
  function particles(emoji, x, y) {
    const lite = document.documentElement.getAttribute('data-fx') === 'lite';
    const hearts = HEART_EMOJI.has(emoji);
    const count = lite ? 5 : 8;
    for (let i = 0; i < count; i++) {
      const angle = (Math.PI * 2 * i) / count + (Math.random() - 0.5) * 0.6 - Math.PI / 2;
      const dist = 40 + Math.random() * 26;
      const dx = Math.cos(angle) * dist;
      const dy = Math.sin(angle) * dist;
      const heart = hearts && i % 2 === 0;
      const size = heart ? 15 : 5 + Math.random() * 4;
      const p = document.createElement('span');
      p.setAttribute('aria-hidden', 'true');
      p.style.cssText = `position:fixed;left:${x - size / 2}px;top:${y - size / 2}px;width:${size}px;height:${size}px;`
        + 'pointer-events:none;z-index:2147482999;will-change:transform,opacity;opacity:0;'
        + (heart
          ? `display:flex;align-items:center;justify-content:center;font-size:${size - 2}px;line-height:1;`
          : `border-radius:50%;background:${DOT_COLORS[i % DOT_COLORS.length]};`);
      if (heart) {
        p.className = 'sm-emoji';
        p.textContent = emoji;
      }
      document.body.appendChild(p);
      setTimeout(() => p.remove(), 900);
      p.animate([
        { transform: 'translate(0, 0) scale(0.4)', opacity: 0 },
        { transform: `translate(${(dx * 0.6).toFixed(1)}px, ${(dy * 0.6).toFixed(1)}px) scale(1)`, opacity: 1, offset: 0.35 },
        { transform: `translate(${dx.toFixed(1)}px, ${dy.toFixed(1)}px) scale(0.6)`, opacity: 0 },
      ], { duration: 300 + Math.round(Math.random() * 60), easing: 'cubic-bezier(0.16, 1, 0.3, 1)' }).onfinish = () => p.remove();
    }
  }

  function burst(key, emoji, x, y, adding) {
    if (disposed || !emoji || reduceMotion() || typeof document.body.animate !== 'function') return;
    if (adding) particles(emoji, x, y);
    const size = adding ? 52 : 40;
    const el = document.createElement('span');
    el.className = 'sm-emoji';
    el.textContent = emoji;
    el.setAttribute('aria-hidden', 'true');
    el.style.cssText = `position:fixed;left:${x - size / 2}px;top:${y - size / 2}px;width:${size}px;height:${size}px;`
      + `display:flex;align-items:center;justify-content:center;font-size:${Math.round(size * 0.78)}px;line-height:1;`
      + 'pointer-events:none;z-index:2147483000;will-change:transform,opacity';
    document.body.appendChild(el);
    let gone = false;
    const remove = () => {
      if (gone) return;
      gone = true;
      el.remove();
    };
    setTimeout(remove, 800);
    if (!adding) {
      el.animate([
        { transform: 'scale(0.6)', opacity: 0 },
        { transform: 'scale(1.1)', opacity: 1, offset: 0.4 },
        { transform: 'scale(0.5)', opacity: 0 },
      ], { duration: 300, easing: 'ease-out' }).onfinish = remove;
      return;
    }
    el.animate([
      { transform: 'scale(0.3)', opacity: 0 },
      { transform: 'scale(1.18)', opacity: 1, offset: 0.65 },
      { transform: 'scale(1)', opacity: 1 },
    ], { duration: 150, easing: 'cubic-bezier(0.16, 1, 0.3, 1)', fill: 'forwards' }).onfinish = () => {
      // By now the chip has rendered (the reaction is applied optimistically): fly into it.
      const to = burstTarget(rowByKey(key), emoji, x, y);
      el.animate([
        { transform: 'translate(0, 0) scale(1)', opacity: 1 },
        { transform: `translate(${to.x - x}px, ${to.y - y}px) scale(0.38)`, opacity: 0.3 },
      ], { duration: 170, easing: 'cubic-bezier(0.4, 0, 1, 1)', fill: 'forwards' }).onfinish = remove;
    };
  }

  // The click that ends a long-press or a swipe must not also activate the row.
  const onClickCapture = (e) => {
    if (firedAt && Date.now() - firedAt < 700) {
      e.preventDefault();
      e.stopPropagation();
      firedAt = 0;
    }
  };

  content.addEventListener('pointerdown', onPointerDown, { passive: true });
  content.addEventListener('pointermove', onPointerMove, { passive: true });
  content.addEventListener('pointerup', onPointerUp, { passive: true });
  content.addEventListener('pointercancel', onPointerUp, { passive: true });
  content.addEventListener('contextmenu', onContextMenu);
  content.addEventListener('click', onClickCapture, true);
  scroller.addEventListener('scroll', cancelPress, { passive: true });

  // Images fade in once loaded (load does not bubble, but the capture phase reaches the list).
  const onLoad = (e) => {
    const el = e.target;
    if (!el || el.tagName !== 'IMG') return;
    if (el.hasAttribute('data-fade')) el.setAttribute('data-loaded', '');
    // A loaded image may change its row's height; ResizeObserver covers it too, this keeps older engines right.
    restore();
  };
  content.addEventListener('load', onLoad, true);

  // A row that animated in (a new message) was off by its transform while measured: report it again once settled.
  const onAnimationEnd = (e) => {
    if (e.target && e.target.parentElement === content) scheduleReport();
  };
  content.addEventListener('animationend', onAnimationEnd);

  restore();
  requestAnimationFrame(() => restore());

  return {
    scrollToBottom(smooth) {
      stick = true;
      interacting = false;
      clearTimeout(interactTimer);
      const distance = fromBottom();
      if (smooth && distance < scroller.clientHeight * 3 && !reduceMotion()) {
        smoothing = true;
        clearTimeout(smoothTimer);
        smoothTimer = setTimeout(() => {
          smoothing = false;
          stick = true;
          restore();
        }, 600);
        scroller.scrollTo({ top: scroller.scrollHeight, behavior: 'smooth' });
      } else {
        scroller.scrollTop = scroller.scrollHeight;
      }
      capture();
      sendReport(true);
    },
    scrollToKey(key, block, highlight) {
      const el = rowByKey(key);
      if (!el) return false;
      readInsets();
      const top = viewTop();
      const r = el.getBoundingClientRect();
      // Measured from the bottom of the floating header stack, centred in the band the chrome leaves free.
      const band = scroller.clientHeight - padTop - padBottom;
      const offset = padTop + (block === 'start' ? 56 : Math.max(56, (band - r.height) / 2));
      scroller.scrollTop += (r.top - top) - offset;
      stick = fromBottom() < 32;
      capture();
      if (highlight) {
        el.removeAttribute('data-hl');
        void el.offsetWidth;
        el.setAttribute('data-hl', '');
        setTimeout(() => el.removeAttribute('data-hl'), HIGHLIGHT_MS);
        // Landed somewhere else in the history: say which day it is.
        updateFloat();
        wakeFloat();
      }
      sendReport(true);
      return true;
    },
    refresh() { restore(); sendReport(true); },
    setActive(value) {
      active = !!value;
      if (active) sendReport(true);
    },
    isAtBottom() { return fromBottom() < 32; },
    burst,
    dispose() {
      disposed = true;
      cancelPress();
      clearTimeout(reportTimer);
      clearTimeout(interactTimer);
      clearTimeout(smoothTimer);
      clearTimeout(floatTimer);
      mutations.disconnect();
      if (resizes) resizes.disconnect();
      scroller.removeEventListener('scroll', onScroll);
      scroller.removeEventListener('scroll', cancelPress);
      scroller.removeEventListener('wheel', markInteract);
      scroller.removeEventListener('touchstart', markInteract);
      scroller.removeEventListener('touchmove', markInteract);
      scroller.removeEventListener('pointerdown', onScrollerPointer);
      scroller.removeEventListener('keydown', onScrollerKey);
      content.removeEventListener('pointerdown', onPointerDown);
      content.removeEventListener('pointermove', onPointerMove);
      content.removeEventListener('pointerup', onPointerUp);
      content.removeEventListener('pointercancel', onPointerUp);
      content.removeEventListener('contextmenu', onContextMenu);
      content.removeEventListener('click', onClickCapture, true);
      content.removeEventListener('load', onLoad, true);
      content.removeEventListener('animationend', onAnimationEnd);
    }
  };
}

// ---- Composer ---------------------------------------------------------------------------------------------------

/**
 * Text copied from Office (Excel cells, Word) and similar rich-text sources comes with a picture of itself in
 * clipboardData.files: that paste means the text. A copied picture or file is still attached: it has no rich text
 * (a file manager may add the file name as plain text), or only its address as plain text (browser "Copy image").
 */
function isRichText(data) {
  const types = Array.from(data.types || []);
  if (types.indexOf('text/html') < 0 && types.indexOf('text/rtf') < 0) return false;
  let text = '';
  try { text = (data.getData('text/plain') || '').trim(); } catch (e) { /* no plain text flavour */ }
  return text.length > 0 && !/^(https?|data|blob|file):\S*$/i.test(text);
}

/**
 * Binds the composer textarea. .NET owns the text model but never binds `value`: it receives OnInput(value, caret,
 * version) and writes back only through setText/insert. The field owns the value: every setText carries a version,
 * and input reported before that write reached the field (the WebView round trip) has an older version, which .NET
 * ignores; characters typed during that time stay after the written text and are reported again.
 * Keys: Enter sends per settings (Ctrl/Cmd+Enter always), Up in an empty field edits the last message, Escape
 * cancels reply/edit, arrows/Tab/Enter drive mention suggestions.
 */
export function attachComposer(textarea, ref, options) {
  if (!textarea) return { setText: noop, insert: noop, focus: noop, blur: noop, configure: noop, dispose: noop };
  let sendByEnter = !!(options && options.sendByEnter);
  let hasContext = false;
  let suggestOpen = false;
  let version = (options && options.version) || 0;
  const maxRows = (options && options.maxRows) || 8;

  const resize = () => {
    const style = getComputedStyle(textarea);
    const line = parseFloat(style.lineHeight) || 20;
    const padding = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom);
    const border = parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
    const max = line * maxRows + padding + border;
    textarea.style.height = 'auto';
    const next = Math.min(textarea.scrollHeight + border, max);
    textarea.style.height = `${next}px`;
    textarea.style.overflowY = textarea.scrollHeight + border > max ? 'auto' : 'hidden';
  };

  const report = () => safeInvoke(ref, 'OnInput', textarea.value, textarea.selectionStart == null ? textarea.value.length : textarea.selectionStart, version);

  const onInput = () => { resize(); report(); };

  const onKeyDown = (e) => {
    if (e.isComposing || e.keyCode === 229) return;
    if (suggestOpen && (e.key === 'ArrowUp' || e.key === 'ArrowDown' || e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey))) {
      e.preventDefault();
      safeInvoke(ref, 'OnSuggestKey', e.key);
      return;
    }
    if (e.key === 'Enter') {
      const force = e.ctrlKey || e.metaKey;
      if (force || (sendByEnter && !e.shiftKey && !e.altKey)) {
        e.preventDefault();
        // A held Enter sends once (its repeats would save an edit and then send the draft that comes back).
        if (!e.repeat) safeInvoke(ref, 'OnSendKey');
      }
      return;
    }
    if (e.key === 'ArrowUp' && !textarea.value && !e.shiftKey && !e.altKey && !e.ctrlKey && !e.metaKey) {
      e.preventDefault();
      safeInvoke(ref, 'OnEditLastKey');
      return;
    }
    if (e.key === 'Escape' && (hasContext || suggestOpen) && !overlaysOpen()) {
      e.preventDefault();
      e.stopPropagation();
      safeInvoke(ref, 'OnEscapeKey');
    }
  };

  const onPaste = (e) => {
    const data = e.clipboardData;
    if (!data) return;
    const files = Array.from(data.files || []);
    if (!files.length || isRichText(data)) return;
    e.preventDefault();
    safeInvoke(ref, 'OnFiles', keepFiles(files));
  };

  // Caret moves without input (clicks, arrows) matter for the mention suggestions.
  const onCaret = () => { if (suggestOpen) report(); };

  const onFocus = () => safeInvoke(ref, 'OnFocus');

  textarea.addEventListener('input', onInput);
  textarea.addEventListener('keydown', onKeyDown);
  textarea.addEventListener('paste', onPaste);
  textarea.addEventListener('click', onCaret);
  textarea.addEventListener('keyup', onCaret);
  textarea.addEventListener('focus', onFocus);
  requestAnimationFrame(resize);

  return {
    /**
     * Writes `text` as version `v`. `known` is the value .NET had when it decided on `text`: when the field has grown
     * past it since (typing right after Send, ✓ or a mention pick), the new characters are kept after `text` and the
     * result is reported back.
     */
    setText(text, caret, v, known) {
      if (typeof v === 'number') version = v;
      let next = text || '';
      let c = caret;
      const now = textarea.value;
      const typed = typeof known === 'string' && now.length > known.length && now.startsWith(known) ? now.slice(known.length) : '';
      if (typed) {
        next += typed;
        c = next.length;
      }
      textarea.value = next;
      c = c == null || c < 0 ? next.length : Math.min(c, next.length);
      try { textarea.setSelectionRange(c, c); } catch (e) { /* not focusable yet */ }
      resize();
      if (typed) report();
    },
    insert(text) {
      const start = textarea.selectionStart == null ? textarea.value.length : textarea.selectionStart;
      const end = textarea.selectionEnd == null ? start : textarea.selectionEnd;
      if (typeof textarea.setRangeText === 'function') textarea.setRangeText(text, start, end, 'end');
      else {
        textarea.value = textarea.value.slice(0, start) + text + textarea.value.slice(end);
        const c = start + text.length;
        textarea.setSelectionRange(c, c);
      }
      resize();
      report();
    },
    focus(moveToEnd) {
      textarea.focus({ preventScroll: true });
      if (moveToEnd) {
        const c = textarea.value.length;
        try { textarea.setSelectionRange(c, c); } catch (e) { /* ignore */ }
      }
    },
    blur() { textarea.blur(); },
    configure(o) {
      if (!o) return;
      if (o.sendByEnter !== undefined) sendByEnter = !!o.sendByEnter;
      if (o.hasContext !== undefined) hasContext = !!o.hasContext;
      if (o.suggestOpen !== undefined) suggestOpen = !!o.suggestOpen;
    },
    dispose() {
      textarea.removeEventListener('input', onInput);
      textarea.removeEventListener('keydown', onKeyDown);
      textarea.removeEventListener('paste', onPaste);
      textarea.removeEventListener('click', onCaret);
      textarea.removeEventListener('keyup', onCaret);
      textarea.removeEventListener('focus', onFocus);
    }
  };
}

// ---- Floating chrome ------------------------------------------------------------------------------------------------

/**
 * The chat screen's header stack ([data-chat-top]: header capsule, call banner, pinned strip) and composer
 * ([data-chat-bottom]) float over the message list. Their sizes are published on `root` as --chat-stack-h (the
 * stack's height; CSS adds its offset and the status-bar inset into --chat-top-h, so inset changes need no JS) and
 * --chat-bottom-h (the composer's height, 0 while it is hidden): the list pads its content by them and attachList knows
 * what is really on screen. Layout boxes only (offset*), so the chrome's own enter animations (transforms) never feed
 * back into the list.
 */
export function attachChrome(root) {
  if (!root) return { dispose: noop };
  const top = root.querySelector(':scope > [data-chat-top]');
  const bottom = root.querySelector(':scope > [data-chat-bottom]');
  let last = '';
  const measure = () => {
    const t = top ? top.offsetHeight : 0;
    const b = bottom && bottom.offsetParent ? bottom.offsetHeight : 0;
    const key = `${t}|${b}`;
    if (key === last) return;
    last = key;
    root.style.setProperty('--chat-stack-h', `${t}px`);
    root.style.setProperty('--chat-bottom-h', `${b}px`);
  };
  const observer = 'ResizeObserver' in window ? new ResizeObserver(measure) : null;
  if (observer) {
    observer.observe(root);
    if (top) observer.observe(top);
    if (bottom) observer.observe(bottom);
  } else {
    window.addEventListener('resize', measure);
  }
  measure();
  return {
    dispose() {
      if (observer) observer.disconnect();
      else window.removeEventListener('resize', measure);
    }
  };
}

// ---- Drop zone and page shortcuts -----------------------------------------------------------------------------------

/** Files dragged over `root` show its drop hint (data-drop attribute) and are reported with OnFiles. */
export function attachDropZone(root, ref) {
  if (!root) return { setEnabled: noop, dispose: noop };
  let depth = 0;
  let enabled = true;
  const hasFiles = (e) => enabled && e.dataTransfer && Array.from(e.dataTransfer.types || []).indexOf('Files') >= 0;
  const clear = () => { depth = 0; root.removeAttribute('data-drop'); };
  const onEnter = (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    depth++;
    root.setAttribute('data-drop', '');
  };
  const onOver = (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  };
  const onLeave = (e) => {
    if (!hasFiles(e)) return;
    depth = Math.max(0, depth - 1);
    if (!depth) root.removeAttribute('data-drop');
  };
  const onDrop = (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    clear();
    const files = Array.from(e.dataTransfer.files || []);
    if (files.length) safeInvoke(ref, 'OnFiles', keepFiles(files));
  };
  root.addEventListener('dragenter', onEnter);
  root.addEventListener('dragover', onOver);
  root.addEventListener('dragleave', onLeave);
  root.addEventListener('drop', onDrop);
  return {
    setEnabled(value) { enabled = !!value; if (!enabled) clear(); },
    dispose() {
      root.removeEventListener('dragenter', onEnter);
      root.removeEventListener('dragover', onOver);
      root.removeEventListener('dragleave', onLeave);
      root.removeEventListener('drop', onDrop);
    }
  };
}

function isEditable(el) {
  if (!el) return false;
  const tag = el.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
}

/**
 * Chat-wide keys while the chat is on screen: Ctrl/Cmd+F opens the in-chat search (OnFindKey); typing a character
 * while nothing editable has focus moves focus into the composer.
 */
export function attachShortcuts(root, ref) {
  if (!root) return { setActive: noop, dispose: noop };
  let active = true;
  const onKey = (e) => {
    if (!active || e.defaultPrevented || e.isComposing) return;
    if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.code === 'KeyF' || e.key === 'f' || e.key === 'F' || e.key === 'а' || e.key === 'А')) {
      if (document.querySelector('.sm-overlay, .sm-menu-layer, [data-viewer-open]')) return;
      e.preventDefault();
      safeInvoke(ref, 'OnFindKey');
      return;
    }
    // Space keeps scrolling the page; other printable keys start typing a message.
    if (e.ctrlKey || e.metaKey || e.altKey || !e.key || e.key.length !== 1 || e.key === ' ' || overlaysOpen()) return;
    const focused = document.activeElement;
    if (focused && focused !== document.body && (isEditable(focused) || !root.contains(focused))) return;
    const input = root.querySelector('[data-composer-input]');
    if (input && !input.disabled) input.focus({ preventScroll: true });
  };
  window.addEventListener('keydown', onKey);
  return {
    setActive(value) { active = !!value; },
    dispose() { window.removeEventListener('keydown', onKey); }
  };
}
