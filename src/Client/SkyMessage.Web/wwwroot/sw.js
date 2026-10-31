// SkyMessage service worker: notifications only. It deliberately has no fetch handler and caches nothing,
// so every page load gets the current app from the server (no stale versions after an update).
'use strict';

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('notificationclick', event => {
    const notification = event.notification;
    const data = notification.data || {};
    const action = event.action || 'open';
    notification.close();

    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const client = windows.find(c => new URL(c.url).origin === self.location.origin);
        const message = { type: 'sm-notification', action, data };
        if (client) {
            try { await client.focus(); } catch (e) { /* focus may be refused */ }
            client.postMessage(message);
            return;
        }
        // No open window: start the app; calls carry the chosen action in the URL, messages open the chat.
        let url = new URL('./', self.registration.scope);
        if (data.kind === 'call' && data.callId) url.searchParams.set('smcall', `${action}:${data.callId}`);
        else if (data.url) url = new URL(data.url, self.registration.scope);
        await self.clients.openWindow(url.href);
    })());
});
