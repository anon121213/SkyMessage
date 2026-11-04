// Chat folders (ES module, imported by FolderTabs, ChatListPanel and FoldersSection). See docs/MOTION.md §2.
// • attachFolderTabs(track, ref): the gradient pill sits under the active tab ([data-active]) and glides when it changes;
//   the strip scrolls the active tab into view, and a vertical mouse wheel scrolls it sideways while it overflows. Arrow
//   keys move between the tabs (roving tabindex; Home/End). A folder's tab ([data-folder-key]: not "All chats") lifted by
//   a long-press — or pressed and moved with the mouse — is dragged sideways among the others, which glide aside; the new
//   order goes to ref.OnTabsReorder(keys) (ref.OnTabLift() for the haptic); letting go without moving, or resting, opens
//   its menu as the long-press would have. While a swipe between folders is in progress the pill moves part of the way
//   (preview).
// • attachFolderSwipe(pane, scroller, head, track, ref): a sideways drag on the chat list follows the finger. Once the
//   gesture is claimed .NET renders the neighbouring folder's first rows into a [data-folder-peek] pane inside the list
//   pane (ref.OnFolderSwipePeek(dir)), placed a width to that side, so both lists move together. Past a third of the width
//   or with a flick they slide on until the neighbour fills the screen; .NET switches the list under it
//   (ref.OnFolderSwipe(index)) and calls enter(dir), which hands over to the real list in the same frame (no jump) and
//   lets .NET drop the peek panes (ref.OnFolderSwipeDone()). Without a neighbour in time, in html[data-fx="lite"] and with
//   reduced motion the list alone slides out and the next folder slides in from that side. Only while the pane carries
//   [data-folder-swipe] (the "Swipe on a chat" setting); the axis is decided after 8 px (|dx| > 1.5·|dy|), so vertical
//   scrolling stays native. Touch and pen only.
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
const SWAP_WAIT_MS = 1500;

const TAB_SLOP = 6;            // mouse: px before a press on a tab becomes a drag
const TAB_TOUCH_SLOP = 8;      // touch: px a lifted tab may wobble before it counts as a drag
const TAB_HOLD_MENU = 500;     // touch: ms a lifted tab may rest before its menu opens anyway
const TAB_EDGE = 40;           // px from the strip's ends where it scrolls by itself during a drag
const TAB_SPEED = 10;          // px per frame at the very end
const TAB_WAIT_MS = 2500;      // how long a dropped order waits for the tabs to re-render

function noop() { }

function call(ref, method, ...args) {
  if (!ref) return Promise.resolve();
  try {
    return ref.invokeMethodAsync(method, ...args).catch(noop);
  } catch (e) {
    return Promise.resolve();
  }
}

function lite() {
  return document.documentElement.getAttribute('data-fx') === 'lite';
}

function cssMs(name, fallback) {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  const value = parseFloat(raw);
  if (!Number.isFinite(value)) return fallback;
  return raw.endsWith('ms') ? value : raw.endsWith('s') ? value * 1000 : value;
}

// Ends the entrance animations (not the endless ones) of what was already on screen in another copy.
function finishEntrances(el) {
  if (!el || typeof el.getAnimations !== 'function') return;
  for (const a of el.getAnimations({ subtree: true })) {
    try {
      const timing = a.effect && typeof a.effect.getTiming === 'function' ? a.effect.getTiming() : null;
      if (timing && timing.iterations !== Infinity) a.finish();
    } catch (e) { /* gone or endless */ }
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

export function attachFolderTabs(track, ref) {
  if (!track) return { dispose: noop, preview: noop, settle: noop };
  let placed = false;
  let lastActive = -1;
  let frame = 0;
  const row = track.querySelector('[data-folder-row]');

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
    // A tab being dragged places the pill itself.
    if (r && r.dragging) return;
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
    if (row) sizes.observe(row);
  }

  // ---- the mouse wheel: a vertical wheel scrolls the strip sideways while it overflows ----

  const wheel = (e) => {
    if (e.ctrlKey || r) return;
    const max = track.scrollWidth - track.clientWidth;
    if (max <= 1) return;
    // A trackpad's own sideways scroll stays native.
    if (Math.abs(e.deltaX) >= Math.abs(e.deltaY)) return;
    const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? track.clientWidth : 1;
    const target = Math.max(0, Math.min(max, track.scrollLeft + e.deltaY * unit));
    // At an end the wheel goes on to the list.
    if (Math.abs(target - track.scrollLeft) < 1) return;
    e.preventDefault();
    track.scrollLeft = target;
  };

  // ---- the keyboard: one tab stop, the arrows move between the tabs (and open them, as tabs do) ----

  const key = (e) => {
    const tab = e.target instanceof Element ? e.target.closest('[data-folder-tab]') : null;
    if (!tab || e.altKey || e.ctrlKey || e.metaKey) return;
    const tabs = tabsOf(track);
    const index = tabs.indexOf(tab);
    let next = -1;
    switch (e.key) {
      case 'ArrowRight': next = Math.min(tabs.length - 1, index + 1); break;
      case 'ArrowLeft': next = Math.max(0, index - 1); break;
      case 'Home': next = 0; break;
      case 'End': next = tabs.length - 1; break;
      default: return;
    }
    e.preventDefault();
    if (next === index || next < 0) return;
    tabs[next].focus();
    tabs[next].click();
  };

  // ---- reordering: a folder's tab lifted and dragged sideways ----

  let r = null;            // the drag in progress
  let settle = null;       // { order, send, timer } a dropped order waiting for the re-render
  let lastTouch = null;    // { id, x, y } the primary touch down on the strip
  let swallowUntil = 0;

  const movable = () => (row ? Array.from(row.children).filter((el) => el.hasAttribute('data-folder-key')) : []);
  const keysOf = (slots) => slots.map((el) => el.getAttribute('data-folder-key'));
  const sameOrder = (a, b) => a.length === b.length && a.every((k, i) => k === b[i]);
  const slotOf = (target) => {
    const slot = target instanceof Element ? target.closest('[data-folder-key]') : null;
    return slot && slot.parentElement === row ? slot : null;
  };

  const clearSlot = (el) => {
    el.style.transform = '';
    el.style.transition = '';
    el.style.zIndex = '';
    el.style.willChange = '';
    el.removeAttribute('data-tab-lifted');
  };
  const clearAll = () => {
    for (const el of movable()) clearSlot(el);
    track.removeAttribute('data-reordering');
  };

  const lift = (slot, pointerId, touch, x, menu) => {
    const slots = movable();
    if (slots.length < 2 || !slots.includes(slot)) return false;
    finishSettle(false);
    const box = row.getBoundingClientRect();
    const items = slots.map((el) => {
      const rect = el.getBoundingClientRect();
      return { el, key: el.getAttribute('data-folder-key'), left: rect.left - box.left, width: rect.width, shift: 0 };
    });
    const gap = items.length > 1 ? Math.max(0, items[1].left - (items[0].left + items[0].width)) : 0;
    const from = slots.indexOf(slot);
    r = { slot, pointerId, touch, items, gap, x0: x, x, scroll0: track.scrollLeft, from, at: from, dragging: false, menu, holdTimer: 0, frame: 0 };
    slot.setAttribute('data-tab-lifted', '');
    slot.style.zIndex = '2';
    slot.style.willChange = 'transform';
    track.setAttribute('data-reordering', '');
    if (touch) {
      call(ref, 'OnTabLift');
      r.holdTimer = setTimeout(openMenu, TAB_HOLD_MENU);
    }
    return true;
  };

  // Touch: the lifted tab rested or was let go without a drag — its menu opens, as the long-press would have.
  const openMenu = () => {
    if (!r || r.dragging) return;
    const menu = r.menu;
    drop(false);
    if (menu) menu();
  };

  const startDrag = () => {
    if (!r || r.dragging) return;
    clearTimeout(r.holdTimer);
    r.dragging = true;
    const ms = reduceMotion() ? 0 : cssMs('--sm-dur', 220);
    for (const item of r.items) {
      if (item.el !== r.slot) item.el.style.transition = ms ? `transform ${ms}ms cubic-bezier(0.2, 0.8, 0.2, 1)` : '';
    }
    tick();
  };

  // Where each tab goes when the dragged one sits at `at`: the tabs line up from the first one's left.
  const targets = (g, at) => {
    const order = g.items.filter((_, i) => i !== g.from);
    order.splice(at, 0, g.items[g.from]);
    const lefts = new Map();
    let cursor = g.items[0].left;
    for (const item of order) {
      lefts.set(item, cursor);
      cursor += item.width + g.gap;
    }
    return { order, lefts };
  };

  // The pill stays under the active tab where it goes (the dragged one, or one gliding aside; "All chats" stays put).
  const pillFollows = () => {
    const tabs = tabsOf(track);
    const active = tabs[activeIndexOf(tabs)];
    const item = active ? r.items.find((it) => it.el.contains(active)) : null;
    if (item) place(item.left + item.shift, item.width, false);
  };

  const layout = () => {
    const dragged = r.items[r.from];
    const scrolled = track.scrollLeft - r.scroll0;
    const first = r.items[0];
    const last = r.items[r.items.length - 1];
    const min = first.left - dragged.left;
    const max = last.left + last.width - (dragged.left + dragged.width);
    const dx = Math.max(min, Math.min(max, r.x - r.x0 + scrolled));
    dragged.shift = dx;
    dragged.el.style.transform = `translate3d(${dx.toFixed(1)}px, 0, 0)`;
    // The place: how many of the other tabs have their middle left of the dragged tab's middle.
    const middle = dragged.left + dragged.width / 2 + dx;
    let at = 0;
    r.items.forEach((item, i) => { if (i !== r.from && item.left + item.width / 2 < middle) at++; });
    if (at !== r.at) {
      r.at = at;
      const { lefts } = targets(r, at);
      for (const item of r.items) {
        if (item === dragged) continue;
        item.shift = lefts.get(item) - item.left;
        item.el.style.transform = Math.abs(item.shift) >= 0.5 ? `translate3d(${item.shift.toFixed(1)}px, 0, 0)` : '';
      }
    }
    pillFollows();
  };

  // Near the strip's ends it scrolls by itself while there is more that way.
  const autoScroll = () => {
    const rect = track.getBoundingClientRect();
    const max = track.scrollWidth - track.clientWidth;
    let speed = 0;
    if (r.x < rect.left + TAB_EDGE && track.scrollLeft > 0) speed = -TAB_SPEED * Math.min(1, (rect.left + TAB_EDGE - r.x) / TAB_EDGE);
    else if (r.x > rect.right - TAB_EDGE && track.scrollLeft < max) speed = TAB_SPEED * Math.min(1, (r.x - (rect.right - TAB_EDGE)) / TAB_EDGE);
    if (!speed) return false;
    const before = track.scrollLeft;
    track.scrollLeft = before + speed;
    return track.scrollLeft !== before;
  };

  const tick = () => {
    if (!r || !r.dragging) return;
    r.frame = 0;
    const scrolling = autoScroll();
    layout();
    if (scrolling) r.frame = requestAnimationFrame(tick);
  };

  const request = () => {
    if (r && r.dragging && !r.frame) r.frame = requestAnimationFrame(tick);
  };

  // Ends the drag. commit: the dragged tab lands in its place and the new order goes to .NET.
  const drop = (commit) => {
    if (!r) return;
    const s = r;
    r = null;
    clearTimeout(s.holdTimer);
    if (s.frame) cancelAnimationFrame(s.frame);
    const ms = reduceMotion() ? 0 : cssMs('--sm-dur', 220);
    const ease = 'cubic-bezier(0.16, 1, 0.3, 1)';
    if (!s.dragging) { clearAll(); return; }
    if (!commit || s.at === s.from) {
      for (const item of s.items) {
        item.el.style.transition = ms ? `transform ${ms}ms ${ease}` : '';
        item.el.style.transform = '';
      }
      s.slot.removeAttribute('data-tab-lifted');
      setTimeout(() => { if (!r && !settle) clearAll(); }, ms + 40);
      update(true);
      return;
    }
    const { order, lefts } = targets(s, s.at);
    const dragged = s.items[s.from];
    dragged.el.style.transition = ms ? `transform ${ms}ms ${ease}` : '';
    dragged.el.style.transform = `translate3d(${(lefts.get(dragged) - dragged.left).toFixed(1)}px, 0, 0)`;
    dragged.el.removeAttribute('data-tab-lifted');
    const keys = order.map((item) => item.key);
    // The order goes out once the tab has landed: the re-render then swaps transforms for the same places.
    const send = setTimeout(() => call(ref, 'OnTabsReorder', keys), ms);
    settle = { order: keys, send, timer: setTimeout(() => finishSettle(true), ms + TAB_WAIT_MS) };
  };

  // The re-render arrived (or never did: the tabs glide back to where they really are).
  const finishSettle = (animateBack) => {
    if (!settle) return;
    clearTimeout(settle.timer);
    settle = null;
    if (animateBack && !reduceMotion()) {
      const ms = cssMs('--sm-dur', 220);
      for (const el of movable()) { el.style.transition = `transform ${ms}ms cubic-bezier(0.16, 1, 0.3, 1)`; el.style.transform = ''; }
      setTimeout(() => { if (!r && !settle) clearAll(); }, ms + 40);
    } else {
      clearAll();
    }
    update(false);
  };

  // Blazor moved the tabs: the order shown is the real one now — the transforms go in the same frame. A change while
  // dragging (a folder added or removed elsewhere) ends the drag where it is.
  const order = row ? new MutationObserver(() => {
    const keys = keysOf(movable());
    if (settle) {
      finishSettle(!sameOrder(keys, settle.order));
      return;
    }
    if (r && !sameOrder(keys, r.items.map((item) => item.key))) drop(false);
  }) : null;
  if (order) order.observe(row, { childList: true });

  const onClaim = (e) => {
    if (r || !e.detail) return;
    const slot = slotOf(e.target);
    const touch = lastTouch;
    if (!slot || !touch) return;
    if (lift(slot, touch.id, true, touch.x, e.detail.fire)) e.preventDefault();
  };

  const down = (e) => {
    if (!e.isPrimary) return;
    if (e.pointerType === 'mouse') {
      if (e.button !== 0 || r) return;
      const slot = slotOf(e.target);
      if (slot) r = { pending: true, slot, pointerId: e.pointerId, x0: e.clientX, y0: e.clientY };
      return;
    }
    lastTouch = { id: e.pointerId, x: e.clientX, y: e.clientY };
  };

  const move = (e) => {
    if (lastTouch && e.pointerId === lastTouch.id && !r) { lastTouch.x = e.clientX; lastTouch.y = e.clientY; }
    if (!r || e.pointerId !== r.pointerId) return;
    if (r.pending) {
      const dx = e.clientX - r.x0;
      const dy = e.clientY - r.y0;
      if (Math.abs(dx) < TAB_SLOP || Math.abs(dx) < Math.abs(dy)) return;
      const { slot, pointerId, x0 } = r;
      r = null;
      if (!lift(slot, pointerId, false, x0, null)) return;
      try { track.setPointerCapture(pointerId); } catch (err) { /* released meanwhile */ }
      startDrag();
    }
    r.x = e.clientX;
    if (!r.dragging) {
      if (Math.abs(e.clientX - r.x0) < TAB_TOUCH_SLOP) return;
      startDrag();
      return;
    }
    request();
  };

  const up = (e) => {
    if (lastTouch && e.pointerId === lastTouch.id) lastTouch = null;
    if (!r || e.pointerId !== r.pointerId) return;
    if (r.pending) { r = null; return; }
    if (r.dragging) {
      swallowUntil = Date.now() + 400;
      drop(e.type === 'pointerup');
    } else if (e.type === 'pointerup') {
      openMenu();
    } else {
      drop(false);
    }
  };

  // A lifted tab owns its touch: the strip does not scroll under it.
  const touchMove = (e) => { if (r && !r.pending && e.cancelable) e.preventDefault(); };

  const click = (e) => {
    if (Date.now() < swallowUntil) {
      e.preventDefault();
      e.stopPropagation();
      swallowUntil = 0;
    }
  };
  const onScroll = () => { if (r && r.dragging) request(); };

  track.addEventListener('wheel', wheel, { passive: false });
  track.addEventListener('keydown', key);
  track.addEventListener('sm-longpress', onClaim);
  track.addEventListener('pointerdown', down, { passive: true });
  track.addEventListener('pointermove', move, { passive: true });
  track.addEventListener('pointerup', up, { passive: true });
  track.addEventListener('pointercancel', up, { passive: true });
  track.addEventListener('touchmove', touchMove, { passive: false });
  track.addEventListener('click', click, true);
  track.addEventListener('scroll', onScroll, { passive: true });
  update(false);

  const api = {
    /** Part of the way (0..1) from the active tab to tab `target` (a swipe in progress). */
    preview(target, progress) {
      if (r) return;
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
      if (order) order.disconnect();
      if (sizes) sizes.disconnect();
      if (frame) cancelAnimationFrame(frame);
      if (r) { clearTimeout(r.holdTimer); if (r.frame) cancelAnimationFrame(r.frame); }
      r = null;
      if (settle) { clearTimeout(settle.timer); clearTimeout(settle.send); }
      settle = null;
      track.removeEventListener('wheel', wheel);
      track.removeEventListener('keydown', key);
      track.removeEventListener('sm-longpress', onClaim);
      track.removeEventListener('pointerdown', down);
      track.removeEventListener('pointermove', move);
      track.removeEventListener('pointerup', up);
      track.removeEventListener('pointercancel', up);
      track.removeEventListener('touchmove', touchMove);
      track.removeEventListener('click', click, true);
      track.removeEventListener('scroll', onScroll);
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
  let swap = null;        // { dir } the neighbour slid in, waiting for .NET to switch the list under it
  let peeking = false;    // .NET was asked for neighbour panes (OnFolderSwipeDone lets it drop them)
  let frame = 0;
  let swallowUntil = 0;
  let safety = 0;
  let cleanup = 0;
  let settling = null;    // { timer, end } a shorter folder scrolling up before the pane lets go of its old height

  const tabs = () => (track ? tabControllers.get(track) : null);
  const enabled = () => pane.hasAttribute('data-folder-swipe');
  const peekOf = (dir) => {
    for (const el of pane.children) if (el.getAttribute('data-folder-peek') === String(dir)) return el;
    return null;
  };
  const peekReady = (dir) => {
    const peek = peekOf(dir);
    return !!peek && peek.childElementCount > 0;
  };

  // How far the pane's top is above the visible part of the list under the sticky header (0 when it starts below it).
  const hiddenTop = () => {
    const bottom = head && head.isConnected ? head.getBoundingClientRect().bottom : scroller ? scroller.getBoundingClientRect().top : 0;
    return Math.max(0, bottom - pane.getBoundingClientRect().top);
  };

  const done = () => {
    if (!peeking) return;
    peeking = false;
    call(ref, 'OnFolderSwipeDone');
  };

  const clear = () => {
    pane.style.transition = '';
    pane.style.transform = '';
    pane.style.opacity = '';
    pane.style.willChange = '';
    pane.style.minHeight = '';
    pane.removeAttribute('data-folder-swiping');
    pane.removeAttribute('data-folder-peek-hidden');
  };

  // The pane kept its height through the switch, so the new folder's rows took the neighbour's place exactly. A folder
  // that is shorter than the scroll position needs scrolls the list up first (smoothly: the search and the notices come
  // back into view above it), and only then lets go of the height, so nothing jumps.
  const settleHeight = () => {
    endSettling();
    const frozen = pane.style.minHeight;
    if (!frozen || !scroller) { clear(); return; }
    const at = scroller.scrollTop;
    pane.style.minHeight = '';
    const max = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
    if (at <= max + 1) { clear(); return; }
    pane.style.minHeight = frozen;
    scroller.scrollTop = at;
    if (reduceMotion()) { scroller.scrollTop = max; clear(); return; }
    const end = () => { if (settling && settling.end === end) { endSettling(); clear(); } };
    settling = { end, timer: setTimeout(end, 900) };
    scroller.addEventListener('scrollend', end, { once: true });
    scroller.scrollTo({ top: max, behavior: 'smooth' });
  };

  const endSettling = () => {
    if (!settling) return;
    clearTimeout(settling.timer);
    if (scroller) scroller.removeEventListener('scrollend', settling.end);
    settling = null;
  };

  const requestPeek = (dir) => {
    if (!g || !g.peek || g.asked.has(dir)) return;
    const target = g.index + dir;
    if (target < 0 || target >= g.count) return;
    g.asked.add(dir);
    peeking = true;
    call(ref, 'OnFolderSwipePeek', dir);
  };

  const render = () => {
    frame = 0;
    if (!g || !g.claimed) return;
    const dir = g.dx < 0 ? 1 : -1;      // a drag to the left goes to the next folder
    const target = g.index + dir;
    const has = target >= 0 && target < g.count;
    // (No side yet while the finger is back where the swipe was claimed.)
    if (has && g.dx !== 0) requestPeek(dir);
    const offset = has ? g.dx : Math.sign(g.dx) * Math.min(RUBBER_MAX, Math.abs(g.dx) * RUBBER);
    pane.style.transform = `translate3d(${offset.toFixed(1)}px, 0, 0)`;
    // With the neighbour beside it both lists stay solid; alone, the leaving list fades a little.
    pane.style.opacity = !g.peek && has ? String(1 - Math.min(0.5, (Math.abs(offset) / g.width) * 0.6)) : '';
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
    g = {
      id: e.pointerId, x0: e.clientX, y0: e.clientY, dx: 0, claimed: false, past: false, samples: [], index, count: all.length,
      width: pane.clientWidth || 1, peek: false, asked: new Set()
    };
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
      if (settling) { endSettling(); pane.style.minHeight = ''; }
      // The neighbour follows the finger too, except on slow devices and with reduced motion (the list alone slides).
      g.peek = !lite() && !reduceMotion();
      if (g.peek) pane.style.setProperty('--folder-peek-top', `${hiddenTop().toFixed(1)}px`);
      pane.removeAttribute('data-folder-peek-hidden');
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
    if (reduceMotion()) { clear(); done(); return; }
    pane.style.transition = 'transform 300ms cubic-bezier(0.22, 1.25, 0.36, 1), opacity 200ms linear';
    pane.style.transform = '';
    pane.style.opacity = '';
    cleanup = setTimeout(() => { cleanup = 0; if (!g) { clear(); done(); } }, 320);
  };

  // .NET did not switch in time: the list comes back as it is.
  const abort = () => {
    safety = 0;
    waiting = false;
    swap = null;
    clear();
    const t = tabs();
    if (t) t.settle();
    done();
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
    const t = tabs();
    if (s.peek && peekReady(dir)) {
      // Both lists slide on until the neighbour fills the screen; .NET then switches the list under it. The pane keeps
      // its height meanwhile, so a shorter folder cannot pull the scroll position away from under the neighbour.
      swap = { dir };
      safety = setTimeout(abort, SWAP_WAIT_MS);
      pane.style.minHeight = `${pane.offsetHeight}px`;
      if (t) t.preview(target, 1);
      const rest = 1 - Math.min(1, Math.abs(s.dx) / s.width);
      const ms = Math.round(Math.max(120, Math.min(280, 90 + 260 * rest)));
      pane.style.transition = `transform ${ms}ms cubic-bezier(0.16, 1, 0.3, 1)`;
      pane.style.transform = `translate3d(${(-dir * s.width).toFixed(1)}px, 0, 0)`;
      setTimeout(() => call(ref, 'OnFolderSwipe', target), ms);
      return;
    }
    // No neighbour on screen: the list slides out alone and the next folder slides in once it rendered.
    pane.setAttribute('data-folder-peek-hidden', '');
    safety = setTimeout(abort, SWAP_WAIT_MS);
    if (reduceMotion()) {
      clear();
      pane.setAttribute('data-folder-peek-hidden', '');
      call(ref, 'OnFolderSwipe', target);
      return;
    }
    if (t) t.preview(target, 1);
    pane.style.transition = `transform ${OUT_MS}ms cubic-bezier(0.4, 0, 1, 1), opacity ${OUT_MS}ms linear`;
    pane.style.transform = `translate3d(${(-dir * s.width).toFixed(1)}px, 0, 0)`;
    pane.style.opacity = '0';
    setTimeout(() => call(ref, 'OnFolderSwipe', target), OUT_MS - 10);
  };

  // The new folder starts under the tabs: a list scrolled further down is brought back to its top.
  const resetScroll = () => {
    if (!scroller) return;
    const over = hiddenTop();
    if (over > 0) scroller.scrollTop = Math.max(0, scroller.scrollTop - over);
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
     * The next folder is on screen: after a swipe with its neighbour beside it, the real list takes the neighbour's place
     * in the same frame (a shorter folder then scrolls up to where it ends); else it slides in from the side it was asked
     * for (dir 1 = from the right, -1 = from the left, 0 = just appears).
     */
    enter(dir) {
      waiting = false;
      if (safety) { clearTimeout(safety); safety = 0; }
      if (cleanup) { clearTimeout(cleanup); cleanup = 0; }
      if (swap) {
        swap = null;
        // The real list takes the neighbour's place in this frame: the same rows, from the top of the visible part.
        pane.style.transition = 'none';
        pane.style.transform = '';
        pane.style.opacity = '';
        pane.setAttribute('data-folder-peek-hidden', '');
        // The archive row's rise-in: it was on screen already, in the neighbour.
        for (const el of pane.children) if (!el.hasAttribute('data-folder-peek')) finishEntrances(el);
        resetScroll();
        if (pane.isConnected) settleHeight();
        else clear();
        done();
        return;
      }
      resetScroll();
      if (!dir || reduceMotion() || !pane.isConnected) { clear(); done(); return; }
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
      done();
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
      endSettling();
      g = null;
      swap = null;
      clear();
      pane.style.removeProperty('--folder-peek-top');
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
