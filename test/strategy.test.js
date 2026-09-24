'use strict';
// TDD test: winws argument parser must reproduce service.bat :service_install
// output for the same strategy file. Semantics verified against the byte-level
// source of service.bat (v1.10.3):
//  - cmd FOR-tokenization splits on space/tab/comma/semicolon/EQUALS
//  - state machine reassembles: flag, first value (space), further values (comma)
//  - quoted tokens containing ":" become escaped-quote paths; otherwise %~dp0-prefixed
//  - first captured line is stripped through winws.exe" (drops start/title/min)
const { test } = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');
const { parseStrategyFile, listStrategies, toScArgsString } = require('../src/main/zapret/strategy');

const ROOT = path.join(__dirname, 'root');
const FIXTURE = path.join(__dirname, 'fixtures', 'ALT.bat');

function setupRoot() {
  fs.rmSync(ROOT, { recursive: true, force: true });
  fs.mkdirSync(path.join(ROOT, 'bin'), { recursive: true });
  fs.mkdirSync(path.join(ROOT, 'lists'), { recursive: true });
  fs.writeFileSync(path.join(ROOT, 'bin', 'winws.exe'), 'dummy');
  fs.copyFileSync(FIXTURE, path.join(ROOT, 'strat-test.bat'));
  fs.writeFileSync(path.join(ROOT, 'service.bat'), '@echo off\r\nexit /b\r\n');
}

test('listStrategies finds strategy bats, ignores service*.bat, sorts naturally', () => {
  setupRoot();
  fs.writeFileSync(path.join(ROOT, 'general 2.bat'), 'x');
  fs.writeFileSync(path.join(ROOT, 'general 10.bat'), 'x');
  const list = listStrategies(ROOT).map((s) => path.basename(s.file));
  assert.ok(list.includes('strat-test.bat'));
  assert.ok(!list.some((n) => /^service/i.test(n)), 'service*.bat excluded');
  const g = list.filter((n) => /^general/i.test(n));
  assert.deepStrictEqual(g, ['general 2.bat', 'general 10.bat'], 'natural sort: 2 before 10');
});

test('first captured line is stripped through winws.exe (no start/title junk)', () => {
  setupRoot();
  const { args } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  assert.strictEqual(args[0], '--wf-tcp', 'args begin with first real flag');
  assert.ok(!args.some((a) => /start|\/min|zapret:|winws/i.test(a)), 'no launcher junk');
});

test('flag + values merge: first value space-joined, rest comma-joined', () => {
  setupRoot();
  const { args } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  const i = args.indexOf('--wf-tcp');
  assert.ok(i >= 0);
  assert.strictEqual(args[i + 1], '80,443,2053,2083,2087,2096,8443,12', 'wf-tcp values merged');
  const j = args.indexOf('--filter-udp');
  assert.strictEqual(args[j + 1], '443', 'filter-udp first section is single value');
});

test('quoted paths resolved to absolute with quotes kept on token', () => {
  setupRoot();
  const { args } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  assert.ok(args.includes(`"${path.join(ROOT, 'lists', 'list-general.txt')}"`), 'lists path');
  assert.ok(args.includes(`"${path.join(ROOT, 'bin', 'quic_initial_www_google_com.bin')}"`), 'bin path');
  assert.ok(args.includes(`"${path.join(ROOT, 'bin', 'ACTIVE_DISCORD_UDP.bin')}"`), 'active fake path');
});

test('value tokens survive as separate argv entries (fooling=ts, pattern=0x00, new)', () => {
  setupRoot();
  const { args } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  assert.ok(args.includes('--dpi-desync-fooling'));
  assert.strictEqual(args[args.indexOf('--dpi-desync-fooling') + 1], 'ts');
  assert.strictEqual(args[args.indexOf('--dpi-desync-fakedsplit-pattern') + 1], '0x00');
  assert.ok(args.includes('--new'));
  assert.strictEqual(args[args.indexOf('--ip-id') + 1], 'zero');
});

test('continuation lines merge into one arg stream (many tokens total)', () => {
  setupRoot();
  const { args } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  assert.ok(args.length > 120, `expected 120+ argv tokens, got ${args.length}`);
});

test('GameFilter vars default to 12 when utils/game_filter.enabled absent', () => {
  setupRoot();
  const { args } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  const i = args.indexOf('--wf-tcp');
  assert.strictEqual(args[i + 1], '80,443,2053,2083,2087,2096,8443,12');
});

test('GameFilter ranges honored from utils/game_filter.enabled (mode=all)', () => {
  setupRoot();
  fs.mkdirSync(path.join(ROOT, 'utils'), { recursive: true });
  fs.writeFileSync(
    path.join(ROOT, 'utils', 'game_filter.enabled'),
    'mode=all\r\ntcp=1024-1934\r\nudp=1024-1934\r\n'
  );
  const { args } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  const i = args.indexOf('--wf-tcp');
  assert.strictEqual(args[i + 1], '80,443,2053,2083,2087,2096,8443,1024-1934');
});

test('GameFilter mode=tcp leaves UDP at 12 and vice versa', () => {
  setupRoot();
  fs.mkdirSync(path.join(ROOT, 'utils'), { recursive: true });
  fs.writeFileSync(
    path.join(ROOT, 'utils', 'game_filter.enabled'),
    'mode=tcp\r\ntcp=2000-3000\r\n'
  );
  const { args } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  const t = args.indexOf('--wf-tcp');
  const u = args.indexOf('--wf-udp');
  assert.strictEqual(args[t + 1], '80,443,2053,2083,2087,2096,8443,2000-3000');
  assert.strictEqual(args[u + 1], '443,19294-19344,50000-50100,12');
});

test('argsString joins tokens; quoted paths kept quoted', () => {
  setupRoot();
  const { args, argsString } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  assert.ok(argsString.startsWith('--wf-tcp 80,443'));
  assert.ok(argsString.includes(`"${path.join(ROOT, 'lists', 'list-general.txt')}"`));
  assert.strictEqual(argsString, args.join(' '));
});

test('toScArgsString escapes embedded quotes for sc binPath= embedding', () => {
  setupRoot();
  const { args } = parseStrategyFile(path.join(ROOT, 'strat-test.bat'));
  const scStr = toScArgsString(args);
  assert.ok(scStr.includes(`\\"${path.join(ROOT, 'lists', 'list-general.txt')}\\"`), 'quotes escaped');
  assert.ok(!scStr.replace(/\\"/g, '').includes('"'), 'no unescaped inner quotes remain');
});
