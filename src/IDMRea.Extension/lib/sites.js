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
