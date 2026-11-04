// Public block lists, downloaded by the extension and matched locally. Nothing about the pages visited leaves the PC.

export const LISTS = [
  { id: 'malware', url: 'https://malware-filter.gitlab.io/malware-filter/urlhaus-filter-hosts.txt', format: 'hosts', source: 'URLhaus (malware-filter)' },
  { id: 'phishing', url: 'https://malware-filter.gitlab.io/malware-filter/phishing-filter-hosts.txt', format: 'hosts', source: 'Phishing filter (malware-filter)' },
  { id: 'popups', url: 'https://raw.githubusercontent.com/easylist/easylist/master/easylist/easylist_adservers_popup.txt', format: 'abp', source: 'EasyList pop-up servers' },
  { id: 'ads', url: 'https://raw.githubusercontent.com/easylist/easylist/master/easylist/easylist_adservers.txt', format: 'abp', source: 'EasyList ad servers' },
];

const DOMAIN = /^(?=.{1,253}$)(?!-)[a-z0-9-]{1,63}(\.(?!-)[a-z0-9-]{1,63})+$/;
const IGNORED = new Set(['localhost', 'localhost.localdomain', 'local', 'broadcasthost', '0.0.0.0', 'ip6-localhost', 'ip6-loopback']);

/** A hosts file ("0.0.0.0 bad.example", "127.0.0.1 bad.example" or a bare "bad.example" per line). */
export function parseHosts(text) {
  const out = new Set();
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.replace(/#.*/, '').trim();
    if (!line) continue;
    const parts = line.split(/\s+/);
    const host = (parts.length > 1 ? parts[1] : parts[0]).toLowerCase();
    if (!IGNORED.has(host) && DOMAIN.test(host)) out.add(host);
  }
  return [...out];
}

/** Adblock rules that block a whole domain ("||bad.example^" with optional options); paths and exceptions are left out. */
export function parseAbp(text) {
  const out = new Set();
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    const match = /^\|\|([a-z0-9.-]+)\^(\$[^/]*)?$/i.exec(line);
    if (!match) continue;
    const options = (match[2] || '').toLowerCase();
    if (/(^|[$,])domain=/.test(options)) continue; // limited to some sites only
    const host = match[1].toLowerCase();
    if (DOMAIN.test(host)) out.add(host);
  }
  return [...out];
}

export function parse(format, text) {
  return format === 'abp' ? parseAbp(text) : parseHosts(text);
}

/** Domains matched with their subdomains: "ads.example" also covers "cdn.ads.example". */
export class DomainSet {
  constructor(domains = []) { this.set = new Set(domains); }
  get size() { return this.set.size; }
  has(host) {
    // Lists hold only names with a dot, so checking stops before a bare top-level domain.
    for (let name = (host || '').toLowerCase(); name.includes('.'); name = name.slice(name.indexOf('.') + 1)) {
      if (this.set.has(name)) return true;
    }
    return false;
  }
}
