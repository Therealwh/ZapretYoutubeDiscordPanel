'use strict';
const { run, processImagePath } = require('../util');

const CONFLICT_SERVICES = ['GoodbyeDPI', 'discordfix_zapret', 'winws1', 'winws2'];

// Service state is parsed from the NUMERIC code (1 stopped, 2 start_pending,
// 3 stop_pending, 4 running...). The state WORD is localized on non-English
// Windows (RU prints "Состояние" instead of "STATE"), the numbers are not.
//
// The number must be read from the STATE line, not from the first number in the
// output: `sc query` prints TYPE before STATE, and for WinDivert the TYPE value
// is also "1" (`TYPE : 1  KERNEL_DRIVER`), which made a RUNNING driver look
// STOPPED (issue #3). The type VALUES stay English on every locale, so they are
// used as the anchor.
const SC_TYPE_VALUES = /KERNEL_DRIVER|WIN32_OWN_PROCESS|WIN32_SHARE_PROCESS/i;

function parseScState(stdout) {
  const lines = String(stdout || '').split(/\r?\n/);
  const typeIdx = lines.findIndex((l) => SC_TYPE_VALUES.test(l));
  if (typeIdx >= 0 && lines[typeIdx + 1]) {
    const m = lines[typeIdx + 1].match(/:\s*([1-7])\b/);
    if (m) return Number(m[1]);
  }
  const any = String(stdout || '').match(/:\s*([1-7])\s+\S+/);
  return any ? Number(any[1]) : null;
}

async function queryService(name) {
  const { code, stdout } = await run('sc', ['query', name], { timeout: 6000 });
  if (code !== 0) return { exists: false, running: false, state: null };
  const n = parseScState(stdout);
  return { exists: true, running: n === 4, state: n === 4 ? 'RUNNING' : n === 2 ? 'START_PENDING' : n === 3 ? 'STOP_PENDING' : n === 1 ? 'STOPPED' : 'OTHER' };
}

async function processRunning(image) {
  const { stdout } = await run('tasklist', ['/FI', `IMAGENAME eq ${image}`], { timeout: 6000 });
  return new RegExp(`^${image.replace('.', '\\.')}`, 'im').test(stdout);
}

// Service install strategy name from registry value zapret-discord-youtube
async function getServiceStrategy() {
  const { stdout } = await run(
    'reg',
    ['query', 'HKLM\\System\\CurrentControlSet\\Services\\zapret', '/v', 'zapret-discord-youtube'],
    { timeout: 6000 }
  );
  const m = stdout.match(/zapret-discord-youtube\s+REG_SZ\s+(.+?)\r?\n/i);
  return m ? m[1].trim() : null;
}

// Is the kernel driver loaded? WinDivert works as a driver, and depending on
// the build it may not appear as a service at all.
async function queryDriver(name) {
  const { code, stdout } = await run('driverquery', ['/v', `/${name}`], { timeout: 8000 });
  if (code !== 0) return false;
  return /\bRunning\b/i.test(stdout) || /\bActive\b/i.test(stdout);
}

async function getStatus(rootDir) {
  // WinDivert registers under different names depending on the winws build
  // ("WinDivert" for v2, "WinDivert64" for newer ones) — check both, plus the
  // loaded kernel driver, so the badge reflects reality (issue #3 reported the
  // driver was running while the panel showed "not installed").
  const [zapret, wd1, wd2, wdDriver, winws, winwsPath, strategy] = await Promise.all([
    queryService('zapret'),
    queryService('WinDivert'),
    queryService('WinDivert64'),
    queryDriver('WinDivert64'),
    processRunning('winws.exe'),
    processImagePath('winws.exe'),
    getServiceStrategy(),
  ]);

  const winDivert = wd1.exists ? wd1 : wd2.exists ? wd2 : { exists: wdDriver, running: wdDriver, state: wdDriver ? 'RUNNING' : null, name: 'WinDivert64' };

  let activeMode = 'off';
  if (zapret.running) activeMode = 'service';
  else if (winws) activeMode = 'process';

  const result = {
    activeMode,
    service: zapret,
    winDivert,
    winwsProcess: winws,
    winwsProcessPath: winwsPath,
    serviceStrategy: strategy,
    rootDir,
    conflicts: [],
  };

  // Conflicting bypass services (only when our bypass is off)
  if (activeMode === 'off') {
    const checks = await Promise.all(CONFLICT_SERVICES.map((n) => queryService(n)));
    CONFLICT_SERVICES.forEach((name, i) => {
      if (checks[i].exists) {
        result.conflicts.push({ name, running: checks[i].running, state: checks[i].state });
      }
    });
  }

  return result;
}

// "Something is already running" detector for the startup banner
function deriveBanner(status) {
  if (status.activeMode !== 'off') {
    return { active: true, kind: status.activeMode === 'service' ? 'service' : 'process' };
  }
  const runningConflict = status.conflicts.find((c) => c.running);
  if (runningConflict) return { active: true, kind: 'conflict', name: runningConflict.name };
  return { active: false };
}

module.exports = { getStatus, queryService, parseScState, processRunning, getServiceStrategy, deriveBanner, CONFLICT_SERVICES };
