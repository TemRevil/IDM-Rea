import { test } from 'node:test';
import assert from 'node:assert/strict';
import { qualifies, isMediaRequest, choiceRows, mediaNotes } from '../lib/video.js';

test('a video qualifies when it is visible and large enough', () => {
  const player = { width: 854, height: 480, opacity: 1, hidden: false, loop: false, muted: false, controls: false };
  assert.equal(qualifies(player), true);
  assert.equal(qualifies({ ...player, width: 199 }), false); // a preview thumbnail
  assert.equal(qualifies({ ...player, height: 111 }), false);
  assert.equal(qualifies({ ...player, width: 200, height: 112 }), true);
  assert.equal(qualifies({ ...player, hidden: true }), false);
  assert.equal(qualifies({ ...player, opacity: 0 }), false);
  assert.equal(qualifies({ ...player, loop: true, muted: true }), false); // a moving background
  assert.equal(qualifies({ ...player, loop: true, muted: true, controls: true }), true); // a muted clip someone can play
});

test('media requests are recognised', () => {
  assert.equal(isMediaRequest({ url: 'https://cdn.test/v/master.m3u8?token=1', type: 'xmlhttprequest' }), true);
  assert.equal(isMediaRequest({ url: 'https://cdn.test/v/manifest.mpd', type: 'xmlhttprequest' }), true);
  assert.equal(isMediaRequest({ url: 'https://cdn.test/v/clip.mp4', type: 'media' }), true);
  assert.equal(isMediaRequest({ url: 'https://cdn.test/v/stream', type: 'other', contentType: 'application/vnd.apple.mpegurl; charset=utf-8' }), true);
  assert.equal(isMediaRequest({ url: 'https://cdn.test/play', type: 'media' }), true); // the browser says it is media
  assert.equal(isMediaRequest({ url: 'https://cdn.test/v/seg-00012.ts', type: 'xmlhttprequest' }), false); // one segment of a list
  assert.equal(isMediaRequest({ url: 'https://cdn.test/v/chunk-3.m4s', type: 'xmlhttprequest' }), false);
  assert.equal(isMediaRequest({ url: 'https://cdn.test/app.js', type: 'xmlhttprequest' }), false);
  assert.equal(isMediaRequest({ url: 'blob:https://site.test/1', type: 'media' }), false);
  assert.equal(isMediaRequest({ url: 'https://site.test/beep.mp3', type: 'media' }), false); // sounds are not videos
});

test('media notes keep the latest few, without repeats', () => {
  let notes = [];
  for (let i = 0; i < 25; i++) notes = mediaNotes(notes, `https://cdn.test/${i}.m3u8`);
  notes = mediaNotes(notes, 'https://cdn.test/24.m3u8');
  assert.equal(notes.length, 20);
  assert.equal(notes[0], 'https://cdn.test/24.m3u8'); // newest first
  assert.equal(notes.at(-1), 'https://cdn.test/5.m3u8');
});

test('choice labels show quality and size', () => {
  const rows = choiceRows([
    { id: '137+140', label: '1080p MP4, 255 MB', container: 'mp4', bytes: 267_891_149, height: 1080, audioOnly: false },
    { id: '136+140', label: '720p MP4, 98 MB', container: 'mp4', bytes: null, height: 720, audioOnly: false },
    { id: '140', label: 'Sound only M4A, 9.8 MB', container: 'm4a', bytes: 10_271_496, height: null, audioOnly: true },
    { id: 'file', label: 'MP4, 2 MB', container: 'mp4', bytes: 2_097_152, height: null, audioOnly: false },
  ]);
  assert.deepEqual(rows, [
    { id: '137+140', quality: '1080p', detail: 'MP4, 255 MB' },
    { id: '136+140', quality: '720p', detail: 'MP4' },
    { id: '140', quality: 'Sound only', detail: 'M4A, 9.8 MB' },
    { id: 'file', quality: 'Video file', detail: 'MP4, 2 MB' },
  ]);
});
