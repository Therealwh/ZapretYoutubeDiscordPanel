'use strict';
// Foreign / portable zapret detection + takeover.
//
// Two conflict shapes that break "the panel says everything is fine, but the
// bypass misbehaves" scenarios:
//   1. the `zapret` service exists but its ImagePath points OUTSIDE the zapret
//      root we manage — a foreign install (other GUI, old folder, C:\zapret);
//   2. no service at all, but winws.exe runs — a portable/leftover instance.
// In both cases the panel cannot control the running bypass, and two DPI
// engines fighting over the same socket is the classic cause of a "working"
// strategy that does not work in a browser.
const path = require('node:path');
const fs = require('node:fs');
const { run } = require('../util');

const SERVICE = 'zapret';

// `sc qc <svc>` prints BINARY_PATH_NAME (EN) or Имя_ двоичного_файла (RU).
// Take the line that mentions BIN_PATH in any locale, then strip the label.
async function serviceImagePath(name = SERVICE) {
  const { code, stdout } = await run('sc', ['qc', name], { timeout: 8000 });
  if (code !== 0) return null;
  const line = stdout
    .split(/\r?\n/)
    .find((l) => /BINARY_PATH_NAME|двоичного|BINPATH|путь/i.test(l));
  if (!line) return null;
  const raw = line.slice(line.indexOf(':') + 1).trim().replace(/^"|"$/g, '');
  if (!raw) return null;
  // "C:\dir\winws.exe --args..." → executable only
  const exe = raw.match(/^"?(.+?\.exe)"?(?:\s|$)/i);
  return exe ? exe[1] : raw;
}

// Folder that owns the given winws.exe path (…/bin/winws.exe → …).
function rootOfExe(exePath) {
  if (!exePath) return null;
  const dir = path.dirname(exePath);
  return /[\\/]bin$/i.test(dir) ? path.dirname(dir) : null;
}

function sameRoot(a, b) {
  if (!a || !b) return false;
  const norm = (p) => path.resolve(p).replace(/[\\/]+$/, '').toLowerCase();
  return norm(a) === norm(b);
}

/**
 * Inspect who owns the running bypass.
 * @param {string|null} managedRoot zapret root this panel controls
 */
async function detectOwnership(managedRoot) {
  const out = {
    ownership: 'none', // none | own | foreign | portable
    servicePath: null,
    serviceRoot: null,
    processPath: null,
    processRoot: null,
    reason: null,
    canTakeover: false,
  };

  const imagePath = await serviceImagePath(SERVICE);
  out.servicePath = imagePath;
  out.serviceRoot = rootOfExe(imagePath);
  if (imagePath && out.serviceRoot) {
    if (sameRoot(out.serviceRoot, managedRoot)) out.ownership = 'own';
    else {
      out.ownership = 'foreign';
      out.reason = 'service_outside_root';
    }
  } else if (imagePath) {
    // ImagePath exists but is not a …/bin/winws.exe layout — still foreign.
    out.ownership = 'foreign';
    out.reason = 'service_unrecognized_path';
  }

  const { stdout } = await run(
    'powershell',
    ['-NoProfile', '-Command', "(Get-CimInstance Win32_Process -Filter \"Name='winws.exe'\" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty ExecutablePath)"],
    { timeout: 10000 }
  );
  const procPath = (stdout || '').trim().split(/\r?\n/)[0] || null;
  out.processPath = procPath;
  out.processRoot = rootOfExe(procPath);

  // A winws running from the folder the panel manages is OURS, even without a
  // service (process mode). Only call it "portable" when it runs from somewhere
  // else, or when the panel has no root configured at all — otherwise the
  // banner would never disappear after the user adopts the folder.
  if (out.processPath) {
    if (out.ownership === 'none' || out.ownership === 'own') {
      if (out.processRoot && !sameRoot(out.processRoot, managedRoot)) {
        out.ownership = 'portable';
        out.reason = 'winws_outside_root';
      } else if (out.processRoot && sameRoot(out.processRoot, managedRoot)) {
        out.ownership = 'own';
        out.reason = 'winws_in_managed_root';
      } else if (!out.servicePath) {
        out.ownership = 'portable';
        out.reason = 'winws_without_service';
      }
    }
  }

  out.canTakeover = out.ownership === 'foreign';
  return out;
}

// Replace a foreign service with one we own.
// Safety: refuse unless the detected path really is outside our root — we
// never delete a service that the panel itself installed.
async function takeoverForeignService({ managedRoot, force = false }) {
  const own = await detectOwnership(managedRoot);
  if (own.ownership !== 'foreign' && !force) {
    return { ok: false, reason: 'not_foreign', ownership: own.ownership };
  }
  const { runCommands } = require('../admin');
  const cmds = ['sc query zapret', 'net stop zapret', 'sc delete zapret'];
  const r = await runCommands(cmds);
  if (r && r.ok === false) return { ok: false, reason: r.reason || 'commands_failed' };
  // Kill any winws still running from the foreign folder.
  await run('taskkill', ['/IM', 'winws.exe', '/F'], { timeout: 8000 });
  const after = await detectOwnership(managedRoot);
  return { ok: after.ownership !== 'foreign', ownership: after.ownership, removed: own.servicePath };
}

// Adopt a portable instance: point the panel at the folder that actually runs.
function adoptPortable(detected, configuredRoot) {
  if (!detected || detected.ownership !== 'portable') return { ok: false, reason: 'not_portable' };
  const root = detected.processRoot || configuredRoot;
  if (!root) return { ok: false, reason: 'no_root' };
  try {
    if (!fs.existsSync(path.join(root, 'bin', 'winws.exe'))) {
      return { ok: false, reason: 'not_a_zapret_root' };
    }
  } catch {
    return { ok: false, reason: 'not_a_zapret_root' };
  }
  return { ok: true, root };
}

module.exports = { detectOwnership, takeoverForeignService, adoptPortable, serviceImagePath, rootOfExe, sameRoot };