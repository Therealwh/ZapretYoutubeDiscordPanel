'use strict';
const { spawn } = require('node:child_process');
const path = require('node:path');
const { isAdminSync, run } = require('./util');

// Build an elevated PowerShell one-shot that runs cmd lines in a hidden cmd.
// Prints ELEVATE_OK only when the UAC launch succeeded (decline => catch).
function elevatedPs(script) {
  return [
    '$psi = New-Object System.Diagnostics.ProcessStartInfo',
    "$psi.FileName = 'cmd.exe'",
    "$psi.Arguments = '/c ' + [string]::Join(' & ', @(",
    ...script.map((l) => `  '${l.replace(/'/g, "''")}',`),
    "  ''",
    '))',
    "$psi.Verb = 'RunAs'",
    "$psi.WindowStyle = 'Hidden'",
    '$psi.UseShellExecute = $true',
    "try { [System.Diagnostics.Process]::Start($psi) | Out-Null; Write-Output 'ELEVATE_OK' } catch { Write-Output 'ELEVATE_FAIL' }",
  ].join('; ');
}

// Run an elevated batch of cmd commands with a single UAC prompt.
// The elevated child outlives us; we can only observe whether the elevation
// itself was granted (UAC decline -> Start throws -> ELEVATE_FAIL).
async function elevateCommands(cmdLines) {
  const script = elevatedPs(cmdLines);
  return new Promise((resolve) => {
    const p = spawn('powershell.exe', ['-NoProfile', '-Command', script], {
      windowsHide: true,
    });
    let out = '';
    p.stdout?.on('data', (d) => (out += d));
    p.on('error', () => resolve({ ok: false, cancelled: true }));
    p.on('exit', () => {
      if (out.includes('ELEVATE_OK')) resolve({ ok: true });
      else resolve({ ok: false, cancelled: true });
    });
  });
}

// Run an elevated .bat file with a single UAC prompt and wait for it.
// Returns { ok, cancelled, exitCode }: ok=false/cancelled=true when UAC was
// declined or the launch failed.
async function elevateBatFile(batFile) {
  const q = (s) => String(s).replace(/'/g, "''");
  const script = [
    `try {`,
    `  $p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/d /c "${q(batFile)}"' -Verb RunAs -WindowStyle Hidden -Wait -PassThru`,
    `  exit $p.ExitCode`,
    `} catch { exit 3 }`,
  ].join('; ');
  return new Promise((resolve) => {
    const p = spawn('powershell.exe', ['-NoProfile', '-Command', script], {
      windowsHide: true,
    });
    p.on('error', () => resolve({ ok: false, cancelled: true, exitCode: 3 }));
    p.on('exit', (code) => {
      // 3 = Start-Process threw (UAC declined / elevation unavailable)
      if (code === 3) resolve({ ok: false, cancelled: true, exitCode: code });
      else if (code === 0) resolve({ ok: true, cancelled: false, exitCode: code });
      else resolve({ ok: false, cancelled: false, exitCode: code });
    });
  });
}

// Run cmd commands with admin rights: directly when the panel is already
// elevated (no UAC flicker), otherwise via one elevated batch (single UAC).
async function runCommands(cmdLines) {
  if (isAdminSync()) {
    const script = cmdLines.join(' & ');
    const r = await run('cmd.exe', ['/d', '/s', '/c', script], { timeout: 60000 });
    return { ok: true, elevated: false, code: r.code, output: r.stdout, stderr: r.stderr };
  }
  const res = await elevateCommands(cmdLines);
  return { ...res, elevated: true };
}

// Restart this app elevated (single UAC prompt). Used by the UI banner.
function restartElevated() {
  const exe = process.execPath;
  const isPackaged = !/electron\.exe$/i.test(path.basename(exe));
  let target;
  let args;
  if (isPackaged) {
    target = exe;
    args = [];
  } else {
    // dev mode: relaunch through electron cli
    target = exe;
    args = ['.'];
  }
  const script = [
    `$p = Start-Process -FilePath '${target.replace(/'/g, "''")}'${args.length ? ` -ArgumentList '${args.join(' ')}'` : ''} -Verb RunAs -PassThru`,
    'if ($p) { Start-Sleep -Milliseconds 800; exit 0 } else { exit 1 }',
  ].join('; ');
  const p = spawn('powershell.exe', ['-NoProfile', '-Command', script], {
    windowsHide: true,
    stdio: 'ignore',
    detached: true,
  });
  p.unref();
  return true;
}

module.exports = { elevateCommands, elevateBatFile, restartElevated, isAdminSync, runCommands };
