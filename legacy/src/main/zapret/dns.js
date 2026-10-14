'use strict';
const { execFile } = require('node:child_process');
const { isAdminSync } = require('../util');

// Clean resolvers on this network: 8.8.8.8 and 77.88.8.8 are not hijacked
// by the ISP (unlike 1.1.1.1 and 8.8.4.4 which answer NXDOMAIN for youtube).
const FIX_SERVERS = ['8.8.8.8', '77.88.8.8'];

function runPowerShell(script) {
  return new Promise((resolve) => {
    const ps = `[Console]::OutputEncoding=[Text.Encoding]::UTF8; ${script}`;
    execFile(
      'powershell.exe',
      ['-NoProfile', '-Command', ps],
      { windowsHide: true, timeout: 20000, encoding: 'utf8' },
      (err, stdout) => resolve(String(stdout || ''))
    );
  });
}

function scriptFor(reset) {
  const set = reset
    ? 'Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses'
    : `Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses ${FIX_SERVERS.map((s) => `'${s}'`).join(',')}`;
  return (
    "$ErrorActionPreference='Continue'; $ok=@(); $fail=@(); " +
    "Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } | ForEach-Object { " +
    `try { ${set}; $ok += $_.Name } catch { $fail += $_.Name } }; ` +
    'try { Clear-DnsClientCache } catch {}; ' +
    "Write-Output ('OK:' + $ok.Count); " +
    "$ok | ForEach-Object { Write-Output ('A:' + $_) }; " +
    "$fail | ForEach-Object { Write-Output ('F:' + $_) }"
  );
}

function parse(out) {
  const changed = [];
  const failed = [];
  let okCount = -1;
  for (const raw of out.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    if (line.startsWith('OK:')) okCount = parseInt(line.slice(3), 10) || 0;
    else if (line.startsWith('A:')) changed.push(line.slice(2));
    else if (line.startsWith('F:')) failed.push(line.slice(2));
  }
  return { okCount, changed, failed };
}

async function apply(reset) {
  if (!isAdminSync()) return { ok: false, reason: 'admin_required' };
  const out = await runPowerShell(scriptFor(reset));
  const { okCount, changed, failed } = parse(out);
  if (okCount === 0 && changed.length === 0) return { ok: false, reason: 'no_adapters', failed };
  return { ok: changed.length > 0, changed, failed };
}

async function setDnsFix() {
  return apply(false);
}

async function resetDns() {
  return apply(true);
}

module.exports = { setDnsFix, resetDns };
