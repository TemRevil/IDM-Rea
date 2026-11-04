import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DomainSet, parseAbp, parseHosts } from '../lib/lists.js';
import { checksumNear, expectedBytes, extensionOf, isCandidate, westernDigits } from '../lib/links.js';
import { sameSite, siteOf, isFileHost } from '../lib/sites.js';
import { classify, formatBytes, nameFromDisposition, sniff } from '../lib/verdict.js';

const GiB = 1024 ** 3, MiB = 1024 ** 2;
const MZ = new Uint8Array([0x4d, 0x5a, 0x90, 0x00]);
const PDF = new Uint8Array([0x25, 0x50, 0x44, 0x46, 0x2d]);
const bytes = (text) => new Uint8Array([...text].map((c) => c.charCodeAt(0)));
const lists = {
  malware: new DomainSet(['malware.example']),
  phishing: new DomainSet(['phish.example']),
  popups: new DomainSet(['popunder.example']),
  ads: new DomainSet(['adserver.example']),
};
const file = (over) => classify({ url: 'https://files.example/game.iso', pageUrl: 'https://games.example/page', status: 206,
  contentType: 'application/octet-stream', contentRange: 'bytes 0-1023/5000000000', firstBytes: new Uint8Array(16), lists, ...over });
const codes = (verdict) => verdict.warnings.map((w) => w.code);

test('hosts and adblock lists keep only whole domains', () => {
  assert.deepEqual(parseHosts('# comment\n0.0.0.0 bad.example\n127.0.0.1 also.bad.example # note\nplain.example\n0.0.0.0 localhost\n0.0.0.0 0.0.0.0\n'),
    ['bad.example', 'also.bad.example', 'plain.example']);
  assert.deepEqual(parseAbp('! comment\n||pop.example^$popup\n||ads.example^\n||site.example/path^\n@@||good.example^\n||only.example^$domain=a.com\n||x.example^$third-party'),
    ['pop.example', 'ads.example', 'x.example']);
});

test('a listed domain covers its subdomains, not its neighbours', () => {
  const set = new DomainSet(['ads.example']);
  assert.ok(set.has('ads.example'));
  assert.ok(set.has('cdn.eu.ads.example'));
  assert.ok(!set.has('badads.example'));
  assert.ok(!set.has('example'));
});

test('sites group subdomains, and two-part suffixes count', () => {
  assert.equal(siteOf('dl.games.example.com'), 'example.com');
  assert.equal(siteOf('shop.example.co.uk'), 'example.co.uk');
  assert.equal(siteOf('www.example.com.sa'), 'example.com.sa');
  assert.ok(sameSite('a.example.com', 'b.example.com'));
  assert.ok(!sameSite('example.com', 'other.com'));
  assert.equal(siteOf('127.0.0.1'), '127.0.0.1');
  assert.ok(isFileHost('www.mediafire.com'));
  assert.ok(!isFileHost('mediafire.com.evil.example'));
});

test('candidate links: file names, file hosts, download words, the download attribute', () => {
  assert.ok(isCandidate({ href: 'https://x.example/files/Game.Setup.part1.rar' }));
  assert.ok(isCandidate({ href: 'https://x.example/files/archive.7z.001' }));
  assert.ok(isCandidate({ href: 'https://www.mediafire.com/file/abc/game' }));
  assert.ok(isCandidate({ href: 'https://x.example/get?id=5', text: 'Download now' }));
  assert.ok(isCandidate({ href: 'https://x.example/get?id=5', text: 'تحميل اللعبة' }));
  assert.ok(isCandidate({ href: 'https://x.example/get', download: '' }));
  assert.ok(!isCandidate({ href: 'https://x.example/about', text: 'About us' }));
  assert.ok(!isCandidate({ href: 'javascript:void(0)', text: 'Download' }));
  assert.ok(!isCandidate({ href: 'mailto:a@b.example', text: 'Download' }));
  assert.equal(extensionOf('https://x.example/a/My%20Game.ISO?x=1'), 'iso');
});

test('sizes named in text, in Arabic digits too', () => {
  assert.equal(expectedBytes('Download (4.2 GB)'), Math.round(4.2 * GiB));
  assert.equal(expectedBytes('Size: 700 MB, or the full 12 GB pack'), 12 * GiB);
  assert.equal(expectedBytes('الحجم ٣,٥ جيجا'), Math.round(3.5 * GiB));
  assert.equal(expectedBytes('no size here'), null);
  assert.equal(westernDigits('٠١٢٣٤٥٦٧٨٩ ۰۹'), '0123456789 09');
});

test('one checksum near a link is used; two different ones are not', () => {
  const a = 'a'.repeat(64), b = 'b'.repeat(64);
  assert.equal(checksumNear(`SHA-256: ${a.toUpperCase()}`), a);
  assert.equal(checksumNear(`${a} and again ${a}`), a);
  assert.equal(checksumNear(`${a} ${b}`), null);
  assert.equal(checksumNear('a'.repeat(65)), null);
  assert.equal(checksumNear('nothing'), null);
});

test('first bytes tell programs, archives and pages apart', () => {
  assert.equal(sniff(MZ), 'windows-program');
  assert.equal(sniff(new Uint8Array([0x7f, 0x45, 0x4c, 0x46])), 'linux-program');
  assert.equal(sniff(PDF), 'pdf');
  assert.equal(sniff(bytes('  <!DOCTYPE html><html>')), 'html');
  assert.equal(sniff(new Uint8Array(16)), null);
});

test('a real file gets its name, size and type and no warnings', () => {
  const v = file();
  assert.equal(v.kind, 'file');
  assert.equal(v.name, 'game.iso');
  assert.equal(v.size, 5000000000);
  assert.equal(v.sizeText, '4.7 GB');
  assert.equal(v.type, 'Disc image');
  assert.deepEqual(v.warnings, []);
});

test('Content-Disposition names win over the address', () => {
  assert.equal(nameFromDisposition(`attachment; filename*=UTF-8''%D9%84%D8%B9%D8%A8%D8%A9.zip; filename="x.zip"`), 'لعبة.zip');
  assert.equal(nameFromDisposition('attachment; filename="Game Setup.exe"'), 'Game Setup.exe');
  assert.equal(file({ disposition: 'attachment; filename="Real.rar"' }).name, 'Real.rar');
});

test('pages are pages, file hosts are marked, attachments are files', () => {
  assert.equal(file({ contentType: 'text/html; charset=utf-8' }).kind, 'page');
  assert.equal(file({ contentType: 'application/octet-stream', firstBytes: bytes('<html><body>') }).kind, 'page');
  assert.equal(file({ url: 'https://www.mediafire.com/file/x', contentType: 'text/html' }).kind, 'host');
  assert.equal(file({ contentType: 'text/html', disposition: 'attachment; filename="page.html"' }).kind, 'file');
  assert.equal(file({ status: 404 }).kind, 'unknown');
});

test('listed sites are blocked or marked as ads', () => {
  assert.equal(file({ url: 'https://dl.malware.example/a.zip' }).kind, 'blocked');
  assert.deepEqual(codes(file({ url: 'https://phish.example/a.zip' })), ['phishing-site']);
  assert.equal(file({ url: 'https://go.popunder.example/click' }).kind, 'ad');
  assert.equal(file({ url: 'https://adserver.example/x' }).kind, 'ad');
  assert.deepEqual(codes(file({ pageUrl: 'https://malware.example/page' })), ['bad-page']);
});

test('a program hidden behind a harmless name', () => {
  assert.deepEqual(codes(file({ url: 'https://files.example/invoice.pdf.exe', firstBytes: MZ })), ['double-extension']);
  assert.match(file({ url: 'https://files.example/invoice.pdf.exe', firstBytes: MZ }).warnings[0].text, /\.exe after \.pdf/);
  assert.deepEqual(codes(file({ url: 'https://files.example/movie.mp4', firstBytes: MZ })), ['disguised']);
  assert.deepEqual(codes(file({ url: 'https://files.example/notes.pdf', firstBytes: PDF })), []);
  assert.deepEqual(codes(file({ url: 'https://files.example/setup.exe', firstBytes: MZ, contentRange: 'bytes 0-1023/300000000' })), []);
  assert.deepEqual(codes(file({ url: 'https://files.example/my.app.v2.exe', firstBytes: MZ, contentRange: 'bytes 0-1023/300000000' })), []);
});

test('a program over plain HTTP', () => {
  assert.deepEqual(codes(file({ url: 'http://files.example/tool.exe', firstBytes: MZ, contentRange: 'bytes 0-1023/300000000' })), ['insecure-program']);
  assert.deepEqual(codes(file({ url: 'http://files.example/video.mp4', contentRange: 'bytes 0-1023/300000000' })), []);
});

test('a small installer where the page promises a big file', () => {
  const small = { url: 'https://files.example/Game_Setup.exe', firstBytes: MZ, contentRange: `bytes 0-1023/${2 * MiB}` };
  assert.deepEqual(codes(file({ ...small, linkText: 'Download Game (45 GB)' })), ['small-installer']);
  assert.match(file({ ...small, linkText: 'Download Game (45 GB)' }).warnings[0].text, /2 MB program, not the 45 GB file/);
  assert.deepEqual(codes(file({ ...small, nearText: 'Full version, 12 GB' })), ['small-installer']);
  assert.deepEqual(codes(file({ ...small, linkText: 'Download the 2 MB tool' })), []);
  assert.deepEqual(codes(file({ ...small, contentRange: `bytes 0-1023/${3 * GiB}`, linkText: '45 GB' })), []);
  assert.deepEqual(codes(file({ ...small, url: 'https://files.example/FastDownloader.exe' })), ['downloader']);
});

test('sizes read like the app shows them', () => {
  assert.equal(formatBytes(512), '512 bytes');
  assert.equal(formatBytes(1536), '1.5 KB');
  assert.equal(formatBytes(2 * MiB), '2 MB');
  assert.equal(formatBytes(150 * GiB), '150 GB');
});
