'use strict';
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const { execFile } = require('node:child_process');
const { runCommands, elevateBatFile } = require('../admin');
const { isAdminSync, run } = require('../util');
const { toScArgsString, parseStrategyFile } = require('./strategy');
const { parseScState } = require('./status');

// Enable TCP timestamps like service.bat :tcp_enable (best effort)
async function enableTcpTimestamps() {
  return runCommands(['netsh interface tcp set global timestamps=enabled']);
}

// Poll `sc query <name>` until the NUMERIC state matches want (or timeout).
// State words are localized on non-English Windows; numbers are not.
// 1=STOPPED 2=START_PENDING 3=STOP_PENDING 4=RUNNING 5..7 transitions
// A missing service counts as STOPPED.
function waitServiceState(name, want, timeoutMs = 15000) {
  const wantCode = { RUNNING: 4, START_PENDING: 2, STOP_PENDING: 3, STOPPED: 1 }[want] || want;
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve) => {
    const tick = async () => {
      const { code, stdout } = await run('sc', ['query', name], { timeout: 6000 });
      if (code !== 0) return resolve(wantCode === 1);
      const n = parseScState(stdout);
      if (n === wantCode) return resolve(true);
      if (Date.now() > deadline) return resolve(false);
      setTimeout(tick, 500);
    };
    tick();
  });
}

function waitWinws(absent, timeoutMs = 10000) {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve) => {
    const tick = async () => {
      const { stdout } = await run('tasklist', ['/FI', 'IMAGENAME eq winws.exe'], { timeout: 6000 });
      const running = /^winws\.exe/im.test(stdout);
      if (running !== absent) return resolve(true);
      if (Date.now() > deadline) return resolve(false);
      setTimeout(tick, 500);
    };
    tick();
  });
}

// Install the service by writing a .bat with the exact commands of the
// original service.bat :service_install and running it. This avoids the
// cmd.exe double-quoting mangling that breaks `sc create binPath=` when the
// chain is passed as a single execFile argument (sc prints usage, service is
// never created).
async function installStrategyAsService(rootDir, strategyFile) {
  const { args } = parseStrategyFile(strategyFile, { rootDir });
  if (!args.length) return { ok: false, reason: 'parse_failed' };
  const winws = path.join(rootDir, 'bin', 'winws.exe');
  const scArgs = toScArgsString(args);
  const strategyName = path.basename(strategyFile, '.bat');

  const bat = [
    '@echo off',
    'chcp 437 >nul',
    'net stop zapret >nul 2>&1',
    'sc delete zapret >nul 2>&1',
    `sc create zapret binPath= "\\"${winws}\\" ${scArgs}" DisplayName= "zapret" start= auto`,
    'sc description zapret "Zapret DPI bypass software"',
    'sc start zapret',
    `reg add "HKLM\\System\\CurrentControlSet\\Services\\zapret" /v zapret-discord-youtube /t REG_SZ /d "${strategyName}" /f`,
    '',
  ].join('\r\n');

  const batFile = path.join(os.tmpdir(), `ydp-install-${Date.now()}.bat`);
  try {
    fs.writeFileSync(batFile, bat, 'utf8');
  } catch {
    return { ok: false, reason: 'write_failed' };
  }

  let exec;
  if (isAdminSync()) {
    // Direct: cmd runs the bat verbatim, no extra quoting layers.
    exec = await new Promise((resolve) => {
      execFile('cmd.exe', ['/d', '/c', batFile], { windowsHide: true, timeout: 60000 }, (err, stdout, stderr) => {
        resolve({ code: err && err.code != null ? err.code : err ? 1 : 0, stdout: stdout || '', stderr: stderr || '' });
      });
    });
  } else {
    const el = await elevateBatFile(batFile);
    if (!el.ok) {
      try { fs.rmSync(batFile, { force: true }); } catch {}
      return { ok: false, reason: el.cancelled ? 'elevate_cancelled' : 'elevate_failed' };
    }
    exec = { code: el.exitCode, stdout: '', stderr: '' };
  }
  try { fs.rmSync(batFile, { force: true }); } catch {}

  // Trust only the observed state, not exit codes (net stop / sc delete of an
  // absent service fail harmlessly and skew the chain exit code).
  const up = await waitServiceState('zapret', 'RUNNING');
  if (!up) {
    return {
      ok: false,
      reason: 'service_start_failed',
      code: exec.code,
      output: String(exec.stdout || '').split(/\r?\n/).filter(Boolean).slice(-6).join('\n'),
    };
  }
  return { ok: true, args };
}

async function removeServices(rootDir) {
  const cmds = [
    'net stop zapret',
    'sc delete zapret',
    'taskkill /IM winws.exe /F',
    'net stop WinDivert',
    'sc delete WinDivert',
    'net stop WinDivert14',
    'sc delete WinDivert14',
  ];
  const res = await runCommands(cmds);
  const gone = !(await waitServiceState('zapret', 'RUNNING', 3000));
  return { ok: res.ok && gone };
}

async function startService() {
  if (isAdminSync()) await run('sc', ['start', 'zapret'], { timeout: 30000 });
  else await runCommands(['sc start zapret']);
  const up = await waitServiceState('zapret', 'RUNNING');
  return { ok: up, reason: up ? undefined : 'service_start_failed' };
}

async function stopService() {
  const res = await runCommands(['net stop zapret']);
  const down = !(await waitServiceState('zapret', 'RUNNING', 8000));
  return { ok: down, reason: down ? undefined : 'service_stop_failed' };
}

// Launch strategy as a plain process (elevated only when the panel is not
// admin; when elevated we start it directly, no UAC). Minimized window.
// NOTE: process launch needs CANONICAL quotes ("path"), not sc-escaped ones.
function launchStrategyProcess(rootDir, strategyFile) {
  const { args } = parseStrategyFile(strategyFile, { rootDir });
  if (!args.length) return Promise.resolve({ ok: false, reason: 'parse_failed' });
  const bin = path.join(rootDir, 'bin');
  const winws = path.join(bin, 'winws.exe');
  const argsStr = args.join(' ');
  const q = (s) => s.replace(/'/g, "''");
  const ps = [
    `$p = Start-Process -FilePath '${q(winws)}' -ArgumentList '${q(argsStr)}' -WorkingDirectory '${q(bin)}' -WindowStyle Minimized${isAdminSync() ? '' : ' -Verb RunAs'} -PassThru`,
    'if ($p) { exit 0 } else { exit 1 }',
  ].join('; ');
  return new Promise((resolve) => {
    const { spawn: sp } = require('node:child_process');
    const child = sp('powershell.exe', ['-NoProfile', '-Command', ps], { windowsHide: true, stdio: 'ignore' });
    child.on('error', () => resolve({ ok: false, reason: 'launch_failed' }));
    child.on('exit', async (c) => {
      if (c !== 0) return resolve({ ok: false, reason: isAdminSync() ? 'launch_failed' : 'elevate_cancelled' });
      // winws may take a moment to appear in the process list
      const up = await waitWinws(false);
      resolve(up ? { ok: true } : { ok: false, reason: 'process_start_failed' });
    });
  });
}

function killWinws() {
  return runCommands(['taskkill /IM winws.exe /F']);
}

// Stop the WinDivert driver services (no delete) — releases WinDivert64.sys
// so the zapret folder can be replaced during updates. Best effort: wait
// until the driver actually left STOP_PENDING (its .sys file inside the
// zapret tree keeps a handle until then).
async function stopDrivers() {
  const res = await runCommands(['net stop WinDivert', 'net stop WinDivert14']);
  await waitServiceState('WinDivert', 'STOPPED', 12000);
  await waitServiceState('WinDivert14', 'STOPPED', 6000);
  return { ok: true, res };
}

module.exports = {
  enableTcpTimestamps,
  installStrategyAsService,
  removeServices,
  startService,
  stopService,
  launchStrategyProcess,
  killWinws,
  stopDrivers,
  waitServiceState,
};
