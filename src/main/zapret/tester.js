'use strict';
// Strategy tester — port of utils/test zapret.ps1 (standard mode):
// for each general*.bat: stop winws, start strategy, wait for winws.exe,
// run parallel curl checks (HTTP/1.1, TLS1.2, TLS1.3) against targets.txt
// entries + ping, stop, score. Emits live progress via onEvent callback.
//
// Elevation notes:
// - killWinwsAll uses runCommands: direct taskkill when the panel is admin,
//   a single UAC batch otherwise (previously UAC-per-strategy hell).
// - strategies start via cmd start with canonical quotes (matching
//   service.bat's own launch line) instead of brittle nested cmd quoting.
const { spawn } = require('node:child_process');
const path = require('node:path');
const { listStrategies } = require('./strategy');
const { run, isAdminSync } = require('../util');
const { runCommands } = require('../admin');
const { processRunning } = require('./status');

const DEFAULT_TARGETS = {
  DiscordMain: 'https://discord.com',
  DiscordGateway: 'https://gateway.discord.gg',
  DiscordCDN: 'https://cdn.discordapp.com',
  YouTubeWeb: 'https://www.youtube.com',
  YouTubeImage: 'https://i.ytimg.com',
  CloudflareWeb: 'https://www.cloudflare.com',
  CloudflareDNS1111: 'PING:1.1.1.1',
};

function loadTargets(rootDir) {
  const file = path.join(rootDir, 'utils', 'targets.txt');
  try {
    const raw = require('node:fs').readFileSync(file, 'utf8');
    const out = {};
    for (const line of raw.split(/\r?\n/)) {
      const m = line.match(/^\s*(\w+)\s*=\s*"(.+)"\s*$/);
      if (m) out[m[1]] = m[2];
    }
    if (Object.keys(out).length) return out;
  } catch {
    /* defaults */
  }
  return DEFAULT_TARGETS;
}

// One curl probe: HEAD request with specific TLS/http options; returns status word
function curlProbe(url, extraArgs, timeoutSec) {
  const args = [
    '-I', '-s', '-m', String(timeoutSec), '--connect-timeout', String(Math.min(3, timeoutSec)),
    '-o', 'NUL', '-w', '%{http_code}', '--show-error',
    ...extraArgs,
    url,
  ];
  return run('curl.exe', args, { timeout: (timeoutSec + 2) * 1000 }).then(({ code, stdout, stderr }) => {
    const text = (stdout + ' ' + stderr).trim();
    if (code === 0 && /^\d{3}/.test(stdout)) return 'OK';
    if (/Could not resolve host|certificate|SSL/i.test(text)) return 'SSL';
    if (/unsupported|not supported|Unrecognized|Unknown option|schannel/i.test(text)) return 'UNSUP';
    return 'ERR';
  });
}

// One target: 3 http probes (parallel) + optional ping
async function probeTarget(name, value, timeoutSec) {
  if (value.startsWith('PING:')) {
    const host = value.slice(5).trim();
    return { name, kind: 'ping', result: await pingHost(host) };
  }
  const tests = [
    ['HTTP', ['--http1.1']],
    ['TLS1.2', ['--tlsv1.2', '--tls-max', '1.2']],
    ['TLS1.3', ['--tlsv1.3', '--tls-max', '1.3']],
  ];
  const results = await Promise.all(tests.map(([label, extra]) => curlProbe(value, extra, timeoutSec).then((r) => [label, r])));
  return { name, kind: 'url', url: value, results: Object.fromEntries(results) };
}

function pingHost(host) {
  return new Promise((resolve) => {
    const { execFile } = require('node:child_process');
    execFile('ping', ['-n', '1', '-w', '1000', host], { windowsHide: true, timeout: 4000 }, (err, stdout) => {
      if (!err && /TTL=|time[<=]/i.test(stdout || '')) {
        const m = (stdout || '').match(/time[<=](\d+)ms/i);
        return resolve(m ? `${m[1]} ms` : 'OK');
      }
      resolve('Timeout');
    });
  });
}

function waitForWinws(timeoutMs = 8000) {
  return new Promise((resolve) => {
    const started = Date.now();
    const tick = async () => {
      if (await processRunning('winws.exe')) {
        await new Promise((r) => setTimeout(r, 300));
        return resolve(true);
      }
      if (Date.now() - started > timeoutMs) return resolve(false);
      setTimeout(tick, 200);
    };
    tick();
  });
}

function killWinwsAll() {
  return runCommands(['taskkill /IM winws.exe /F']);
}

// Launch winws.exe DIRECTLY with parsed strategy args — no cmd.exe, no
// .bat console windows, no bat version-check noise, no GitHub links.
// The bat wrapper only exists to build args; we already parse them ourselves.
function startStrategyWinws(rootDir, strategyFile) {
  const { args } = require('./strategy').parseStrategyFile(strategyFile, { rootDir });
  if (!args || !args.length) return Promise.resolve(false);
  const winws = path.join(rootDir, 'bin', 'winws.exe');
  const bin = path.join(rootDir, 'bin');
  const q = (s) => s.replace(/'/g, "''");
  const argItems = args.map((a) => `'${q(a)}'`).join(',');
  const ps = `Start-Process -FilePath '${q(winws)}' -ArgumentList @(${argItems}) -WorkingDirectory '${q(bin)}' -WindowStyle Hidden -PassThru | Out-Null`;
  return new Promise((resolve) => {
    const p = spawn('powershell.exe', ['-NoProfile', '-Command', ps], { windowsHide: true, stdio: 'ignore' });
    p.on('exit', (c) => resolve(c === 0));
    p.on('error', () => resolve(false));
  });
}

// Baseline control: is the bypass even running? Without winws the ISP
// throttles googlevideo — so "fast baseline" means the strategy isn't
// actually active (winws died), not that the ISP is unblocked.
function baselineState() {
  return processRunning('winws.exe');
}

// Score: OK tokens count; tiebreak by ping success
function scoreResults(targetResults) {
  let ok = 0;
  let err = 0;
  let unsup = 0;
  let pingOk = 0;
  let pingFail = 0;
  for (const t of targetResults) {
    if (t.kind === 'url') {
      for (const v of Object.values(t.results)) {
        if (v === 'OK') ok++;
        else if (v === 'UNSUP') unsup++;
        else err++;
      }
    } else if (t.kind === 'ping') {
      if (t.result !== 'Timeout') pingOk++;
      else pingFail++;
    }
  }
  return { ok, err, unsup, pingOk, pingFail, total: ok + err + unsup };
}

// Per-service breakdown for verdicts. A connectivity group "works" when at
// least one of its targets passed (DPI block resets kill all probes).
// Reachability-only, exactly like the official zapret tester: groups.youtube
// state is 'ok' when any YouTube target is reachable, 'dead' otherwise.
function groupStats(targetResults) {
  const mk = () => ({ okTargets: 0, total: 0, probesOk: 0, probesAll: 0 });
  const groups = { discord: mk(), youtube: mk(), other: mk() };
  for (const t of targetResults) {
    if (t.kind !== 'url') continue;
    const g = /^discord/i.test(t.name) ? groups.discord : /^youtube/i.test(t.name) ? groups.youtube : groups.other;
    g.total++;
    const okProbes = Object.values(t.results).filter((v) => v === 'OK').length;
    g.probesOk += okProbes;
    g.probesAll += Object.keys(t.results).length;
    if (okProbes > 0) g.okTargets++;
  }
  groups.discord.works = groups.discord.okTargets > 0;
  // Official-zapret style verdict: reachability decides. The optional speed
  // measurement is informational only and never vetoes a working strategy.
  groups.youtube.state = groups.youtube.okTargets > 0 ? 'ok' : 'dead';
  groups.youtube.works = groups.youtube.state === 'ok';
  return groups;
}

// Abort flag shared with the IPC layer so "Stop" actually interrupts the loop.
const abort = { flagged: false };

async function runStrategyTests(rootDir, onEvent, { timeoutSec = 4, stopOnFirst = false } = {}) {
  if (!isAdminSync()) {
    const err = new Error('admin_required');
    err.code = 'admin_required';
    throw err;
  }
  const strategies = listStrategies(rootDir);
  const targets = loadTargets(rootDir);
  const targetList = Object.entries(targets);
  const results = [];
  let winnerFile = null;
  abort.flagged = false;
  await killWinwsAll();

  for (let i = 0; i < strategies.length; i++) {
    if (abort.flagged) break;
    const strat = strategies[i];
    onEvent?.({ type: 'strategy-start', index: i, name: strat.name, total: strategies.length });
    await killWinwsAll();
    if (abort.flagged) break;

    const started = await startStrategyWinws(rootDir, strat.file);
    const up = started ? await waitForWinws() : false;
    if (abort.flagged) {
      await killWinwsAll();
      break;
    }
    if (!up) {
      onEvent?.({ type: 'strategy-fail', name: strat.name, reason: 'not_started' });
      results.push({ name: strat.name, file: strat.file, failed: true, score: { ok: 0, total: 0, pingOk: 0, pingFail: 0, err: 0, unsup: 0 } });
      continue;
    }
    await new Promise((r) => setTimeout(r, 500));

    // Connectivity probes (connect-only sanity: is TCP/TLS path alive)
    const targetResults = [];
    for (let j = 0; j < targetList.length && !abort.flagged; j += 4) {
      const batch = targetList.slice(j, j + 4);
      const batchResults = await Promise.all(batch.map(([name, value]) => probeTarget(name, value, timeoutSec)));
      for (const tr of batchResults) {
        targetResults.push(tr);
        onEvent?.({ type: 'target', strategy: strat.name, target: tr.name, result: tr.kind === 'ping' ? tr.result : tr.results });
      }
    }

    const score = scoreResults(targetResults);
    const groups = groupStats(targetResults);
    results.push({ name: strat.name, file: strat.file, failed: false, score, groups, targets: targetResults });
    onEvent?.({ type: 'strategy-done', name: strat.name, score, groups });

    // stopOnFirst: found a strategy where Discord AND YouTube are reachable —
    // keep it as the winner, LEAVE IT RUNNING and stop searching immediately.
    if (stopOnFirst && groups.discord.works && groups.youtube.state === 'ok') {
      winnerFile = strat.file;
      onEvent?.({ type: 'winner-found', name: strat.name, file: strat.file });
      break;
    }

    await killWinwsAll();
  }

  if (abort.flagged) onEvent?.({ type: 'aborted' });

  // Winner: explicit stopOnFirst match, otherwise max ok with ping tiebreak
  let winner = null;
  if (winnerFile) {
    winner = results.find((r) => r.file === winnerFile);
  } else {
    const okResults = results.filter((r) => !r.failed);
    for (const r of okResults) {
      if (
        !winner ||
        r.score.ok > winner.score.ok ||
        (r.score.ok === winner.score.ok && r.score.pingOk > winner.score.pingOk)
      ) {
        winner = r;
      }
    }
  }
  onEvent?.({ type: 'complete', winner: winner ? winner.name : null, results: results.map((r) => ({ name: r.name, failed: r.failed, score: r.score })) });
  return { results, winner: winner ? winner.name : null, winnerFile: winner ? winner.file : null, aborted: abort.flagged };
}

// Probe reachability WITHOUT touching the running bypass (used to verify the
// currently connected strategy before restarting anything).
async function checkReachability(rootDir, onEvent) {
  const targets = loadTargets(rootDir);
  const targetList = Object.entries(targets);
  const targetResults = [];
  for (let j = 0; j < targetList.length; j += 4) {
    const batch = targetList.slice(j, j + 4);
    const batchResults = await Promise.all(batch.map(([name, value]) => probeTarget(name, value, 4)));
    for (const tr of batchResults) {
      targetResults.push(tr);
      onEvent?.({ type: 'target', target: tr.name, result: tr.kind === 'ping' ? tr.result : tr.results });
    }
  }
  return { groups: groupStats(targetResults), targets: targetResults };
}

module.exports = { runStrategyTests, checkReachability, loadTargets, scoreResults, groupStats, DEFAULT_TARGETS, abort };
