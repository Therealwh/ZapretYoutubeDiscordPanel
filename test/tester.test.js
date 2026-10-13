'use strict';
const { test } = require('node:test');
const assert = require('node:assert');
const { groupStats, isWorking, orderTargets, DECISIVE_TARGETS } = require('../src/main/zapret/tester');

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

test('isWorking matches the stopOnFirst verdict and tolerates junk', () => {
  assert.strictEqual(isWorking(groupStats([urlTarget('DiscordMain', ok3), urlTarget('YouTubeWeb', ok3)])), true);
  assert.strictEqual(isWorking(groupStats([urlTarget('DiscordMain', err3), urlTarget('YouTubeWeb', ok3)])), false);
  assert.strictEqual(isWorking(null), false);
  assert.strictEqual(isWorking(undefined), false);
  assert.strictEqual(isWorking({}), false);
});

test('orderTargets puts one Discord + one YouTube target first', () => {
  const list = orderTargets([
    ['DiscordGateway', 'https://gateway.discord.gg'],
    ['YouTubeImage', 'https://i.ytimg.com'],
    ['CloudflareWeb', 'https://www.cloudflare.com'],
    ['DiscordMain', 'https://discord.com'],
    ['YouTubeWeb', 'https://www.youtube.com'],
    ['CloudflareDNS1111', 'PING:1.1.1.1'],
  ]);
  assert.deepStrictEqual(list.slice(0, 2).map(([n]) => n), DECISIVE_TARGETS);
  // Nothing is lost or duplicated by the reordering.
  assert.strictEqual(list.length, 6);
  assert.strictEqual(new Set(list.map(([n]) => n)).size, 6);
});

test('orderTargets keeps working when targets.txt has no decisive entry', () => {
  const list = orderTargets([['YouTubeWeb', 'u'], ['DiscordMain', 'u'], ['GoogleMain', 'u']]);
  assert.strictEqual(list.length, 3);
  assert.ok(list.map(([n]) => n).includes('GoogleMain'));
});

test('orderTargets leaves an empty target list alone', () => {
  assert.deepStrictEqual(orderTargets([]), []);
});
