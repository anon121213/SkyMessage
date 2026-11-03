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
  // (store "files", { type, blob }, written by web-platform.js) — and drawn once onto a canvas: always a still, opaque
  // picture (a GIF shows its first frame) with its blur baked in, so nothing is filtered while the page scrolls. The
  // result stays an object URL for the session, with the picture's average color (its tone: the chat screen's edges
  // fade into it); the page shows them through --sm-sky-photo / --sm-chat-photo and --sm-sky-tone / --sm-chat-tone.
  var PHOTO_BLUR_PX = 28;
  var PHOTO_CACHE = 16;
  var photoJobs = {};
  var photoUrls = {};
  var photoOrder = [];
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

  // The average color of a picture (drawn down to 8×8 and averaged), "#rrggbb"; null when it cannot be read.
  function toneOf(source) {
    try {
      var c = document.createElement('canvas');
      c.width = 8;
      c.height = 8;
      var g = c.getContext('2d');
      g.imageSmoothingEnabled = true;
      g.imageSmoothingQuality = 'high';
      g.drawImage(source, 0, 0, 8, 8);
      var d = g.getImageData(0, 0, 8, 8).data, r = 0, gr = 0, b = 0;
      for (var i = 0; i < d.length; i += 4) { r += d[i]; gr += d[i + 1]; b += d[i + 2]; }
      return '#' + hex2(r / 64) + hex2(gr / 64) + hex2(b / 64);
    } catch (e) { return null; }
  }

  // A sharp JPEG is shown as stored (it was downscaled to the screen when picked): only a small copy is decoded, for
  // its tone.
  function sharpPhoto(blob) {
    var url = URL.createObjectURL(blob);
    var small = typeof window.createImageBitmap === 'function'
      ? window.createImageBitmap(blob, { resizeWidth: 32, resizeHeight: 32, resizeQuality: 'medium' }).catch(function () { return imageOf(blob); })
      : imageOf(blob);
    return small.then(function (image) {
      var tone = toneOf(image);
      if (image.close) image.close();
      return { url: url, tone: tone };
    }, function () { return { url: url, tone: null }; });
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
      var tone = toneOf(canvas);
      return new Promise(function (resolve) {
        canvas.toBlob(function (out) { resolve(out ? { url: URL.createObjectURL(out), tone: tone } : null); }, 'image/jpeg', 0.9);
      });
    });
  }

  function photoLevel(level) { return Math.max(0, Math.min(100, Math.round(level || 0))); }

  function touchPhoto(key) {
    var index = photoOrder.indexOf(key);
    if (index >= 0) photoOrder.splice(index, 1);
    photoOrder.push(key);
  }

  function trimPhotos() {
    var index = 0;
    while (photoOrder.length > PHOTO_CACHE && index < photoOrder.length) {
      var key = photoOrder[index];
      if (photoApplied.sky === key || photoApplied.chat === key) { index++; continue; }
      photoOrder.splice(index, 1);
      if (photoUrls[key]) URL.revokeObjectURL(photoUrls[key].url);
      delete photoUrls[key];
      delete photoJobs[key];
    }
  }

  // { url, tone } of the picture at `src` with blur `level` (0..100): an object URL and its average color; null when
  // it cannot be loaded (tried again next time).
  function photo(src, level) {
    level = photoLevel(level);
    var key = src + '|' + level;
    if (photoJobs[key]) {
      if (photoUrls[key]) touchPhoto(key);
      return photoJobs[key];
    }
    var job = loadPhoto(src)
      .then(function (blob) { return blob ? bakePhoto(blob, level) : null; })
      .then(null, function () { return null; });
    photoJobs[key] = job;
    job.then(function (result) {
      if (photoJobs[key] !== job) { if (result) URL.revokeObjectURL(result.url); return; }
      if (!result) { delete photoJobs[key]; return; }
      photoUrls[key] = result;
      touchPhoto(key);
      trimPhotos();
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
      if (!item || !item.src) {
        photoApplied[slot] = null;
        root.style.removeProperty(prop);
        root.style.removeProperty(toneProp);
        return;
      }
      var key = item.src + '|' + photoLevel(item.blur);
      if (photoApplied[slot] === key && root.style.getPropertyValue(prop)) return;
      photo(item.src, item.blur).then(function (result) {
        if (seq !== photoSeq) return;
        photoApplied[slot] = result ? key : null;
        if (result) root.style.setProperty(prop, 'url("' + result.url + '")');
        else root.style.removeProperty(prop);
        if (result && result.tone) root.style.setProperty(toneProp, result.tone);
        else root.style.removeProperty(toneProp);
      });
    });
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
    var meta = document.querySelector('meta[name="theme-color"]');
    if (!meta && document.head) {
      meta = document.createElement('meta');
      meta.setAttribute('name', 'theme-color');
      document.head.appendChild(meta);
    }
    if (meta) meta.setAttribute('content', theme === 'dark' ? '#150F38' : '#ECE6FF');
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
