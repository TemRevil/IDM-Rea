import { test } from 'node:test';
import assert from 'node:assert/strict';
import { askHost } from '../lib/host.js';

test('a host that is briefly unreachable is asked once more before giving up', async () => {
  const replies = [{ ok: false, connected: false }, { ok: true, version: '1.0.0' }];
  let pauses = 0;
  const reply = await askHost(async () => replies.shift(), async () => { pauses++; });
  assert.deepEqual(reply, { ok: true, version: '1.0.0' });
  assert.equal(pauses, 1);
});

test('a host still unreachable after the second try is reported', async () => {
  let calls = 0;
  const reply = await askHost(async () => { calls++; return { ok: false, connected: false, detail: 'not found' }; }, async () => {});
  assert.equal(reply.connected, false);
  assert.equal(calls, 2);
});

test('an answer from IDM-Rea, even a refusal, is not asked again', async () => {
  let calls = 0;
  const reply = await askHost(async () => { calls++; return { ok: false, error: 'No video on this page.' }; }, async () => {});
  assert.equal(reply.error, 'No video on this page.');
  assert.equal(calls, 1);
});
