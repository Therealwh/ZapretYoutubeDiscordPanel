'use strict';
// Live application logger.
// - sources: app / winws / updater / diag / tg-proxy
// - levels: info / warn / error
// - file: %APPDATA%\YoutubeDiscordPanel\app.log (rotated at 2 MB -> app.log.1)
// - in-memory ring buffer (2000 lines) replayed to the renderer on subscribe
// - streamed live over IPC to the renderer with throttling
const fs = require('node:fs');
const path = require('node:path');

const MAX_FILE_BYTES = 2 * 1024 * 1024;
const MAX_BUFFER = 2000;
const FLUSH_MS = 250;
const MAX_QUEUED = 500;

const SOURCES = ['app', 'winws', 'updater', 'diag', 'tg-proxy'];
const LEVELS = ['info', 'warn', 'error'];

let userData = null;
let buffer = [];
let queue = [];
let flushTimer = null;
let sink = null; // (payload) => void
let seq = 0;

function init(dir) {
  userData = dir;
  buffer = [];
  seq = 0;
}

function file() {
  if (!userData) return null;
  return path.join(userData, 'app.log');
}

function stamp() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

function rotate(f) {
  try {
    const st = fs.statSync(f);
    if (st.size < MAX_FILE_BYTES) return;
    const bak = `${f}.1`;
    try { fs.unlinkSync(bak); } catch { /* absent */ }
    fs.renameSync(f, bak);
  } catch { /* absent / locked — keep appending */ }
}

function writeLine(line) {
  const f = file();
  if (!f) return;
  try {
    rotate(f);
    fs.appendFileSync(f, line, 'utf8');
  } catch { /* best effort — logging must never throw */ }
}

// Throttled batch flush so a chatty winws stream cannot flood the disk.
function scheduleFlush() {
  if (flushTimer) return;
  flushTimer = setTimeout(() => {
    flushTimer = null;
    if (!queue.length) return;
    const batch = queue;
    queue = [];
    try { sink?.(batch); } catch { /* renderer gone */ }
  }, FLUSH_MS);
  if (typeof flushTimer.unref === 'function') flushTimer.unref();
}

function log(source, level, text, detail) {
  const src = SOURCES.includes(source) ? source : 'app';
  const lvl = LEVELS.includes(level) ? level : 'info';
  const msg = String(text ?? '').replace(/[\r\n]+/g, ' ').slice(0, 2000);
  const entry = { id: ++seq, ts: new Date().toISOString(), time: stamp(), source: src, level: lvl, text: msg };
  if (detail !== undefined && detail !== null) entry.detail = String(detail).slice(0, 500);

  buffer.push(entry);
  if (buffer.length > MAX_BUFFER) buffer.splice(0, buffer.length - MAX_BUFFER);

  writeLine(`${entry.time} [${src}] ${msg}\n`);

  queue.push(entry);
  if (queue.length > MAX_QUEUED) queue.splice(0, queue.length - MAX_QUEUED);
  scheduleFlush();
}

// Convenience wrappers.
const info = (text, source = 'app', detail) => log(source, 'info', text, detail);
const warn = (text, source = 'app', detail) => log(source, 'warn', text, detail);
const error = (text, source = 'app', detail) => log(source, 'error', text, detail);

function getBuffer() {
  return buffer.slice();
}

// Set the live-stream sink (main.js wires this to the BrowserWindow).
function setSink(fn) {
  sink = fn;
}

// Read the file tail for the "open log file" action.
function logPath() {
  return file();
}

function exportLog(destPath, filter) {
  const lines = [];
  for (const e of buffer) {
    if (filter?.level && e.level !== filter.level) continue;
    if (filter?.source && e.source !== filter.source) continue;
    if (filter?.q && !e.text.toLowerCase().includes(String(filter.q).toLowerCase())) continue;
    lines.push(`${e.time} [${e.source}] ${e.text}`);
  }
  if (filter?.full && file()) {
    try {
      lines.length = 0;
      lines.push(fs.readFileSync(file(), 'utf8'));
    } catch { /* fall back to buffer */ }
  }
  try {
    fs.writeFileSync(destPath, lines.join('\n'), 'utf8');
    return { ok: true, lines: lines.length };
  } catch (e) {
    return { ok: false, reason: e.message };
  }
}

function clear() {
  buffer = [];
  seq = 0;
}

module.exports = { init, info, warn, error, log, getBuffer, setSink, logPath, exportLog, clear, SOURCES, LEVELS };