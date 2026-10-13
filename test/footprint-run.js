'use strict';
// Measure an app's real footprint: ONLY the process subtree rooted at the
// process we launched. Necessary because this machine already runs the
// Electron panel and other apps share the WebView2 process pool — a flat
// name filter would count someone else's memory as ours.
const { spawn, execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const EXE = path.resolve(process.argv[2]);
const LABEL = process.argv[3] || path.basename(EXE);
const SETTLE_MS = Number(process.argv[4] || 12000);
const MEASURE_EXISTING = process.argv[5] === 'existing';
const OUT = path.join(__dirname, '..', `footprint-${LABEL}.json`);

function ps(script) {
  return execFileSync('powershell.exe', ['-NoProfile', '-Command', script], { encoding: 'utf8', maxBuffer: 8 << 20 });
}

// Every process with its parent, so a subtree can be rebuilt without guessing.
function processTable() {
  const raw = ps(`Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,WorkingSetSize,PrivatePageCount | ConvertTo-Json -Compress`);
  let rows = [];
  try { rows = JSON.parse(raw.trim() || '[]'); } catch { rows = []; }
  if (!Array.isArray(rows)) rows = [rows];
  return rows.filter((r) => r && r.ProcessId);
}

function subtree(table, rootPid) {
  const byParent = new Map();
  for (const r of table) {
    const k = Number(r.ParentProcessId);
    if (!byParent.has(k)) byParent.set(k, []);
    byParent.get(k).push(r);
  }
  const out = [];
  const walk = (pid) => {
    for (const child of byParent.get(Number(pid)) || []) {
      if (out.some((x) => Number(x.ProcessId) === Number(child.ProcessId))) continue;
      out.push(child);
      walk(child.ProcessId);
    }
  };
  walk(rootPid);
  return out;
}

function findRoot(table, exeName) {
  const mine = table.filter((r) => String(r.Name || '').toLowerCase() === exeName.toLowerCase());
  if (!mine.length) return null;
  const ids = new Set(mine.map((r) => Number(r.ProcessId)));
  // The root is the one whose parent is NOT another instance of the same exe.
  const root = mine.find((r) => !ids.has(Number(r.ParentProcessId)));
  return root ? Number(root.ProcessId) : Number(mine[0].ProcessId);
}

function snapshot(label, table, rootPid) {
  const rows = [{ ...table.find((r) => Number(r.ProcessId) === rootPid) }, ...subtree(table, rootPid)].filter(Boolean);
  const procs = rows.map((r) => ({
    name: r.Name,
    pid: Number(r.ProcessId),
    wsMB: Math.round((Number(r.WorkingSetSize) || 0) / 104857.6) / 10,
    privMB: Math.round((Number(r.PrivatePageCount) || 0) / 104857.6) / 10,
  }));
  return {
    label,
    at: new Date().toISOString(),
    processes: procs,
    count: procs.length,
    totalWsMB: Math.round(procs.reduce((a, b) => a + b.wsMB, 0) * 10) / 10,
    totalPrivMB: Math.round(procs.reduce((a, b) => a + b.privMB, 0) * 10) / 10,
  };
}

// Extra WebView2/Chromium flags can be passed through the environment so a
// trimmed configuration can be measured without touching app code.
const extraArgs = process.env.WEBVIEW2_ARGS || '';
const child = MEASURE_EXISTING
  ? null
  : spawn(EXE, [], {
      stdio: 'ignore',
      env: extraArgs ? { ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: extraArgs } : process.env,
    });
let rootPid = null;

function measure(label) {
  const table = processTable();
  if (rootPid === null) {
    rootPid = MEASURE_EXISTING ? findRoot(table, path.basename(EXE)) : child.pid;
  }
  if (!rootPid) return { label, processes: [], count: 0, totalWsMB: 0, totalPrivMB: 0, missing: true };
  return snapshot(label, table, rootPid);
}

function run() {
  const shots = [];
  const settled = measure('visible');
  shots.push(settled);
  console.log(`FOOTPRINT ${JSON.stringify({ app: LABEL, exeMB, stage: 'visible', ...settled })}`);

  // Second stage: the app hides its own window (SPIKE_HIDE_AFTER). A panel
  // lives in the tray, so the tray-only footprint is the number that matters.
  const hideAfter = Number(process.env.SPIKE_HIDE_AFTER || 0);
  if (hideAfter > 0) {
    setTimeout(() => {
      const hidden = measure('hidden');
      shots.push(hidden);
      fs.writeFileSync(OUT, JSON.stringify({ app: LABEL, exe: EXE, exeMB, shots }, null, 2));
      console.log(`FOOTPRINT ${JSON.stringify({ app: LABEL, exeMB, stage: 'hidden', ...hidden })}`);
      cleanup();
    }, (hideAfter + 8) * 1000);
  } else {
    fs.writeFileSync(OUT, JSON.stringify({ app: LABEL, exe: EXE, exeMB, shots }, null, 2));
    cleanup();
  }
}

function cleanup() {
  if (child) {
    try { spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* ignore */ }
    setTimeout(() => process.exit(0), 2000);
  } else {
    process.exit(0);
  }
}

const exeMB = MEASURE_EXISTING ? null : Math.round(fs.statSync(EXE).size / 104857.6) / 10;
setTimeout(run, SETTLE_MS);
