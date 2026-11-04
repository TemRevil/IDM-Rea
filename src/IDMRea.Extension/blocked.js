// The page shown instead of a blocked site: why, from which list, and the way out.
import { LISTS } from './lib/lists.js';
import { hostOf } from './lib/sites.js';

const REASONS = {
  malware: ['This site spreads malware', 'It is on a public list of sites that send harmful files.'],
  phishing: ['This site is a scam', 'It is on a public list of sites that pose as others to take passwords or payments.'],
  popups: ['This is a pop-up ad network', 'Pages open it behind your back to show ads and fake download buttons.'],
  ad: ['The page sent you to an ad', 'You clicked something on the page, and it sent this tab to an ad server instead.'],
};

const why = new URLSearchParams(location.search).get('why') || 'malware';
const url = location.hash.slice(1); // the blocked address as the browser sent it, already encoded
const host = hostOf(url);
const [title, reason] = REASONS[why] || REASONS.malware;
const list = LISTS.find((l) => l.id === (why === 'ad' ? 'ads' : why));

document.getElementById('title').textContent = title;
document.getElementById('reason').textContent = reason;
document.getElementById('source').textContent = list ? `Source: ${list.source}` : '';
const address = document.getElementById('address');
const at = host ? url.indexOf(host) : -1;
if (at >= 0) {
  const strong = document.createElement('b');
  strong.textContent = host;
  address.append(url.slice(0, at), strong, url.slice(at + host.length));
} else {
  address.textContent = url;
}

document.getElementById('back').addEventListener('click', async () => {
  if (history.length > 1) { history.back(); return; }
  const tab = await chrome.tabs.getCurrent();
  if (tab) chrome.tabs.remove(tab.id);
});

const proceed = document.getElementById('continue');
proceed.disabled = !/^https?:/.test(url);
proceed.addEventListener('click', async () => {
  proceed.disabled = true;
  const reply = await chrome.runtime.sendMessage({ type: 'continue', url });
  if (reply?.ok) location.replace(url);
  else proceed.disabled = false;
});
