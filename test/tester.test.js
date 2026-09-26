'use strict';
const { test } = require('node:test');
const assert = require('node:assert');
const { groupStats } = require('../src/main/zapret/tester');

const urlTarget = (name, results) => ({ kind: 'url', name, results });
const ok3 = { HTTP: 'OK', 'TLS1.2': 'OK', 'TLS1.3': 'UNSUP' };
const err3 = { HTTP: 'SSL', 'TLS1.2': 'SSL', 'TLS1.3': 'UNSUP' };

test('groupStats: youtube reachable -> ok (no speed entry needed)', () => {
  const g = groupStats([
    urlTarget('DiscordMain', ok3),
    urlTarget('YouTubeWeb', ok3),
    urlTarget('YouTubeImage', ok3),
  ]);
  assert.strictEqual(g.discord.works, true);
  assert.strictEqual(g.youtube.state, 'ok');
  assert.strictEqual(g.youtube.works, true);
});

test('groupStats: youtube unreachable -> dead', () => {
  const g = groupStats([urlTarget('YouTubeWeb', err3), urlTarget('YouTubeShort', err3)]);
  assert.strictEqual(g.youtube.state, 'dead');
  assert.strictEqual(g.youtube.works, false);
});

test('groupStats: one reachable youtube target is enough', () => {
  const g = groupStats([
    urlTarget('YouTubeWeb', err3),
    urlTarget('YouTubeImage', ok3),
  ]);
  assert.strictEqual(g.youtube.state, 'ok');
  assert.strictEqual(g.youtube.works, true);
});

test('groupStats: discord ok + youtube ok -> both work (stopOnFirst condition)', () => {
  const g = groupStats([urlTarget('DiscordMain', ok3), urlTarget('YouTubeWeb', ok3)]);
  const shouldStop = g.discord.works && g.youtube.state === 'ok';
  assert.strictEqual(shouldStop, true);
});

test('groupStats: discord dead, youtube ok -> no stop (search continues)', () => {
  const g = groupStats([urlTarget('DiscordMain', err3), urlTarget('YouTubeWeb', ok3)]);
  const shouldStop = g.discord.works && g.youtube.state === 'ok';
  assert.strictEqual(shouldStop, false);
});

test('groupStats: unrelated targets go to other group', () => {
  const g = groupStats([urlTarget('CloudflareWeb', ok3), urlTarget('GoogleMain', ok3)]);
  assert.strictEqual(g.other.total, 2);
  assert.strictEqual(g.discord.total, 0);
  assert.strictEqual(g.youtube.total, 0);
});
