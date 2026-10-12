'use strict';
// Regression tests for launching zapret from a path that CONTAINS SPACES.
//
// Cmd-style tokenizing used to split `--hostlist="C:\Program Files\...\x.txt"`
// into three tokens; winws then read the fragments as a comma list and exited
// with `cannot access ipset file 'C:\Program\,Files\...'`, i.e. the bypass
// simply refused to start (reported in issue #3 as "winws never appears").
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { parseStrategyFile, rejoinFlagValues, stripArgQuotes } = require('../src/main/zapret/strategy');

function tmpRoot() {
  // The whole point is a path WITH a space, so build the root deliberately.
  const base = path.join(os.tmpdir(), 'ydp spaced root');
  fs.mkdirSync(path.join(base, 'lists'), { recursive: true });
  return base;
}

const BAT = [
  '@echo off',
  'set "BIN=%~dp0bin\\"',
  'set "LISTS=%~dp0lists\\"',
  'start "zapret" /min "%BIN%winws.exe" --wf-tcp=80,443 --new ^',
  '--hostlist="%LISTS%list-general.txt" --dpi-desync=fake,multisplit',
  '',
].join('\r\n');

test('a quoted list path with spaces stays in one argument', () => {
  const dir = tmpRoot();
  try {
    const file = path.join(dir, 'spaced.bat');
    fs.writeFileSync(file, BAT, 'utf8');
    const { args } = parseStrategyFile(file, { rootDir: dir });
    // The parser keeps the flag and its quoted value as separate tokens, but
    // the value token must be the WHOLE path — that is the regression.
    const value = args.find((a) => String(a).startsWith('"') && a.includes('list-general.txt'));
    assert.ok(value, 'the quoted path must be its own token');
    assert.ok(value.includes(' '), 'the quoted path must keep its space');
    assert.ok(value.includes(path.join(dir, 'lists', 'list-general.txt')), 'full path must be intact');
    assert.ok(!value.includes(',Files'), 'path must not be split into comma fragments');
    // Rejoining is what makes it usable in a single command-line string.
    const merged = rejoinFlagValues(args).find((a) => a.startsWith('--hostlist='));
    assert.ok(merged && merged.includes(' '), 'rejoined flag keeps the quoted path');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('every list reference in a spaced strategy stays intact', () => {
  const dir = tmpRoot();
  try {
    const file = path.join(dir, 'spaced2.bat');
    fs.writeFileSync(
      file,
      BAT.replace(
        '--hostlist="%LISTS%list-general.txt"',
        '--hostlist="%LISTS%list-general.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt"'
      ),
      'utf8'
    );
    const { args } = parseStrategyFile(file, { rootDir: dir });
    const joined = rejoinFlagValues(args);
    for (const flag of ['--hostlist=', '--ipset-exclude=']) {
      const found = joined.find((a) => a.startsWith(flag));
      assert.ok(found, `${flag} must be present`);
      assert.ok(found.includes(' '), `${flag} keeps its quoted path`);
      assert.ok(!found.includes(','), `${flag} must not contain split fragments`);
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('rejoinFlagValues restores flag=value pairs after tokenizing', () => {
  const out = rejoinFlagValues(['--wf-tcp', '80,443', '--dpi-desync', 'fake']);
  assert.ok(out.includes('--wf-tcp=80,443'));
  assert.ok(out.includes('--dpi-desync=fake'));
});

test('rejoinFlagValues leaves already-joined flags alone', () => {
  const out = rejoinFlagValues(['--wf-tcp=80,443', '--new']);
  assert.deepStrictEqual(out, ['--wf-tcp=80,443', '--new']);
});

test('stripArgQuotes removes only the outer quotes', () => {
  const quoted = '"C:\\a b\\x.txt"';
  assert.deepStrictEqual(stripArgQuotes([quoted, '--flag']), ['C:\\a b\\x.txt', '--flag']);
  // A flag carrying a quoted value must stay untouched.
  assert.deepStrictEqual(stripArgQuotes(['--hostlist="x"']), ['--hostlist="x"']);
});