// Sites and hosts: which site a host belongs to, and which hosts are known file-sharing services.

// Suffixes where the site is the third label from the right (example.co.uk, not co.uk).
const TWO_PART = new Set([
  'co.uk', 'org.uk', 'ac.uk', 'gov.uk', 'me.uk', 'com.au', 'net.au', 'org.au', 'co.nz', 'co.jp', 'ne.jp', 'or.jp',
  'co.kr', 'com.cn', 'com.tw', 'com.hk', 'com.sg', 'com.my', 'co.id', 'co.in', 'co.za', 'com.br', 'com.mx', 'com.ar',
  'com.tr', 'com.eg', 'com.sa', 'net.sa', 'org.sa', 'com.pk', 'com.ng', 'com.ua', 'co.il', 'com.vn', 'com.ph',
]);

// File-sharing services whose own pages lead to the real file.
const FILE_HOSTS = [
  'mediafire.com', 'mega.nz', 'mega.io', 'gofile.io', 'pixeldrain.com', 'drive.google.com', 'drive.usercontent.google.com',
  'dropbox.com', 'dropboxusercontent.com', 'onedrive.live.com', '1drv.ms', 'workupload.com', 'krakenfiles.com',
  'buzzheavier.com', 'qiwi.gg', 'datanodes.to', '1fichier.com', 'uploadhaven.com', 'rapidgator.net', 'nitroflare.com',
  'ddownload.com', 'sendspace.com', 'catbox.moe', 'sourceforge.net', 'archive.org', 'objects.githubusercontent.com',
  'release-assets.githubusercontent.com', 'files.fm', 'send.now', 'filecrypt.cc', 'terabox.com', 'transfer.it', 'wetransfer.com',
];

export function hostOf(url) {
  try { return new URL(url).hostname.toLowerCase().replace(/\.$/, ''); } catch { return ''; }
}

const isIp = (host) => /^\d{1,3}(\.\d{1,3}){3}$/.test(host) || host.includes(':');

export function siteOf(host) {
  host = (host || '').toLowerCase();
  if (!host || isIp(host)) return host;
  const labels = host.split('.');
  if (labels.length <= 2) return host;
  const lastTwo = labels.slice(-2).join('.');
  return labels.slice(TWO_PART.has(lastTwo) ? -3 : -2).join('.');
}

export const sameSite = (a, b) => siteOf(a) === siteOf(b);

/** True when the host is the domain or one of its subdomains. */
export function within(host, domain) {
  return host === domain || host.endsWith('.' + domain);
}

export const isFileHost = (host) => FILE_HOSTS.some((d) => within(host, d));

/** This PC, the local network, or link-local addresses (router pages, cloud metadata). */
export function isPrivateHost(host) {
  host = (host || '').toLowerCase().replace(/^\[|\]$/g, '');
  if (host === 'localhost' || host.endsWith('.localhost')) return true;
  const v4 = /^(\d{1,3})\.(\d{1,3})\.\d{1,3}\.\d{1,3}$/.exec(host);
  if (v4) {
    const [a, b] = [Number(v4[1]), Number(v4[2])];
    return a === 0 || a === 10 || a === 127 || (a === 169 && b === 254) || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168);
  }
  if (!host.includes(':')) return false;
  return host === '::' || host === '::1' || /^f[cd][0-9a-f]{2}:/.test(host) || /^fe[89ab][0-9a-f]:/.test(host);
}

/**
 * How a page's link may be checked: never on the local network from a page that isn't on that host, and with the
 * browser's sign-in cookies only when the link is on the page's own site (so a page can't make IDM-Rea act signed in
 * somewhere else). Null means: don't check it.
 */
export function probePlan(url, pageUrl) {
  const host = hostOf(url), page = hostOf(pageUrl);
  if (!host) return null;
  if (isPrivateHost(host) && host !== page) return null;
  return { credentials: page && sameSite(host, page) ? 'include' : 'omit' };
}
