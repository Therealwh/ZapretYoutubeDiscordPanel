'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { run, exists } = require('../util');
const { pathIssues } = require('./paths');
const { parseScState } = require('./status');

// Diagnostics mirror of service.bat :service_diagnostics.
// Severity: ok (green), warn (yellow), fail (red)
async function scQueryRunning(name) {
  const { code, stdout } = await run('sc', ['query', name], { timeout: 6000 });
  if (code !== 0) return null;
  return parseScState(stdout) === 4; // 4 = RUNNING (locale-independent)
}

async function anyServiceMatching(substr) {
  const { stdout } = await run('sc', ['query'], { timeout: 15000 });
  return new RegExp(`SERVICE_NAME\\s*:\\s*\\S*${substr}`, 'i').test(stdout);
}

async function regQueryValue(key, value) {
  const { stdout } = await run('reg', ['query', key, '/v', value], { timeout: 6000 });
  const re = new RegExp(`${value}\\s+REG_(?:SZ|DWORD)\\s+(\\S+)`, 'i');
  const m = stdout.match(re);
  return m ? m[1] : null;
}

const CHECKS = [
  {
    id: 'bfe',
    async run(rootDir) {
      const running = await scQueryRunning('BFE');
      return running === true
        ? { severity: 'ok' }
        : { severity: 'fail', detail: 'bfe_off', link: 'https://github.com/Flowseal/zapret-discord-youtube#readme' };
    },
  },
  {
    id: 'proxy',
    async run() {
      const enable = await regQueryValue(
        'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings',
        'ProxyEnable'
      );
      if (enable !== '0x1') return { severity: 'ok' };
      const server = await regQueryValue(
        'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings',
        'ProxyServer'
      );
      return { severity: 'warn', detail: 'proxy_on', meta: { server: server || '?' } };
    },
  },
  {
    id: 'tcp_timestamps',
    async run() {
      // netsh output is localized (RU prints no "timestamps...enabled");
      // service.bat switches the console to cp437 for the same reason.
      const { stdout } = await run(
        'cmd.exe',
        ['/d', '/s', '/c', 'chcp 437 >nul & netsh interface tcp show global'],
        { timeout: 12000 }
      );
      if (/timestamps.*enabled/i.test(stdout)) return { severity: 'ok' };
      return { severity: 'warn', detail: 'ts_disabled' };
    },
  },
  {
    id: 'adguard',
    async run() {
      const { stdout } = await run('tasklist', ['/FI', 'IMAGENAME eq AdguardSvc.exe'], { timeout: 6000 });
      return /AdguardSvc\.exe/im.test(stdout)
        ? { severity: 'fail', detail: 'adguard', link: 'https://github.com/Flowseal/zapret-discord-youtube/issues/417' }
        : { severity: 'ok' };
    },
  },
  {
    id: 'killer',
    async run() {
      const found = await anyServiceMatching('Killer');
      return found ? { severity: 'fail', detail: 'killer', link: 'https://github.com/Flowseal/zapret-discord-youtube/issues/2512' } : { severity: 'ok' };
    },
  },
  {
    id: 'intel_connectivity',
    async run() {
      // service.bat: sc query | findstr Intel + Connectivity + Network
      const { stdout } = await run('sc', ['query'], { timeout: 15000 });
      const names = [...stdout.matchAll(/SERVICE_NAME\s*:\s*(.+)/g)].map((m) => m[1]);
      const hit = names.find((n) => /Intel/i.test(n) && /Connectivity/i.test(n) && /Network/i.test(n));
      return hit
        ? { severity: 'fail', detail: 'intel_conn', meta: { name: hit }, link: 'https://github.com/ValdikSS/GoodbyeDPI/issues/541' }
        : { severity: 'ok' };
    },
  },
  {
    id: 'check_point',
    async run() {
      const a = await anyServiceMatching('TracSrvWrapper');
      const b = await anyServiceMatching('EPWD');
      return a || b ? { severity: 'fail', detail: 'checkpoint' } : { severity: 'ok' };
    },
  },
  {
    id: 'smartbyte',
    async run() {
      const found = await anyServiceMatching('SmartByte');
      return found ? { severity: 'fail', detail: 'smartbyte' } : { severity: 'ok' };
    },
  },
  {
    id: 'path',
    async run(rootDir) {
      const issues = pathIssues(rootDir || '');
      if (issues.includes('cyrillic')) return { severity: 'warn', detail: 'cyrillic_path' };
      if (issues.includes('onedrive')) return { severity: 'fail', detail: 'onedrive' };
      return { severity: 'ok' };
    },
  },
  {
    id: 'windivert_sys',
    async run(rootDir) {
      if (rootDir && exists(path.join(rootDir, 'bin'))) {
        const binFiles = fs.readdirSync(path.join(rootDir, 'bin'));
        const hasSys = binFiles.some((f) => f.toLowerCase().endsWith('.sys'));
        if (!hasSys) return { severity: 'fail', detail: 'no_windivert_sys' };
      }
      return { severity: 'ok' };
    },
  },
  {
    id: 'vpn',
    async run() {
      const { stdout } = await run('sc', ['query'], { timeout: 15000 });
      const names = [...stdout.matchAll(/SERVICE_NAME\s*:\s*(.+)/g)].map((m) => m[1]);
      const hits = names.filter((n) => /VPN/i.test(n));
      return hits.length
        ? { severity: 'warn', detail: 'vpn_services', meta: { names: hits.join(', ') } }
        : { severity: 'ok' };
    },
  },
  {
    id: 'secure_dns',
    async run() {
      // DoH active on any interface? (same registry probe as service.bat)
      const ps =
        "$c = (Get-ChildItem -Recurse -Path 'HKLM:\\System\\CurrentControlSet\\Services\\Dnscache\\InterfaceSpecificParameters\\' -ErrorAction SilentlyContinue | Get-ItemProperty -ErrorAction SilentlyContinue | Where-Object { $_.DohFlags -gt 0 } | Measure-Object).Count; if ($c -gt 0) { exit 0 } else { exit 1 }";
      const { code } = await run('powershell', ['-NoProfile', '-Command', ps], { timeout: 20000 });
      return code === 0 ? { severity: 'ok' } : { severity: 'warn', detail: 'no_doh' };
    },
  },
  {
    id: 'hosts_youtube',
    async run() {
      const hosts = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'drivers', 'etc', 'hosts');
      try {
        const content = fs.readFileSync(hosts, 'utf8');
        if (/youtube\.com|youtu\.be/i.test(content)) return { severity: 'warn', detail: 'hosts_youtube' };
      } catch {
        /* unreadable hosts */
      }
      return { severity: 'ok' };
    },
  },
  {
    id: 'conflict_services',
    async run() {
      const names = ['GoodbyeDPI', 'discordfix_zapret', 'winws1', 'winws2'];
      const found = [];
      for (const n of names) {
        const res = await scQueryRunning(n);
        if (res !== null) found.push(n);
      }
      return found.length ? { severity: 'fail', detail: 'conflicts', meta: { names: found.join(', ') } } : { severity: 'ok' };
    },
  },
];

async function runDiagnostics(rootDir) {
  const results = [];
  for (const check of CHECKS) {
    try {
      const r = await check.run(rootDir);
      results.push({ id: check.id, ...r });
    } catch {
      results.push({ id: check.id, severity: 'warn', detail: 'check_error' });
    }
  }
  return results;
}

module.exports = { runDiagnostics, CHECKS };
