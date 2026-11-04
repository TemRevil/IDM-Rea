import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DomainSet } from '../lib/lists.js';
import { CLICK_WINDOW_MS, clickCatcher, newTabVerdict, redirectVerdict } from '../lib/guard.js';

const lists = {
  malware: new DomainSet(['malware.example']),
  phishing: new DomainSet([]),
  popups: new DomainSet(['popunder.example']),
  ads: new DomainSet(['adserver.example']),
};
const now = 100_000;
const click = (kind, href = null, ago = 200) => ({ kind, href, at: now - ago });
const tab = (targetUrl, lastClick, sourceUrl = 'https://games.example/page/1') =>
  newTabVerdict({ sourceUrl, targetUrl, click: lastClick, now, lists });

test('a new tab after a click on a link to that site is allowed', () => {
  assert.equal(tab('https://mediafire.com/file/x', click('link', 'https://www.mediafire.com/file/x')), 'allow');
});

test('a new tab to another site with no click is closed', () => {
  assert.equal(tab('https://somewhere.example/', null), 'close');
});

test('a click on empty space never opens another site', () => {
  assert.equal(tab('https://somewhere.example/', click('empty')), 'close');
});

test('a link click that opens a different site than the link is closed', () => {
  assert.equal(tab('https://somewhere.example/', click('link', 'https://games.example/download')), 'close');
});

test('a button may open another site unless it is an ad network', () => {
  assert.equal(tab('https://accounts.example/login', click('button')), 'allow');
  assert.equal(tab('https://cdn.popunder.example/go', click('button')), 'close');
  assert.equal(tab('https://adserver.example/c', click('button')), 'close');
});

test('a click older than the window does not count', () => {
  assert.equal(tab('https://mediafire.com/f', click('link', 'https://mediafire.com/f', CLICK_WINDOW_MS + 1)), 'close');
  assert.equal(tab('https://mediafire.com/f', click('link', 'https://mediafire.com/f', CLICK_WINDOW_MS)), 'allow');
});

test('the same site may open its own tabs', () => {
  assert.equal(tab('https://cdn.games.example/file.zip', null), 'allow');
});

test('a tab with no address yet is left alone until it gets one', () => {
  assert.equal(tab('about:blank', null), 'wait');
});

const load = (url, transition, lastClick = null, qualifiers = []) =>
  redirectVerdict({ url, transition, qualifiers, click: lastClick, now, lists });

test('a page sent to an ad server without a click is blocked', () => {
  assert.equal(load('https://adserver.example/x', 'link', click('link', 'https://games.example/download')), 'block');
  assert.equal(load('https://adserver.example/x', 'link', null, ['client_redirect']), 'block');
  assert.equal(load('https://adserver.example/x', 'link', click('button')), 'block');
});

test('an ad server the person typed or clicked as a plain link loads', () => {
  assert.equal(load('https://adserver.example/x', 'typed'), 'allow');
  assert.equal(load('https://adserver.example/x', 'link', click('link', 'https://adserver.example/x')), 'allow');
  assert.equal(load('https://adserver.example/x', 'link', null, ['forward_back']), 'allow');
});

test('ordinary pages are never blocked by the redirect guard', () => {
  assert.equal(load('https://games.example/next', 'link', null, ['client_redirect']), 'allow');
});

const layer = (facts) => clickCatcher({ positioned: true, coverage: 1, opacity: 1, empty: false, adLink: false, overMedia: false, ...facts });

test('an invisible layer or an ad link over most of the window is stopped at once', () => {
  assert.equal(layer({ opacity: 0.05 }), 'stop');
  assert.equal(layer({ opacity: 0.1 }), 'stop');
  assert.equal(layer({ adLink: true }), 'stop');
});

test('an empty see-through layer is only watched: menus use those to close on an outside click', () => {
  assert.equal(layer({ empty: true }), 'watch');
});

test('visible content, small layers and unpositioned elements are not traps', () => {
  assert.equal(layer({}), null);
  assert.equal(layer({ opacity: 0.11 }), null);
  assert.equal(layer({ empty: true, coverage: 0.49 }), null);
  assert.equal(layer({ opacity: 0, positioned: false }), null);
});

test('a layer over a video player is the player, but an ad link over it is not', () => {
  assert.equal(layer({ empty: true, overMedia: true }), null);
  assert.equal(layer({ opacity: 0, overMedia: true }), null);
  assert.equal(layer({ adLink: true, overMedia: true }), 'stop');
});
