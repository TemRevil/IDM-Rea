// The toolbar menu: is IDM-Rea there, the files on this page, what was blocked, the guard switch, the lists.
const $ = (id) => document.getElementById(id);
const el = (tag, className, text) => { const e = document.createElement(tag); if (className) e.className = className; if (text != null) e.textContent = text; return e; };
const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
const web = /^https?:/.test(tab?.url || '');

const state = await chrome.runtime.sendMessage({ type: 'menu-state', tabId: tab?.id, url: tab?.url || '' });
const connected = Boolean(state?.app?.ok);
$('status').classList.toggle('on', connected);
$('status-text').textContent = connected ? 'Connected' : 'Not connected';
if (!connected) {
  $('connect-hint').hidden = false;
  const detail = state?.app?.detail || '';
  $('connect-hint').textContent = !detail || /not found/i.test(detail)
    ? 'Downloads stay in the browser until IDM-Rea is connected. In IDM-Rea, open Settings, Browser, and press "Add to browser".'
    : `IDM-Rea is added to this browser, but the browser could not reach it: ${detail}`;
}

// ---------- Files ----------

let files = [];
if (web) {
  try { files = (await chrome.tabs.sendMessage(tab.id, { type: 'state' }))?.files || []; } catch { files = []; }
}
const boxes = [];
if (!web) {
  $('files-title').textContent = 'Files';
  $('no-files').textContent = 'Open a web page to see its files.';
  $('no-files').hidden = false;
} else if (!files.length) {
  $('no-files').hidden = false;
} else {
  $('files-title').textContent = `Files on this page (${files.length})`;
  const groups = new Map();
  for (const f of files) groups.set(f.type, [...(groups.get(f.type) || []), f]);
  for (const [type, list] of [...groups].sort((a, b) => a[0].localeCompare(b[0]))) {
    const group = el('div', 'group');
    group.append(el('div', 'group-name', type));
    for (const f of list) {
      const row = el('label', 'file');
      const box = el('input');
      box.type = 'checkbox';
      box.checked = !f.warnings.length;
      box.addEventListener('change', count);
      boxes.push({ box, file: f });
      row.append(box, el('span', 'name', f.name), el('span', 'size', f.sizeText || ''));
      for (const w of f.warnings) row.append(el('span', 'warning', w));
      group.append(row);
    }
    $('files').append(group);
  }
  $('files-actions').hidden = false;
  count();
}

function count() {
  const n = boxes.filter((b) => b.box.checked).length;
  $('download').textContent = n ? `Download ${plural(n, 'file', 'files')}` : 'Download selected';
  $('download').disabled = !n || !connected;
}

$('download').addEventListener('click', async () => {
  const items = boxes.filter((b) => b.box.checked).map(({ file }) => ({ url: file.url, fileName: file.name, sha256: file.sha256 }));
  $('download').disabled = true;
  const reply = await chrome.runtime.sendMessage({ type: 'download-many', items, referrer: tab.url });
  $('download').textContent = reply?.ok ? `Sent ${plural(reply.added, 'file', 'files')} to IDM-Rea` : reply?.error || 'IDM-Rea did not answer.';
});

// ---------- Protection ----------

const s = state?.stats || { popups: 0, layers: 0, redirects: 0 };
const parts = [];
if (s.popups) parts.push(plural(s.popups, 'pop-up', 'pop-ups'));
if (s.layers) parts.push(plural(s.layers, 'hidden layer', 'hidden layers'));
if (s.redirects) parts.push(plural(s.redirects, 'ad redirect', 'ad redirects'));
$('stats').textContent = parts.length ? `Blocked in this tab: ${parts.join(', ')}.` : 'Nothing blocked in this tab yet.';

$('guard').checked = Boolean(state?.guard);
$('guard').disabled = !web;
$('guard-text').textContent = web && state?.site ? `Block click traps on ${state.site}` : 'Block click traps on this site';
$('guard').addEventListener('change', () => chrome.runtime.sendMessage({ type: 'set-guard', url: tab.url, on: $('guard').checked }));

// ---------- Lists ----------

function showLists(updated) {
  $('lists').textContent = updated
    ? `Lists updated ${new Date(updated).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', numberingSystem: 'latn' })}`
    : 'Lists not downloaded yet';
}
showLists(state?.listsUpdated);
$('update').addEventListener('click', async () => {
  $('update').disabled = true;
  $('update').textContent = 'Updating';
  const reply = await chrome.runtime.sendMessage({ type: 'update-lists' });
  showLists(reply?.updated);
  $('update').textContent = reply?.ok ? 'Updated' : 'Some lists could not be reached';
});
