// SkyMessage browser platform layer (classic script, loaded before Blazor; no inline code — CSP).
// .NET calls window.skyPlatform.*; events flow back through a DotNetObjectReference (WebBridge).
(function () {
    'use strict';

    const DB_NAME = 'skymessage';
    const DB_VERSION = 1;
    const PREF_PREFIX = 'sm.p.';
    const SECURE_PREFIX = 'sm.s.';
    const LOCK_PREFIX = 'sm.lock.';
    const BLOB_MAX_BYTES = 512 * 1024 * 1024;
    const BLOB_MAX_AGE_MS = 2 * 60 * 60 * 1000;

    let dotnet = null;
    const pendingEvents = [];

    function emit(method, ...args) {
        if (dotnet) {
            dotnet.invokeMethodAsync(method, ...args).catch(() => { });
        } else {
            pendingEvents.push([method, args]);
        }
    }

    // ---- Utilities ------------------------------------------------------------------------------

    function safe(fn, fallback) {
        try { return fn(); } catch (e) { return fallback; }
    }

    function base64FromBlob(blob) {
        return new Promise((resolve) => {
            const reader = new FileReader();
            reader.onload = () => {
                const text = String(reader.result || '');
                const comma = text.indexOf(',');
                resolve(comma >= 0 ? text.substring(comma + 1) : null);
            };
            reader.onerror = () => resolve(null);
            reader.readAsDataURL(blob);
        });
    }

    function canvasToBlob(canvas, type, quality) {
        if (canvas.convertToBlob) return canvas.convertToBlob({ type, quality });
        return new Promise((resolve, reject) => canvas.toBlob(b => b ? resolve(b) : reject(new Error('encode failed')), type, quality));
    }

    function makeCanvas(width, height) {
        if (typeof OffscreenCanvas !== 'undefined') return new OffscreenCanvas(width, height);
        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        return canvas;
    }

    function fit(width, height, maxSide) {
        const longSide = Math.max(width, height);
        if (!maxSide || longSide <= maxSide) return { width, height, scaled: false };
        const scale = maxSide / longSide;
        return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)), scaled: true };
    }

    // ---- Blob registry (picked files, compressed results) ---------------------------------------

    const blobs = new Map();
    let blobSeq = 0;

    function registerBlob(blob, prefix) {
        const id = `${prefix || 'b'}:${Date.now().toString(36)}${(++blobSeq).toString(36)}`;
        blobs.set(id, { blob, at: Date.now() });
        evictBlobs();
        return id;
    }

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

    async function blobOf(ref) {
        if (!ref) return null;
        if (ref.startsWith('o:')) {
            const item = await idbGet('outbox', ref.substring(2));
            return item ? item.blob : null;
        }
        const entry = blobs.get(ref);
        if (entry) entry.at = Date.now();
        return entry ? entry.blob : null;
    }

    // ---- IndexedDB --------------------------------------------------------------------------------

    let dbPromise = null;

    function openDb() {
        if (dbPromise) return dbPromise;
        dbPromise = new Promise((resolve, reject) => {
            if (!('indexedDB' in self)) { reject(new Error('IndexedDB unavailable')); return; }
            const request = indexedDB.open(DB_NAME, DB_VERSION);
            request.onupgradeneeded = () => {
                const db = request.result;
                if (!db.objectStoreNames.contains('kv')) db.createObjectStore('kv');
                if (!db.objectStoreNames.contains('outbox')) db.createObjectStore('outbox');
            };
            request.onsuccess = () => {
                const db = request.result;
                db.onversionchange = () => { db.close(); dbPromise = null; };
                resolve(db);
            };
            request.onerror = () => { dbPromise = null; reject(request.error); };
            request.onblocked = () => { dbPromise = null; reject(new Error('IndexedDB blocked')); };
        });
        return dbPromise;
    }

    function tx(storeName, mode, work) {
        return openDb().then(db => new Promise((resolve, reject) => {
            const transaction = db.transaction(storeName, mode);
            const store = transaction.objectStore(storeName);
            let result;
            Promise.resolve(work(store, r => { result = r; })).catch(reject);
            transaction.oncomplete = () => resolve(result);
            transaction.onerror = () => reject(transaction.error);
            transaction.onabort = () => reject(transaction.error || new Error('aborted'));
        }));
    }

    function idbGet(storeName, key) {
        return tx(storeName, 'readonly', (store, set) => {
            const request = store.get(key);
            request.onsuccess = () => set(request.result === undefined ? null : request.result);
        });
    }

    function idbPut(storeName, key, value) {
        return tx(storeName, 'readwrite', store => { store.put(value, key); });
    }

    function idbDelete(storeName, key) {
        return tx(storeName, 'readwrite', store => { store.delete(key); });
    }

    // ---- Notifications (via the service worker: Chrome Android has no `new Notification()`) ----

    let swRegistration = null;

    function registerServiceWorker() {
        if (!('serviceWorker' in navigator) || !self.isSecureContext) return;
        navigator.serviceWorker.register('sw.js', { scope: './' })
            .then(reg => { swRegistration = reg; })
            .catch(() => { });
        navigator.serviceWorker.addEventListener('message', event => {
            const msg = event.data;
            if (msg && msg.type === 'sm-notification') emit('OnNotificationAction', msg.action || 'open', JSON.stringify(msg.data || {}));
        });
    }

    async function registration() {
        if (swRegistration) return swRegistration;
        if (!('serviceWorker' in navigator)) return null;
        try {
            const timeout = new Promise(resolve => setTimeout(() => resolve(null), 3000));
            swRegistration = await Promise.race([navigator.serviceWorker.ready, timeout]);
        } catch (e) {
            swRegistration = null;
        }
        return swRegistration;
    }

    function notificationPermission() {
        if (!('Notification' in self)) return 'unsupported';
        return Notification.permission;
    }

    // ---- Locks ------------------------------------------------------------------------------------

    const heldLocks = new Map();
    const lockOwner = Math.random().toString(36).substring(2) + Date.now().toString(36);

    function leaseAcquire(name, id) {
        const key = LOCK_PREFIX + name;
        return new Promise((resolve, reject) => {
            const state = { cancelled: false, renew: null, key };
            heldLocks.set(id, state);
            const attempt = () => {
                if (state.cancelled) { reject(new Error('AbortError')); return; }
                const now = Date.now();
                const current = safe(() => JSON.parse(localStorage.getItem(key) || 'null'), null);
                if (!current || current.until < now || current.owner === lockOwner + id) {
                    safe(() => localStorage.setItem(key, JSON.stringify({ owner: lockOwner + id, until: now + 8000 })));
                    setTimeout(() => {
                        const confirm = safe(() => JSON.parse(localStorage.getItem(key) || 'null'), null);
                        if (confirm && confirm.owner === lockOwner + id) {
                            state.renew = setInterval(() => safe(() => localStorage.setItem(key, JSON.stringify({ owner: lockOwner + id, until: Date.now() + 8000 }))), 2000);
                            resolve();
                        } else {
                            setTimeout(attempt, 100 + Math.random() * 100);
                        }
                    }, 40);
                } else {
                    setTimeout(attempt, 150 + Math.random() * 100);
                }
            };
            attempt();
        });
    }

    // ---- Media helpers ----------------------------------------------------------------------------

    async function imageBitmapOf(blob) {
        if (typeof createImageBitmap === 'function') {
            try { return await createImageBitmap(blob, { imageOrientation: 'from-image' }); } catch (e) { /* fall back */ }
        }
        return await new Promise((resolve, reject) => {
            const url = URL.createObjectURL(blob);
            const img = new Image();
            img.onload = () => { resolve(img); setTimeout(() => URL.revokeObjectURL(url), 1000); };
            img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('undecodable image')); };
            img.src = url;
        });
    }

    function sizeOf(source) {
        return { width: source.naturalWidth || source.videoWidth || source.width, height: source.naturalHeight || source.videoHeight || source.height };
    }

    async function previewOf(source, opaque) {
        const { width, height } = sizeOf(source);
        if (!width || !height) return null;
        const target = fit(width, height, 320);
        const canvas = makeCanvas(target.width, target.height);
        const ctx = canvas.getContext('2d');
        if (opaque) { ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, target.width, target.height); }
        ctx.drawImage(source, 0, 0, target.width, target.height);
        const blob = await canvasToBlob(canvas, 'image/jpeg', 0.7);
        return await base64FromBlob(blob);
    }

    function videoElementOf(blob) {
        return new Promise((resolve, reject) => {
            const url = URL.createObjectURL(blob);
            const video = document.createElement('video');
            video.preload = 'metadata';
            video.muted = true;
            video.playsInline = true;
            const timer = setTimeout(() => { cleanup(); reject(new Error('timeout')); }, 8000);
            const cleanup = () => { clearTimeout(timer); };
            video.onloadedmetadata = () => {
                const seekTo = Math.min(0.1, (video.duration || 0) / 2);
                if (!isFinite(seekTo) || seekTo <= 0) { cleanup(); resolve({ video, url }); return; }
                video.onseeked = () => { cleanup(); resolve({ video, url }); };
                try { video.currentTime = seekTo; } catch (e) { cleanup(); resolve({ video, url }); }
            };
            video.onerror = () => { cleanup(); URL.revokeObjectURL(url); reject(new Error('undecodable video')); };
            video.src = url;
        });
    }

    // ---- Device name ------------------------------------------------------------------------------

    function describeDevice() {
        const ua = navigator.userAgent || '';
        let browser = 'Browser';
        if (/Edg\//.test(ua)) browser = 'Edge';
        else if (/OPR\//.test(ua)) browser = 'Opera';
        else if (/YaBrowser\//.test(ua)) browser = 'Yandex Browser';
        else if (/SamsungBrowser\//.test(ua)) browser = 'Samsung Internet';
        else if (/Firefox\//.test(ua)) browser = 'Firefox';
        else if (/Chrome\//.test(ua)) browser = 'Chrome';
        else if (/Safari\//.test(ua)) browser = 'Safari';

        let os = null, version = null;
        let m;
        if ((m = ua.match(/Android ([\d.]+)/))) { os = 'Android'; version = m[1]; }
        else if ((m = ua.match(/(iPhone|iPad|iPod).*? OS ([\d_]+)/))) { os = m[1]; version = m[2].replace(/_/g, '.'); }
        else if ((m = ua.match(/Windows NT ([\d.]+)/))) { os = 'Windows'; version = m[1]; }
        else if ((m = ua.match(/Mac OS X ([\d_]+)/))) { os = navigator.maxTouchPoints > 1 ? 'iPad' : 'macOS'; version = m[1].replace(/_/g, '.'); }
        else if (/CrOS/.test(ua)) os = 'ChromeOS';
        else if (/Linux/.test(ua)) os = 'Linux';

        const mobile = /Android|iPhone|iPad|iPod|Mobile/i.test(ua) || (navigator.maxTouchPoints > 1 && /Mac OS X/.test(ua));
        return { deviceName: os ? `${browser} · ${os}` : browser, osVersion: version, isMobile: mobile };
    }

    function countryFromLanguages() {
        const languages = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language || ''];
        for (const language of languages) {
            const parts = String(language).split('-');
            if (parts.length >= 2 && /^[A-Za-z]{2}$/.test(parts[parts.length - 1])) return parts[parts.length - 1].toUpperCase();
        }
        return null;
    }

    function connectionInfo() {
        const c = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
        return {
            online: navigator.onLine !== false,
            type: c && c.type ? String(c.type) : 'unknown',
            metered: !!(c && (c.saveData || c.type === 'cellular' || c.metered)),
        };
    }

    // ---- Media session (call controls on lock screen / headsets, Chrome) ----------------------------

    let wakeLock = null;

    function setMediaSessionHandlers(active) {
        if (!('mediaSession' in navigator)) return;
        const actions = ['togglemicrophone', 'togglecamera', 'hangup'];
        for (const action of actions) {
            try { navigator.mediaSession.setActionHandler(action, active ? () => emit('OnMediaSessionAction', action) : null); } catch (e) { /* unsupported action */ }
        }
    }

    // ---- Public API -------------------------------------------------------------------------------

    const api = {
        init(ref) {
            dotnet = ref;
            const device = describeDevice();
            const connection = connectionInfo();
            let callAction = null;
            try {
                const params = new URLSearchParams(location.search);
                const value = params.get('smcall');
                if (value) {
                    callAction = value;
                    params.delete('smcall');
                    const rest = params.toString();
                    history.replaceState(history.state, '', location.pathname + (rest ? '?' + rest : '') + location.hash);
                }
            } catch (e) { /* ignore */ }
            const queued = pendingEvents.splice(0);
            setTimeout(() => queued.forEach(([method, args]) => emit(method, ...args)), 0);
            return {
                initialUrl: location.href,
                online: connection.online,
                connectionType: connection.type,
                metered: connection.metered,
                visible: document.visibilityState !== 'hidden',
                deviceName: device.deviceName,
                osVersion: device.osVersion,
                isMobile: device.isMobile,
                countryCode: countryFromLanguages(),
                callAction,
                canShare: typeof navigator.share === 'function',
                hasContactPicker: 'contacts' in navigator && 'ContactsManager' in self,
                canVibrate: typeof navigator.vibrate === 'function' && device.isMobile,
            };
        },

        prefs: {
            get: key => safe(() => localStorage.getItem(PREF_PREFIX + key), null),
            set: (key, value) => safe(() => value === null || value === undefined ? localStorage.removeItem(PREF_PREFIX + key) : localStorage.setItem(PREF_PREFIX + key, value)),
        },

        secure: {
            get: key => safe(() => localStorage.getItem(SECURE_PREFIX + key), null),
            set: (key, value) => safe(() => localStorage.setItem(SECURE_PREFIX + key, value)),
            remove: key => safe(() => localStorage.removeItem(SECURE_PREFIX + key)),
        },

        cache: {
            read: key => idbGet('kv', key).then(v => typeof v === 'string' ? v : null),
            write: (key, value) => idbPut('kv', key, value),
            del: key => idbDelete('kv', key),
            clear: prefix => tx('kv', 'readwrite', store => {
                if (!prefix) { store.clear(); return; }
                store.delete(IDBKeyRange.bound(prefix, prefix + '￿'));
            }),
            size: () => tx('kv', 'readonly', (store, set) => {
                let total = 0;
                const request = store.openCursor();
                request.onsuccess = () => {
                    const cursor = request.result;
                    if (!cursor) { set(total); return; }
                    total += (typeof cursor.value === 'string' ? cursor.value.length : 0) + String(cursor.key).length;
                    cursor.continue();
                };
            }),
        },

        outbox: {
            async persistRef(itemKey, ref, name, type) {
                const blob = await blobOf(ref);
                if (!blob) return null;
                await idbPut('outbox', itemKey, { name, type, size: blob.size, blob });
                return blob.size;
            },
            async persistStream(itemKey, streamRef, name, type) {
                const buffer = await streamRef.arrayBuffer();
                const blob = new Blob([buffer], { type: type || 'application/octet-stream' });
                await idbPut('outbox', itemKey, { name, type, size: blob.size, blob });
                return blob.size;
            },
            async info(itemKey) {
                const item = await idbGet('outbox', itemKey);
                return item ? { name: item.name, type: item.type, size: item.size } : null;
            },
            del: itemKey => idbDelete('outbox', itemKey),
            size: () => tx('outbox', 'readonly', (store, set) => {
                let total = 0;
                const request = store.openCursor();
                request.onsuccess = () => {
                    const cursor = request.result;
                    if (!cursor) { set(total); return; }
                    total += (cursor.value && cursor.value.size) || 0;
                    cursor.continue();
                };
            }),
        },

        blobs: {
            async open(ref) {
                const blob = await blobOf(ref);
                if (!blob) throw new Error('blob not found');
                return blob;
            },
            async fromStream(streamRef, type) {
                const buffer = await streamRef.arrayBuffer();
                return registerBlob(new Blob([buffer], { type: type || 'application/octet-stream' }), 's');
            },
            release: ref => { blobs.delete(ref); },
        },

        files: {
            pick(accept, multiple, capture) {
                return new Promise(resolve => {
                    const input = document.createElement('input');
                    input.type = 'file';
                    if (accept) input.accept = accept;
                    input.multiple = !!multiple;
                    if (capture) input.setAttribute('capture', capture);
                    input.style.position = 'fixed';
                    input.style.left = '-10000px';
                    input.style.opacity = '0';
                    let settled = false;
                    const finish = files => {
                        if (settled) return;
                        settled = true;
                        window.removeEventListener('focus', onFocus);
                        input.remove();
                        resolve(files.map(file => ({
                            ref: registerBlob(file, 'f'),
                            name: file.name || 'file',
                            type: file.type || 'application/octet-stream',
                            size: file.size,
                        })));
                    };
                    // Browsers without the input "cancel" event: treat "focus came back, still nothing picked" as a
                    // cancel, after a grace period (cloud-backed picks can deliver "change" seconds later).
                    const onFocus = () => setTimeout(() => { if (!input.files || input.files.length === 0) finish([]); }, 4000);
                    input.addEventListener('change', () => finish(Array.from(input.files || [])));
                    input.addEventListener('cancel', () => finish([]));
                    window.addEventListener('focus', onFocus);
                    document.body.appendChild(input);
                    input.click();
                });
            },
        },

        media: {
            async probe(ref) {
                const blob = await blobOf(ref);
                if (!blob) return null;
                const type = blob.type || '';
                try {
                    if (type.startsWith('image/')) {
                        const image = await imageBitmapOf(blob);
                        const { width, height } = sizeOf(image);
                        const preview = await previewOf(image, true);
                        if (image.close) image.close();
                        return { width, height, durationMs: null, preview };
                    }
                    if (type.startsWith('video/')) {
                        const { video, url } = await videoElementOf(blob);
                        const result = {
                            width: video.videoWidth || null,
                            height: video.videoHeight || null,
                            durationMs: isFinite(video.duration) ? Math.round(video.duration * 1000) : null,
                            preview: video.videoWidth ? await previewOf(video, true).catch(() => null) : null,
                        };
                        URL.revokeObjectURL(url);
                        return result;
                    }
                } catch (e) { /* undecodable: no preview */ }
                return { width: null, height: null, durationMs: null, preview: null };
            },
            async compressImage(ref, maxSide, quality, keepPng) {
                const blob = await blobOf(ref);
                if (!blob) return null;
                const image = await imageBitmapOf(blob);
                const { width, height } = sizeOf(image);
                const target = fit(width, height, maxSide);
                const canvas = makeCanvas(target.width, target.height);
                const ctx = canvas.getContext('2d');
                const type = keepPng ? 'image/png' : 'image/jpeg';
                if (!keepPng) { ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, target.width, target.height); }
                ctx.imageSmoothingEnabled = true;
                ctx.imageSmoothingQuality = 'high';
                ctx.drawImage(image, 0, 0, target.width, target.height);
                if (image.close) image.close();
                const output = await canvasToBlob(canvas, type, Math.min(1, Math.max(0.01, quality / 100)));
                const preview = await previewOf(canvas, true).catch(() => null);
                return { ref: registerBlob(output, 'c'), type, size: output.size, width: target.width, height: target.height, preview };
            },
        },

        notify: {
            permission: () => notificationPermission(),
            async request() {
                if (!('Notification' in self)) return 'unsupported';
                try { return await Notification.requestPermission(); } catch (e) { return Notification.permission; }
            },
            async show(options) {
                if (notificationPermission() !== 'granted') return false;
                const reg = await registration();
                // Browsers show at most Notification.maxActions buttons (Chrome: 2). Actions arrive in priority
                // order; "decline" must always survive the cut.
                let actions = options.actions || [];
                const maxActions = (typeof Notification !== 'undefined' && Notification.maxActions) || 2;
                if (actions.length > maxActions) {
                    const decline = actions.find(a => a.action === 'decline');
                    actions = actions.filter(a => a !== decline).slice(0, Math.max(0, maxActions - (decline ? 1 : 0)));
                    if (decline) actions.push(decline);
                }
                const payload = {
                    body: options.body || '',
                    tag: options.tag,
                    data: options.data || {},
                    icon: 'icons/icon-192.png',
                    badge: 'icons/badge-96.png',
                    silent: !!options.silent,
                    renotify: !!options.tag && !options.silent,
                    requireInteraction: !!options.requireInteraction,
                    actions,
                    vibrate: options.vibrate || undefined,
                };
                try {
                    if (reg && reg.showNotification) { await reg.showNotification(options.title || 'SkyMessage', payload); return true; }
                    delete payload.actions;
                    const notification = new Notification(options.title || 'SkyMessage', payload);
                    notification.onclick = () => { window.focus(); emit('OnNotificationAction', 'open', JSON.stringify(payload.data)); notification.close(); };
                    return true;
                } catch (e) {
                    return false;
                }
            },
            async closeTag(tag) {
                const reg = await registration();
                if (!reg || !reg.getNotifications) return;
                try { (await reg.getNotifications({ tag })).forEach(n => n.close()); } catch (e) { /* ignore */ }
            },
            badge(count) {
                try {
                    if (count > 0 && navigator.setAppBadge) navigator.setAppBadge(count).catch(() => { });
                    else if (navigator.clearAppBadge) navigator.clearAppBadge().catch(() => { });
                } catch (e) { /* unsupported */ }
            },
        },

        perm: {
            async query(name) {
                if (!navigator.permissions || !navigator.permissions.query) return 'unsupported';
                try { return (await navigator.permissions.query({ name })).state; } catch (e) { return 'unsupported'; }
            },
            async requestMedia(audio, video) {
                if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return 'unsupported';
                try {
                    const stream = await navigator.mediaDevices.getUserMedia({ audio: !!audio, video: !!video });
                    stream.getTracks().forEach(t => t.stop());
                    return 'granted';
                } catch (e) {
                    return e && (e.name === 'NotAllowedError' || e.name === 'SecurityError') ? 'denied' : 'unsupported';
                }
            },
        },

        share: {
            async text(text, title) {
                if (typeof navigator.share !== 'function') return false;
                try { await navigator.share({ text, title: title || undefined }); return true; } catch (e) { return e && e.name === 'AbortError'; }
            },
            async copy(text) {
                try {
                    if (navigator.clipboard && navigator.clipboard.writeText) { await navigator.clipboard.writeText(text); return true; }
                } catch (e) { /* fall back */ }
                try {
                    const area = document.createElement('textarea');
                    area.value = text;
                    area.setAttribute('readonly', '');
                    area.style.position = 'fixed';
                    area.style.left = '-10000px';
                    document.body.appendChild(area);
                    area.select();
                    const ok = document.execCommand('copy');
                    area.remove();
                    return ok;
                } catch (e) {
                    return false;
                }
            },
        },

        download: {
            async fromStream(streamRef, name, type) {
                const buffer = await streamRef.arrayBuffer();
                const blob = new Blob([buffer], { type: type || 'application/octet-stream' });
                const url = URL.createObjectURL(blob);
                const anchor = document.createElement('a');
                anchor.href = url;
                anchor.download = name || 'file';
                anchor.rel = 'noopener';
                anchor.style.display = 'none';
                document.body.appendChild(anchor);
                anchor.click();
                anchor.remove();
                setTimeout(() => URL.revokeObjectURL(url), 60000);
                return true;
            },
        },

        open: {
            url(url) {
                const anchor = document.createElement('a');
                anchor.href = url;
                anchor.target = '_blank';
                anchor.rel = 'noopener noreferrer';
                anchor.style.display = 'none';
                document.body.appendChild(anchor);
                anchor.click();
                anchor.remove();
            },
        },

        ui: {
            themeColor(hex) {
                let meta = document.querySelector('meta[name="theme-color"]');
                if (!meta) { meta = document.createElement('meta'); meta.name = 'theme-color'; document.head.appendChild(meta); }
                meta.setAttribute('content', hex);
            },
        },

        haptics: {
            vibrate(pattern) { try { if (navigator.vibrate) navigator.vibrate(pattern); } catch (e) { /* ignore */ } },
        },

        locks: {
            acquire(name, id) {
                if (navigator.locks && navigator.locks.request) {
                    return new Promise((resolve, reject) => {
                        const controller = new AbortController();
                        const state = { controller, release: null };
                        heldLocks.set(id, state);
                        navigator.locks.request(name, { signal: controller.signal }, () => new Promise(release => {
                            state.release = release;
                            resolve();
                        })).catch(error => { heldLocks.delete(id); reject(error); });
                    });
                }
                return leaseAcquire(name, id);
            },
            release(id) {
                const state = heldLocks.get(id);
                if (!state) return;
                heldLocks.delete(id);
                if (state.release) state.release();
                if (state.renew) clearInterval(state.renew);
                if (state.key) {
                    const current = safe(() => JSON.parse(localStorage.getItem(state.key) || 'null'), null);
                    if (current && current.owner === lockOwner + id) safe(() => localStorage.removeItem(state.key));
                }
            },
            abort(id) {
                const state = heldLocks.get(id);
                if (!state) return;
                if (state.controller && !state.release) state.controller.abort();
                if (state.key !== undefined) state.cancelled = true;
                api.locks.release(id);
            },
        },

        contacts: {
            async select() {
                if (!('contacts' in navigator) || !navigator.contacts.select) return [];
                try {
                    const items = await navigator.contacts.select(['name', 'tel'], { multiple: true });
                    return items.map(c => ({ name: (c.name && c.name[0]) || '', tel: c.tel || [] }));
                } catch (e) {
                    return [];
                }
            },
        },

        call: {
            async start(video) {
                setMediaSessionHandlers(true);
                if (video && navigator.wakeLock && !wakeLock) {
                    try { wakeLock = await navigator.wakeLock.request('screen'); wakeLock.addEventListener('release', () => { wakeLock = null; }); } catch (e) { wakeLock = null; }
                }
            },
            end() {
                setMediaSessionHandlers(false);
                if (wakeLock) { wakeLock.release().catch(() => { }); wakeLock = null; }
            },
        },
    };

    // ---- Event wiring -------------------------------------------------------------------------------

    function reportNetwork() {
        const info = connectionInfo();
        emit('OnNetworkChanged', info.online, info.type, info.metered);
    }

    window.addEventListener('online', reportNetwork);
    window.addEventListener('offline', reportNetwork);
    const connection = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
    if (connection && connection.addEventListener) connection.addEventListener('change', reportNetwork);

    document.addEventListener('visibilitychange', () => emit('OnVisibilityChanged', document.visibilityState !== 'hidden'));
    window.addEventListener('pagehide', () => emit('OnVisibilityChanged', false));
    window.addEventListener('pageshow', () => emit('OnVisibilityChanged', document.visibilityState !== 'hidden'));

    registerServiceWorker();

    window.skyPlatform = api;
})();
