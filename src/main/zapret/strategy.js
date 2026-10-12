'use strict';
// winws argument parser — faithful re-implementation of service.bat's
// :service_install parser (Flowseal/zapret-discord-youtube v1.10.3), so that
// "install as service" from this panel produces the same service args as
// the original script. Reference copy: test/fixtures/service-reference.bat
//
// Verified cmd.exe semantics this reproduces:
//   1. `for %%i in (!line!)` tokenizes on space, tab, comma, semicolon and '='
//   2. The state machine ("mergeargs") reassembles tokens:
//        flag → set mergeargs=2; next token (value): "flag value" (space);
//        further tokens continue with ","; a token in args_with_value → "flag value" again
//   3. Quoted tokens containing ":" → escaped-quotes kept; otherwise prefixed with root dir
//   4. First captured line is stripped through winws.exe" (drops start/title/min)
//   5. '^' continuation tokens are skipped; mergeargs state persists across lines

const fs = require('node:fs');
const path = require('node:path');

const ARGS_WITH_VALUE = ['sni', 'host', 'altorder'];

// Desync methods zapret understands — used for strategy badges in the UI.
const KNOWN_METHODS = [
  'fake', 'fakedsplit', 'multisplit', 'multidisorder', 'multisplit_ovl',
  'hostfakesplit', 'syndata', 'split', 'split2', 'disorder', 'disorder2',
  'fake_unknown', 'udplen', 'ipfrag', 'tcpfrag',
];

// Detect `--dpi-desync=` methods plus notable fakes/filters in the args.
//
// Our parser mirrors cmd tokenizing: `=` is a separator, so
// `--dpi-desync=fake,multisplit` becomes `--dpi-desync` + `fake` +
// `multisplit`, while a fake WITHOUT `=` stays glued to its flag
// (`--dpi-desync-fake-quic`). Both shapes must be recognised, and
// `--dpi-desync-*` sub-flags (repeats, fooling, fake-tls…) must NOT match.
function detectMethods(args) {
  const found = new Set();
  const list = args.map((a) => String(a));
  for (let i = 0; i < list.length; i += 1) {
    const eq = list[i].match(/^--dpi-desync=(.+)$/i);
    const bare = !eq && /^--dpi-desync$/i.test(list[i]);
    if (!eq && !bare) continue;
    if (eq) {
      for (const part of eq[1].split(',')) {
        const name = part.trim().toLowerCase();
        if (name) found.add(name);
      }
    }
    // Continuation values: bare tokens until the next "--" flag. A value may
    // itself be comma-joined (--dpi-desync fake,fakedsplit).
    for (let j = i + 1; j < list.length; j += 1) {
      if (list[j].startsWith('--')) break;
      for (const part of list[j].trim().toLowerCase().split(',')) {
        const v = part.trim();
        if (v) found.add(v);
      }
    }
  }
  const all = list.join(' ');
  // Sub-flags reach us WITHOUT '=' (--dpi-desync-fake-quic <path>), and may
  // also arrive glued when the file used the '=' form.
  if (/(?:^|\s)--dpi-desync-fake-quic(?:=|\s|$)/i.test(all)) found.add('fake QUIC');
  if (/fake-tls-mod=rnd/i.test(all)) found.add('auto fake TLS');
  else if (/(?:^|\s)--dpi-desync-fake-tls(?:=|\s|$)/i.test(all)) found.add('fake TLS');
  if (/(?:^|\s)--filter-l7(?:=|\s)\s*discord/i.test(all)) found.add('Discord voice/STUN');
  return [...found];
}

// Short human-readable badges for the strategies list.
function methodBadges(args) {
  const methods = detectMethods(args);
  const badges = [];
  if (methods.includes('multisplit')) badges.push('multisplit');
  if (methods.includes('multidisorder')) badges.push('multidisorder');
  if (methods.includes('fakedsplit')) badges.push('fakedsplit');
  if (methods.includes('hostfakesplit')) badges.push('hostfakesplit');
  if (methods.includes('syndata')) badges.push('syndata');
  if (methods.includes('fake QUIC')) badges.push('fake QUIC');
  if (methods.includes('auto fake TLS')) badges.push('auto fake TLS');
  else if (methods.includes('fake TLS')) badges.push('fake TLS');
  if (methods.includes('Discord voice/STUN')) badges.push('Discord voice');
  return badges;
}

function listStrategies(rootDir) {
  let entries;
  try {
    entries = fs.readdirSync(rootDir, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((e) => e.isFile() && /\.bat$/i.test(e.name) && !/^service/i.test(e.name))
    .map((e) => {
      const name = e.name;
      const natural = name.replace(/(\d+)/g, (_, d) => d.padStart(8, '0'));
      // Best-effort human title: strip extension and accents like (ALT), (FAKE)
      const title = name.replace(/\.bat$/i, '').replace(/^\s*general[\s_-]*/i, '');
      // Badges are best-effort: a parse failure must never hide the strategy.
      let badges = [];
      try {
        badges = methodBadges(parseStrategyFile(path.join(rootDir, name), { rootDir }).args);
      } catch { /* unparsable file still listed */ }
      return {
        file: path.join(rootDir, name),
        name,
        title: title || name,
        badges,
        sortKey: natural,
      };
    })
    .sort((a, b) => (a.sortKey < b.sortKey ? -1 : a.sortKey > b.sortKey ? 1 : 0));
}

// Tokenize like cmd's `for %%i in (!line!)`: split on space/tab, comma, semicolon, '='
function cmdTokenize(line) {
  return line.split(/[\s,;=]+/).filter(Boolean);
}

// Expand cmd %-variables the way `call set` re-expansion does inside service.bat.
// Known vars are replaced; unknown ones stay literal (cmd batch behavior),
// so `%~n0` fragments cannot swallow the following %BIN% (see test/fixtures/exp).
function expandVars(line, vars) {
  let out = '';
  let i = 0;
  while (i < line.length) {
    const c = line[i];
    if (c === '%') {
      const end = line.indexOf('%', i + 1);
      if (end !== -1) {
        const name = line.slice(i + 1, end);
        if (vars.has(name)) {
          out += vars.get(name);
          i = end + 1;
          continue;
        }
      }
    }
    out += c;
    i += 1;
  }
  return out;
}

function buildVarMap(rootDir) {
  const vars = new Map();
  const BIN = path.join(rootDir, 'bin') + path.sep;
  const LISTS = path.join(rootDir, 'lists') + path.sep;
  vars.set('BIN', BIN);
  vars.set('LISTS', LISTS);
  // service.bat status_zapret/load_game_filter set these before install runs;
  // when the utils file is absent the script defaults are 12/12 (mode disabled).
  vars.set('GameFilterTCP', '12');
  vars.set('GameFilterUDP', '12');

  const gfFile = path.join(rootDir, 'utils', 'game_filter.enabled');
  try {
    const raw = fs.readFileSync(gfFile, 'utf8');
    let mode = 'disabled';
    let tcp;
    let udp;
    for (const rawLine of raw.split(/\r?\n/)) {
      const line = rawLine.trim();
      if (!line || line.startsWith('#')) continue;
      const eq = line.indexOf('=');
      const key = (eq === -1 ? line : line.slice(0, eq)).trim().toLowerCase();
      const val = eq === -1 ? '' : line.slice(eq + 1).trim();
      if (key === 'mode') mode = val || 'all';
      else if (key === 'all') mode = 'all';
      else if (key === 'udp' && !val) mode = 'udp';
      else if (key === 'tcp' && !val) mode = 'tcp';
      else if (key === 'udp') udp = val;
      else if (key === 'tcp') tcp = val;
    }
    const tcpRange = tcp || '1024-65535';
    const udpRange = udp || '1024-65535';
    if (mode === 'all') {
      vars.set('GameFilterTCP', tcpRange);
      vars.set('GameFilterUDP', udpRange);
    } else if (mode === 'tcp') {
      vars.set('GameFilterTCP', tcpRange);
      vars.set('GameFilterUDP', '12');
    } else if (mode === 'udp') {
      vars.set('GameFilterTCP', '12');
      vars.set('GameFilterUDP', udpRange);
    }
  } catch {
    /* absent → defaults */
  }
  return vars;
}

function parseStrategyFile(file, opts = {}) {
  const rootDir = opts.rootDir || path.dirname(file);
  const vars = opts.vars || buildVarMap(rootDir);
  const rawLines = fs.readFileSync(file, 'utf8').split(/\r?\n/);

  const args = [];
  let capture = false;
  let mergeargs = 0; // 0 default | 1 params-args | 2 start param | 3 arg-with-value
  let firstCapturedLineDone = false;

  for (const rawLine of rawLines) {
    let line = rawLine;
    if (!capture) {
      if (/winws\.exe/i.test(line)) {
        capture = true;
      } else {
        continue;
      }
    }
    // Expansion (approximates call-set re-expansion inside service.bat)
    line = expandVars(line, vars);
    if (!firstCapturedLineDone) {
      // strip everything through the first `winws.exe"` (inclusive)
      const idx = line.search(/winws\.exe"/i);
      if (idx >= 0) {
        line = line.slice(idx + 'winws.exe"'.length);
        firstCapturedLineDone = true;
      }
    }
    const tokens = cmdTokenize(line);
    for (let token of tokens) {
      if (token === '^' || token === '^^') continue;

      if (/^--/.test(token) && mergeargs !== 0) mergeargs = 0;

      if (token.startsWith('"')) {
        token = token.slice(1, token.length > 1 && token.endsWith('"') ? -1 : undefined);
        const absolute = path.isAbsolute(token) || /^[a-zA-Z]:/.test(token);
        const finalPath = absolute ? token : path.join(rootDir, token);
        token = `"${finalPath}"`;
      }

      if (mergeargs === 1) {
        args[args.length - 1] += `,${token}`;
      } else if (mergeargs === 3) {
        args.push(token);
        mergeargs = 1;
      } else {
        args.push(token);
      }

      if (/^--/.test(token)) {
        mergeargs = 2;
      } else if (mergeargs >= 1) {
        if (mergeargs === 2) mergeargs = 1;
        const bare = token.replace(/^"|"$/g, '');
        if (ARGS_WITH_VALUE.some((x) => x.toLowerCase() === bare.toLowerCase())) {
          mergeargs = 3;
        }
      }
    }
  }

  // Trim the leading junk if stripping failed (defensive) — the parser must
  // never emit launcher tokens like "start" or "/min".
  while (args.length && /^(start|\/min|min)$/i.test(args[0])) args.shift();
  const argsString = args.join(' ');
  return { args, argsString };
}

// Render args for embedding into `sc create ... binPath= "..."`:
// quotes inside must be escaped ("\"C:\path\file\"") exactly as service.bat does.
function toScArgsString(args) {
  return args.map((a) => a.replace(/"/g, '\\"')).join(' ');
}

// ---- import / delete of user strategies ----
const IMPORT_PREFIX = 'user-';

// Sanitize a user-supplied file name into a safe strategy file name.
function safeStrategyName(raw) {
  const base = path.basename(String(raw || '')).replace(/\.bat$/i, '');
  const clean = base
    .replace(/[^\w\s().-]+/g, ' ') // drop path separators and unsafe chars
    .replace(/\s+/g, ' ')
    .trim();
  if (!clean) return null;
  return `${IMPORT_PREFIX}${clean.slice(0, 80)}`;
}

// Copy a .bat into the zapret root as an importable strategy.
// Refuses to overwrite an existing UPSTREAM (non-imported) file, so an import
// can never shadow a shipped general*.bat.
function importStrategy(rootDir, srcFile) {
  let content;
  try {
    content = fs.readFileSync(srcFile, 'utf8');
  } catch {
    return { ok: false, reason: 'read_failed' };
  }
  if (!/winws\.exe/i.test(content)) {
    return { ok: false, reason: 'no_winws' };
  }
  const name = safeStrategyName(srcFile);
  if (!name) return { ok: false, reason: 'bad_name' };
  const dest = path.join(rootDir, `${name}.bat`);
  if (fs.existsSync(dest)) {
    return { ok: false, reason: 'exists', file: dest };
  }
  try {
    fs.writeFileSync(dest, content, 'utf8');
  } catch {
    return { ok: false, reason: 'write_failed' };
  }
  let badges = [];
  try {
    badges = methodBadges(parseStrategyFile(dest, { rootDir }).args);
  } catch { /* imported file may be exotic */ }
  return { ok: true, file: dest, name: path.basename(dest), badges };
}

// Delete an imported strategy. Upstream files (no user- prefix) are protected.
function deleteStrategy(rootDir, file) {
  const base = path.basename(String(file || ''));
  if (!base.toLowerCase().startsWith(IMPORT_PREFIX)) {
    return { ok: false, reason: 'protected' };
  }
  const target = path.join(rootDir, base);
  try {
    fs.unlinkSync(target);
    return { ok: true };
  } catch {
    return { ok: false, reason: 'delete_failed' };
  }
}

function isImported(name) {
  return String(name || '').toLowerCase().startsWith(IMPORT_PREFIX);
}

module.exports = {
  listStrategies, parseStrategyFile, buildVarMap, toScArgsString,
  detectMethods, methodBadges, importStrategy, deleteStrategy, isImported, safeStrategyName,
};
