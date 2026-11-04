// Which links on a page are worth checking, and what the text around them promises.
import { hostOf, isFileHost } from './sites.js';

export const FILE_EXTENSIONS = new Set([
  // archives and disc images
  'zip', 'rar', '7z', 'tar', 'gz', 'tgz', 'bz2', 'xz', 'zst', 'cab', 'iso', 'img', 'bin', 'cue', 'mdf', 'nrg', 'vhd', 'vhdx', 'wim', 'esd',
  // programs and packages
  'exe', 'msi', 'msix', 'msixbundle', 'appx', 'appxbundle', 'dmg', 'pkg', 'deb', 'rpm', 'apk', 'xapk', 'apks', 'jar', 'appimage',
  // video and audio
  'mp4', 'mkv', 'avi', 'mov', 'wmv', 'webm', 'flv', 'm4v', 'ts', 'mp3', 'flac', 'wav', 'aac', 'ogg', 'm4a', 'opus',
  // documents
  'pdf', 'epub', 'mobi', 'djvu', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'odt', 'ods', 'odp', 'csv', 'torrent',
]);

const ARABIC_DIGITS = /[٠-٩۰-۹]/g;
/** Arabic-Indic digits become 0-9 before anything reads a number. */
export const westernDigits = (text) => (text || '').replace(ARABIC_DIGITS, (d) => String((d.charCodeAt(0) & 0xf) % 10));

const DOWNLOAD_WORDS = /\b(download|descargar|t[eé]l[eé]charger|herunterladen|scarica|baixar|pobierz|indir|скачать)\b|تحميل|تنزيل|حمّل|حمل الملف/i;

/** The extension of the last path part, in lower case ("part" files like ".001" and ".r00" count as "part"). */
export function extensionOf(url) {
  let path;
  try { path = decodeURIComponent(new URL(url).pathname); } catch { return ''; }
  const name = path.split('/').pop() || '';
  if (/\.(\d{3}|r\d{2})$/i.test(name)) return 'part';
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : '';
}

export function isCandidate({ href, text = '', download = null }) {
  let url;
  try { url = new URL(href); } catch { return false; }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return false;
  if (download !== null && download !== undefined) return true;
  const ext = extensionOf(href);
  if (ext === 'part' || FILE_EXTENSIONS.has(ext)) return true;
  if (isFileHost(hostOf(href))) return true;
  return DOWNLOAD_WORDS.test(text);
}

const UNITS = { tb: 1024 ** 4, gb: 1024 ** 3, gib: 1024 ** 3, 'جيجا': 1024 ** 3, mb: 1024 ** 2, mib: 1024 ** 2, 'ميجا': 1024 ** 2, kb: 1024, kib: 1024 };

/** The largest size a text names ("Download (4.2 GB)", "حجم 3,5 جيجا"), in bytes; null when it names none. */
export function expectedBytes(text) {
  let best = null;
  for (const m of westernDigits(text).matchAll(/(\d+(?:[.,]\d+)?)\s*(TB|GiB|GB|MiB|MB|KiB|KB|جيجا|ميجا)(?![a-z])/gi)) {
    const bytes = parseFloat(m[1].replace(',', '.')) * UNITS[m[2].toLowerCase()];
    if (best === null || bytes > best) best = bytes;
  }
  return best === null ? null : Math.round(best);
}

/** The one SHA-256 written near a link; null if there is none, or several different ones. */
export function checksumNear(text) {
  const found = new Set([...(text || '').matchAll(/(?<![a-f0-9])[a-f0-9]{64}(?![a-f0-9])/gi)].map((m) => m[0].toLowerCase()));
  return found.size === 1 ? [...found][0] : null;
}
