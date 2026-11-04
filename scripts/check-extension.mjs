// End-to-end check of the browser extension: Playwright's Chromium, headless, with the extension loaded, against the
// trap pages of scripts/fixture-server.py, talking to a real engine and native host on their own data folder.
// Run: node scripts/check-extension.mjs   (Windows; needs .NET 10, Python 3 and Playwright with its Chromium)
//
// The browser's native host key (HKCU\Software\Chromium\NativeMessagingHosts\com.idmrea.host, read by Chromium
// builds only, not Chrome or Edge) points at this run's host while it runs and is put back as it was afterwards.
// Everything else stays in artifacts\extension-check.
import { spawn, execFileSync, execSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const work = path.join(root, 'artifacts', 'extension-check');
const engineDir = path.join(work, 'engine');
const dataDir = path.join(work, 'data');
const saveDir = path.join(work, 'downloads');
const profileDir = path.join(work, 'profile');
const shots = path.join(root, 'artifacts', 'screenshots');
const extensionDir = path.join(root, 'src', 'IDMRea.Extension');
const port = 18790;
const site = (name, page) => `http://${name}.test:${port}${page}`;
const HOST_KEY = 'HKCU\\Software\\Chromium\\NativeMessagingHosts\\com.idmrea.host';
const EXTENSION_ID = 'ioenfhcbkjnjlpkmjngfhohmimmjgllg';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let failed = 0;
function check(name, ok, detail = '') {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${!ok && detail ? `: ${detail}` : ''}`);
  if (!ok) failed++;
}

async function until(what, test, ms = 10000) {
  const end = Date.now() + ms;
  for (;;) {
    const value = await test().catch(() => null);
    if (value) return value;
    if (Date.now() > end) return null;
    await sleep(200);
  }
}

function playwright() {
  const require = createRequire(import.meta.url);
  try { return require('playwright'); } catch { /* not in the project */ }
  const globalRoot = execSync('npm root -g', { encoding: 'utf8' }).trim();
  return require(path.join(globalRoot, 'playwright'));
}

/** Playwright's own Chromium, or any other Playwright Chromium build on this PC that starts. */
function chromiumBuilds(chromium) {
  const builds = [process.env.IDMREA_CHROMIUM, chromium.executablePath()];
  const cache = path.join(process.env.LOCALAPPDATA || '', 'ms-playwright');
  if (fs.existsSync(cache)) {
    for (const dir of fs.readdirSync(cache).filter((d) => /^chromium-\d+$/.test(d)).sort().reverse()) {
      builds.push(path.join(cache, dir, 'chrome-win64', 'chrome.exe'));
    }
  }
  return [...new Set(builds.filter((b) => b && fs.existsSync(b)))];
}

// ---------- The engine pipe ----------

const pipeName = (folder) => `\\\\.\\pipe\\IDMRea.${createHash('sha256').update(folder, 'utf8').digest('hex').toUpperCase().slice(0, 20)}`;

function send(request) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(pipeName(dataDir));
    let text = '';
    socket.setEncoding('utf8');
    socket.on('connect', () => socket.write(JSON.stringify({ version: 2, ...request }) + '\n'));
    socket.on('data', (chunk) => {
      text += chunk;
      const end = text.indexOf('\n');
      if (end >= 0) { socket.end(); resolve(JSON.parse(text.slice(0, end))); }
    });
    socket.on('error', reject);
  });
}

// ---------- Registry ----------

function readHostKey() {
  try {
    const out = execFileSync('reg', ['query', HOST_KEY, '/ve'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
    return /REG_SZ\s+(.*)$/m.exec(out)?.[1]?.trim() ?? null;
  } catch { return null; }
}
const writeHostKey = (value) => execFileSync('reg', ['add', HOST_KEY, '/ve', '/d', value, '/f'], { stdio: 'ignore' });
const deleteHostKey = () => { try { execFileSync('reg', ['delete', HOST_KEY, '/f'], { stdio: 'ignore' }); } catch { /* gone */ } };

// ---------- Run ----------

const children = [];
const previousKey = readHostKey();
let context = null;

try {
  for (const dir of [dataDir, saveDir, profileDir]) fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(saveDir, { recursive: true });
  fs.mkdirSync(shots, { recursive: true });

  console.log('Publishing the engine and the native host');
  for (const [project, framework] of [['src/IDMRea.Engine', 'net10.0-windows10.0.19041.0'], ['src/IDMRea.BrowserHost', 'net10.0']]) {
    execFileSync('dotnet', ['publish', project, '-c', 'Release', '-f', framework, '-r', 'win-x64', '--self-contained', 'false', '-o', engineDir, '--nologo', '-v', 'q'],
      { cwd: root, stdio: ['ignore', 'ignore', 'inherit'] });
  }
  const hostExe = path.join(engineDir, 'IDMRea.BrowserHost.exe');
  const env = { ...process.env, IDMREA_DATA_DIRECTORY: dataDir };

  children.push(spawn('python', [path.join(root, 'scripts', 'fixture-server.py'), String(port)], { stdio: 'ignore' }));
  children.push(spawn(path.join(engineDir, 'IDMRea.Engine.exe'), [], { env, stdio: 'ignore' }));
  const first = await until('engine', () => send({ command: 'getSettings' }), 20000);
  if (!first?.ok) throw new Error('The engine did not start.');
  const folders = Object.fromEntries(['Video', 'Music', 'Documents', 'Programs', 'Archives', 'Images', 'Other'].map((c) => [c, saveDir]));
  const saved = await send({ command: 'setSettings', settings: { ...first.settings, folders, notifyOnComplete: false, notifyOnFailure: false } });
  if (!saved.ok) throw new Error(`Settings: ${saved.error}`);

  const manifest = path.join(work, 'com.idmrea.host.json');
  fs.writeFileSync(manifest, JSON.stringify({ name: 'com.idmrea.host', description: 'IDM-Rea check', path: hostExe, type: 'stdio',
    allowed_origins: [`chrome-extension://${EXTENSION_ID}/`] }, null, 2));
  writeHostKey(manifest);

  const { chromium } = playwright();
  const options = {
    headless: true, env, acceptDownloads: true, viewport: { width: 1100, height: 760 },
    args: [`--disable-extensions-except=${extensionDir}`, `--load-extension=${extensionDir}`, '--host-resolver-rules=MAP *.test 127.0.0.1'],
  };
  for (const executablePath of chromiumBuilds(chromium)) {
    try { context = await chromium.launchPersistentContext(profileDir, { ...options, executablePath }); break; } catch (e) { console.log(`(${executablePath} did not start: ${e.message.split('\n')[0]})`); }
  }
  if (!context) throw new Error('No Playwright Chromium build would start. Run: npx playwright install chromium');

  const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker', { timeout: 15000 });
  worker.on('console', (m) => { if (m.type() !== 'log') console.log(`  (extension: ${m.text()})`); });
  check('extension has its fixed ID', worker.url().startsWith(`chrome-extension://${EXTENSION_ID}/`), worker.url());

  // The first list download runs at install; once it has written, add this run's made-up sites to the lists.
  await until('lists', () => worker.evaluate(async () => Boolean((await chrome.storage.local.get('lists')).lists)), 60000);
  await worker.evaluate(async () => {
    const { lists } = await chrome.storage.local.get('lists');
    const add = (id, host) => { lists[id] = { domains: [...(lists[id]?.domains || []), host] }; };
    add('malware', 'bad-site.test'); add('popups', 'ads-net.test'); add('ads', 'adserver.test');
    await chrome.storage.local.set({ lists });
  });
  const ruled = await until('rules', () => worker.evaluate(async () =>
    (await chrome.declarativeNetRequest.getDynamicRules()).some((r) => r.condition.requestDomains?.includes('bad-site.test'))));
  check('block lists become blocking rules', Boolean(ruled));

  const tabOf = (page) => worker.evaluate(async (url) => (await chrome.tabs.query({})).find((t) => t.url === url)?.id, page.url());
  const stateOf = async (page) => { const id = await tabOf(page); return worker.evaluate((tabId) => chrome.tabs.sendMessage(tabId, { type: 'state' }), id); };
  const center = async (page, selector) => { const b = await page.locator(selector).boundingBox(); return [b.x + b.width / 2, b.y + b.height / 2]; };
  const page = await context.newPage();
  const pagesNow = () => context.pages().filter((p) => !p.isClosed()).length;

  // Files on a page: the real ones found, the fake installer named for what it is, the ad left out.
  await page.goto(site('games', '/files.html'));
  const state = await until('files', async () => { const s = await stateOf(page); return s?.files?.length >= 2 ? s : null; });
  const game = state?.files.find((f) => f.name === 'game.iso');
  const setup = state?.files.find((f) => f.name === 'Setup.exe');
  const gameSha = createHash('sha256').update(Buffer.from('IDMRea fixture!\n'.repeat(4 * 1024 * 1024 / 16))).digest('hex');
  check('game.iso is found as a disc image with the checksum beside it', game?.type === 'Disc image' && game?.sha256 === gameSha, JSON.stringify(game));
  check('a 2 MB Setup.exe offered as a 4.2 GB game is called out', Boolean(setup?.warnings.some((w) => w.includes('not the 4.2 GB file'))), JSON.stringify(setup));
  check('the ad link is not offered as a file', state?.files.length === 2, JSON.stringify(state?.files.map((f) => f.name)));
  check('the toolbar badge counts the files', await worker.evaluate(async (tabId) => chrome.action.getBadgeText({ tabId }), await tabOf(page)) === '2');
  await page.hover('#real');
  await sleep(600);
  await page.screenshot({ path: path.join(shots, 'extension-files.png') });

  // An invisible layer over the page: the press goes to the link underneath, and its pop-up never opens.
  let before = pagesNow();
  await page.goto(site('games', '/layer.html'));
  await sleep(1200);
  await page.mouse.click(...await center(page, '#next'));
  check('an invisible layer is taken away and the link under it works',
    Boolean(await until('next', async () => page.url().endsWith('/next.html'), 4000)) && pagesNow() === before, page.url());

  // A see-through empty layer: watched, and removed once its press tries to open a pop-up.
  await page.goto(site('games', '/empty-layer.html'));
  await sleep(1200);
  await page.mouse.click(...await center(page, '#next'));
  check('an empty layer that opens a pop-up is taken away and the link under it works',
    Boolean(await until('next', async () => page.url().endsWith('/next.html'), 4000)) && pagesNow() === before, page.url());

  // A pop-under on every click: a click on empty space opens nothing; a real link to a mirror opens only the mirror.
  await page.goto(site('games', '/popunder.html'));
  await sleep(1200);
  await page.mouse.click(300, 150);
  await sleep(2000);
  check('a click on empty space opens no other site', pagesNow() === before);
  await page.click('#mirror');
  await sleep(2500);
  const urls = context.pages().filter((p) => !p.isClosed()).map((p) => p.url());
  check('a link to a mirror opens the mirror and nothing else', urls.some((u) => u.includes('mirror.test')) && !urls.some((u) => u.includes('other.test')), urls.join(' '));
  for (const p of context.pages()) if (p !== page && !p.isClosed()) await p.close();

  // A fake download button that opens an ad network in a new tab: the tab is closed.
  before = pagesNow();
  await page.goto(site('games', '/fake-button.html'));
  await sleep(1200);
  await page.click('#fake');
  await sleep(2500);
  check('a fake download button\'s ad tab is closed', pagesNow() === before, context.pages().map((p) => p.url()).join(' '));

  // A button that sends the tab to an ad server: the tab comes back.
  await page.goto(site('games', '/redirect.html'));
  await sleep(1200);
  await page.click('#go');
  check('a page sent to an ad server comes back', Boolean(await until('back', async () => page.url().endsWith('/redirect.html') && (await sleep(1500), page.url().endsWith('/redirect.html')), 6000)), page.url());

  // A site on the malware list: the blocked page, and "Continue anyway" for this tab.
  await page.goto(site('bad-site', '/bad.html')).catch(() => {});
  const blocked = await until('blocked', async () => page.url().startsWith(`chrome-extension://${EXTENSION_ID}/blocked.html?why=malware`), 5000);
  check('a malware-listed site shows the blocked page', Boolean(blocked), page.url());
  if (blocked) {
    await page.click('#continue');
    check('Continue anyway opens the site in this tab', Boolean(await until('continue', async () => page.url() === site('bad-site', '/bad.html'), 5000)), page.url());
  }

  const hello = await worker.evaluate(() => new Promise((resolve) => chrome.runtime.sendNativeMessage('com.idmrea.host', { type: 'hello', id: 1 },
    (reply) => resolve(reply || { error: chrome.runtime.lastError?.message }))));
  check('IDM-Rea answers the extension and takes over downloads', hello?.ok === true && hello?.takeover === true, JSON.stringify(hello));

  // A browser download: handed to IDM-Rea with the page's checksum, and the browser's copy removed. The engine looks
  // names up in real DNS, where the made-up *.test sites don't exist, so this page is opened by address.
  await page.goto(`http://127.0.0.1:${port}/files.html`);
  await until('files again', async () => (await stateOf(page))?.files?.length >= 2);
  await page.click('#real').catch(() => {});
  const job = await until('job', async () => {
    const reply = await send({ command: 'list' });
    return reply.jobs?.find((j) => j.url.endsWith('/game.iso') && j.status === 'Completed');
  }, 40000);
  const browserCopies = () => worker.evaluate(async () => (await chrome.downloads.search({})).filter((d) => d.url.includes('game.iso'))
    .map((d) => ({ state: d.state, error: d.error, paused: d.paused, bytes: d.bytesReceived, total: d.totalBytes })));
  check('a browser download is taken over and finishes in IDM-Rea', Boolean(job),
    JSON.stringify({ jobs: (await send({ command: 'list' })).jobs?.map((j) => ({ url: j.url, status: j.status, error: j.errorKind })), browser: await browserCopies() }));
  check('the download is checked against the checksum on the page', job?.verified === true && job?.expectedSha256 === gameSha, JSON.stringify(job && { verified: job.verified, sha: job.expectedSha256 }));
  check('the file is saved with its real contents', fs.existsSync(path.join(saveDir, 'game.iso')) && fs.statSync(path.join(saveDir, 'game.iso')).size === 4 * 1024 * 1024);
  const leftovers = await worker.evaluate(async () => (await chrome.downloads.search({})).filter((d) => d.url.includes('game.iso')).length);
  check('the browser keeps no copy of it', leftovers === 0, `${leftovers} left`);
} catch (error) {
  console.error(error);
  failed++;
} finally {
  if (context) await context.close().catch(() => {});
  await send({ command: 'shutdown' }).catch(() => {});
  for (const child of children) child.kill();
  if (previousKey) writeHostKey(previousKey); else deleteHostKey();
  check('the browser host key is back as it was', readHostKey() === previousKey);
}
console.log(failed ? `${failed} check(s) failed` : 'All checks passed');
process.exit(failed ? 1 : 0);
