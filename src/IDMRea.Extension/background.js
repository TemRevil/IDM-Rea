// IDM-Rea's service worker: talks to the app, checks links, takes over downloads, guards tabs, keeps the lists.
import { LISTS, parse, DomainSet } from './lib/lists.js';
import { classify } from './lib/verdict.js';
import { hostOf, siteOf } from './lib/sites.js';
import { newTabVerdict, redirectVerdict } from './lib/guard.js';

const HOST = 'com.idmrea.host';
const DAY_MS = 24 * 60 * 60 * 1000;
const PROBE_TTL_MS = 10 * 60 * 1000;
const PROBE_TIMEOUT_MS = 8000;
const PROBE_BYTES = 1024;
const PROBE_PARALLEL = 4;
const RULES_PER_LIST = 5000;
const BLOCKED_PAGE = chrome.runtime.getURL('blocked.html');
const NOT_CONNECTED = 'IDM-Rea is not connected to this browser. In IDM-Rea, open Settings, Browser, and press "Add to browser".';

// ---------- Lists ----------

let lists = Object.fromEntries(LISTS.map((l) => [l.id, new DomainSet()]));
let listsUpdated = 0;
let ready = loadLists();

async function loadLists() {
  const { lists: saved } = await chrome.storage.local.get('lists');
  lists = Object.fromEntries(LISTS.map((l) => [l.id, new DomainSet(saved?.[l.id]?.domains || [])]));
  listsUpdated = saved?.updated || 0;
}

/** Downloads every list; a list that can't be reached keeps its last copy. True when all four came in. */
async function updateLists() {
  const { lists: saved } = await chrome.storage.local.get('lists');
  const next = { updated: saved?.updated || 0 };
  let fetched = 0;
  for (const list of LISTS) {
    try {
      const response = await fetch(list.url, { cache: 'no-store' });
      if (!response.ok) throw new Error(String(response.status));
      next[list.id] = { domains: parse(list.format, await response.text()) };
      fetched++;
    } catch {
      next[list.id] = saved?.[list.id] || { domains: [] };
    }
  }
  if (fetched) next.updated = Date.now();
  await chrome.storage.local.set({ lists: next }); // storage.onChanged reloads the sets and the blocking rules
  return fetched === LISTS.length;
}

/** Malware, phishing and pop-up network sites open blocked.html instead, in tabs and in frames. */
async function applyBlockingRules(saved) {
  const addRules = [];
  let id = 1;
  for (const why of ['malware', 'phishing', 'popups']) {
    const domains = saved?.[why]?.domains || [];
    for (let i = 0; i < domains.length; i += RULES_PER_LIST) {
      addRules.push({
        id: id++,
        priority: 1,
        action: { type: 'redirect', redirect: { regexSubstitution: `${BLOCKED_PAGE}?why=${why}#\\0` } },
        condition: { regexFilter: '^https?://.*', requestDomains: domains.slice(i, i + RULES_PER_LIST), resourceTypes: ['main_frame', 'sub_frame'] },
      });
    }
  }
  const old = await chrome.declarativeNetRequest.getDynamicRules();
  await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: old.map((r) => r.id), addRules });
}

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.lists) {
    ready = loadLists();
    applyBlockingRules(changes.lists.newValue).catch((e) => console.warn('IDM-Rea: blocking rules', e));
  }
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create('lists', { periodInMinutes: 24 * 60 });
  updateLists();
});
chrome.runtime.onStartup.addListener(async () => {
  await ready;
  if (Date.now() - listsUpdated > DAY_MS) updateLists();
});
chrome.alarms.onAlarm.addListener((alarm) => { if (alarm.name === 'lists') updateLists(); });

// ---------- IDM-Rea (native host) ----------

let nextId = 1;
function native(message) {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(HOST, { id: nextId++, ...message }, (reply) => {
      if (chrome.runtime.lastError) resolve({ ok: false, connected: false, error: NOT_CONNECTED });
      else resolve(reply || { ok: false, error: 'IDM-Rea did not answer.' });
    });
  });
}

let helloCache = null;
async function hello() {
  if (helloCache && Date.now() - helloCache.at < 60_000) return helloCache.reply;
  const reply = await native({ type: 'hello' });
  helloCache = { at: Date.now(), reply };
  return reply;
}

async function cookiesFor(url) {
  try {
    const all = await chrome.cookies.getAll({ url });
    return all.map((c) => `${c.name}=${c.value}`).join('; ') || null;
  } catch {
    return null;
  }
}

async function addToIdmRea({ url, fileName = null, referrer = null, sha256 = null }) {
  return native({
    type: 'add', url, fileName, referrer, sha256: sha256 || checksums.get(url) || null,
    cookies: await cookiesFor(url), userAgent: navigator.userAgent,
  });
}

// ---------- Probes ----------

const probes = new Map(); // url -> { at, promise }
const queue = [];
let running = 0;

function probe(request) {
  const cached = probes.get(request.url);
  if (cached && Date.now() - cached.at < PROBE_TTL_MS) return cached.promise;
  const promise = new Promise((resolve) => { queue.push({ request, resolve }); pump(); });
  probes.set(request.url, { at: Date.now(), promise });
  return promise;
}

function pump() {
  while (running < PROBE_PARALLEL && queue.length) {
    const { request, resolve } = queue.shift();
    running++;
    check(request).then(resolve, () => resolve({ kind: 'unknown', warnings: [] })).finally(() => { running--; pump(); });
  }
}

async function check({ url, pageUrl, linkText, nearText }) {
  await ready;
  const first = classify({ url, pageUrl, lists });
  if (first.kind === 'blocked' || first.kind === 'ad') return first;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS);
  try {
    const response = await fetch(url, { headers: { Range: `bytes=0-${PROBE_BYTES - 1}` }, credentials: 'include', signal: controller.signal });
    const firstBytes = await readFirst(response, PROBE_BYTES);
    const h = (name) => response.headers.get(name) || '';
    const verdict = classify({
      url: response.url || url, pageUrl, status: response.status, contentType: h('content-type'), disposition: h('content-disposition'),
      contentRange: h('content-range'), contentLength: response.headers.get('content-length'), firstBytes, linkText, nearText, lists,
    });
    return { ...verdict, finalUrl: response.url || url };
  } catch {
    return { kind: 'unknown', warnings: [] };
  } finally {
    clearTimeout(timer);
    controller.abort(); // stops a server that ignored the range
  }
}

async function readFirst(response, limit) {
  if (!response.body) return new Uint8Array();
  const reader = response.body.getReader();
  const out = new Uint8Array(limit);
  let length = 0;
  try {
    while (length < limit) {
      const { done, value } = await reader.read();
      if (done) break;
      const take = Math.min(value.length, limit - length);
      out.set(value.subarray(0, take), length);
      length += take;
    }
  } finally {
    reader.cancel().catch(() => {});
  }
  return out.subarray(0, length);
}

// ---------- Per-tab state ----------

const clicks = new Map(); // tabId -> { kind, href, at }
const checksums = new Map(); // url -> sha256 written next to its link
const allowOnce = new Set(); // urls the person chose to open from a "Blocked a pop-up" notice

async function guardOn(url) {
  const { guardOff = {} } = await chrome.storage.local.get('guardOff');
  return !guardOff[siteOf(hostOf(url))];
}

async function count(tabId, what) {
  const key = `stats:${tabId}`;
  const { [key]: stats = { popups: 0, layers: 0, redirects: 0 } } = await chrome.storage.session.get(key);
  stats[what]++;
  await chrome.storage.session.set({ [key]: stats });
}

chrome.tabs.onRemoved.addListener((tabId) => {
  clicks.delete(tabId);
  chrome.storage.session.remove(`stats:${tabId}`);
});

// ---------- New tab guard ----------

const pendingTabs = new Map(); // new tabId -> { sourceTabId, sourceUrl }

chrome.webNavigation.onCreatedNavigationTarget.addListener(async ({ sourceTabId, tabId, url }) => {
  let sourceUrl;
  try { sourceUrl = (await chrome.tabs.get(sourceTabId)).url; } catch { return; }
  if (!/^https?:/.test(sourceUrl || '') || !(await guardOn(sourceUrl))) return;
  pendingTabs.set(tabId, { sourceTabId, sourceUrl });
  setTimeout(() => pendingTabs.delete(tabId), 10_000);
  judgeNewTab(tabId, url);
});

// A tab opened as about:blank is judged at its first real address.
chrome.webNavigation.onBeforeNavigate.addListener(({ tabId, frameId, url }) => {
  if (frameId === 0 && pendingTabs.has(tabId)) judgeNewTab(tabId, url);
});

async function judgeNewTab(tabId, url) {
  const pending = pendingTabs.get(tabId);
  if (!pending) return;
  if (allowOnce.delete(url)) { pendingTabs.delete(tabId); return; }
  await ready;
  // The page's click report and the new tab race each other; give the report a moment.
  if (!clicks.has(pending.sourceTabId)) await new Promise((r) => setTimeout(r, 150));
  const verdict = newTabVerdict({ sourceUrl: pending.sourceUrl, targetUrl: url, click: clicks.get(pending.sourceTabId), now: Date.now(), lists });
  if (verdict === 'wait') return;
  pendingTabs.delete(tabId);
  if (verdict !== 'close') return;
  chrome.tabs.remove(tabId).catch(() => {});
  count(pending.sourceTabId, 'popups');
  chrome.tabs.sendMessage(pending.sourceTabId, { type: 'blocked-popup', url }).catch(() => {});
}

// ---------- Redirect guard ----------

chrome.webNavigation.onCommitted.addListener(async ({ tabId, frameId, url, transitionType, transitionQualifiers }) => {
  if (frameId !== 0 || !/^https?:/.test(url)) return;
  await ready;
  const verdict = redirectVerdict({ url, transition: transitionType, qualifiers: transitionQualifiers, click: clicks.get(tabId), now: Date.now(), lists });
  if (verdict !== 'block') return;
  count(tabId, 'redirects');
  const back = await chrome.tabs.goBack(tabId).then(() => true, () => false);
  if (!back) chrome.tabs.update(tabId, { url: `${BLOCKED_PAGE}?why=ad#${url}` }).catch(() => {});
});

// ---------- Download takeover ----------

chrome.downloads.onCreated.addListener(async (item) => {
  const url = item.finalUrl || item.url;
  if (!/^https?:/.test(url) || item.incognito || item.byExtensionId === chrome.runtime.id || item.state !== 'in_progress') return;
  const declines = (s) => !s.ok || !s.takeover || (item.totalBytes > 0 && item.totalBytes < s.minBytes);
  // Hold the browser's copy first: asking IDM-Rea takes a moment, and a fast download could finish meanwhile.
  const known = helloCache && Date.now() - helloCache.at < 60_000 ? helloCache.reply : null;
  if (known && declines(known)) return;
  try { await chrome.downloads.pause(item.id); } catch { return; } // already finished
  if (declines(await hello())) { chrome.downloads.resume(item.id).catch(() => {}); return; }
  const fileName = (item.filename || '').split(/[\\/]/).pop() || null;
  const reply = await addToIdmRea({ url, fileName, referrer: item.referrer || null });
  if (reply.ok) {
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  } else {
    console.warn('IDM-Rea did not take the download:', reply.error);
    chrome.downloads.resume(item.id).catch(() => {});
  }
});

// ---------- Messages from pages, the menu and the blocked page ----------

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  const tabId = sender.tab?.id;
  switch (message?.type) {
    case 'click':
      if (tabId != null) clicks.set(tabId, { kind: message.kind, href: message.href || null, at: Date.now() });
      return false;
    case 'probe':
      probe(message).then(reply);
      return true;
    case 'hosts':
      ready.then(() => reply({ ads: (message.hosts || []).filter((h) => lists.ads.has(h) || lists.popups.has(h)) }));
      return true;
    case 'checksums':
      for (const { url, sha256 } of message.items || []) checksums.set(url, sha256);
      while (checksums.size > 2000) checksums.delete(checksums.keys().next().value);
      return false;
    case 'found':
      if (tabId != null) {
        chrome.action.setBadgeText({ tabId, text: message.count ? String(message.count) : '' });
        chrome.action.setBadgeBackgroundColor({ tabId, color: '#3B3B3B' });
      }
      return false;
    case 'stat':
      if (tabId != null && ['popups', 'layers'].includes(message.what)) count(tabId, message.what);
      return false;
    case 'open-anyway':
      allowOnce.add(message.url);
      chrome.tabs.create({ url: message.url, openerTabId: tabId, index: sender.tab ? sender.tab.index + 1 : undefined });
      return false;
    case 'download':
      addToIdmRea(message).then(reply);
      return true;
    case 'download-many':
      (async () => {
        const results = [];
        for (const item of message.items || []) results.push(await addToIdmRea({ ...item, referrer: message.referrer }));
        reply({ ok: results.every((r) => r.ok), added: results.filter((r) => r.ok).length, error: results.find((r) => !r.ok)?.error });
      })();
      return true;
    case 'menu-state':
      menuState(message.tabId, message.url).then(reply);
      return true;
    case 'set-guard':
      setGuard(message.url, message.on).then(reply);
      return true;
    case 'update-lists':
      updateLists().then((ok) => reply({ ok, updated: listsUpdated }));
      return true;
    case 'open-app':
      native({ type: 'open' }).then(reply);
      return true;
    case 'continue':
      continueTo(message.url, tabId).then(reply);
      return true;
    default:
      return false;
  }
});

async function menuState(tabId, url) {
  await ready;
  helloCache = null;
  const key = `stats:${tabId}`;
  const { [key]: stats = { popups: 0, layers: 0, redirects: 0 } } = await chrome.storage.session.get(key);
  return { app: await hello(), stats, guard: await guardOn(url), site: siteOf(hostOf(url)), listsUpdated };
}

async function setGuard(url, on) {
  const site = siteOf(hostOf(url));
  const { guardOff = {} } = await chrome.storage.local.get('guardOff');
  if (on) delete guardOff[site]; else guardOff[site] = true;
  await chrome.storage.local.set({ guardOff });
  return { ok: true };
}

/** "Continue anyway" on the blocked page: this tab may load that site until it closes. */
async function continueTo(url, tabId) {
  const host = hostOf(url);
  if (!host || tabId == null) return { ok: false };
  const rules = await chrome.declarativeNetRequest.getSessionRules();
  const id = rules.reduce((max, r) => Math.max(max, r.id), 0) + 1;
  await chrome.declarativeNetRequest.updateSessionRules({
    addRules: [{ id, priority: 2, action: { type: 'allow' }, condition: { requestDomains: [host], tabIds: [tabId], resourceTypes: ['main_frame', 'sub_frame'] } }],
  });
  clicks.set(tabId, { kind: 'link', href: url, at: Date.now() }); // the redirect guard lets it through too
  return { ok: true };
}
