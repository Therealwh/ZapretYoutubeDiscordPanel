'use strict';
// Load the spike UI in Electron (fast feedback loop) and report what the
// renderer actually did: uncaught errors, missing DOM ids, whether the
// dashboard painted. Tauri build takes minutes; this takes seconds.
const { app, BrowserWindow } = require('electron');
const path = require('node:path');

const UI = path.join(__dirname, '..', 'tauri-spike', 'ui', 'index.html');
const errors = [];
const logs = [];

app.commandLine.appendSwitch('disable-gpu');

app.whenReady().then(async () => {
  const w = new BrowserWindow({
    width: 1400,
    height: 900,
    show: false,
    webPreferences: { preload: undefined, contextIsolation: true, nodeIntegration: false },
  });
  w.webContents.on('console-message', (_e, level, message) => {
    if (level >= 2) errors.push(`console: ${message}`);
    else logs.push(message);
  });
  w.webContents.on('did-fail-load', (_e, code, desc) => errors.push(`did-fail-load ${code} ${desc}`));
  await w.loadFile(UI);
  await new Promise((r) => setTimeout(r, 4000));

  const report = await w.webContents.executeJavaScript(`(() => {
    const vis = (sel) => { const el = document.querySelector(sel); if (!el) return sel + ':MISSING'; const r = el.getBoundingClientRect(); return sel + ':' + (r.width && r.height ? 'ok' : 'hidden'); };
    return {
      title: document.title,
      bodyChildren: document.body.children.length,
      nav: [...document.querySelectorAll('.nav-item, nav button, [data-page]')].length,
      cards: document.querySelectorAll('.card, .stat, .stat-card').length,
      checks: ['#page-dashboard', '#page-logs', '#page-strategies', '#btn-auto', '#tg-state', '#log-view'].map(vis),
      text: (document.body.innerText || '').replace(/\\s+/g, ' ').slice(0, 400),
      html: document.body.innerHTML.length,
    };
  })()`);

  const mem = w.getProcessMemoryInfo ? w.getProcessMemoryInfo() : {};
  console.log('SPIKE_REPORT ' + JSON.stringify({ ...report, errors: errors.slice(0, 12) }));
  console.log('RENDERER_MEMORY_KB ' + JSON.stringify(mem));
  console.log(errors.length ? `SPIKE_ERRORS ${errors.length}` : 'SPIKE_OK renderer loaded');
  app.exit(errors.length ? 1 : 0);
});
