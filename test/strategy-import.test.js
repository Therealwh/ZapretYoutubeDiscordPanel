'use strict';
// Tests for strategy badges + .bat import/delete.
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const strategy = require('../src/main/zapret/strategy');

function tmpRoot() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ydp-strat-'));
  fs.mkdirSync(path.join(dir, 'lists'), { recursive: true });
  return dir;
}

const BAT = [
  '@echo off',
  'set "BIN=%~dp0bin\\"',
  'set "LISTS=%~dp0lists\\"',
  'start "zapret" /min "%BIN%winws.exe" --wf-tcp=80,443 --new ^',
  '--filter-tcp=443 --hostlist="%LISTS%list-google.txt" --dpi-desync=fake,multisplit --dpi-desync-repeats=6 ^',
  '--filter-udp=443 --filter-l7=discord,stun --dpi-desync fake --dpi-desync-fake-quic="%BIN%quic.bin"',
  '',
].join('\r\n');

test('detectMethods reads value glued with = and split by comma', () => {
  const m = strategy.detectMethods(['--dpi-desync=fake,multisplit', '--dpi-desync-repeats', '6']);
  assert.ok(m.includes('fake'));
  assert.ok(m.includes('multisplit'));
  assert.ok(!m.includes('repeats'));
});

test('detectMethods reads bare --dpi-desync with separated value', () => {
  const m = strategy.detectMethods(['--dpi-desync', 'fake']);
  assert.deepStrictEqual(m, ['fake']);
});

test('detectMethods reads comma-joined continuation value', () => {
  const m = strategy.detectMethods(['--dpi-desync', 'fake,fakedsplit', '--dpi-desync-repeats', '6']);
  assert.ok(m.includes('fake'));
  assert.ok(m.includes('fakedsplit'));
});

test('detectMethods ignores sub-flags like --dpi-desync-repeats', () => {
  const m = strategy.detectMethods(['--dpi-desync-repeats', '6', '--dpi-desync-fake-tls', 'x.bin']);
  // Only the fake-TLS marker survives; "repeats" is not a desync method.
  assert.ok(!m.includes('repeats'));
  assert.ok(!m.includes('6'));
  assert.deepStrictEqual(m, ['fake TLS']);
});

test('detectMethods flags QUIC, fake TLS and Discord voice filters', () => {
  const m = strategy.detectMethods([
    '--filter-l7', 'discord,stun',
    '--dpi-desync-fake-quic', '"C:\\x\\q.bin"',
    '--dpi-desync-fake-tls', '"C:\\x\\t.bin"',
  ]);
  assert.ok(m.includes('fake QUIC'));
  assert.ok(m.includes('fake TLS'));
  assert.ok(m.includes('Discord voice/STUN'));
});

test('methodBadges produce human labels', () => {
  const b = strategy.methodBadges(['--dpi-desync', 'multisplit', '--dpi-desync-fake-quic', 'q.bin']);
  assert.ok(b.includes('multisplit'));
  assert.ok(b.includes('fake QUIC'));
});

test('safeStrategyName sanitizes path and extension', () => {
  assert.strictEqual(strategy.safeStrategyName('C:\\Users\\me\\My Strat (v2).bat'), 'user-My Strat (v2)');
  // Traversal attempts collapse to the bare basename — nothing escapes the root.
  assert.strictEqual(strategy.safeStrategyName('../../evil.bat'), 'user-evil');
  assert.strictEqual(strategy.safeStrategyName('   '), null);
});

test('isImported recognizes the user- prefix', () => {
  assert.strictEqual(strategy.isImported('user-My Strat'), true);
  assert.strictEqual(strategy.isImported('USER-My Strat'), true);
  assert.strictEqual(strategy.isImported('general.bat'), false);
});

test('importStrategy copies the file and returns badges', () => {
  const root = tmpRoot();
  const src = path.join(root, 'custom.bat');
  fs.writeFileSync(src, BAT, 'utf8');
  const r = strategy.importStrategy(root, src);
  assert.strictEqual(r.ok, true);
  assert.ok(fs.existsSync(r.file));
  assert.ok(r.badges.includes('multisplit'));
});

test('importStrategy refuses a .bat without winws.exe', () => {
  const root = tmpRoot();
  const src = path.join(root, 'nope.bat');
  fs.writeFileSync(src, '@echo off\necho hi', 'utf8');
  const r = strategy.importStrategy(root, src);
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.reason, 'no_winws');
});

test('importStrategy refuses to overwrite an existing name', () => {
  const root = tmpRoot();
  const src = path.join(root, 'dup.bat');
  fs.writeFileSync(src, BAT, 'utf8');
  assert.strictEqual(strategy.importStrategy(root, src).ok, true);
  const again = strategy.importStrategy(root, src);
  assert.strictEqual(again.ok, false);
  assert.strictEqual(again.reason, 'exists');
});

test('deleteStrategy removes imported files only', () => {
  const root = tmpRoot();
  const src = path.join(root, 'gone.bat');
  fs.writeFileSync(src, BAT, 'utf8');
  const imp = strategy.importStrategy(root, src);
  assert.strictEqual(strategy.deleteStrategy(root, imp.file).ok, true);
  assert.strictEqual(fs.existsSync(imp.file), false);

  const upstream = path.join(root, 'general.bat');
  fs.writeFileSync(upstream, BAT, 'utf8');
  const r = strategy.deleteStrategy(root, upstream);
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.reason, 'protected');
  assert.ok(fs.existsSync(upstream));
});

test('listStrategies marks imported files with badges', () => {
  const root = tmpRoot();
  fs.writeFileSync(path.join(root, 'general.bat'), BAT, 'utf8');
  fs.writeFileSync(path.join(root, 'service.bat'), BAT, 'utf8');
  const src = path.join(root, 'mine.bat');
  fs.writeFileSync(src, BAT, 'utf8');
  strategy.importStrategy(root, src);
  const list = strategy.listStrategies(root);
  const names = list.map((s) => s.name);
  assert.ok(names.includes('general.bat'));
  assert.ok(names.includes('user-mine.bat'));
  assert.ok(!names.includes('service.bat'), 'service.bat must stay hidden');
});