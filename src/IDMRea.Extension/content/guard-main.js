// Runs in the page's own world before its scripts. A page may open another site in a new window only during a
// click on a button, or on a link to that same site. Anything else is refused (as the browser's pop-up blocker
// would) and reported to page.js. The service worker's new-tab guard backs this up for tabs opened other ways.
(() => {
  const WINDOW_MS = 1500;
  const LINK = 'a[href], area[href]';
  const BUTTON = 'button, input[type=button], input[type=submit], input[type=image], [role=button]';
  let last = null; // { kind, host, at }
  let enabled = true;

  const parse = (url) => { try { return new URL(url, location.href); } catch { return null; } };

  function record(event) {
    if (!event.isTrusted) return;
    let kind = 'empty', host = '';
    for (const node of event.composedPath()) {
      if (!(node instanceof Element)) continue;
      if (node.matches(LINK)) { kind = 'link'; host = parse(node.href)?.hostname || ''; break; }
      if (node.matches(BUTTON)) { kind = 'button'; break; }
    }
    last = { kind, host, at: performance.now() };
  }
  for (const type of ['pointerdown', 'click', 'auxclick', 'keydown']) addEventListener(type, record, true);

  // page.js says "off" on sites the person turned the guard off for, and which link or button it clicked for the
  // person after taking a click-catching layer away (that click is synthetic, so record() can't see it).
  addEventListener('message', (event) => {
    if (event.source !== window) return;
    if (event.data?.idmrea === 'guard') enabled = event.data.on !== false;
    if (event.data?.idmrea === 'delivered') last = { kind: event.data.kind, host: String(event.data.host || ''), at: performance.now() };
  });

  function allowed(target) {
    if (!enabled || !target || !/^https?:$/.test(target.protocol)) return true;
    if (target.hostname === location.hostname) return true;
    const recent = last && performance.now() - last.at <= WINDOW_MS ? last : null;
    if (!recent) return false;
    if (recent.kind === 'button') return true;
    return recent.kind === 'link' && recent.host === target.hostname;
  }

  window.open = new Proxy(window.open, {
    apply(open, self, args) {
      const target = args[0] == null || args[0] === '' ? null : parse(String(args[0]));
      if (!allowed(target)) {
        window.postMessage({ idmrea: 'blocked-open', url: target.href }, '*');
        return null;
      }
      return Reflect.apply(open, self, args);
    },
  });
})();
