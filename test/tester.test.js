'use strict';
const { test } = require('node:test');
const assert = require('node:assert');
const { groupStats } = require('../src/main/zapret/tester');

const urlTarget = (name, results) => ({ kind: 'url', name, results });
const ok3 = { HTTP: 'OK', 'TLS1.2': 'OK', 'TLS1.3': 'UNSUP' };

test('groupStats: youtube speed unavailable -> fall back to reachability (all OK => works)', () => {
  const g = groupStats([
    urlTarget('DiscordMain', ok3),
    urlTarget('YouTubeWeb', ok3),
    urlTarget('YouTubeImage', ok3),
    { kind: 'speed', name: 'YouTubeVideoSpeed', result: { verdict: 'unavailable', bytesPerSec: 0, http: 0 } },
  ]);
  assert.strictEqual(g.discord.works, true);
  assert.strictEqual(g.youtube.state, 'ok');
  assert.strictEqual(g.youtube.works, true);
});

test('groupStats: youtube speed ok => ok even if reachability failed', () => {
  const g = groupStats([
    urlTarget('DiscordMain', { HTTP: 'ERR', 'TLS1.2': 'ERR', 'TLS1.3': 'ERR' }),
    urlTarget('YouTubeWeb', { HTTP: 'ERR', 'TLS1.2': 'ERR', 'TLS1.3': 'ERR' }),
    { kind: 'speed', name: 'YouTubeVideoSpeed', result: { verdict: 'ok', bytesPerSec: 4 * 1024 * 1024, http: 206 } },
  ]);
  assert.strictEqual(g.youtube.state, 'ok');
  assert.strictEqual(g.youtube.works, true);
});

test('groupStats: youtube speed slow => throttled (not dead)', () => {
  const g = groupStats([
    { kind: 'speed', name: 'YouTubeVideoSpeed', result: { verdict: 'slow', bytesPerSec: 64 * 1024, http: 200 } },
  ]);
  assert.strictEqual(g.youtube.state, 'throttled');
  assert.strictEqual(g.youtube.works, false);
});

test('groupStats: youtube speed fail (transfer died) => dead', () => {
  const g = groupStats([
    urlTarget('YouTubeWeb', ok3),
    { kind: 'speed', name: 'YouTubeVideoSpeed', result: { verdict: 'fail', bytesPerSec: 0, http: 403 } },
  ]);
  assert.strictEqual(g.youtube.state, 'dead');
  assert.strictEqual(g.youtube.works, false);
});

test('groupStats: no speed entry => reachability decides', () => {
  const g = groupStats([urlTarget('YouTubeWeb', ok3)]);
  assert.strictEqual(g.youtube.state, 'ok');
  const g2 = groupStats([urlTarget('YouTubeWeb', { HTTP: 'ERR', 'TLS1.2': 'ERR', 'TLS1.3': 'UNSUP' })]);
  assert.strictEqual(g2.youtube.state, 'dead');
});
