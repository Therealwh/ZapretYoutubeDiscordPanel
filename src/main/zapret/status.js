'use strict';
const { run, processImagePath } = require('../util');

const CONFLICT_SERVICES = ['GoodbyeDPI', 'discordfix_zapret', 'winws1', 'winws2'];

// Service state is parsed from the NUMERIC code (1 stopped, 2 start_pending,
// 3 stop_pending, 4 running...). The state word is localized on non-English
// Windows (RU prints "Состояние" instead of "STATE"), the number is not.
function parseScState(stdout) {
  const m = stdout.match(/:\s*([1-7])\s+\S+/);
  return m ? Number(m[1]) : null;
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

async function getStatus(rootDir) {
  const [zapret, winDivert, winws, winwsPath, strategy] = await Promise.all([
    queryService('zapret'),
    queryService('WinDivert'),
    processRunning('winws.exe'),
    processImagePath('winws.exe'),
    getServiceStrategy(),
  ]);

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
