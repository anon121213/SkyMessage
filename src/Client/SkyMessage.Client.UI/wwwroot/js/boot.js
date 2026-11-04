// SkyMessage boot script (classic script). Hosts include it BEFORE the Blazor script:
//   <script src="_content/SkyMessage.Client.UI/js/boot.js"></script>
// 1. Applies the last persisted theme (light/dark, accent, backgrounds, font size) before the first paint,
//    so the app never flashes the wrong colors on launch; the pictures of the user's own backgrounds load right
//    after (see "Background pictures").
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

  // ---- Background pictures ----------------------------------------------------------------------------
  // The user's own pictures behind the screens and the chats (Settings › Appearance, a chat's menu). The theme state's
  // "images" name them per slot ("sky": the app's, "chat": the chats' default) and theme. Each is loaded from its source
  // — an app-origin path the native host serves, or "idb:<name>" for the browser's IndexedDB "skymessage-backdrops"
  // (store "files", { type, blob, savedAt }, written by web-platform.js) — and drawn once onto a canvas: always a still,
  // opaque picture (a GIF shows its first frame) with its blur baked in, so nothing is filtered while the page scrolls.
  // The result stays an object URL for the session, with the picture's average color (its tone: the chat screen's edges
  // fade into it) and the colors of its 8×8 cells, measured on the sharp picture; the page shows them through
  // --sm-sky-photo / --sm-chat-photo and --sm-sky-tone / --sm-chat-tone. The app's picture carries a readability floor
  // ("floor": the theme's background color and the text that sits on the picture): row by row, the least veil under
  // which that text reads on every cell of the row, and the veil becomes a top-to-bottom gradient that is everywhere
  // the larger of that and the user's dim (--sm-sky-veil; --sm-sky-dim-min is its strongest row).
  var PHOTO_BLUR_PX = 28;
  var photoJobs = {};
  var photoUrls = {};
  var photoApplied = {};
  var photoSeq = 0;
  var photoDbPromise = null;
  var filterSupport = null;
  var lastState = null;

  function photoDb() {
    if (photoDbPromise) return photoDbPromise;
    photoDbPromise = new Promise(function (resolve, reject) {
      if (!window.indexedDB) { reject(new Error('IndexedDB unavailable')); return; }
      var request = window.indexedDB.open('skymessage-backdrops', 1);
      request.onupgradeneeded = function () {
        if (!request.result.objectStoreNames.contains('files')) request.result.createObjectStore('files');
      };
      request.onsuccess = function () {
        var db = request.result;
        db.onversionchange = function () { db.close(); photoDbPromise = null; };
        resolve(db);
      };
      request.onerror = function () { photoDbPromise = null; reject(request.error); };
      request.onblocked = function () { photoDbPromise = null; reject(new Error('IndexedDB blocked')); };
    });
    return photoDbPromise;
  }

  function storedPhoto(name) {
    return photoDb().then(function (db) {
      return new Promise(function (resolve) {
        var request = db.transaction('files', 'readonly').objectStore('files').get(name);
        request.onsuccess = function () { resolve(request.result && request.result.blob ? request.result.blob : null); };
        request.onerror = function () { resolve(null); };
      });
    });
  }

  function loadPhoto(src) {
    if (src.indexOf('idb:') === 0) return storedPhoto(src.substring(4));
    return window.fetch(src).then(function (response) { return response.ok ? response.blob() : null; });
  }

  function imageOf(blob) {
    return new Promise(function (resolve, reject) {
      var url = URL.createObjectURL(blob);
      var img = new Image();
      img.onload = function () { resolve(img); setTimeout(function () { URL.revokeObjectURL(url); }, 0); };
      img.onerror = function () { URL.revokeObjectURL(url); reject(new Error('undecodable picture')); };
      img.src = url;
    });
  }

  function decodePhoto(blob) {
    if (typeof window.createImageBitmap !== 'function') return imageOf(blob);
    return window.createImageBitmap(blob, { imageOrientation: 'from-image' }).catch(function () { return imageOf(blob); });
  }

  function canvasFilter() {
    if (filterSupport === null) {
      try {
        var ctx = document.createElement('canvas').getContext('2d');
        ctx.filter = 'blur(2px)';
        filterSupport = ctx.filter === 'blur(2px)';
      } catch (e) { filterSupport = false; }
    }
    return filterSupport;
  }

  function hex2(v) { return ('0' + Math.max(0, Math.min(255, Math.round(v))).toString(16)).slice(-2); }

  // The part of a picture the window shows (it covers the window, centered), drawn down to 8×8: { tone, cells } — its
  // average color and the 64 cells' colors ("#rrggbb"), nulls when it cannot be read.
  function measure(source) {
    try {
      var w = source.naturalWidth || source.width, h = source.naturalHeight || source.height;
      var aspect = Math.max(320, window.innerWidth || 0) / Math.max(320, window.innerHeight || 0);
      var sw = w, sh = h;
      if (w / h > aspect) sw = h * aspect;
      else sh = w / aspect;
      var c = document.createElement('canvas');
      c.width = 8;
      c.height = 8;
      var g = c.getContext('2d');
      g.imageSmoothingEnabled = true;
      g.imageSmoothingQuality = 'high';
      g.drawImage(source, (w - sw) / 2, (h - sh) / 2, sw, sh, 0, 0, 8, 8);
      var d = g.getImageData(0, 0, 8, 8).data, r = 0, gr = 0, b = 0, cells = [];
      for (var i = 0; i < d.length; i += 4) {
        r += d[i]; gr += d[i + 1]; b += d[i + 2];
        cells.push('#' + hex2(d[i]) + hex2(d[i + 1]) + hex2(d[i + 2]));
      }
      return { tone: '#' + hex2(r / 64) + hex2(gr / 64) + hex2(b / 64), cells: cells };
    } catch (e) { return { tone: null, cells: null }; }
  }

  // ---- Readability floor (ThemeCatalog.ScreenVeil: keep the two in step) ----
  function channel(v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }
  function luminanceOf(c) { return 0.2126 * channel(c[0]) + 0.7152 * channel(c[1]) + 0.0722 * channel(c[2]); }
  function rgbOf(hex) {
    var v = parseInt(String(hex || '').replace('#', ''), 16) || 0;
    return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
  }

  // The least veil (0..0.9, in hundredths) of floor.veil over which every ink of floor.inks keeps its contrast on every
  // cell; 0 without cells or a floor.
  function screenVeil(cells, floor) {
    if (!cells || !cells.length || !floor || !floor.veil || !floor.inks || !floor.inks.length) return 0;
    var veil = rgbOf(floor.veil);
    var inks = floor.inks.map(function (ink) { return { lum: luminanceOf(rgbOf(ink.color)), min: ink.min || 0 }; });
    var colors = cells.map(rgbOf);
    for (var step = 0; step < 90; step++) {
      var amount = step / 100, reads = true;
      for (var i = 0; reads && i < colors.length; i++) {
        var c = colors[i];
        var lum = luminanceOf([
          Math.round(c[0] + (veil[0] - c[0]) * amount),
          Math.round(c[1] + (veil[1] - c[1]) * amount),
          Math.round(c[2] + (veil[2] - c[2]) * amount)]);
        for (var k = 0; k < inks.length; k++) {
          var hi = Math.max(lum, inks[k].lum), lo = Math.min(lum, inks[k].lum);
          if ((hi + 0.05) / (lo + 0.05) < inks[k].min) { reads = false; break; }
        }
      }
      if (reads) return amount;
    }
    return 0.9;
  }

  // The floor row by row (8 rows of 8 cells, ThemeCatalog.ScreenVeilRows); [] without a full grid.
  function screenVeilRows(cells, floor) {
    if (!cells || cells.length !== 64) return [];
    var rows = [];
    for (var r = 0; r < 8; r++) rows.push(screenVeil(cells.slice(r * 8, r * 8 + 8), floor));
    return rows;
  }

  // The veil as one gradient of the theme's background (ThemeCatalog.ScreenVeilCss): at each row's edges at least what
  // the rows on both sides need, and never less than the user's dim (resolved on <html>, per theme).
  function veilImage(rows) {
    var stops = [];
    for (var edge = 0; edge <= rows.length; edge++) {
      var need = Math.max(edge > 0 ? rows[edge - 1] : 0, edge < rows.length ? rows[edge] : 0);
      stops.push('color-mix(in srgb, var(--sm-bg) calc(max(var(--sm-sky-dim, 0), ' + need + ') * 100%), transparent) ' +
        (edge * 100 / rows.length) + '%');
    }
    return 'linear-gradient(to bottom, ' + stops.join(', ') + ')';
  }

  // A sharp JPEG is shown as stored (it was downscaled to the screen when picked): only a small copy is decoded, for
  // its tone and cells.
  function sharpPhoto(blob) {
    var url = URL.createObjectURL(blob);
    // Only the width given: the small copy keeps the picture's proportions (measure crops it like the window does).
    var small = typeof window.createImageBitmap === 'function'
      ? window.createImageBitmap(blob, { resizeWidth: 48, resizeQuality: 'medium' }).catch(function () { return imageOf(blob); })
      : imageOf(blob);
    return small.then(function (image) {
      var measured = measure(image);
      if (image.close) image.close();
      return { url: url, tone: measured.tone, cells: measured.cells };
    }, function () { return { url: url, tone: null, cells: null }; });
  }

  // The picture at the size it covers the window with (no more pixels than the screen shows; far fewer when blurred,
  // the blur hides the detail anyway), blurred by level/100 × 28 CSS px. Without canvas filters (old Safari) the
  // upscaling of an even smaller picture does the softening.
  function bakePhoto(blob, level) {
    if (!level && blob.type === 'image/jpeg') return sharpPhoto(blob);
    return decodePhoto(blob).then(function (image) {
      var w = image.naturalWidth || image.width, h = image.naturalHeight || image.height;
      if (!w || !h) return null;
      var vw = Math.max(320, window.innerWidth || 0), vh = Math.max(320, window.innerHeight || 0);
      var dpr = Math.min(3, Math.max(1, window.devicePixelRatio || 1));
      var cover = Math.max(vw / w, vh / h);
      var blurPx = level / 100 * PHOTO_BLUR_PX;
      var filter = canvasFilter();
      var scale = Math.min(1, cover * dpr);
      if (blurPx > 0) scale = Math.min(scale, cover / Math.max(1, blurPx / (filter ? 4 : 1.6)));
      var cw = Math.max(1, Math.round(w * scale)), ch = Math.max(1, Math.round(h * scale));
      var canvas = document.createElement('canvas');
      canvas.width = cw;
      canvas.height = ch;
      var ctx = canvas.getContext('2d');
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, cw, ch);
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(image, 0, 0, cw, ch);
      // Measured sharp: the same cells whatever the blur (the editor's previews judge the floor on them too).
      var measured = measure(canvas);
      if (blurPx > 0 && filter) {
        // In canvas pixels: the canvas covers the window at max(vw/cw, vh/ch) CSS px each. Drawn a little larger than
        // the canvas, so the blur has picture to take from at the edges too (the sharp copy underneath backs them).
        var radius = blurPx / Math.max(vw / cw, vh / ch);
        var margin = Math.ceil(radius * 2);
        ctx.filter = 'blur(' + radius.toFixed(2) + 'px)';
        ctx.drawImage(image, -margin, -margin, cw + margin * 2, ch + margin * 2);
        ctx.filter = 'none';
      }
      if (image.close) image.close();
      return new Promise(function (resolve) {
        canvas.toBlob(function (out) {
          resolve(out ? { url: URL.createObjectURL(out), tone: measured.tone, cells: measured.cells } : null);
        }, 'image/jpeg', 0.9);
      });
    });
  }

  function photoLevel(level) { return Math.max(0, Math.min(100, Math.round(level || 0))); }

  // { url, tone, cells } of the picture at `src` with blur `level` (0..100): an object URL, its average color and its
  // 8×8 cells; null when it cannot be loaded (tried again next time).
  function photo(src, level) {
    level = photoLevel(level);
    var key = src + '|' + level;
    if (photoJobs[key]) return photoJobs[key];
    var job = loadPhoto(src)
      .then(function (blob) { return blob ? bakePhoto(blob, level) : null; })
      .then(null, function () { return null; });
    photoJobs[key] = job;
    job.then(function (result) {
      if (photoJobs[key] !== job) { if (result) URL.revokeObjectURL(result.url); return; }
      if (!result) { delete photoJobs[key]; return; }
      // Kept for the session: the page (and a chat's inline style from .NET) may still show any of them.
      photoUrls[key] = result;
    });
    return job;
  }

  function applyPhotos(state, theme) {
    var seq = ++photoSeq;
    var images = (state && state.images) || [];
    ['sky', 'chat'].forEach(function (slot) {
      var item = null;
      for (var i = 0; i < images.length; i++) {
        if (images[i] && images[i].slot === slot && images[i].theme === theme) item = images[i];
      }
      var prop = '--sm-' + slot + '-photo';
      var toneProp = '--sm-' + slot + '-tone';
      var floorProp = '--sm-' + slot + '-dim-min';
      var veilProp = '--sm-' + slot + '-veil';
      var veilOpacityProp = '--sm-' + slot + '-veil-opacity';
      var clearFloor = function () {
        root.style.removeProperty(floorProp);
        root.style.removeProperty(veilProp);
        root.style.removeProperty(veilOpacityProp);
      };
      if (!item || !item.src) {
        photoApplied[slot] = null;
        root.style.removeProperty(prop);
        root.style.removeProperty(toneProp);
        clearFloor();
        return;
      }
      // Per theme: the same picture may need another floor by day than at night.
      var key = item.src + '|' + photoLevel(item.blur) + '|' + theme;
      if (photoApplied[slot] === key && root.style.getPropertyValue(prop)) return;
      photo(item.src, item.blur).then(function (result) {
        if (seq !== photoSeq) return;
        photoApplied[slot] = result ? key : null;
        if (result) root.style.setProperty(prop, 'url("' + result.url + '")');
        else root.style.removeProperty(prop);
        if (result && result.tone) root.style.setProperty(toneProp, result.tone);
        else root.style.removeProperty(toneProp);
        var rows = result && item.floor ? screenVeilRows(result.cells, item.floor) : [];
        var strongest = rows.length ? Math.max.apply(null, rows) : 0;
        if (strongest > 0) {
          root.style.setProperty(floorProp, String(strongest));
          root.style.setProperty(veilProp, veilImage(rows));
          root.style.setProperty(veilOpacityProp, '1');
        } else {
          clearFloor();
        }
      });
    });
  }

  // What the state says about a theme beyond its CSS (ThemeCatalog's ThemeScene): { own, bar }, or null.
  function sceneOf(state, theme) {
    var scenes = (state && state.scenes) || [];
    for (var i = 0; i < scenes.length; i++) {
      if (scenes[i] && scenes[i].theme === theme) return scenes[i];
    }
    return null;
  }

  function apply(state) {
    if (state) lastState = state;
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
    // Backgrounds of one's own (html[data-sky-own] = "app chat"): the sky's lights, clouds and stars are not drawn
    // over them. The browser's bar takes the color at the top of the app's background.
    var scene = sceneOf(lastState, theme);
    if (scene && scene.own) root.setAttribute('data-sky-own', scene.own);
    else root.removeAttribute('data-sky-own');
    var meta = document.querySelector('meta[name="theme-color"]');
    if (!meta && document.head) {
      meta = document.createElement('meta');
      meta.setAttribute('name', 'theme-color');
      document.head.appendChild(meta);
    }
    if (meta) meta.setAttribute('content', scene && scene.bar ? scene.bar : theme === 'dark' ? '#150F38' : '#ECE6FF');
    try { applyPhotos(lastState, theme); } catch (e) { /* the colors are applied; pictures are a bonus */ }
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

  window.__smBoot = { applyTheme: apply, resolveTheme: resolve, photo: photo, themeKey: THEME_KEY, langKey: LANG_KEY };

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
