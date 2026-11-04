// The click-trap rules: which new tabs to close, which page loads to send back, which layers catch clicks.
import { hostOf, sameSite } from './sites.js';

/** A click counts for what opens or loads within this long after it. */
export const CLICK_WINDOW_MS = 1500;
/** A link's right-click menu: choosing "Open link in new tab" may take a while. */
export const MENU_WINDOW_MS = 60_000;

const isAdNetwork = (lists, host) => Boolean(lists.popups?.has(host) || lists.ads?.has(host));
const recentClick = (click, now) => (click && now - click.at <= (click.kind === 'menu' ? MENU_WINDOW_MS : CLICK_WINDOW_MS) ? click : null);

/**
 * A tab a page opened. click is the last click the page reported ({kind: 'link' | 'button' | 'empty' | 'menu', href, at})
 * or null; 'menu' is a right-click on a link.
 * 'allow', 'close', or 'wait' while the tab has no web address yet.
 */
export function newTabVerdict({ sourceUrl, targetUrl, click, now, lists = {} }) {
  const target = /^https?:/i.test(targetUrl || '') ? hostOf(targetUrl) : '';
  if (!target) return 'wait';
  if (isAdNetwork(lists, target)) return 'close';
  if (sameSite(hostOf(sourceUrl), target)) return 'allow';
  const recent = recentClick(click, now);
  if (!recent) return 'close';
  if (recent.kind === 'button') return 'allow';
  return recent.href && sameSite(hostOf(recent.href), target) ? 'allow' : 'close';
}

const CHOSEN = new Set(['typed', 'auto_bookmark', 'generated', 'keyword', 'keyword_generated', 'reload', 'start_page']);

/** A page load in a tab: 'block' when an ad server is reached without the person typing it or clicking a link to it. */
export function redirectVerdict({ url, transition, qualifiers = [], click, now, lists = {} }) {
  const host = hostOf(url);
  if (!isAdNetwork(lists, host)) return 'allow';
  if (CHOSEN.has(transition) || qualifiers.includes('forward_back')) return 'allow';
  const recent = recentClick(click, now);
  return recent?.kind === 'link' && recent.href && sameSite(hostOf(recent.href), host) ? 'allow' : 'block';
}

/**
 * Facts about the element on top where the person pressed: is it a fixed or absolute layer (positioned) covering at
 * least half the window? 'stop' when it is invisible or a link to an ad server: the press never reaches the page.
 * 'watch' when it is merely empty and see-through: menus use those to close on an outside click, so it is removed
 * only if the press opens a pop-up. An invisible or empty layer over a video or canvas is the player's own.
 */
export function clickCatcher({ positioned, coverage, opacity, empty, adLink, overMedia }) {
  if (!positioned || coverage < 0.5) return null;
  if (adLink) return 'stop';
  if (overMedia) return null;
  if (opacity <= 0.1) return 'stop';
  return empty ? 'watch' : null;
}
