// What a link really is, from the server's answer to a 1 KB probe, and what is wrong with it. Every warning says why.
import { expectedBytes, extensionOf, westernDigits } from './links.js';
import { hostOf, isFileHost } from './sites.js';

const PROGRAMS = new Set(['exe', 'msi', 'msix', 'appx', 'scr', 'com', 'pif', 'bat', 'cmd', 'vbs', 'vbe', 'js', 'jse', 'wsf', 'hta', 'ps1', 'lnk', 'jar', 'cpl', 'reg']);
const LOOKS_HARMLESS = new Set(['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'txt', 'rtf', 'jpg', 'jpeg', 'png', 'gif', 'webp', 'mp4', 'mkv', 'avi', 'mov', 'mp3', 'flac', 'wav', 'zip', 'rar', '7z', 'iso', 'epub']);
const SMALL_INSTALLER = 50 * 1024 * 1024;
const BIG_PROMISE = 1024 ** 3;

const TYPES = [
  ['Video', ['mp4', 'mkv', 'avi', 'mov', 'wmv', 'webm', 'flv', 'm4v', 'ts']],
  ['Music', ['mp3', 'flac', 'wav', 'aac', 'ogg', 'm4a', 'opus']],
  ['Document', ['pdf', 'epub', 'mobi', 'djvu', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'odt', 'ods', 'odp', 'csv', 'txt']],
  ['Program', ['exe', 'msi', 'msix', 'msixbundle', 'appx', 'appxbundle', 'dmg', 'pkg', 'deb', 'rpm', 'apk', 'xapk', 'apks', 'jar', 'appimage']],
  ['Archive', ['zip', 'rar', '7z', 'tar', 'gz', 'tgz', 'bz2', 'xz', 'zst', 'cab', 'part']],
  ['Disc image', ['iso', 'img', 'bin', 'cue', 'mdf', 'nrg', 'vhd', 'vhdx', 'wim', 'esd']],
  ['Image', ['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg']],
];
export const typeOf = (ext) => (TYPES.find(([, exts]) => exts.includes(ext)) || ['File'])[0];

/** What the first bytes say the file is, whatever its name claims. */
export function sniff(bytes) {
  if (!bytes || bytes.length < 2) return null;
  const at = (i, ...b) => b.every((v, k) => bytes[i + k] === v);
  if (at(0, 0x4d, 0x5a)) return 'windows-program';
  if (at(0, 0x7f, 0x45, 0x4c, 0x46)) return 'linux-program';
  if (at(0, 0xcf, 0xfa, 0xed, 0xfe) || at(0, 0xfe, 0xed, 0xfa, 0xcf) || at(0, 0xca, 0xfe, 0xba, 0xbe)) return 'mac-program';
  if (at(0, 0x50, 0x4b, 0x03, 0x04)) return 'zip';
  if (at(0, 0x52, 0x61, 0x72, 0x21)) return 'rar';
  if (at(0, 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c)) return '7z';
  if (at(0, 0x25, 0x50, 0x44, 0x46)) return 'pdf';
  const head = String.fromCharCode(...bytes.slice(0, 64)).trimStart().toLowerCase();
  if (head.startsWith('<!doctype html') || head.startsWith('<html') || head.startsWith('<head') || head.startsWith('<script')) return 'html';
  return null;
}

/** The file name a Content-Disposition header gives: RFC 5987 filename* wins over filename. */
export function nameFromDisposition(disposition) {
  if (!disposition) return null;
  const star = /filename\*\s*=\s*([^']*)'[^']*'([^;]+)/i.exec(disposition);
  if (star) { try { return decodeURIComponent(star[2].trim().replace(/^"|"$/g, '')); } catch { /* fall through */ } }
  const plain = /filename\s*=\s*("([^"]*)"|[^;]+)/i.exec(disposition);
  return plain ? (plain[2] ?? plain[1]).trim() : null;
}

function nameFromUrl(url) {
  try { return decodeURIComponent(new URL(url).pathname.split('/').pop() || '') || null; } catch { return null; }
}

/** Total size from "bytes 0-1023/12345", or the length of a full answer. */
export function sizeOf({ status, contentRange, contentLength }) {
  const total = /\/(\d+)\s*$/.exec(contentRange || '');
  if (total) return Number(total[1]);
  return status === 200 && contentLength != null && contentLength !== '' ? Number(contentLength) : null;
}

export function formatBytes(bytes) {
  if (bytes == null) return '';
  const units = ['bytes', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes, unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return unit === 0 ? `${value} bytes` : `${value >= 100 ? Math.round(value) : value.toFixed(1).replace(/\.0$/, '')} ${units[unit]}`;
}

const extOfName = (name) => { const dot = (name || '').lastIndexOf('.'); return dot > 0 ? name.slice(dot + 1).toLowerCase() : ''; };

/**
 * kind: 'file' a real file; 'page' an ordinary page; 'host' a file-sharing service's page; 'ad' a link to an ad
 * network; 'blocked' a site on a malware or phishing list; 'unknown' no usable answer.
 */
export function classify({ url, pageUrl = '', status = 0, contentType = '', disposition = '', contentRange = '', contentLength = null,
  firstBytes = null, linkText = '', nearText = '', lists = {} }) {
  const host = hostOf(url);
  const warnings = [];
  const listed = (id) => lists[id]?.has(host);
  if (listed('malware')) return { kind: 'blocked', warnings: [{ code: 'malware-site', text: 'This site is on a malware list.' }] };
  if (listed('phishing')) return { kind: 'blocked', warnings: [{ code: 'phishing-site', text: 'This site is on a list of scam and phishing sites.' }] };
  if (listed('popups') || listed('ads')) return { kind: 'ad', warnings: [{ code: 'ad-link', text: 'Ad link, not a download.' }] };
  if (!(status >= 200 && status < 300)) return { kind: 'unknown', warnings };

  const sniffed = sniff(firstBytes);
  const type = (contentType || '').split(';')[0].trim().toLowerCase();
  const attachment = /^\s*attachment/i.test(disposition || '');
  if (!attachment && (type === 'text/html' || type === 'application/xhtml+xml' || sniffed === 'html')) {
    return { kind: isFileHost(host) ? 'host' : 'page', warnings };
  }

  const name = nameFromDisposition(disposition) || nameFromUrl(url) || 'download';
  const ext = extOfName(name) || extensionOf(url);
  const size = sizeOf({ status, contentRange, contentLength });
  const program = PROGRAMS.has(ext) || sniffed?.endsWith('-program');

  const double = /\.([a-z0-9]{2,5})\.([a-z0-9]{2,4})$/i.exec(name);
  if (double && LOOKS_HARMLESS.has(double[1].toLowerCase()) && PROGRAMS.has(double[2].toLowerCase())) {
    warnings.push({ code: 'double-extension', text: `The name hides a program: it ends in .${double[2].toLowerCase()} after .${double[1].toLowerCase()}.` });
  } else if (sniffed?.endsWith('-program') && !PROGRAMS.has(ext) && !['dmg', 'pkg', 'deb', 'rpm', 'appimage', 'bin', ''].includes(ext)) {
    warnings.push({ code: 'disguised', text: `This is a program, though it is named like a .${ext} file.` });
  }
  if (program && url.startsWith('http:')) {
    warnings.push({ code: 'insecure-program', text: 'A program sent without encryption (HTTP) could have been changed on the way.' });
  }
  const promised = expectedBytes(`${linkText} ${nearText}`);
  if (program && size != null && size < SMALL_INSTALLER && promised != null && promised >= BIG_PROMISE) {
    warnings.push({ code: 'small-installer', text: `This is a ${formatBytes(size)} program, not the ${formatBytes(promised)} file the page describes. It may be a downloader or an ad.` });
  } else if (program && /download(er|[\s_-]*manager)|installer[\s_-]*manager/i.test(westernDigits(name))) {
    warnings.push({ code: 'downloader', text: 'A downloader program, not the file itself.' });
  }
  if (lists.malware?.has(hostOf(pageUrl)) || lists.phishing?.has(hostOf(pageUrl))) {
    warnings.push({ code: 'bad-page', text: 'The page offering this file is on a malware or phishing list.' });
  }
  return { kind: 'file', name, size, sizeText: formatBytes(size), type: typeOf(ext), ext, warnings };
}
