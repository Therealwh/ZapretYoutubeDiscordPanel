'use strict';
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const DURATION = Number(process.argv[2] || 15000);
// In plain Node require('electron') resolves to the electron.exe path.
const electronExe = require('electron');
const p = spawn(electronExe, ['.', '--smoke'], { cwd: path.join(__dirname, '..') });
let out = '';
p.stdout?.on('data', (d) => (out += d));
p.stderr?.on('data', (d) => (out += d));

setTimeout(() => {
  fs.writeFileSync(path.join(__dirname, '..', 'smoke.log'), out || '(no output)');
  try {
    p.kill();
  } catch {}
  try {
    spawn('taskkill', ['/IM', 'electron.exe', '/F'], { stdio: 'ignore' });
  } catch {}
  setTimeout(() => process.exit(0), 2000);
}, DURATION);
