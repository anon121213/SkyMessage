// SkyMessage boot script (classic script). Hosts include it BEFORE the Blazor script:
//   <script src="_content/SkyMessage.Client.UI/js/boot.js"></script>
// 1. Applies the last persisted theme (light/dark, accent, wallpaper, font size) before the first paint,
//    so the app never flashes the wrong colors on launch.
// 2. Picks the effects level: slow phones (≤ 2 GB memory, ≤ 2 cores or Data Saver) get html[data-fx="lite"] — solid
//    surfaces instead of blur and a still sky (docs/DESIGN.md); localStorage "sm.fx" = "lite"/"full" overrides it.
// 3. Installs the overlay-history filter on `popstate` ahead of Blazor's router listener: every overlay
//    (dialog, sheet, menu, viewer) owns a same-URL history entry, and Back closes overlays first without the
//    router ever seeing those entries.
(function () {
  'use strict';

  var THEME_KEY = 'sm.theme.v1';
  var LANG_KEY = 'sm.lang';
  var root = document.documentElement;
  var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

  function read(key) {
    try { return window.localStorage.getItem(key); } catch (e) { return null; }
  }

  function resolve(mode) {
    if (mode === 'dark') return 'dark';
    if (mode === 'light') return 'light';
    return media && media.matches ? 'dark' : 'light';
  }

  function apply(state) {
    var mode = (state && state.mode) || root.getAttribute('data-theme-mode') || 'system';
    var theme = resolve(mode);
    root.setAttribute('data-theme-mode', mode);
    root.setAttribute('data-theme', theme);
    if (state) {
      root.setAttribute('data-reduce-motion', state.reduceMotion ? 'true' : 'false');
      root.setAttribute('data-compact', state.compact ? 'true' : 'false');
      var style = document.getElementById('sm-theme');
      if (!style) {
        style = document.createElement('style');
        style.id = 'sm-theme';
        (document.head || root).appendChild(style);
      }
      style.textContent = state.css || '';
    }
    var meta = document.querySelector('meta[name="theme-color"]');
    if (!meta && document.head) {
      meta = document.createElement('meta');
      meta.setAttribute('name', 'theme-color');
      document.head.appendChild(meta);
    }
    if (meta) meta.setAttribute('content', theme === 'dark' ? '#150F38' : '#ECE6FF');
    return theme === 'dark';
  }

  var saved = null;
  try { saved = JSON.parse(read(THEME_KEY) || 'null'); } catch (e) { saved = null; }
  apply(saved);
  root.lang = read(LANG_KEY) || root.lang || 'ru';

  var fx = read('sm.fx');
  if (fx !== 'lite' && fx !== 'full') {
    var nav = window.navigator || {};
    var slow = (nav.deviceMemory && nav.deviceMemory <= 2) || (nav.hardwareConcurrency && nav.hardwareConcurrency <= 2) ||
      (nav.connection && nav.connection.saveData);
    fx = slow ? 'lite' : 'full';
  }
  root.setAttribute('data-fx', fx);

  if (media) {
    var onScheme = function () {
      if ((root.getAttribute('data-theme-mode') || 'system') === 'system') apply(null);
    };
    if (media.addEventListener) media.addEventListener('change', onScheme);
    else if (media.addListener) media.addListener(onScheme);
  }

  window.__smBoot = { applyTheme: apply, resolveTheme: resolve, themeKey: THEME_KEY, langKey: LANG_KEY };

  // ---- Overlay history ----------------------------------------------------------------------------
  // stack: open overlay ids (bottom first); base: overlay marker of the entry below the first overlay (a stale
  // entry after a reload, usually null); expect: the landing entry of the programmatic traversal in flight.
  // queue: pushes and programmatic traversals run one at a time. Two closes started together (overlays disposed in
  // one render batch) would otherwise land on entries the second one no longer expects, and a push made while a
  // traversal is still on its way would be taken back by it.
  var H = { stack: [], base: null, expect: null, listener: null, queue: Promise.resolve() };

  function stateOf(value) { return value && typeof value === 'object' ? value : {}; }

  function notify(ids) {
    if (!ids.length || !H.listener) return;
    try { H.listener.invokeMethodAsync('OnOverlaysPopped', ids); } catch (e) { /* page shutting down */ }
  }

  function serial(work) {
    var run = H.queue.then(work);
    H.queue = run.then(null, function () { /* a failed step never blocks the next one */ });
    return run;
  }

  function pushNow(id) {
    // Copy the current entry's state (Blazor keeps its own bookkeeping there) and mark it as ours.
    var current = stateOf(history.state);
    var next = {};
    for (var key in current) {
      if (Object.prototype.hasOwnProperty.call(current, key)) next[key] = current[key];
    }
    next.smOverlay = id;
    var below = current.smOverlay || null;
    try { history.pushState(next, '', location.href); } catch (e) { return false; }
    if (!H.stack.length) H.base = below;
    H.stack.push(id);
    return true;
  }

  function push(id) {
    return serial(function () { return pushNow(id); });
  }

  // history.go(delta) with its popstate kept from the router: it is expected to land on the entry marked `target`
  // (on any entry with `anywhere`). Resolves once it landed, or after 700 ms.
  function traverse(delta, target, anywhere) {
    return new Promise(function (resolve) {
      var finished = false;
      function finish() {
        if (finished) return;
        finished = true;
        resolve();
      }
      H.expect = { target: target, anywhere: anywhere, resolve: finish, until: Date.now() + 2000 };
      setTimeout(function () {
        if (H.expect && H.expect.resolve === finish) H.expect = null;
        finish();
      }, 700);
      history.go(delta);
    });
  }

  // Removes the entry of `id` and every overlay entry above it. Resolves once the browser has popped them.
  function close(id) {
    return serial(function () {
      var index = H.stack.indexOf(id);
      if (index < 0) return;
      var removed = H.stack.splice(index);
      return traverse(-removed.length, index > 0 ? H.stack[index - 1] : H.base, false);
    });
  }

  // Goes back past the open overlays and `pages` page entries without the router seeing it; the caller then replaces
  // the entry it landed on (sign-in and sign-out start the history over). Never more pages than Blazor's own entry
  // index (history.state._index, when present) puts behind this one, so it cannot step out of the app.
  function rewind(pages) {
    return serial(function () {
      var behind = stateOf(history.state)._index;
      var count = Math.max(0, pages | 0);
      if (typeof behind === 'number' && behind >= 0) count = Math.min(count, behind);
      var steps = H.stack.length + count;
      H.stack = [];
      if (!steps) return;
      return traverse(-steps, null, true);
    });
  }

  function forget() {
    H.stack = [];
    H.expect = null;
  }

  function onPopState(event) {
    var here = stateOf(event.state).smOverlay || null;

    if (H.expect && Date.now() < H.expect.until && (H.expect.anywhere || here === H.expect.target)) {
      var done = H.expect.resolve;
      H.expect = null;
      event.stopImmediatePropagation();
      done();
      return;
    }

    if (H.stack.length) {
      var index = here ? H.stack.indexOf(here) : -1;
      if (here && index === H.stack.length - 1) {
        event.stopImmediatePropagation();
        return;
      }
      // Back from overlay entries: close every overlay above the entry we landed on. An unknown (stale)
      // overlay entry counts as the page itself.
      var closed = H.stack.splice(index + 1).reverse();
      event.stopImmediatePropagation();
      notify(closed);
      return;
    }

    if (here) {
      // A leftover entry of an overlay that is gone (closed by navigation or a reload): step over it.
      event.stopImmediatePropagation();
      history.back();
    }
  }

  window.addEventListener('popstate', onPopState);

  window.__smHistory = {
    push: push,
    close: close,
    rewind: rewind,
    forget: forget,
    depth: function () { return H.stack.length; },
    setListener: function (listener) { H.listener = listener; },
    back: function () { history.back(); }
  };
})();
