'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { exists } = require('../util');

// ---- Game Filter (utils/game_filter.enabled) ----
// File format (service.bat): mode=disabled|tcp|udp|all, tcp=<ranges>, udp=<ranges>
function readGameFilter(rootDir) {
  const file = path.join(rootDir, 'utils', 'game_filter.enabled');
  const out = { mode: 'disabled', tcp: '1024-65535', udp: '1024-65535' };
  if (!exists(file)) return out;
  try {
    const raw = fs.readFileSync(file, 'utf8');
    for (const rawLine of raw.split(/\r?\n/)) {
      const line = rawLine.trim();
      if (!line || line.startsWith('#')) continue;
      const eq = line.indexOf('=');
      const key = (eq === -1 ? line : line.slice(0, eq)).trim().toLowerCase();
      const val = eq === -1 ? '' : line.slice(eq + 1).trim();
      if (key === 'mode') out.mode = val || 'all';
      else if (key === 'all') out.mode = 'all';
      else if (key === 'udp' && !val) out.mode = 'udp';
      else if (key === 'tcp' && !val) out.mode = 'tcp';
      else if (key === 'tcp' && val) out.tcp = val;
      else if (key === 'udp' && val) out.udp = val;
    }
    if (!['disabled', 'tcp', 'udp', 'all'].includes(out.mode)) out.mode = 'disabled';
  } catch {
    /* keep defaults */
  }
  return out;
}

// Valid range item: number or a-b, 1..65535, start<=end (as in service.bat)
function validRangeItem(item) {
  const m = item.match(/^(\d{1,5})(?:-(\d{1,5}))?$/);
  if (!m) return false;
  const a = Number(m[1]);
  const b = m[2] !== undefined ? Number(m[2]) : a;
  if (a < 1 || b < 1 || a > 65535 || b > 65535 || a > b) return false;
  return true;
}

function validateRanges(spec) {
  const clean = spec.replace(/\s/g, '');
  if (!clean) return { ok: true, value: '1024-65535' };
  const items = clean.split(',');
  return items.every(validRangeItem) ? { ok: true, value: clean } : { ok: false };
}

function writeGameFilter(rootDir, { mode, tcp, udp }) {
  if (!['disabled', 'tcp', 'udp', 'all'].includes(mode)) {
    return { ok: false, reason: 'bad_mode' };
  }
  const tcpOk = validateRanges(tcp || '1024-65535');
  const udpOk = validateRanges(udp || '1024-65535');
  if (!tcpOk.ok || !udpOk.ok) return { ok: false, reason: 'bad_ranges' };
  const utils = path.join(rootDir, 'utils');
  fs.mkdirSync(utils, { recursive: true });
  const file = path.join(utils, 'game_filter.enabled');
  if (mode === 'disabled') {
    try {
      fs.rmSync(file, { force: true });
    } catch {
      /* ignore */
    }
    return { ok: true };
  }
  fs.writeFileSync(
    file,
    `mode=${mode}\r\ntcp=${tcpOk.value}\r\nudp=${udpOk.value}\r\n`,
    'utf8'
  );
  return { ok: true };
}

// ---- IPSet Filter (lists/ipset-all.txt) ----
// none  -> file contains only 203.0.113.113/32 (service.bat sentinel)
// any   -> file is empty
// loaded-> real list content (backup preserved)
function readIpsetStatus(rootDir) {
  const file = path.join(rootDir, 'lists', 'ipset-all.txt');
  let raw = '';
  try {
    raw = fs.readFileSync(file, 'utf8');
  } catch {
    return 'none';
  }
  if (!raw.trim()) return 'any';
  if (raw.includes('203.0.113.113/32')) return 'none';
  return 'loaded';
}

function writeIpsetMode(rootDir, target) {
  const dir = path.join(rootDir, 'lists');
  const file = path.join(dir, 'ipset-all.txt');
  const backup = path.join(dir, 'ipset-all.txt.backup');
  fs.mkdirSync(dir, { recursive: true });
  const current = readIpsetStatus(rootDir);
  if (target === current) return { ok: true, mode: current };

  if (target === 'loaded') {
    if (!exists(backup)) return { ok: false, reason: 'no_backup' };
    try {
      fs.rmSync(file, { force: true });
      fs.copyFileSync(backup, file);
    } catch {
      return { ok: false, reason: 'restore_failed' };
    }
    return { ok: true, mode: 'loaded' };
  }

  // switching away from 'loaded': preserve content in backup (only if no backup yet)
  if (current === 'loaded' && !exists(backup)) {
    try {
      fs.copyFileSync(file, backup);
    } catch {
      return { ok: false, reason: 'backup_failed' };
    }
  }
  try {
    if (target === 'none') {
      fs.writeFileSync(file, '203.0.113.113/32\r\n', 'utf8');
    } else if (target === 'any') {
      fs.writeFileSync(file, '', 'utf8');
    } else {
      return { ok: false, reason: 'bad_mode' };
    }
  } catch {
    return { ok: false, reason: 'write_failed' };
  }
  return { ok: true, mode: target };
}

function hasIpsetBackup(rootDir) {
  return exists(path.join(rootDir, 'lists', 'ipset-all.txt.backup'));
}

module.exports = {
  readGameFilter,
  writeGameFilter,
  validateRanges,
  readIpsetStatus,
  writeIpsetMode,
  hasIpsetBackup,
};
