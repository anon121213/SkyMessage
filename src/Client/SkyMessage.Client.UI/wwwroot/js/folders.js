// Chat folders (ES module, imported by FolderTabs, ChatListPanel and FoldersSection). See docs/MOTION.md §2.
// • attachFolderTabs(track): the gradient pill sits under the active tab ([data-active]) and glides when it changes; the
//   strip scrolls the active tab into view. While a swipe is in progress the pill moves part of the way (preview).
// • attachFolderSwipe(pane, scroller, head, track, ref): a sideways drag on the chat list follows the finger and, past a
//   third of the width or with a flick, slides the list out and asks .NET for the next/previous folder
//   (ref.OnFolderSwipe(index)); .NET renders it and calls enter(dir), which slides the new list in from that side.
//   Only while the pane carries [data-folder-swipe] (the "Swipe on a chat" setting); the axis is decided after 8 px
//   (|dx| > 1.5·|dy|), so vertical scrolling stays native. Touch and pen only.
// • attachHandleReorder(list, ref): rows [data-reorder-key] (children of list) move up and down by their [data-drag-handle]
//   (any pointer, the handle is the affordance); the others glide aside; the new order goes to ref.OnReorder(keys).
import { reduceMotion } from './app.js';

const DECIDE_PX = 8;
const COMMIT = 0.3;
const FLICK = 0.45;
const RUBBER = 0.25;
const RUBBER_MAX = 56;
const OUT_MS = 150;
const ENTER_MS = 260;
const ENTER_SHIFT = 0.28;

function noop() { }

function call(ref, method, ...args) {
  if (!ref) return Promise.resolve();
  try {
    return ref.invokeMethodAsync(method, ...args).catch(noop);
  } catch (e) {
    return Promise.resolve();
  }
}

const tabControllers = new WeakMap();

function tabsOf(track) {
  return track ? Array.from(track.querySelectorAll('[data-folder-tab]')) : [];
}

function activeIndexOf(tabs) {
  return tabs.findIndex((t) => t.hasAttribute('data-active'));
}

// ---- Tabs ---------------------------------------------------------------------------------------------------

export function attachFolderTabs(track) {
  if (!track) return { dispose: noop, preview: noop, settle: noop };
  let placed = false;
  let lastActive = -1;
  let frame = 0;

  const indicator = () => track.querySelector('[data-folder-indicator]');

  const place = (x, w, animate) => {
    const pill = indicator();
    if (!pill) return;
    pill.style.transition = animate && !reduceMotion() ? '' : 'none';
    pill.style.transform = `translateX(${x.toFixed(1)}px)`;
    pill.style.width = `${w.toFixed(1)}px`;
    pill.style.opacity = w > 0 ? '1' : '0';
  };

  // Keeps the tab inside the visible part of the strip, centred where the strip allows.
  const reveal = (tab, smooth) => {
    const max = track.scrollWidth - track.clientWidth;
    if (max <= 0) return;
    const target = Math.max(0, Math.min(max, tab.offsetLeft - (track.clientWidth - tab.offsetWidth) / 2));
    if (Math.abs(track.scrollLeft - target) < 1) return;
    track.scrollTo({ left: target, behavior: smooth && !reduceMotion() ? 'smooth' : 'auto' });
  };

  // While a swipe moves the pill between two tabs it turns into a soft tint under dark labels (CSS: [data-previewing]),
  // so no label is ever white on the light strip; the gradient comes back once the pill settles under a tab.
  const previewing = (on) => track.toggleAttribute('data-previewing', on);

  const update = (animate) => {
    frame = 0;
    previewing(false);
    const tabs = tabsOf(track);
    const index = activeIndexOf(tabs);
    if (index < 0) { place(0, 0, false); return; }
    const tab = tabs[index];
    place(tab.offsetLeft, tab.offsetWidth, animate && placed);
    placed = true;
    if (index !== lastActive) {
      reveal(tab, lastActive >= 0);
      lastActive = index;
    }
  };

  const schedule = (animate) => {
    if (frame) cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => update(animate));
  };

  const mutations = new MutationObserver(() => schedule(true));
  mutations.observe(track, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-active'] });
  // Widths change with fonts, counters and the screen.
  const sizes = typeof ResizeObserver === 'function' ? new ResizeObserver(() => schedule(false)) : null;
  if (sizes) {
    sizes.observe(track);
    const row = track.querySelector('[data-folder-row]');
    if (row) sizes.observe(row);
  }
  update(false);

  const api = {
    /** Part of the way (0..1) from the active tab to tab `target` (a swipe in progress). */
    preview(target, progress) {
      const tabs = tabsOf(track);
      const from = tabs[activeIndexOf(tabs)];
      const to = tabs[target];
      if (!from || !to) return;
      const p = Math.max(0, Math.min(1, progress));
      place(from.offsetLeft + (to.offsetLeft - from.offsetLeft) * p, from.offsetWidth + (to.offsetWidth - from.offsetWidth) * p, false);
      previewing(p > 0.02);
    },
    /** Back under the active tab (a swipe that did not commit). */
    settle() { update(true); },
    dispose() {
      mutations.disconnect();
      if (sizes) sizes.disconnect();
      if (frame) cancelAnimationFrame(frame);
      tabControllers.delete(track);
    }
  };
  tabControllers.set(track, api);
  return api;
}

// ---- Swipe between folders ----------------------------------------------------------------------------------

export function attachFolderSwipe(pane, scroller, head, track, ref) {
  if (!pane) return { dispose: noop, enter: noop };
  let g = null;           // gesture in progress
  let waiting = false;    // slid out, waiting for .NET to render the next folder
  let frame = 0;
  let swallowUntil = 0;
  let safety = 0;
  let cleanup = 0;

  const tabs = () => (track ? tabControllers.get(track) : null);
  const enabled = () => pane.hasAttribute('data-folder-swipe');

  const clear = () => {
    pane.style.transition = '';
    pane.style.transform = '';
    pane.style.opacity = '';
    pane.style.willChange = '';
    pane.removeAttribute('data-folder-swiping');
  };

  const render = () => {
    frame = 0;
    if (!g || !g.claimed) return;
    const dir = g.dx < 0 ? 1 : -1;      // a drag to the left goes to the next folder
    const target = g.index + dir;
    const has = target >= 0 && target < g.count;
    const offset = has ? g.dx : Math.sign(g.dx) * Math.min(RUBBER_MAX, Math.abs(g.dx) * RUBBER);
    pane.style.transform = `translate3d(${offset.toFixed(1)}px, 0, 0)`;
    pane.style.opacity = has ? String(1 - Math.min(0.5, (Math.abs(offset) / g.width) * 0.6)) : '';
    const t = tabs();
    if (t) {
      if (has) t.preview(target, Math.abs(offset) / g.width);
      else t.preview(g.index, 0);
    }
    const past = has && Math.abs(offset) > g.width * COMMIT;
    if (past !== g.past) {
      g.past = past;
      if (past) call(ref, 'OnFolderSwipeThreshold');
    }
  };

  const down = (e) => {
    if (waiting || g || !enabled() || !e.isPrimary || (e.pointerType !== 'touch' && e.pointerType !== 'pen')) return;
    if (e.target instanceof Element && e.target.closest('input, textarea, select, [data-no-swipe]')) return;
    const all = tabsOf(track);
    const index = activeIndexOf(all);
    if (index < 0) return;
    g = { id: e.pointerId, x0: e.clientX, y0: e.clientY, dx: 0, claimed: false, past: false, samples: [], index, count: all.length, width: pane.clientWidth || 1 };
  };

  const move = (e) => {
    if (!g || e.pointerId !== g.id) return;
    if (!g.claimed) {
      // A pinned chat lifted to be dragged (attachPinReorder) owns the touch.
      if (pane.querySelector('[data-reorder-drag]')) { g = null; return; }
      const dx = e.clientX - g.x0;
      const dy = e.clientY - g.y0;
      if (Math.abs(dx) < DECIDE_PX && Math.abs(dy) < DECIDE_PX) return;
      if (Math.abs(dx) <= Math.abs(dy) * 1.5) { g = null; return; }
      g.claimed = true;
      g.x0 = e.clientX;
      if (cleanup) { clearTimeout(cleanup); cleanup = 0; }
      pane.style.transition = 'none';
      pane.style.willChange = 'transform, opacity';
      pane.setAttribute('data-folder-swiping', '');
    }
    g.dx = e.clientX - g.x0;
    const now = performance.now();
    g.samples.push({ x: e.clientX, t: now });
    while (g.samples.length > 2 && now - g.samples[0].t > 100) g.samples.shift();
    if (!frame) frame = requestAnimationFrame(render);
  };

  const springBack = () => {
    const t = tabs();
    if (t) t.settle();
    if (reduceMotion()) { clear(); return; }
    pane.style.transition = 'transform 300ms cubic-bezier(0.22, 1.25, 0.36, 1), opacity 200ms linear';
    pane.style.transform = '';
    pane.style.opacity = '';
    cleanup = setTimeout(() => { cleanup = 0; if (!g) clear(); }, 320);
  };

  const up = (e) => {
    if (!g || e.pointerId !== g.id) return;
    const s = g;
    g = null;
    if (!s.claimed) return;
    if (frame) { cancelAnimationFrame(frame); frame = 0; }
    swallowUntil = Date.now() + 400;
    const dir = s.dx < 0 ? 1 : -1;
    const target = s.index + dir;
    const has = target >= 0 && target < s.count;
    const first = s.samples[0];
    const last = s.samples[s.samples.length - 1];
    const v = first && last && last.t - first.t >= 8 ? (last.x - first.x) / (last.t - first.t) : 0;
    const flick = Math.abs(v) > FLICK && Math.sign(v) === Math.sign(s.dx) && Math.abs(s.dx) > 24;
    if (e.type !== 'pointerup' || !has || !(Math.abs(s.dx) > s.width * COMMIT || flick)) {
      springBack();
      return;
    }
    waiting = true;
    safety = setTimeout(() => { waiting = false; clear(); const t = tabs(); if (t) t.settle(); }, 1200);
    if (reduceMotion()) {
      clear();
      call(ref, 'OnFolderSwipe', target);
      return;
    }
    const t = tabs();
    if (t) t.preview(target, 1);
    pane.style.transition = `transform ${OUT_MS}ms cubic-bezier(0.4, 0, 1, 1), opacity ${OUT_MS}ms linear`;
    pane.style.transform = `translate3d(${(-dir * s.width).toFixed(1)}px, 0, 0)`;
    pane.style.opacity = '0';
    setTimeout(() => call(ref, 'OnFolderSwipe', target), OUT_MS - 10);
  };

  // The new folder starts under the tabs: a list scrolled further down is brought back to its top.
  const resetScroll = () => {
    if (!scroller) return;
    const top = pane.getBoundingClientRect().top - scroller.getBoundingClientRect().top + scroller.scrollTop - (head ? head.offsetHeight : 0);
    if (scroller.scrollTop > top) scroller.scrollTop = Math.max(0, top);
  };

  const click = (e) => {
    if (Date.now() < swallowUntil) {
      e.preventDefault();
      e.stopPropagation();
      swallowUntil = 0;
    }
  };

  pane.addEventListener('pointerdown', down, { passive: true });
  pane.addEventListener('pointermove', move, { passive: true });
  pane.addEventListener('pointerup', up, { passive: true });
  pane.addEventListener('pointercancel', up, { passive: true });
  pane.addEventListener('click', click, true);

  return {
    /**
     * The next folder is on screen: it slides in from the side it was asked for (dir 1 = from the right, -1 = from the
     * left, 0 = just appears).
     */
    enter(dir) {
      waiting = false;
      if (safety) { clearTimeout(safety); safety = 0; }
      if (cleanup) { clearTimeout(cleanup); cleanup = 0; }
      resetScroll();
      if (!dir || reduceMotion() || !pane.isConnected) { clear(); return; }
      const width = pane.clientWidth || 1;
      pane.style.transition = 'none';
      pane.style.willChange = 'transform, opacity';
      pane.style.transform = `translate3d(${(dir * width * ENTER_SHIFT).toFixed(1)}px, 0, 0)`;
      pane.style.opacity = '0';
      pane.getBoundingClientRect();
      pane.style.transition = `transform ${ENTER_MS}ms cubic-bezier(0.16, 1, 0.3, 1), opacity 200ms linear`;
      pane.style.transform = '';
      pane.style.opacity = '';
      cleanup = setTimeout(() => { cleanup = 0; if (!g) clear(); }, ENTER_MS + 30);
    },
    dispose() {
      pane.removeEventListener('pointerdown', down);
      pane.removeEventListener('pointermove', move);
      pane.removeEventListener('pointerup', up);
      pane.removeEventListener('pointercancel', up);
      pane.removeEventListener('click', click, true);
      if (frame) cancelAnimationFrame(frame);
      if (safety) clearTimeout(safety);
      if (cleanup) clearTimeout(cleanup);
      g = null;
      clear();
    }
  };
}

// ---- Reorder by a handle ------------------------------------------------------------------------------------

export function attachHandleReorder(list, ref) {
  if (!list) return { dispose: noop };
  let d = null;
  let hold = 0;

  const rowsOf = () => Array.from(list.querySelectorAll('[data-reorder-key]'));

  const release = () => {
    for (const row of rowsOf()) {
      row.style.transition = '';
      row.style.transform = '';
      row.style.zIndex = '';
      row.removeAttribute('data-dragging');
    }
    list.removeAttribute('data-reordering');
  };

  const down = (e) => {
    if (d || (e.pointerType === 'mouse' && e.button !== 0)) return;
    const handle = e.target instanceof Element ? e.target.closest('[data-drag-handle]') : null;
    const row = handle ? handle.closest('[data-reorder-key]') : null;
    if (!row || !list.contains(row)) return;
    // Captured before the row sees it: holding the handle must not also start the row's long-press menu.
    e.preventDefault();
    e.stopPropagation();
    const rows = rowsOf();
    const index = rows.indexOf(row);
    const rects = rows.map((r) => r.getBoundingClientRect());
    const step = rows.length > 1 ? Math.abs(rects[Math.min(1, rows.length - 1)].top - rects[0].top) : rects[index].height;
    d = { id: e.pointerId, row, rows, rects, index, target: index, step: step || rects[index].height, y0: e.clientY };
    try { handle.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
    list.setAttribute('data-reordering', '');
    row.setAttribute('data-dragging', '');
    row.style.zIndex = '3';
    row.style.transition = 'none';
    call(ref, 'OnReorderLift');
  };

  const move = (e) => {
    if (!d || e.pointerId !== d.id) return;
    const { rows, rects, index, step } = d;
    const min = rects[0].top - rects[index].top;
    const max = rects[rects.length - 1].top - rects[index].top;
    const dy = Math.max(min, Math.min(max, e.clientY - d.y0));
    d.row.style.transform = `translateY(${dy.toFixed(1)}px)`;
    const target = Math.max(0, Math.min(rows.length - 1, index + Math.round(dy / step)));
    if (target === d.target) return;
    d.target = target;
    rows.forEach((row, i) => {
      if (i === index) return;
      const shift = index < target && i > index && i <= target ? -step : index > target && i >= target && i < index ? step : 0;
      row.style.transition = reduceMotion() ? 'none' : 'transform 180ms cubic-bezier(0.2, 0.8, 0.2, 1)';
      row.style.transform = shift ? `translateY(${shift}px)` : '';
    });
  };

  const up = (e) => {
    if (!d || e.pointerId !== d.id) return;
    const s = d;
    d = null;
    if (s.target === s.index || e.type !== 'pointerup') {
      if (reduceMotion()) { release(); return; }
      s.row.style.transition = 'transform 180ms cubic-bezier(0.2, 0.8, 0.2, 1)';
      for (const row of s.rows) row.style.transform = '';
      hold = setTimeout(release, 200);
      return;
    }
    const keys = s.rows.map((r) => r.getAttribute('data-reorder-key'));
    const [moved] = keys.splice(s.index, 1);
    keys.splice(s.target, 0, moved);
    s.row.style.transition = reduceMotion() ? 'none' : 'transform 160ms cubic-bezier(0.2, 0.8, 0.2, 1)';
    s.row.style.transform = `translateY(${((s.target - s.index) * s.step).toFixed(1)}px)`;
    // The rows stay where they show until Blazor puts them in that order (or a moment passes), so nothing jumps.
    const done = new MutationObserver(() => { done.disconnect(); clearTimeout(hold); release(); });
    done.observe(list, { childList: true });
    hold = setTimeout(() => { done.disconnect(); release(); }, 1500);
    call(ref, 'OnReorder', keys);
  };

  list.addEventListener('pointerdown', down, true);
  list.addEventListener('pointermove', move, { passive: true });
  list.addEventListener('pointerup', up, { passive: true });
  list.addEventListener('pointercancel', up, { passive: true });
  return {
    dispose() {
      list.removeEventListener('pointerdown', down, true);
      list.removeEventListener('pointermove', move);
      list.removeEventListener('pointerup', up);
      list.removeEventListener('pointercancel', up);
      clearTimeout(hold);
      d = null;
    }
  };
}
