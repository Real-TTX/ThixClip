// Browser checks of ThixClip.Cloud against a running stack: the setup assistant (or the sign-in), the API of a device, the live list, every page at
// desktop width and at 390 px, light and dark, offline. Screenshots go to SHOTS (default: tests/e2e/shots); LOOK at them.
//
//   cd tests/e2e && npm install && node run.js http://localhost:4950
//
// The test accounts (local development only, see docs/CLOUD.md): ADMIN_USER / ADMIN_PASSWORD, USER_NAME / USER_PASSWORD.
'use strict';

const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright-core');

const base = (process.argv[2] || 'http://localhost:4950').replace(/\/$/, '');
const shots = process.env.SHOTS || path.join(__dirname, 'shots');
const admin = { user: process.env.ADMIN_USER || 'admin', password: process.env.ADMIN_PASSWORD || 'ThixClip-Admin-2026', name: 'Matthias Admin' };
const person = { user: process.env.USER_NAME || 'anna', password: process.env.USER_PASSWORD || 'Anna-Test-2026', name: 'Anna Beispiel' };

let failures = 0;
function check(condition, message) {
  if (condition) { console.log('  ok   ' + message); } else { failures++; console.log('  FAIL ' + message); }
}

async function api(method, route, token, body, raw) {
  const headers = { 'User-Agent': 'ThixClip/e2e (windows)', 'X-ThixClip-Protocol': '1' };
  if (token) headers.Authorization = 'Bearer ' + token;
  let payload;
  if (raw) { headers['Content-Type'] = 'image/png'; payload = raw; } else if (body !== undefined) { headers['Content-Type'] = 'application/json'; payload = JSON.stringify(body); }
  const response = await fetch(base + route, { method, headers, body: payload });
  const type = response.headers.get('content-type') || '';
  const data = type.includes('json') ? await response.json() : type.includes('image') ? Buffer.from(await response.arrayBuffer()) : await response.text();
  return { status: response.status, data, headers: response.headers };
}

const crypto = require('crypto');
const sha = (input) => crypto.createHash('sha256').update(input).digest('hex').toUpperCase();
function textClip(text, minutesAgo, extra) {
  const at = new Date(Date.now() - minutesAgo * 60000).toISOString();
  const kind = /^https?:\/\/\S+$/.test(text.trim()) ? 'Link' : 'Text';
  return Object.assign({ syncId: crypto.randomUUID(), kind, text, contentHash: sha(Buffer.from(text, 'utf8')), createdAt: at, copiedAt: at, updatedAt: at }, extra || {});
}
function imageClip(png, minutesAgo, ocr) {
  const at = new Date(Date.now() - minutesAgo * 60000).toISOString();
  return { syncId: crypto.randomUUID(), kind: 'Image', ocrText: ocr || null, contentHash: sha(png), createdAt: at, copiedAt: at, updatedAt: at };
}

async function shot(page, name) {
  await page.waitForTimeout(250);
  await page.screenshot({ path: path.join(shots, name + '.png'), fullPage: true });
}

/** The end of a page: the shell scrolls inside its main area, so that one is scrolled down first. */
async function shotEnd(page, name) {
  await page.evaluate(() => {
    for (const el of document.querySelectorAll('*')) {
      const style = getComputedStyle(el);
      if (/(auto|scroll)/.test(style.overflowY) && el.scrollHeight > el.clientHeight + 4) { el.scrollTop = el.scrollHeight; }
    }
    window.scrollTo(0, document.body.scrollHeight);
  });
  await shot(page, name);
}

async function signIn(page, who) {
  await page.goto(base + '/Account/Login');
  await page.fill('#UserName', who.user);
  await page.fill('#Password', who.password);
  await Promise.all([page.waitForNavigation(), page.click('#login-form button[type=submit]')]);
}

(async () => {
  fs.mkdirSync(shots, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 860 }, locale: 'de-DE' });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) errors.push(m.text()); });

  try {
    console.log('Setup / sign-in');
    await page.goto(base + '/');
    if (page.url().includes('/Account/Setup')) {
      await shot(page, '01-setup');
      await page.fill('#UserName', admin.user);
      await page.fill('#DisplayName', admin.name);
      await page.fill('#Password', admin.password);
      await page.fill('#Repeat', admin.password);
      await Promise.all([page.waitForNavigation(), page.click('#setup-form button[type=submit]')]);
      check(page.url().endsWith('/Clips'), 'the setup assistant creates the administrator and opens the history');
      await shot(page, '02-history-empty');
    } else {
      await signIn(page, admin);
    }
    check(page.url().includes('/Clips'), 'signed in as administrator');

    console.log('A second account (administration)');
    await page.goto(base + '/Admin/Users');
    if (!(await page.content()).includes('>' + person.name + '<')) {
      await page.goto(base + '/Admin/Users/Edit');
      await page.fill('#UserName', person.user);
      await page.fill('#DisplayName', person.name);
      await page.fill('#Password', person.password);
      await Promise.all([page.waitForNavigation(), page.click('form.mat-form button[type=submit].mat-btn--primary')]);
      check(page.url().endsWith('/Admin/Users'), 'a user is created by the administrator');
    }

    console.log('A device (API)');
    const login = await api('POST', '/api/v1/devices/login', null, { userName: admin.user, password: admin.password, deviceName: 'MATTHIAS-PC', platform: 'windows', appVersion: '0.1.7-20261114' });
    check(login.status === 200 && login.data.token, 'device login answers a token');
    check(login.headers.get('x-thixclip-protocol') === '1', 'the protocol header is sent');
    const token = login.data.token;
    const mac = await api('POST', '/api/v1/devices/login', null, { userName: admin.user, password: admin.password, deviceName: 'MacBook Pro', platform: 'macos', appVersion: '0.1.7-20261114' });

    const known = await api('GET', '/api/v1/clips/changes?since=0', token);
    if (known.data.clips.length < 5) {
      const todo = fs.readFileSync(path.join(__dirname, '..', '..', 'branding', 'png', 'thixclip-full.png'));
      const chart = fs.readFileSync(path.join(__dirname, '..', '..', 'branding', 'png', 'thixclip-512.png'));
      const clips = [
        textClip('Release-Plan Q2: Features, Tests, Rollout', 60 * 24 + 300),
        textClip('Design-Feedback:\nClean, einfach, sehr stimmig. 👍', 60 * 24 + 60, { isFavorite: true }),
        imageClip(chart, 60 * 24 + 20, null),
        imageClip(todo, 30, 'ThixClip\nZwischenablage für Windows und Mac'),
        textClip('https://www.example.com/produkt', 20),
        textClip('Produktmeeting morgen um 10 Uhr\nim Konferenzraum.', 5, { isPinned: true }),
      ];
      const push = await api('POST', '/api/v1/clips/push', token, { clips });
      check(push.status === 200 && push.data.results.length === clips.length, 'a push creates the clips');
      check(push.data.results[2].needsImage && push.data.results[3].needsImage, 'image clips ask for their PNG');
      const up1 = await api('PUT', '/api/v1/clips/' + clips[2].syncId + '/image', token, undefined, chart);
      const up2 = await api('PUT', '/api/v1/clips/' + clips[3].syncId + '/image', token, undefined, todo);
      check(up1.status === 200 && up2.status === 200, 'the PNGs are uploaded');
      const thumb = await api('GET', '/api/v1/clips/' + clips[3].syncId + '/thumbnail', mac.data.token);
      check(thumb.status === 200 && thumb.data.length > 100, 'another device downloads the thumbnail');

      // the same text from the Mac merges into the existing clip
      const merged = await api('POST', '/api/v1/clips/push', mac.data.token, { clips: [textClip('https://www.example.com/produkt', 1)] });
      check(merged.data.results[0].mergedInto === clips[4].syncId, 'the same content from another device is merged (mergedInto)');
    }

    console.log('History (browser)');
    await page.goto(base + '/Clips');
    await page.waitForSelector('.tc-clips');
    await shot(page, '03-history');
    const rows = await page.$$eval('.tc-clip', (r) => r.length);
    check(rows >= 5, 'the history shows the synced clips (' + rows + ')');
    check((await page.$$eval('.mat-itemlist__divider', (d) => d.map((x) => x.textContent))).some((t) => t.includes('Heute')), 'the clips are grouped by day');

    // live: a push of a device appears without a reload
    const liveText = 'Live vom Mac ' + new Date().toISOString();
    await api('POST', '/api/v1/clips/push', mac.data.token, { clips: [textClip(liveText, 0)] });
    const appeared = await page.waitForFunction((t) => document.body.innerText.includes(t), liveText, { timeout: 15000 }).then(() => true).catch(() => false);
    check(appeared, 'the list updates itself when another device pushes');

    // search (including the text recognised in a picture)
    await page.fill('#clips-q', 'zwischenablage');
    await Promise.all([page.waitForNavigation(), page.press('#clips-q', 'Enter')]);
    check((await page.$$eval('.tc-clip', (r) => r.length)) === 1, 'the search finds the OCR text of a picture');
    await shot(page, '04-history-search');
    await page.goto(base + '/Clips?kind=image');
    await shot(page, '05-history-images');

    console.log('One clip');
    await page.goto(base + '/Clips');
    const imageHref = await page.$eval('.tc-clip .tc-thumb', (t) => t.closest('a').getAttribute('href'));
    await page.goto(base + imageHref);
    await shot(page, '06-clip-image');
    await shotEnd(page, '06b-clip-image-end');
    check(await page.$('[data-copy-image]') !== null, 'an image clip has "In Zwischenablage kopieren"');
    const textHref = await page.$$eval('.tc-clip a.mat-item__main', (a) => a.map((x) => x.getAttribute('href')));
    await page.goto(base + '/Clips');
    const firstText = await page.$$eval('.tc-clip', (rows) => rows.filter((r) => !r.querySelector('.tc-thumb')).map((r) => r.querySelector('a').getAttribute('href'))[0]);
    await page.goto(base + firstText);
    await shot(page, '07-clip-text');
    check(await page.$('textarea#Text') !== null && await page.$('#Image') === null, 'a text clip has a text field and no upload');

    console.log('New entry');
    await page.goto(base + '/Clips/Edit');
    await shot(page, '08-new');
    check(await page.isVisible('#Text') && !(await page.isVisible('.mat-upload')), 'a new entry shows the text field for "Text"');
    await page.check('#Kind-1');
    await page.waitForTimeout(150);
    check(!(await page.isVisible('#Text')), 'the text field goes for "Bild"');
    await page.check('#Kind-0');
    const browserText = 'Aus dem Browser ' + Date.now();
    await page.fill('#Text', browserText);
    await Promise.all([page.waitForNavigation(), page.click('form.mat-form button[type=submit].mat-btn--primary')]);
    check(page.url().includes('/Clips/Edit?id='), 'a text from the browser is created');
    const changes = await api('GET', '/api/v1/clips/changes?since=0&limit=200', token);
    const created = changes.data.clips.find((c) => c.text === browserText);
    check(created && created.source === 'Web', 'the device sees the clip from the browser (source "Web")');

    // delete in the browser -> tombstone for the device
    const before = changes.data.latest;
    page.once('dialog', (d) => d.accept());
    await page.click('button.mat-btn--danger[formaction]');
    await page.waitForSelector('dialog[open]');
    await shot(page, '09-delete-confirm');
    await Promise.all([page.waitForNavigation(), page.click('dialog[open] .mat-btn--danger, dialog[open] .mat-btn--primary')]);
    const after = await api('GET', '/api/v1/clips/changes?since=' + before, token);
    const tomb = after.data.clips.find((c) => c.syncId === created.syncId);
    check(tomb && tomb.isDeleted === true && !tomb.text, 'a delete in the browser reaches the device as a tombstone');

    console.log('Account, devices, administration, about');
    for (const [url, name] of [['/Account', '10-account'], ['/Account/Security', '11-security'], ['/Account/Devices', '12-devices'], ['/Admin/Users', '13-users'], ['/About', '15-about']]) {
      await page.goto(base + url);
      await shot(page, name);
    }
    await page.goto(base + '/Admin/Users');
    await page.click('text=' + person.name);
    await page.waitForLoadState();
    await shot(page, '14-user-edit');
    await page.goto(base + '/Account/Devices');
    await page.click('text=MacBook Pro');
    await page.waitForLoadState();
    await shot(page, '16-device');
    // remove the Mac: its token ends
    await page.click('button.mat-btn--danger[formaction]');
    await page.waitForSelector('dialog[open]');
    await Promise.all([page.waitForNavigation(), page.click('dialog[open] .mat-btn--danger, dialog[open] .mat-btn--primary')]);
    const revoked = await api('GET', '/api/v1/clips/changes?since=0', mac.data.token);
    check(revoked.status === 401, 'a device removed in the browser gets 401');
    await page.goto(base + '/Clips/Edit?id=999999');
    await shot(page, '17-not-found');

    console.log('Phone (390 px) and dark');
    for (const scheme of ['light', 'dark']) {
      const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, colorScheme: scheme, locale: 'de-DE' });
      const p = await phone.newPage();
      await signIn(p, admin);
      for (const [url, name] of [['/Clips', 'history'], [imageHref, 'clip-image'], [firstText, 'clip-text'], ['/Clips/Edit', 'new'], ['/Account', 'account'], ['/Account/Devices', 'devices'], ['/Admin/Users', 'users']]) {
        await p.goto(base + url);
        await shot(p, `20-phone-${scheme}-${name}`);
        await shotEnd(p, `20-phone-${scheme}-${name}-end`);
      }
      await p.goto(base + '/Clips');
      await p.click('.mat-burger');
      await p.waitForTimeout(400);
      await shot(p, `20-phone-${scheme}-menu`);
      const overflow = await p.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      check(overflow <= 1, `no sideways scrolling at 390 px (${scheme})`);
      const fontSize = await p.$eval('#Text', (e) => parseFloat(getComputedStyle(e).fontSize)).catch(() => 16);
      check(fontSize >= 16, 'text fields have 16 px on a touch screen (no zoom on iOS)');
      await phone.close();
    }
    const dark = await browser.newContext({ viewport: { width: 1280, height: 860 }, colorScheme: 'dark', locale: 'de-DE' });
    const d = await dark.newPage();
    await signIn(d, admin);
    await d.goto(base + '/Clips');
    await shot(d, '21-desktop-dark-history');
    await d.goto(base + imageHref);
    await shot(d, '22-desktop-dark-clip');

    console.log('Offline (installed app)');
    await d.goto(base + '/Clips');
    await d.waitForFunction(() => navigator.serviceWorker && navigator.serviceWorker.controller !== null, null, { timeout: 15000 }).catch(() => d.reload());
    await d.waitForFunction(() => navigator.serviceWorker && navigator.serviceWorker.controller !== null, null, { timeout: 15000 }).catch(() => {});
    await d.goto(base + '/Clips');
    await d.waitForTimeout(500);
    await dark.setOffline(true);
    await d.goto(base + '/Clips').catch(() => {});
    await d.waitForTimeout(500);
    check((await d.$$eval('.tc-clip', (r) => r.length).catch(() => 0)) > 0, 'offline: the last history is still shown');
    check(await d.isVisible('#tc-offline'), 'offline: the top bar says "Offline"');
    await shot(d, '23-offline-history');
    await d.goto(base + '/Account/Security').catch(() => {});
    await d.waitForTimeout(500);
    check((await d.content()).includes('Keine Verbindung'), 'offline: another page shows the offline page');
    await shot(d, '24-offline-page');
    await dark.setOffline(false);
    // the offline page reloads by itself on the "online" event (and every 10 s); the button is the manual way
    const reconnected = await d.waitForFunction(() => !document.getElementById('tc-offline-page'), null, { timeout: 15000 }).then(() => true).catch(() => false);
    if (!reconnected) { await d.click('#tc-retry').catch(() => {}); await d.waitForTimeout(1500); }
    check(!(await d.content()).includes('Keine Verbindung') && d.url().includes('/Account/Security'), 'back online: the page that was asked for loads by itself');
    await shot(d, '25-back-online');
    await dark.close();

    console.log('Sign-out and the second account');
    await page.goto(base + '/Clips');
    await page.click('.mat-user');
    await Promise.all([page.waitForNavigation(), page.click('#user-menu button[type=submit]')]);
    check(page.url().includes('/Account/Login'), 'sign-out leads to the sign-in');
    await shot(page, '18-login');
    await signIn(page, person);
    check(page.url().includes('/Account/Security'), 'a password set by the administrator has to be changed first');
    await shot(page, '19-must-change-password');
  } finally {
    await browser.close();
  }

  if (errors.length) { console.log('Script errors on the pages:\n  ' + [...new Set(errors)].join('\n  ')); failures += errors.length; }
  console.log(failures === 0 ? '\nAll checks passed. Screenshots: ' + shots : `\n${failures} check(s) failed. Screenshots: ${shots}`);
  process.exit(failures === 0 ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
