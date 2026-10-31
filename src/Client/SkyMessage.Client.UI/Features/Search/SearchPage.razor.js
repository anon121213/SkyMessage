// Recent searches: drag a row to the left to remove it (SearchPage). One set of listeners on the page body serves
// every row marked [data-swipe-key]. See docs/MOTION.md: the axis is decided after 8 px (|dx| > 1.5·|dy|), only
// leftward drags are claimed, so vertical scrolling and the shell's rightward swipe-back keep working.
import { reduceMotion } from '../../js/app.js';

const DECIDE_PX = 8;
const FLICK_PX_PER_MS = 0.6;

function noop() { }

export function attachRowSwipe(container, ref) {
  if (!container) return { dispose: noop };
  let row = null, id = null, startX = 0, startY = 0, startT = 0, dx = 0, decided = false, armed = false, swallowClick = false;

  const threshold = () => Math.max(96, row.offsetWidth * 0.33);
  const call = (method, ...args) => {
    try { return ref.invokeMethodAsync(method, ...args).catch(noop); } catch (e) { return Promise.resolve(); }
  };
  const settle = (el, transform, opacity, ms, easing) => {
    if (reduceMotion()) {
      el.style.transition = 'none';
    } else {
      el.style.transition = `transform ${ms}ms ${easing}, opacity ${ms}ms ${easing}`;
    }
    el.style.transform = transform;
    el.style.opacity = opacity;
  };
  const clear = (el) => {
    el.style.transition = '';
    el.style.transform = '';
    el.style.opacity = '';
    el.style.willChange = '';
  };

  const down = (e) => {
    if (row || (e.pointerType === 'mouse' && e.button !== 0)) return;
    const target = e.target.closest ? e.target.closest('[data-swipe-key]') : null;
    // The row's own buttons (remove ✕) keep their tap.
    if (!target || !container.contains(target) || e.target.closest('button')) return;
    row = target; id = e.pointerId; decided = false; armed = false; dx = 0;
    startX = e.clientX; startY = e.clientY; startT = performance.now();
  };

  const move = (e) => {
    if (!row || e.pointerId !== id) return;
    const mx = e.clientX - startX, my = e.clientY - startY;
    if (!decided) {
      if (Math.abs(mx) < DECIDE_PX && Math.abs(my) < DECIDE_PX) return;
      if (mx >= 0 || Math.abs(mx) <= 1.5 * Math.abs(my)) { row = null; return; }
      decided = true;
      startX = e.clientX;
      try { row.setPointerCapture(id); } catch (err) { /* ignore */ }
      row.style.transition = 'none';
      row.style.willChange = 'transform';
    }
    dx = Math.min(0, e.clientX - startX);
    row.style.transform = `translateX(${dx}px)`;
    row.style.opacity = String(Math.max(0.35, 1 + dx / (row.offsetWidth * 1.4)));
    const past = -dx > threshold();
    if (past !== armed) {
      armed = past;
      if (past) call('OnRowSwipeArmed');
    }
  };

  const up = (e) => {
    if (!row || e.pointerId !== id) return;
    const el = row;
    row = null;
    if (!decided) return;
    swallowClick = true;
    setTimeout(() => { swallowClick = false; }, 0);
    const velocity = -dx / Math.max(1, performance.now() - startT);
    if (e.type !== 'pointercancel' && (armed || velocity > FLICK_PX_PER_MS)) {
      // Out to the left, then .NET drops the entry; if the row is still there afterwards (failed), it comes back.
      settle(el, 'translateX(-100%)', '0', 160, 'cubic-bezier(0.4, 0, 1, 1)');
      const key = el.getAttribute('data-swipe-key');
      setTimeout(() => {
        call('OnRowSwiped', key).then(() => requestAnimationFrame(() => requestAnimationFrame(() => {
          if (el.isConnected) clear(el);
        })));
      }, reduceMotion() ? 0 : 160);
    } else {
      settle(el, '', '', 220, 'cubic-bezier(0.34, 1.56, 0.64, 1)');
      setTimeout(() => clear(el), 240);
    }
  };

  // A drag that ends over the row must not also open it.
  const click = (e) => {
    if (swallowClick) { e.stopPropagation(); e.preventDefault(); }
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
    }
  };
}
