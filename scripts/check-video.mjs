// End-to-end check of video downloads against the real world: a real engine on its own data folder sets up its video
// tools from their official releases, downloads a real YouTube video and an AES-128 encrypted HLS stream served from
// this PC, and each result is opened with ffprobe (picture, sound, length).
// Run: node scripts/check-video.mjs [--fresh]   (Windows; needs .NET 10 and an internet connection)
//
// Everything stays in artifacts\video-check. The tools are kept between runs (about 160 MB); --fresh downloads them again.
import { spawn, execFileSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const work = path.join(root, 'artifacts', 'video-check');
const engineDir = path.join(work, 'engine');
const dataDir = path.join(work, 'data');
const saveDir = path.join(work, 'downloads');
const hlsDir = path.join(work, 'hls');
const tools = path.join(dataDir, 'tools');
const ffmpeg = path.join(tools, 'ffmpeg', 'ffmpeg.exe');
const ffprobe = path.join(tools, 'ffmpeg', 'ffprobe.exe');
// "Me at the zoo": 19 seconds, picture and sound as separate streams on YouTube.
const YOUTUBE = 'https://www.youtube.com/watch?v=jNQXAC9IVRw';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let failed = 0;
function check(name, ok, detail = '') {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${!ok && detail ? `: ${detail}` : ''}`);
  if (!ok) failed++;
}

async function until(test, ms) {
  const end = Date.now() + ms;
  for (;;) {
    const value = await test().catch(() => null);
    if (value) return value;
    if (Date.now() > end) return null;
    await sleep(500);
  }
}

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

/** What ffprobe sees in a file: its stream kinds and its length in seconds. */
function probe(file) {
  const out = execFileSync(ffprobe, ['-v', 'error', '-show_entries', 'stream=codec_type:format=duration', '-of', 'json', file], { encoding: 'utf8' });
  const json = JSON.parse(out);
  return { kinds: json.streams.map((s) => s.codec_type).sort(), seconds: Number(json.format.duration) };
}

/** Adds the video, waits for it, and checks the file on disk. */
async function download(name, url, pick, seconds) {
  const found = await send({ command: 'probeVideo', url });
  check(`${name}: the qualities are found`, found.ok && found.video?.choices?.length > 0, found.error ?? JSON.stringify(found.tools));
  if (!found.video) return;
  const choice = pick(found.video.choices);
  check(`${name}: the wanted quality is offered`, Boolean(choice), JSON.stringify(found.video.choices));
  if (!choice) return;
  console.log(`     ${found.video.title} - ${choice.label}`);
  const added = await send({ command: 'addVideo', url: found.video.pageUrl, choice: choice.id });
  check(`${name}: the video is added`, added.ok, added.error);
  const job = await until(async () => {
    const jobs = (await send({ command: 'list' })).jobs ?? [];
    const mine = jobs.find((j) => j.video?.choiceId === choice.id && j.url === found.video.pageUrl);
    if (mine?.status === 'Failed') throw new Error(mine.error);
    return mine?.status === 'Completed' ? mine : null;
  }, 5 * 60 * 1000);
  const last = (await send({ command: 'list' })).jobs?.find((j) => j.video?.choiceId === choice.id);
  check(`${name}: the download completes`, Boolean(job), JSON.stringify(last && { status: last.status, error: last.error, stage: last.video?.stage }));
  if (!job) return;
  if (choice.height) check(`${name}: the file is named after the title and quality`, path.basename(job.destination).includes(`(${choice.height}p)`), job.destination);
  const seen = probe(job.destination);
  check(`${name}: the file has picture and sound`, seen.kinds.join(',') === 'audio,video', seen.kinds.join(','));
  check(`${name}: the file is the full length (${seconds} s)`, Math.abs(seen.seconds - seconds) < 1.5, `${seen.seconds} s`);
  const leftovers = fs.readdirSync(path.dirname(job.destination)).filter((f) => f.endsWith('.part'));
  check(`${name}: no partial files are left`, leftovers.length === 0, leftovers.join(', '));
}

/** A 6-second test clip as an AES-128 encrypted HLS list of 2-second segments, made with the engine's own FFmpeg. */
function makeHls(port) {
  fs.rmSync(hlsDir, { recursive: true, force: true });
  fs.mkdirSync(hlsDir, { recursive: true });
  fs.writeFileSync(path.join(hlsDir, 'key.bin'), randomBytes(16));
  fs.writeFileSync(path.join(hlsDir, 'key.info'), `http://127.0.0.1:${port}/key.bin\n${path.join(hlsDir, 'key.bin')}\n`);
  execFileSync(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=duration=6:size=320x240:rate=25',
    '-f', 'lavfi', '-i', 'sine=frequency=440:duration=6', '-c:v', 'libx264', '-g', '50', '-c:a', 'aac', '-shortest',
    '-hls_time', '2', '-hls_playlist_type', 'vod', '-hls_key_info_file', path.join(hlsDir, 'key.info'),
    '-hls_segment_filename', path.join(hlsDir, 'clip%d.ts'), path.join(hlsDir, 'clip.m3u8')]);
}

const children = [];
let server = null;
try {
  const fresh = process.argv.includes('--fresh');
  for (const dir of [saveDir, fresh ? dataDir : null].filter(Boolean)) fs.rmSync(dir, { recursive: true, force: true });
  for (const file of ['jobs2.db', 'jobs2.db-wal', 'jobs2.db-shm']) fs.rmSync(path.join(dataDir, file), { force: true });
  fs.mkdirSync(saveDir, { recursive: true });

  console.log('Building the engine');
  execFileSync('dotnet', ['publish', 'src/IDMRea.Engine', '-c', 'Release', '-f', 'net10.0', '-r', 'win-x64', '--self-contained', 'false', '-o', engineDir, '--nologo', '-v', 'q'],
    { cwd: root, stdio: ['ignore', 'ignore', 'inherit'] });
  children.push(spawn(path.join(engineDir, 'IDMRea.Engine.exe'), [], { env: { ...process.env, IDMREA_DATA_DIRECTORY: dataDir }, stdio: 'ignore' }));
  const first = await until(() => send({ command: 'getSettings' }), 20000);
  if (!first?.ok) throw new Error('The engine did not start.');
  const folders = Object.fromEntries(['Video', 'Music', 'Documents', 'Programs', 'Archives', 'Images', 'Other'].map((c) => [c, saveDir]));
  const saved = await send({ command: 'setSettings', settings: { ...first.settings, folders, notifyOnComplete: false, notifyOnFailure: false } });
  if (!saved.ok) throw new Error(`Settings: ${saved.error}`);

  // Settings' "Set up now": the engine fetches yt-dlp, Deno and FFmpeg and checks each against its published checksum.
  console.log('Setting up the video tools');
  const started = await send({ command: 'updateVideoTools' });
  check('setting up the tools starts', started.ok, started.error);
  let shown = -1;
  const ready = await until(async () => {
    const reply = await send({ command: 'videoTools' });
    if (reply.tools?.error && !reply.tools.installing) throw new Error(reply.tools.error);
    if (reply.tools?.installing && reply.tools.percent >= shown + 20) { shown = reply.tools.percent; console.log(`     ${shown}%`); }
    return reply.tools?.ready ? reply.tools : null;
  }, 10 * 60 * 1000);
  check('the tools are ready', Boolean(ready), JSON.stringify((await send({ command: 'videoTools' })).tools));
  if (!ready) throw new Error('No video tools.');
  console.log(`     yt-dlp ${ready.ytDlpVersion}`);
  check('FFmpeg and ffprobe are installed', fs.existsSync(ffmpeg) && fs.existsSync(ffprobe));

  // The smallest quality that still has separate picture and sound, so the join is exercised.
  const smallestJoined = (choices) => choices.filter((c) => !c.audioOnly && c.id.includes('+') && c.height).at(-1);
  await download('YouTube', YOUTUBE, smallestJoined, 19);

  const port = await new Promise((resolve) => {
    server = http.createServer((req, res) => {
      const file = path.join(hlsDir, path.basename(new URL(req.url, 'http://x').pathname));
      if (!fs.existsSync(file)) { res.writeHead(404).end(); return; }
      res.writeHead(200, { 'content-type': file.endsWith('.m3u8') ? 'application/vnd.apple.mpegurl' : 'application/octet-stream' });
      fs.createReadStream(file).pipe(res);
    }).listen(0, '127.0.0.1', () => resolve(server.address().port));
  });
  makeHls(port);
  const anyStream = (choices) => choices.find((c) => !c.audioOnly);
  await download('Encrypted HLS', `http://127.0.0.1:${port}/clip.m3u8`, anyStream, 6);
} catch (error) {
  console.error(error);
  failed++;
} finally {
  await send({ command: 'shutdown' }).catch(() => {});
  await sleep(1000);
  for (const child of children) child.kill();
  server?.close();
}
console.log(failed ? `${failed} check(s) failed` : 'All checks passed');
process.exit(failed ? 1 : 0);
