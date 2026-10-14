'use strict';
const { execFile } = require('node:child_process');
const fs = require('node:fs');

// Decode console output robustly: commands we parse emit ASCII status words
// (RUNNING, winws.exe, STATE), so latin1 never throws on OEM codepages.
function run(cmd, args, opts = {}) {
  return new Promise((resolve) => {
    execFile(
      cmd,
      args,
      {
        windowsHide: true,
        timeout: opts.timeout || 10000,
        encoding: 'latin1',
        cwd: opts.cwd,
      },
      (err, stdout, stderr) => {
        resolve({ code: err && err.code != null ? err.code : err ? 1 : 0, stdout: stdout || '', stderr: stderr || '' });
      }
    );
  });
}

function runRaw(cmd, args, opts = {}) {
  return new Promise((resolve) => {
    execFile(
      cmd,
      args,
      { windowsHide: true, timeout: opts.timeout || 10000, cwd: opts.cwd },
      (err, stdout, stderr) => {
        resolve({ code: err && err.code != null ? err.code : err ? 1 : 0, stdout: stdout || '', stderr: stderr || '' });
      }
    );
  });
}

function exists(p) {
  try {
    fs.accessSync(p);
    return true;
  } catch {
    return false;
  }
}

function isDir(p) {
  try {
    return fs.statSync(p).isDirectory();
  } catch {
    return false;
  }
}

// Full path of a running process image via WMI (works without admin rights).
// UTF-8 console output so Cyrillic paths survive on RU Windows.
function processImagePath(image) {
  const name = String(image).replace(/"/g, '`"');
  const ps = `[Console]::OutputEncoding=[Text.Encoding]::UTF8; (Get-CimInstance Win32_Process -Filter "Name='${name}'").ExecutablePath`;
  return new Promise((resolve) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-Command', ps],
      { windowsHide: true, timeout: 12000, encoding: 'utf8' },
      (err, stdout) => {
        if (err || !stdout) return resolve(null);
        const line = String(stdout).split(/\r?\n/).map((s) => s.trim()).find(Boolean);
        resolve(line || null);
      }
    );
  });
}

function isAdminSync() {
  if (process.platform !== 'win32') return true;
  try {
    // flaky-free: net session only succeeds when elevated
    const out = require('node:child_process').execSync('net session', { stdio: 'pipe', timeout: 5000 });
    return true;
  } catch {
    return false;
  }
}

module.exports = { run, runRaw, exists, isDir, isAdminSync, processImagePath };
