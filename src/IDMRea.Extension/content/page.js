// IDM-Rea in the page: reports clicks for the tab guards, takes away click-catching layers, finds the real files
// among the links, and marks them. Everything drawn sits in a closed shadow root on a fixed layer, so the page's
// styles can't reach it and the page's layout never moves.
(() => {
  if (window.top !== window) return;
  const LINK = 'a[href], area[href]';
  const BUTTON = 'button, input[type=button], input[type=submit], input[type=image], [role=button]';
  const MAX_LINKS = 60;
  const WATCH_MS = 1500;
  const send = (message) => chrome.runtime.sendMessage(message).catch(() => null);
  const lib = (name) => import(chrome.runtime.getURL(`lib/${name}.js`));
  const loaded = Promise.all([lib('links'), lib('guard'), lib('sites')]);
  let links = null, guard = null, sites = null;
  loaded.then(([l, g, s]) => { links = l; guard = g; sites = s; });

  let guardOn = true;
  let layer = null; // the shadow root, once something is drawn
  const adHosts = new Set();
  const asked = new Set();

  // ---------- Clicks ----------

  function pressed(event) {
    let kind = 'empty', href = null, target = null;
    for (const node of event.composedPath()) {
      if (!(node instanceof Element)) continue;
      if (node.matches(LINK)) { kind = 'link'; href = node.href; target = node; break; }
      if (node.matches(BUTTON)) { kind = 'button'; target = node; break; }
    }
    return { kind, href, target };
  }

  const ours = (event) => layer && event.composedPath().includes(layer.host);
  let stopped = null; // a press taken from a layer: { under, at }
  let watched = null; // an empty layer pressed just now: { element, x, y, at }

  function onPointerDown(event) {
    if (!event.isTrusted || ours(event)) return;
    if (guardOn && guard && event.button === 0 && catchLayer(event)) return;
    const { kind, href } = pressed(event);
    send({ type: 'click', kind, href });
  }

  function onClick(event) {
    if (!event.isTrusted || ours(event)) return;
    if (stopped && performance.now() - stopped.at < 1000) {
      event.preventDefault();
      event.stopImmediatePropagation();
      const under = stopped.under;
      stopped = null;
      if (under) deliver(under);
      return;
    }
    const { kind, href } = pressed(event);
    send({ type: 'click', kind, href });
  }

  addEventListener('pointerdown', onPointerDown, true);
  addEventListener('click', onClick, true);
  // A link's right-click menu can open it in a new tab well after the press.
  addEventListener('contextmenu', (event) => {
    if (!event.isTrusted || ours(event)) return;
    const { kind, href } = pressed(event);
    if (kind === 'link') send({ type: 'click', kind: 'menu', href });
  }, true);
  addEventListener('auxclick', (event) => { if (event.isTrusted && !ours(event)) { const { kind, href } = pressed(event); send({ type: 'click', kind, href }); } }, true);

  /** Clicks the link or button the person meant, telling both guards it was theirs. */
  function deliver(element) {
    const isLink = element.matches(LINK);
    const href = isLink ? element.href : null;
    send({ type: 'click', kind: isLink ? 'link' : 'button', href });
    window.postMessage({ idmrea: 'delivered', kind: isLink ? 'link' : 'button', host: href ? new URL(href).hostname : '' }, '*');
    element.click();
  }

  function positionedAncestor(element) {
    for (let el = element; el && el !== document.body && el !== document.documentElement; el = el.parentElement) {
      const position = getComputedStyle(el).position;
      if (position === 'fixed' || position === 'absolute') return el;
    }
    return null;
  }

  function opacityOf(element) {
    let opacity = 1;
    for (let el = element; el; el = el.parentElement) opacity *= Number(getComputedStyle(el).opacity);
    return opacity;
  }

  function isEmpty(element) {
    const style = getComputedStyle(element);
    const clear = /rgba\(\d+, \d+, \d+, 0\)|transparent/.test(style.backgroundColor) && style.backgroundImage === 'none';
    return clear && !element.innerText.trim() && !element.querySelector('img, video, canvas, svg, picture, iframe, embed, object');
  }

  function coverage(element) {
    const r = element.getBoundingClientRect();
    const w = Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
    const h = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0));
    return (w * h) / (innerWidth * innerHeight || 1);
  }

  const hostOfUrl = (url) => { try { return new URL(url, location.href).hostname.toLowerCase(); } catch { return ''; } };
  const isAdElement = (el) => {
    const link = el.closest?.(LINK);
    const url = link ? link.href : el.tagName === 'IFRAME' ? el.src : '';
    return Boolean(url) && adHosts.has(hostOfUrl(url));
  };

  /** What lies under a layer at a point, with the layer out of the way. */
  function underneath(element, x, y) {
    const before = element.style.getPropertyValue('pointer-events');
    const priority = element.style.getPropertyPriority('pointer-events');
    element.style.setProperty('pointer-events', 'none', 'important');
    const under = document.elementFromPoint(x, y);
    element.style.setProperty('pointer-events', before, priority);
    return under;
  }

  function factsAbout(element, x, y) {
    const under = underneath(element, x, y);
    return {
      positioned: true,
      coverage: coverage(element),
      opacity: opacityOf(element),
      empty: isEmpty(element),
      adLink: isAdElement(element),
      overMedia: Boolean(under?.closest('video, canvas, embed, object')),
    };
  }

  function removeLayer(element) {
    element.style.setProperty('pointer-events', 'none', 'important');
    send({ type: 'stat', what: 'layers' });
  }

  function catchLayer(event) {
    const top = event.composedPath().find((n) => n instanceof Element);
    const element = top && positionedAncestor(top);
    if (!element) return false;
    const verdict = guard.clickCatcher(factsAbout(element, event.clientX, event.clientY));
    if (verdict === 'watch') watched = { element, x: event.clientX, y: event.clientY, at: performance.now() };
    if (verdict !== 'stop') return false;
    event.preventDefault(); // also keeps mousedown and mouseup from the page
    event.stopImmediatePropagation();
    removeLayer(element);
    const under = document.elementFromPoint(event.clientX, event.clientY);
    stopped = { under: under?.closest(`${LINK}, ${BUTTON}`) || null, at: performance.now() };
    return true;
  }

  /** A pop-up was refused: if an empty layer was pressed just before, it was a trap. */
  function popupRefused() {
    if (!watched || performance.now() - watched.at > WATCH_MS) return false;
    const { element, x, y } = watched;
    watched = null;
    removeLayer(element);
    const meant = document.elementFromPoint(x, y)?.closest(`${LINK}, ${BUTTON}`);
    if (meant) deliver(meant);
    return true;
  }

  // ---------- Pop-ups refused in the page (guard-main.js) and by the tab guard ----------

  addEventListener('message', (event) => {
    if (event.source !== window || event.data?.idmrea !== 'blocked-open') return;
    send({ type: 'stat', what: 'popups' });
    noticePopup(String(event.data.url || ''));
  });

  chrome.runtime.onMessage.addListener((message, _sender, reply) => {
    if (message?.type === 'blocked-popup') noticePopup(message.url);
    if (message?.type === 'state') { reply({ files: filesFound(), url: location.href }); return false; }
    return false;
  });

  function noticePopup(url) {
    const layerToo = popupRefused();
    const host = hostOfUrl(url) || 'another site';
    toast(`Blocked a pop-up to ${host}${layerToo ? ', and the hidden layer that opened it' : ''}.`, url && /^https?:/.test(url) ? {
      label: 'Open it', run: () => send({ type: 'open-anyway', url }),
    } : null);
  }

  // ---------- The guard switch ----------

  function applyGuard(off) {
    const site = sites ? sites.siteOf(location.hostname) : location.hostname;
    guardOn = !off?.[site];
    window.postMessage({ idmrea: 'guard', on: guardOn }, '*');
  }
  loaded.then(() => chrome.storage.local.get('guardOff')).then(({ guardOff }) => applyGuard(guardOff)).catch(() => {});
  chrome.storage.onChanged.addListener((changes, area) => { if (area === 'local' && changes.guardOff) applyGuard(changes.guardOff.newValue); });

  // ---------- Finding files ----------

  const entries = new Map(); // url -> { url, links: Set, verdict, sha256 }
  let probed = 0;

  const labelOf = (a) => (a.innerText || a.getAttribute('aria-label') || a.title || a.querySelector('img')?.alt || '').trim().slice(0, 200);

  function nearTextOf(a, label) {
    let block = a.parentElement;
    for (let i = 0; i < 4 && block && block !== document.body; i++) {
      if ((block.innerText || '').length >= 80) break;
      block = block.parentElement;
    }
    let text = block?.innerText || '';
    if (text.length > 600) {
      const at = label ? text.indexOf(label) : -1;
      text = at < 0 ? text.slice(0, 600) : text.slice(Math.max(0, at - 300), at + label.length + 300);
    }
    return text;
  }

  function distanceToView(element) {
    const r = element.getBoundingClientRect();
    if (r.bottom >= 0 && r.top <= innerHeight) return 0;
    return r.top > innerHeight ? r.top - innerHeight : -r.bottom;
  }

  async function askHosts(urls) {
    const fresh = [...new Set(urls.map(hostOfUrl))].filter((h) => h && h !== location.hostname && !asked.has(h));
    if (!fresh.length) return;
    fresh.forEach((h) => asked.add(h));
    const answer = await send({ type: 'hosts', hosts: fresh });
    for (const h of answer?.ads || []) adHosts.add(h);
  }

  /** Ad links and ad frames stretched over the page lose their grip as soon as they are seen. */
  function disarmAdLayers() {
    if (!guardOn || !adHosts.size) return;
    for (const el of document.querySelectorAll(`${LINK}, iframe[src]`)) {
      if (!isAdElement(el)) continue;
      const element = positionedAncestor(el);
      if (element && element.style.getPropertyValue('pointer-events') !== 'none' && coverage(element) >= 0.5) removeLayer(element);
    }
  }

  async function scan() {
    if (!document.body) return;
    await loaded;
    const anchors = [...document.querySelectorAll('a[href]')];
    await askHosts([...anchors.map((a) => a.href), ...[...document.querySelectorAll('iframe[src]')].map((f) => f.src)]);
    disarmAdLayers();
    const fresh = anchors
      .filter((a) => !entries.get(a.href)?.links.has(a))
      .filter((a) => links.isCandidate({ href: a.href, text: labelOf(a), download: a.getAttribute('download') }))
      .map((a) => ({ a, distance: distanceToView(a) }))
      .sort((x, y) => x.distance - y.distance);
    const checksums = [];
    for (const { a } of fresh) {
      const known = entries.get(a.href);
      if (known) { known.links.add(a); if (known.verdict) mark(a, known); continue; }
      if (probed >= MAX_LINKS) break;
      probed++;
      const label = labelOf(a);
      const nearText = nearTextOf(a, label);
      const entry = { url: a.href, links: new Set([a]), verdict: null, sha256: links.checksumNear(nearText) };
      entries.set(a.href, entry);
      if (entry.sha256) checksums.push({ url: entry.url, sha256: entry.sha256 });
      send({ type: 'probe', url: a.href, pageUrl: location.href, linkText: label, nearText }).then((verdict) => {
        entry.verdict = verdict || { kind: 'unknown', warnings: [] };
        for (const link of entry.links) mark(link, entry);
        send({ type: 'found', count: filesFound().length });
      });
    }
    if (checksums.length) send({ type: 'checksums', items: checksums });
  }

  function filesFound() {
    return [...entries.values()].filter((e) => e.verdict?.kind === 'file').map((e) => ({
      url: e.url, name: e.verdict.name, type: e.verdict.type, ext: e.verdict.ext, size: e.verdict.size, sizeText: e.verdict.sizeText,
      warnings: e.verdict.warnings.map((w) => w.text), sha256: e.sha256,
    }));
  }

  let scanTimer = 0;
  const scanSoon = () => { clearTimeout(scanTimer); scanTimer = setTimeout(scan, 800); };
  new MutationObserver((records) => {
    if (records.some((r) => !layer || r.target !== layer.host)) scanSoon();
  }).observe(document, { childList: true, subtree: true });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', scanSoon, { once: true });
  else scanSoon();

  // ---------- Drawing ----------

  const STYLE = `
    :host { all: initial; }
    * { box-sizing: border-box; }
    .badge, .card, .toast { font: 500 11px/1 "Inter Tight", "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; color: #F2F2F0; }
    .badge { position: fixed; left: 0; top: 0; height: 18px; padding: 0 7px; border-radius: 9px; display: inline-flex; align-items: center; gap: 4px;
      background: #2B2B2B; box-shadow: 0 1px 2px rgba(0,0,0,.25); white-space: nowrap; pointer-events: auto; cursor: default; letter-spacing: .01em; }
    .badge.warn { background: #3A2E1B; color: #F3D9A4; box-shadow: inset 0 0 0 1px #B7791F; }
    .badge.host { background: #ECECEA; color: #2B2B2B; }
    .badge.bad { background: #7A1F1A; color: #FBE3E1; }
    .badge[hidden], .card[hidden] { display: none; }
    .card { position: fixed; left: 0; top: 0; width: 300px; padding: 14px 16px; border-radius: 10px; background: #232323; pointer-events: auto;
      box-shadow: 0 10px 30px rgba(0,0,0,.35); font-size: 12.5px; line-height: 1.45; opacity: 0; transition: opacity 240ms ease; direction: ltr; text-align: left; }
    .card.shown { opacity: 1; }
    .name { font-weight: 600; font-size: 13px; overflow-wrap: anywhere; }
    .meta { color: #B8B8B4; margin-top: 2px; }
    .warn-line { margin-top: 8px; color: #F3D9A4; }
    .bad-line { margin-top: 8px; color: #F6B8B2; }
    .note { margin-top: 8px; color: #B8B8B4; }
    .actions { display: flex; gap: 8px; margin-top: 12px; }
    button { font: inherit; font-weight: 500; border: 0; border-radius: 6px; padding: 6px 10px; cursor: pointer; color: #F2F2F0; background: #3A3A3A; }
    button.primary { background: #F2F2F0; color: #1E1E1E; }
    button:focus-visible { outline: 2px solid #9AA4B2; outline-offset: 1px; }
    .toasts { position: fixed; right: 16px; bottom: 16px; display: flex; flex-direction: column; gap: 8px; align-items: flex-end; }
    .toast { pointer-events: auto; display: flex; align-items: center; gap: 12px; max-width: 380px; padding: 10px 12px 10px 14px; border-radius: 8px;
      background: #232323; box-shadow: 0 6px 20px rgba(0,0,0,.3); font-size: 12.5px; line-height: 1.4; opacity: 0; transform: translateY(6px);
      transition: opacity 280ms ease, transform 280ms ease; }
    .toast.shown { opacity: 1; transform: none; }
    @media (prefers-reduced-motion: reduce) { .card, .toast { transition: none; } }
  `;

  function ensureLayer() {
    if (layer) return layer;
    const host = document.createElement('idmrea-layer');
    host.style.cssText = 'all: initial; position: fixed; inset: 0; pointer-events: none; z-index: 2147483647; contain: layout style;';
    layer = host.attachShadow({ mode: 'closed' });
    const style = document.createElement('style');
    style.textContent = STYLE;
    const toasts = document.createElement('div');
    toasts.className = 'toasts';
    layer.append(style, toasts);
    document.documentElement.append(host);
    return layer;
  }

  const el = (tag, className, text) => { const e = document.createElement(tag); if (className) e.className = className; if (text != null) e.textContent = text; return e; };

  const badges = new Map(); // link -> badge element

  function badgeText(verdict) {
    if (verdict.kind === 'blocked') return 'Blocked site';
    if (verdict.kind === 'ad') return 'Ad link, not a download';
    if (verdict.kind === 'host') return 'File host';
    const label = [verdict.ext ? verdict.ext.toUpperCase() : verdict.type, verdict.sizeText].filter(Boolean).join('  ');
    return verdict.warnings.length ? `! ${label}` : label;
  }

  function mark(link, entry) {
    const { verdict } = entry;
    if (!['file', 'host', 'ad', 'blocked'].includes(verdict.kind) || badges.has(link)) return;
    const root = ensureLayer();
    const badge = el('div', 'badge', badgeText(verdict));
    if (verdict.kind === 'host') badge.classList.add('host');
    else if (verdict.kind === 'ad' || verdict.kind === 'blocked') badge.classList.add('bad');
    else if (verdict.warnings.length) badge.classList.add('warn');
    root.append(badge);
    badges.set(link, badge);
    const show = () => openCard(link, entry, badge);
    badge.addEventListener('mouseenter', show);
    badge.addEventListener('mouseleave', closeCardSoon);
    link.addEventListener('mouseenter', show);
    link.addEventListener('mouseleave', closeCardSoon);
    placeSoon();
  }

  let placing = 0;
  function placeSoon() { if (!placing) placing = requestAnimationFrame(place); }
  function place() {
    placing = 0;
    for (const [link, badge] of badges) {
      const r = link.isConnected ? link.getBoundingClientRect() : null;
      const visible = r && r.width + r.height > 0 && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth;
      badge.hidden = !visible;
      if (!visible) continue;
      const width = badge.offsetWidth;
      const x = r.right + 6 + width <= innerWidth ? r.right + 6 : Math.max(0, r.right - width);
      badge.style.transform = `translate(${Math.round(x)}px, ${Math.round(r.top + (r.height - 18) / 2)}px)`;
    }
  }
  addEventListener('scroll', placeSoon, { capture: true, passive: true });
  addEventListener('resize', placeSoon, { passive: true });
  setInterval(() => { if (badges.size && !document.hidden) placeSoon(); }, 1000);

  let card = null, closeTimer = 0;

  function closeCardSoon() {
    clearTimeout(closeTimer);
    closeTimer = setTimeout(() => { if (card) { card.remove(); card = null; } }, 250);
  }

  function openCard(link, entry, badge) {
    clearTimeout(closeTimer);
    if (card?.dataset.url === entry.url) return;
    card?.remove();
    const { verdict } = entry;
    card = el('div', 'card');
    card.dataset.url = entry.url;
    card.addEventListener('mouseenter', () => clearTimeout(closeTimer));
    card.addEventListener('mouseleave', closeCardSoon);
    if (verdict.kind === 'file') {
      card.append(el('div', 'name', verdict.name), el('div', 'meta', [verdict.type, verdict.sizeText].filter(Boolean).join(', ')));
    } else {
      card.append(el('div', 'name', hostOfUrl(entry.url)));
    }
    for (const w of verdict.warnings) card.append(el('div', verdict.kind === 'file' ? 'warn-line' : 'bad-line', w.text));
    if (verdict.kind === 'host') card.append(el('div', 'note', 'A file-sharing page. Open it to reach the file.'));
    if (verdict.kind === 'file' && entry.sha256) card.append(el('div', 'note', 'The page lists a SHA-256 checksum. IDM-Rea checks the file against it.'));
    const actions = el('div', 'actions');
    if (verdict.kind === 'file') {
      const download = el('button', 'primary', 'Download with IDM-Rea');
      download.addEventListener('click', async () => {
        download.disabled = true;
        const reply = await send({ type: 'download', url: entry.url, fileName: verdict.name, referrer: location.href, sha256: entry.sha256 });
        download.disabled = false;
        toast(reply?.ok ? `Sent ${verdict.name} to IDM-Rea.` : reply?.error || 'IDM-Rea did not answer.');
      });
      actions.append(download);
    }
    if (verdict.kind === 'file' || verdict.kind === 'host') {
      const copy = el('button', '', 'Copy link');
      copy.addEventListener('click', async () => {
        try { await navigator.clipboard.writeText(entry.url); toast('Link copied.'); } catch { toast('The browser did not allow copying here.'); }
      });
      actions.append(copy);
    }
    if (actions.childElementCount) card.append(actions);
    ensureLayer().append(card);
    const r = badge.getBoundingClientRect();
    const height = card.offsetHeight;
    const x = Math.min(Math.max(8, r.left), innerWidth - 308);
    const y = r.bottom + 8 + height <= innerHeight ? r.bottom + 8 : Math.max(8, r.top - 8 - height);
    card.style.transform = `translate(${Math.round(x)}px, ${Math.round(y)}px)`;
    requestAnimationFrame(() => card?.classList.add('shown'));
  }

  function toast(text, action = null) {
    const root = ensureLayer();
    const box = root.querySelector('.toasts');
    const item = el('div', 'toast');
    item.append(el('span', '', text));
    if (action) {
      const button = el('button', '', action.label);
      button.addEventListener('click', () => { action.run(); item.remove(); });
      item.append(button);
    }
    box.append(item);
    while (box.childElementCount > 3) box.firstElementChild.remove();
    requestAnimationFrame(() => item.classList.add('shown'));
    setTimeout(() => { item.classList.remove('shown'); setTimeout(() => item.remove(), 300); }, action ? 8000 : 4000);
  }
})();
