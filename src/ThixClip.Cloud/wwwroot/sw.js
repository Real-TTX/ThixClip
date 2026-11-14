/* ThixClip.Cloud service worker: the app as a PWA - small on purpose.
   - The shell (toolkit, styles, scripts, icons, the offline page): from the cache first, refreshed in the background.
   - The pages of the history and of a clip come from the network, and a copy is kept: without a network the last copy is shown (marked as a copy,
     offline.js shows "Offline"), any other page shows the offline page with a button to try again (it also tries by itself).
   - The previews of pictures are kept (they never change); a full picture that was opened is kept too.
   - The long poll (/live/revision), the API of the devices (/api), every change (POST) and the sign-in are never touched.
   - Signing out clears the copies (pwa.js sends "tc-clear"). */
const VERSION = 'thixclip-v1';
const PAGES = 'tc-pages';
const IMAGES = 'tc-images';
const SHELL = [
  '/offline.html', '/js/offline.js', '/js/app.js', '/js/live.js', '/js/pwa.js', '/css/app.css',
  '/matui/matui.css', '/matui/matui.js', '/matui/matui.boot.js',
  '/img/favicon.svg', '/img/logo.svg', '/img/icon-192.png', '/img/icon-512.png', '/manifest.webmanifest',
];
const MAX_PAGES = 30;
const MAX_IMAGES = 400;

self.addEventListener('install', event => {
  event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

// an older version of the shell goes (the copies of pages and pictures stay: they are the person's until the sign-out)
self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== VERSION && k !== PAGES && k !== IMAGES).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('message', event => {
  if (event.data === 'tc-clear') {
    event.waitUntil(Promise.all([caches.delete(PAGES), caches.delete(IMAGES)]));
  }
});

function isStatic(url) {
  return /^\/(matui|css|js|img)\//.test(url.pathname) || url.pathname === '/manifest.webmanifest' || url.pathname === '/offline.html';
}

function isKeptPage(url) {
  return /^\/Clips(\/Edit)?$/i.test(url.pathname) || url.pathname === '/';
}

function trim(cache, max) {
  return cache.keys().then(keys => (keys.length > max ? cache.delete(keys[0]).then(() => trim(cache, max)) : undefined));
}

/** A page that comes from the copy says so (<html data-offline-copy>), so the page can show it. */
function markCopy(response) {
  return response.text().then(html => new Response(html.replace('<html ', '<html data-offline-copy="true" '), {
    status: 200,
    headers: { 'Content-Type': 'text/html; charset=utf-8' },
  }));
}

function offlinePage(url) {
  return caches.match('/offline.html', { ignoreSearch: true }).then(hit => {
    if (!hit) { return Response.error(); }
    return hit.text().then(html => new Response(html.replace('data-from=""', 'data-from="' + url.pathname.replace(/"/g, '') + '"'), {
      status: 503,
      headers: { 'Content-Type': 'text/html; charset=utf-8' },
    }));
  });
}

/** A page from the network (a copy is kept), without one the last copy, without that the offline page. */
function page(request, url) {
  return fetch(request).then(response => {
    // only a real page of the person is kept (not the sign-in a redirect led to)
    if (response.ok && !response.redirected && isKeptPage(url)) {
      const copy = response.clone();
      caches.open(PAGES).then(cache => cache.put(request, copy).then(() => trim(cache, MAX_PAGES)));
    }
    return response;
  }).catch(() => caches.open(PAGES)
    .then(cache => cache.match(request))
    .then(hit => (hit ? markCopy(hit) : offlinePage(url))));
}

/** From the cache first, refreshed in the background (the shell). A versioned address (?v=) also finds the copy of the plain one. */
function shell(request) {
  return caches.open(VERSION).then(cache => cache.match(request).then(hit => {
    const fresh = fetch(request).then(response => {
      if (response.ok) { cache.put(request, response.clone()); }
      return response;
    }).catch(() => hit || cache.match(request, { ignoreSearch: true }));
    return hit || fresh;
  }));
}

/** A picture of a clip: from the cache first (it never changes), else from the network (and kept). */
function image(request) {
  return caches.open(IMAGES).then(cache => cache.match(request).then(hit => hit || fetch(request).then(response => {
    if (response.ok) { cache.put(request, response.clone()).then(() => trim(cache, MAX_IMAGES)); }
    return response;
  })));
}

self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname === '/sw.js' || url.pathname === '/health') return;
  if (url.pathname.startsWith('/api/') || url.pathname === '/live/revision') return;

  if (/^\/live\/clips\/\d+\/(thumbnail|image)$/.test(url.pathname) && !url.searchParams.has('download')) {
    event.respondWith(image(request));
    return;
  }

  if (request.mode === 'navigate') {
    if (url.pathname.startsWith('/Account/Login') || url.pathname.startsWith('/Account/Setup')) {
      event.respondWith(fetch(request).catch(() => offlinePage(url)));
      return;
    }
    event.respondWith(page(request, url));
    return;
  }

  if (isStatic(url)) {
    event.respondWith(shell(request));
  }
});
