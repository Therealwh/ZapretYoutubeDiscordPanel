'use strict';
const { app, BrowserWindow, ipcMain, dialog, shell } = require('electron');
const path = require('node:path');
const fs = require('node:fs');

const config = require('./config');
const { createTray, setTrayStatus, setTrayI18n, destroyTray } = require('./tray');
const { isAdminSync, elevateCommands, restartElevated } = require('./admin');
const tester = require('./zapret/tester');

const paths = require('./zapret/paths');
const status = require('./zapret/status');
const service = require('./zapret/service');
const strategy = require('./zapret/strategy');
const filters = require('./zapret/filters');
const lists = require('./zapret/lists');
const fakes = require('./zapret/fakes');
const discord = require('./zapret/discord');
const diagnostics = require('./zapret/diagnostics');
const updates = require('./zapret/updates');
const panelUpdater = require('./updater');
updates.setVtKeyProvider(() => config.get('virustotalKey', null));

let win = null;
let quitting = false;

// Smoke runs must not collide with a real running instance (single-instance
// lock and cache are per-userData), so give them a throwaway profile.
if (process.argv.includes('--smoke')) {
  app.setPath('userData', path.join(require('node:os').tmpdir(), 'ydp-smoke-userdata'));
}

const single = app.requestSingleInstanceLock();
if (!single) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (win) {
      if (win.isMinimized()) win.restore();
      win.show();
      win.focus();
    }
  });
}

function rootDir() {
  return config.get('zapretRoot', null);
}

function createWindow(startHidden) {
  win = new BrowserWindow({
    width: 1120,
    height: 760,
    minWidth: 940,
    minHeight: 640,
    show: !startHidden,
    backgroundColor: '#0b0c14',
    frame: false,
    title: 'YoutubeDiscordPanel',
    icon: path.join(__dirname, '..', 'assets', 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      spellcheck: false,
    },
  });
  win.loadFile(path.join(__dirname, '..', 'renderer', 'index.html'));

  if (process.argv.includes('--smoke')) {
    win.webContents.on('did-fail-load', (_e, code, desc) => {
      console.error(`SMOKE_FAIL did-fail-load ${code} ${desc}`);
    });
    win.webContents.on('console-message', (_e, level, message) => {
      if (level >= 2) console.error(`SMOKE_RENDERER_ERROR: ${message}`);
    });
    win.webContents.on('did-finish-load', () => {
      console.log('SMOKE_OK renderer loaded');
      // Dev tool: --smoke-shot captures page screenshots for UI review.
      if (process.argv.includes('--smoke-shot')) {
        (async () => {
          const os = require('node:os');
          const shot = async (name) => {
            const img = await win.webContents.capturePage();
            fs.writeFileSync(path.join(os.tmpdir(), `ydp-shot-${name}.png`), img.toPNG());
            console.log(`SMOKE_SHOT ${name}`);
          };
          await new Promise((r) => setTimeout(r, 2500));
          await shot('dashboard');
          for (const page of ['updates', 'settings', 'strategies']) {
            await win.webContents.executeJavaScript(`document.querySelector('[data-page=${page}]').click()`);
            await new Promise((r) => setTimeout(r, 900));
            await shot(page);
          }
          app.exit(0);
        })().catch((e) => console.error('SMOKE_SHOT_FAIL', e.message));
      }
    });
  }

  // Minimize goes to the tray (notification area near the clock): the window
  // is hidden instead of minimized, so no taskbar button remains.
  win.on('minimize', (e) => {
    e.preventDefault();
    win.hide();
  });

  // Close to tray instead of quitting (unless quitting explicitly)
  win.on('close', (e) => {
    if (!quitting && config.get('closeToTray', true)) {
      e.preventDefault();
      win.hide();
    }
  });
  win.on('closed', () => {
    win = null;
  });
}

function showWindow() {
  if (win) {
    win.show();
    win.focus();
  } else {
    createWindow(false);
  }
}

// Notify renderer of tester progress and installer progress
function emitToWindow(channel, payload) {
  if (win && !win.isDestroyed()) {
    win.webContents.send(channel, payload);
  }
}

// ---- IPC ----
function registerIpc() {
  ipcMain.handle('app:info', () => ({
    version: app.getVersion(),
    admin: isAdminSync(),
    platform: process.platform,
    lang: app.getLocale(),
  }));

  ipcMain.handle('config:get', () => config.all());
  ipcMain.handle('config:set', (_e, kv) => {
    for (const [k, v] of Object.entries(kv || {})) {
      config.set(k, v);
      if (k === 'autostart' || k === 'startHidden') {
        const autostart = config.get('autostart', false);
        const hidden = config.get('startHidden', false);
        app.setLoginItemSettings({ openAtLogin: !!autostart, args: autostart && hidden ? ['--hidden'] : [] });
      } else if (k === 'lang') {
        const ru = v === 'ru';
        setTrayI18n({
          trayShow: ru ? 'Показать' : 'Show',
          trayToggle: ru ? 'Вкл/Выкл обход' : 'Toggle bypass',
          trayQuit: ru ? 'Выход' : 'Quit',
        });
      }
    }
    return config.all();
  });

  ipcMain.handle('zapret:discover', async () => {
    let root = paths.discoverZapretRoot(rootDir());
    // Non-standard install dirs (e.g. C:\zapret-discord-youtube-1.10.2) are
    // found via the image path of a running winws.exe.
    if (!root) root = await paths.discoverFromRunningProcess();
    if (root) {
      const v = paths.validateRoot(root);
      if (v.ok) config.set('zapretRoot', root);
    }
    return root;
  });

  // Setup wizard: is a bypass already running (winws.exe / zapret service)?
  // Used to show a "stop it first" hint instead of pretending nothing runs.
  ipcMain.handle('zapret:runningInfo', async () => {
    const st = await status.getStatus(rootDir());
    const winwsRoot = st.winwsProcessPath
      ? st.winwsProcessPath.replace(/[\\/]bin[\\/]winws\.exe$/i, '')
      : null;
    const rootCandidate = winwsRoot && paths.validateRoot(winwsRoot).ok ? winwsRoot : null;
    return {
      running: st.activeMode !== 'off',
      mode: st.activeMode,
      conflictService: st.conflicts.find((c) => c.running)?.name || null,
      winwsRoot,
      rootCandidate,
      rootKnown: !!rootDir(),
    };
  });

  ipcMain.handle('zapret:validateRoot', (_e, dir) => paths.validateRoot(dir));

  ipcMain.handle('dialog:chooseDir', async () => {
    const res = await dialog.showOpenDialog(win, { properties: ['openDirectory', 'createDirectory'] });
    if (res.canceled || !res.filePaths.length) return null;
    return res.filePaths[0];
  });

  ipcMain.handle('admin:restartElevated', (_e, pendingAction) => {
    if (pendingAction) config.set('pendingAction', pendingAction);
    restartElevated();
    quitting = true;
    app.quit();
    return true;
  });

  ipcMain.handle('admin:elevateCommands', (_e, cmds) => elevateCommands(cmds));

  ipcMain.handle('zapret:getStatus', async () => {
    const root = rootDir();
    const st = await status.getStatus(root);
    // In process mode there is no registry hint — show the remembered name
    // of the strategy that was last launched as a process.
    if (st.activeMode === 'process' && !st.serviceStrategy) {
      const procName = config.get('processStrategyName', null);
      if (procName) st.serviceStrategy = procName;
    }
    const banner = status.deriveBanner(st);
    setTrayStatus(st.activeMode !== 'off');
    return { status: st, banner };
  });

  ipcMain.handle('zapret:listStrategies', () => {
    const root = rootDir();
    return root ? strategy.listStrategies(root) : [];
  });

  ipcMain.handle('zapret:installService', async (_e, strategyFile) => {
    const root = rootDir();
    if (!root) return { ok: false, reason: 'no_root' };
    return service.installStrategyAsService(root, strategyFile);
  });

  ipcMain.handle('zapret:launchProcess', (_e, strategyFile) => {
    const root = rootDir();
    if (!root) return { ok: false, reason: 'no_root' };
    // Remember which strategy runs as a process: the registry hint is only
    // written for service installs, so the dashboard would otherwise show
    // "no strategy" in process mode.
    config.set('processStrategyName', path.basename(strategyFile, '.bat'));
    return service.launchStrategyProcess(root, strategyFile);
  });

  ipcMain.handle('zapret:removeServices', () => service.removeServices(rootDir()));
  ipcMain.handle('zapret:startService', () => service.startService());
  ipcMain.handle('zapret:stopService', () => service.stopService());
  ipcMain.handle('zapret:killWinws', () => service.killWinws());

  ipcMain.handle('filters:get', () => {
    const root = rootDir();
    if (!root) return null;
    return {
      game: filters.readGameFilter(root),
      ipset: filters.readIpsetStatus(root),
      ipsetBackup: filters.hasIpsetBackup(root),
    };
  });
  ipcMain.handle('filters:setGame', (_e, gf) => {
    const root = rootDir();
    if (!root) return { ok: false };
    return filters.writeGameFilter(root, gf);
  });
  ipcMain.handle('filters:setIpset', (_e, mode) => {
    const root = rootDir();
    if (!root) return { ok: false };
    return filters.writeIpsetMode(root, mode);
  });

  ipcMain.handle('lists:ensure', () => lists.ensureUserLists(rootDir()));
  ipcMain.handle('lists:read', (_e, key) => lists.readList(rootDir(), key));
  ipcMain.handle('lists:write', (_e, key, content) => lists.writeList(rootDir(), key, content));
  ipcMain.handle('lists:readIpsetAll', () => lists.readIpsetAll(rootDir()));

  ipcMain.handle('fakes:list', () => fakes.listFakes(rootDir()));
  ipcMain.handle('fakes:set', (_e, kind, fakeFile) => fakes.setFake(rootDir(), kind, fakeFile));

  ipcMain.handle('discord:clearCache', () => discord.clearDiscordCache());

  ipcMain.handle('diag:run', () => diagnostics.runDiagnostics(rootDir()));

  ipcMain.handle('updates:checkVersion', () => updates.checkVersion(rootDir()));
  ipcMain.handle('updates:installZapret', async (_e, targetDir) => {
    const res = await updates.installZapret(
      targetDir,
      (frac) => emitToWindow('install:progress', frac),
      (stage) => emitToWindow('install:stage', stage)
    );
    if (res.ok) {
      // Safety gate: refuse a distribution that looks tampered with.
      const sec = await updates.verifyReleaseSecurity(res.releaseUrl, res.releaseZip);
      res.security = sec;
      if (sec.ok === false && sec.level === 'red') {
        // Roll back the swap: restore the backup if one was made.
        try {
          if (res.backup && fs.existsSync(res.backup)) {
            fs.rmSync(targetDir, { recursive: true, force: true });
            fs.renameSync(res.backup, targetDir);
          } else {
            fs.rmSync(targetDir, { recursive: true, force: true });
          }
        } catch {
          /* best effort */
        }
        return res;
      }
      config.set('zapretRoot', targetDir);
    }
    return res;
  });
  ipcMain.handle('updates:updateIpset', () => updates.updateIpset(rootDir()));
  ipcMain.handle('updates:updateHosts', (_e, opts) => updates.updateHosts(rootDir(), opts || {}));

  ipcMain.handle('tester:run', () => {
    const root = rootDir();
    if (!root) return { ok: false, reason: 'no_root' };
    if (!isAdminSync()) return { ok: false, reason: 'admin_required' };
    return tester
      .runStrategyTests(root, (ev) => emitToWindow('tester:event', ev))
      .then((r) => ({ ok: true, ...r }))
      .catch((e) => ({ ok: false, reason: e.code || 'error', message: e.message }));
  });

  // One-click "connect working bypass": if the current bypass already works —
  // keep it. Otherwise test strategies in order and connect the first one
  // where Discord AND YouTube are reachable; if none is perfect, connect the
  // best available so the user is never left without bypass.
  ipcMain.handle('autoconnect:run', async () => {
    const root = rootDir();
    if (!root) return { ok: false, reason: 'no_root' };
    if (!isAdminSync()) return { ok: false, reason: 'admin_required' };
    try {
      // 1. Already running? Verify it without touching anything.
      const st = await status.getStatus(root);
      if (st.activeMode !== 'off') {
        const check = await tester.checkReachability(root, (ev) => emitToWindow('tester:event', ev));
        const currentName = config.get('processStrategyName', null) || st.serviceStrategy || 'текущая';
        if (check.groups.discord.works && check.groups.youtube.state === 'ok') {
          return { ok: true, already: true, strategy: currentName };
        }
        // Not working — stop it and search for a better one.
        await service.killWinws();
        await service.stopService();
      }

      // 2. Search: connect the first fully working strategy.
      const r = await tester.runStrategyTests(root, (ev) => emitToWindow('tester:event', ev), { stopOnFirst: true });
      const preferService = config.get('preferServiceMode', true);

      if (r.winnerFile) {
        config.set('lastStrategyFile', r.winnerFile);
        config.set('processStrategyName', r.winner);
        // stopOnFirst leaves the winning winws RUNNING in process mode.
        const winwsUp = await status.processRunning('winws.exe');
        if (preferService) {
          const res = await service.installStrategyAsService(root, r.winnerFile);
          return { ok: !!res.ok, strategy: r.winner, mode: 'service', reason: res.reason };
        }
        if (winwsUp) return { ok: true, strategy: r.winner, mode: 'process-kept' };
        const res = await service.launchStrategyProcess(root, r.winnerFile);
        return { ok: !!res.ok, strategy: r.winner, mode: 'process', reason: res.reason };
      }

      // 3. Nothing perfect: connect the BEST available strategy so the user
      // is not left without bypass.
      const winner = r.results.filter((x) => !x.failed).sort((a, b) => b.score.ok - a.score.ok)[0];
      if (!winner) return { ok: false, reason: 'none_works' };
      config.set('lastStrategyFile', winner.file);
      config.set('processStrategyName', winner.name);
      let res;
      if (preferService) res = await service.installStrategyAsService(root, winner.file);
      else res = await service.launchStrategyProcess(root, winner.file);
      return { ok: !!res.ok, strategy: winner.name, fallback: true, reason: res.reason };
    } catch (e) {
      return { ok: false, reason: e.code || e.message || 'error' };
    }
  });
  ipcMain.handle('tester:abort', () => {
    // Flag first: the loop checks it between steps, then kill winws.
    tester.abort.flagged = true;
    service.killWinws();
    return { ok: true };
  });

  ipcMain.handle('shell:openExternal', (_e, url) => shell.openExternal(url));
  ipcMain.handle('shell:showItem', (_e, p) => shell.showItemInFolder(p));

  // ---- panel self-update (GitHub Releases) ----
  ipcMain.handle('updater:state', () => panelUpdater.getState());
  ipcMain.handle('updater:check', () => panelUpdater.check());
  ipcMain.handle('updater:download', () => panelUpdater.download());
  ipcMain.handle('updater:install', () => panelUpdater.install());

  ipcMain.handle('app:windowAction', (_e, action) => {
    if (!win) return;
    if (action === 'minimize') win.minimize();
    else if (action === 'maximize') win.isMaximized() ? win.unmaximize() : win.maximize();
    else if (action === 'close') win.hide(); // to tray
  });
}

// Execute an action saved before the UAC restart (toggle bypass / run tests
// picker). Runs once, only when the panel actually has admin rights.
async function runPendingAction() {
  const act = config.get('pendingAction', null);
  if (!act || !isAdminSync()) return;
  config.set('pendingAction', null);
  if (act === 'toggle-on') {
    const st = await status.getStatus(rootDir());
    if (st.activeMode === 'off') {
      const last = config.get('lastStrategyFile', null);
      const list = rootDir() ? strategy.listStrategies(rootDir()) : [];
      const target = list.find((s) => s.file === last) || list[0];
      if (target) {
        if (config.get('preferServiceMode', true) !== false) await service.installStrategyAsService(rootDir(), target.file);
        else await service.launchStrategyProcess(rootDir(), target.file);
      }
    }
  } else if (act === 'toggle-off') {
    await service.stopService();
    await service.killWinws();
  }
  // 'run-tests' is executed by the renderer (it owns the UI flow)
}

app.whenReady().then(() => {
  config.init(app.getPath('userData'));
  registerIpc();
  // Panel self-update: forward events to the renderer, then check quietly
  // (autoDownload is off — the user decides whether to fetch the update).
  panelUpdater.init((channel, payload) => emitToWindow(channel, payload));
  panelUpdater.check().catch(() => {});
  // zapret update check at startup — notify the renderer if a newer release exists.
  setTimeout(async () => {
    try {
      const r = await updates.checkVersion(rootDir());
      if (r && r.ok && r.upToDate === false && r.remote) emitToWindow('zapret:update', { remote: r.remote });
    } catch { /* offline — silent */ }
  }, 6000);
  // Hidden start applies ONLY to the autostart launch (argv --hidden).
  // A manual launch must always open the window, even if the user enabled
  // "start hidden to tray" in settings.
  const startHidden = process.argv.includes('--hidden');
  createWindow(startHidden);
  setTimeout(runPendingAction, 2000);
  createTray({
    onShow: showWindow,
    onToggle: async () => {
      const st = await status.getStatus(rootDir());
      if (st.activeMode !== 'off') await service.stopService();
      else {
        // start last strategy as service, or launch process fallback
        const last = config.get('lastStrategyFile', null);
        if (last && fs.existsSync(last)) {
          if (config.get('preferServiceMode', true)) await service.installStrategyAsService(rootDir(), last);
          else await service.launchStrategyProcess(rootDir(), last);
        }
      }
    },
    onQuit: () => {
      quitting = true;
      app.quit();
    },
    i18n: {
      trayShow: config.get('lang', 'ru') === 'ru' ? 'Показать' : 'Show',
      trayToggle: config.get('lang', 'ru') === 'ru' ? 'Вкл/Выкл обход' : 'Toggle bypass',
      trayQuit: config.get('lang', 'ru') === 'ru' ? 'Выход' : 'Quit',
    },
  });
});

app.on('before-quit', () => {
  quitting = true;
  destroyTray();
});

app.on('window-all-closed', () => {
  if (process.platform !== 'win32') app.quit();
});
