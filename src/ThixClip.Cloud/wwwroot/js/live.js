/* ThixClip.Cloud: the history updates itself.
   The list ([data-live], with the revision it shows in data-live-revision) waits on /live/revision?since=N (a long poll: the server answers as soon as
   another device, the browser or the retention changed something, or after 25 s). A newer revision loads the same address again and replaces the
   rows ([data-live-region]) and the count of the toolbar; the search field, open dialogs and the scroll position stay as they are.
   Without a network it waits for the "online" event and then loads at once, so nothing stale stays on the screen. After a sign-out (401) it stops. */
(function () {
  'use strict';

  var root = document.querySelector('[data-live]');
  if (!root || !window.fetch) { return; }

  var revision = Number(root.getAttribute('data-live-revision') || '0');
  var failures = 0;
  var stopped = false;
  var running = false;

  function sleep(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }

  function busy() {
    // never swap the rows under an open dialog or a menu the person is using
    return !!document.querySelector('dialog[open], .mat-menu:not([hidden])');
  }

  function refresh() {
    return fetch(location.href, { credentials: 'same-origin', headers: { 'Accept': 'text/html' }, cache: 'no-store' }).then(function (r) {
      if (r.status === 401 || r.redirected && /\/Account\/Login/.test(r.url)) { stopped = true; return; }
      if (!r.ok) { throw new Error('HTTP ' + r.status); }
      return r.text().then(function (html) {
        var next = new DOMParser().parseFromString(html, 'text/html');
        var fresh = next.querySelector('[data-live]');
        if (!fresh) { return; }
        var region = root.querySelector('[data-live-region]');
        var freshRegion = fresh.querySelector('[data-live-region]');
        if (region && freshRegion) {
          region.replaceWith(document.importNode(freshRegion, true));
        }
        var info = root.querySelector('.mat-toolbar__info');
        var freshInfo = fresh.querySelector('.mat-toolbar__info');
        if (info && freshInfo) { info.textContent = freshInfo.textContent; }
        revision = Math.max(revision, Number(fresh.getAttribute('data-live-revision') || '0'));
        root.setAttribute('data-live-revision', String(revision));
        if (window.MatUi && window.MatUi.refresh) { window.MatUi.refresh(root); }
      });
    });
  }

  function loop() {
    if (running) { return; }
    running = true;
    (function next() {
      if (stopped) { running = false; return; }
      if (navigator.onLine === false) { running = false; return; }
      fetch('/live/revision?since=' + revision + '&timeout=25', { credentials: 'same-origin', headers: { 'Accept': 'application/json' }, cache: 'no-store' })
        .then(function (r) {
          if (r.status === 401) { stopped = true; return null; }
          if (!r.ok) { throw new Error('HTTP ' + r.status); }
          return r.json();
        })
        .then(function (answer) {
          failures = 0;
          if (!answer || answer.latest <= revision) { return; }
          var wait = function () { return busy() ? sleep(1500).then(wait) : refresh(); };
          return wait();
        })
        .catch(function () {
          failures++;
          return sleep(Math.min(30000, 1000 * Math.pow(2, Math.min(failures, 5))));
        })
        .then(next);
    })();
  }

  // back online: load what changed meanwhile at once, then wait again
  window.addEventListener('online', function () { refresh().catch(function () { /* the loop tries again */ }).then(loop); });
  // a tab that comes back from the background checks at once (a phone may have frozen the request)
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'visible') { refresh().catch(function () { /* the loop tries again */ }); }
  });
  loop();
})();
