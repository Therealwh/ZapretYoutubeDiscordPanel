'use strict';
const { app, BrowserWindow, ipcMain, dialog, shell } = require('electron');
const path = require('node:path');
const fs = require('node:fs');

const { decideRecovery, formatExitCode, SAFE_SWITCHES } = require('./render-guard');

// Smoke runs must not collide with a real running instance (single-instance
// lock and cache are per-userData), so give them a throwaway profile.
// Must run before anything reads userData.
if (process.argv.includes('--smoke')) {
  app.setPath('userData', path.join(require('node:os').tmpdir(), 'ydp-smoke-userdata'));
}

// Black-window guard: flags persisted by the crash handler below are applied
// here, before the app is ready (Chromium switches can't change later).
let bootCfg = {};
try {
  bootCfg = JSON.parse(fs.readFileSync(path.join(app.getPath('userData'), 'config.json'), 'utf8')) || {};
} catch { /* first run / unreadable config — defaults */ }
const gpuOff = !!bootCfg.disableHardwareAcceleration;
const safeRenderer = !!bootCfg.safeRenderer;
if (gpuOff) app.disableHardwareAcceleration();
if (safeRenderer) {
  for (const [k, v] of SAFE_SWITCHES) app.commandLine.appendSwitch(k, v);
}

function appendRendererLog(line) {
  try {
    fs.appendFileSync(path.join(app.getPath('userData'), 'renderer-log.txt'), `${new Date().toISOString()} ${line}\n`);
  } catch { /* best effort */ }
}

const config = require('./config');
const { createTray, setTrayStatus, setTrayI18n, setTrayState, destroyTray } = require('./tray');
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
const dns = require('./zapret/dns');
  const ownership = require('./zapret/ownership');
  const engine = require('./zapret/engine');
  const tgProxy = require('./zapret/tg-proxy');
const panelUpdater = require('./updater');
  const logger = require('./logger');
updates.setVtKeyProvider(() => config.get('virustotalKey', null));

let win = null;
let quitting = false;

const single = app.requestSingleInstanceLock();
if (!single) {
  // exit() instead of quit(): quit() is async and would let whenReady below
  // still create a second window/tray in this duplicate process.
  app.exit(0);
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

  // Black-window recovery ladder (see render-guard.js): reload, then
  // relaunch with GPU off, then relaunch in safe renderer mode, then tell
  // the user where the log is. Exit codes are logged for diagnostics.
  let renderCrashes = 0;
  let recovering = false;
  win.webContents.on('render-process-gone', (_e, details) => {
    renderCrashes += 1;
    appendRendererLog(`render-process-gone: ${details.reason} exit=${formatExitCode(details.exitCode)} `
      + `gpuOff=${gpuOff} safeRenderer=${safeRenderer} crash#${renderCrashes}`);
    if (recovering) return;
    const step = decideRecovery({ crashes: renderCrashes, reason: details.reason, gpuOff, safeMode: safeRenderer });
    if (step.action === 'reload') {
      setTimeout(() => {
        try { if (win && !win.isDestroyed()) win.webContents.reload(); } catch {}
      }, 800);
    } else if (step.action === 'relaunch') {
      recovering = true;
      for (const [k, v] of Object.entries(step.set)) config.set(k, v);
      appendRendererLog(`recovery: relaunch with ${Object.keys(step.set).join(', ')}`);
      quitting = true;
      try { destroyTray(); } catch {}
      app.relaunch();
      app.exit(0);
    } else if (step.action === 'give-up') {
      recovering = true;
      const logFile = path.join(app.getPath('userData'), 'renderer-log.txt');
      const ru = config.get('lang', 'ru') === 'ru';
      dialog.showMessageBox({
        type: 'error',
        title: 'YoutubeDiscordPanel',
        message: ru ? 'Не удаётся отобразить окно программы' : 'The app window cannot be displayed',
        detail: (ru
          ? 'Окно падает даже в безопасном режиме. Обычно причина — антивирус. Добавьте программу в исключения и пришлите этот файл в issue на GitHub:\n'
          : 'The window crashes even in safe mode, usually because of antivirus software. Add the app to exclusions and attach this file to a GitHub issue:\n') + logFile,
        buttons: [ru ? 'Открыть лог' : 'Open log', ru ? 'Закрыть' : 'Close'],
      }).then(({ response }) => { if (response === 0) shell.showItemInFolder(logFile); }).catch(() => {});
    }
  });
  // Log the first successful paint so the log shows which mode finally worked.
  win.webContents.once('did-finish-load', () => {
    if (renderCrashes || gpuOff || safeRenderer) {
      appendRendererLog(`renderer ok (gpuOff=${gpuOff} safeRenderer=${safeRenderer} crashes=${renderCrashes})`);
    }
  });
  win.webContents.on('did-fail-load', (_e, code, desc, url, isMain) => {
    appendRendererLog(`did-fail-load: ${code} ${desc} ${url}`);
    if (isMain && code !== -3) {
      setTimeout(() => {
        try { if (win && !win.isDestroyed()) win.webContents.loadFile(path.join(__dirname, '..', 'renderer', 'index.html')); } catch {}
      }, 1000);
    }
  });

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
          for (const page of ['updates', 'strategies', 'lists', 'logs', 'settings']) {
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

// Ask the renderer to switch pages (tray "Go to" submenu).
function navigateTo(page) {
  showWindow();
  setTimeout(() => emitToWindow('app:navigate', page), 250);
}

// Refresh everything the tray menu renders from (status, strategies, filters).
async function refreshTrayState() {
  try {
    const root = rootDir();
    const st = await status.getStatus(root);
    let strategies = [];
    try {
      strategies = root ? strategy.listStrategies(root).map((s) => s.name) : [];
    } catch { /* no root yet */ }
    let gf = 'disabled';
    let ipset = 'unknown';
    try {
      if (root) {
        gf = filters.readGameFilter(root).mode;
        const ip = filters.readIpsetStatus(root);
        ipset = ip && ip.mode ? ip.mode : 'unknown';
      }
    } catch { /* filters optional */ }
    let blocked = false;
    try {
      const det = await ownership.detectOwnership(root);
      blocked = det.ownership === 'foreign';
    } catch { /* detection is best effort */ }
    setTrayState({
      running: st.activeMode !== 'off',
      mode: st.activeMode,
      strategy: st.serviceStrategy || config.get('processStrategyName', null),
      strategies,
      gameFilter: gf,
      ipset,
      autostart: !!config.get('autostart', false),
      blocked,
    });
  } catch (e) {
    logger.warn(`Tray state refresh failed: ${e.message}`);
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
        setTrayI18n(trayI18n());
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

  // ---- foreign / portable zapret ----
  ipcMain.handle('zapret:ownership', () => ownership.detectOwnership(rootDir()));
  ipcMain.handle('zapret:takeover', async () => {
    const r = await ownership.takeoverForeignService({ managedRoot: rootDir() });
    logger[r.ok ? 'info' : 'warn'](`Foreign service takeover: ${r.ok ? 'done' : r.reason}`);
    return r;
  });
  ipcMain.handle('zapret:adoptPortable', async () => {
    const det = await ownership.detectOwnership(rootDir());
    const r = ownership.adoptPortable(det, rootDir());
    if (r.ok) {
      config.set('zapretRoot', r.root);
      logger.info(`Adopted portable zapret at ${r.root}`);
    } else {
      logger.warn(`Adopt portable failed: ${r.reason}`);
    }
    return r;
  });

  ipcMain.handle('dialog:chooseDir', async () => {
    const res = await dialog.showOpenDialog(win, { properties: ['openDirectory', 'createDirectory'] });
    if (res.canceled || !res.filePaths.length) return null;
    return res.filePaths[0];
  });

  ipcMain.handle('dialog:chooseZip', async () => {
    const res = await dialog.showOpenDialog(win, {
      title: 'zapret-discord-youtube archive',
      properties: ['openFile'],
      filters: [{ name: 'Zip archive', extensions: ['zip'] }],
    });
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

  ipcMain.handle('strategy:import', async () => {
    const root = rootDir();
    if (!root) return { ok: false, reason: 'no_root' };
    const res = await dialog.showOpenDialog(win, {
      title: 'Import .bat strategy',
      properties: ['openFile'],
      filters: [{ name: 'Batch files', extensions: ['bat'] }],
    });
    if (res.canceled || !res.filePaths.length) return { ok: false, reason: 'canceled' };
    const r = strategy.importStrategy(root, res.filePaths[0]);
    logger[r.ok ? 'info' : 'error'](`Strategy import: ${r.ok ? r.name : r.reason}`);
    return r;
  });
  ipcMain.handle('strategy:delete', (_e, file) => {
    const root = rootDir();
    if (!root) return { ok: false, reason: 'no_root' };
    const r = strategy.deleteStrategy(root, file);
    logger[r.ok ? 'info' : 'warn'](`Strategy delete: ${r.ok ? path.basename(file) : r.reason}`);
    return r;
  });

  ipcMain.handle('zapret:installService', async (_e, strategyFile) => {
    const root = rootDir();
    if (!root) return { ok: false, reason: 'no_root' };
    logger.info(`Applying strategy as service: ${path.basename(strategyFile)}`);
    const r = await service.installStrategyAsService(root, strategyFile);
    r?.ok ? logger.info(`Strategy applied: ${path.basename(strategyFile)}`) : logger.error(`Strategy apply failed: ${r?.reason || 'unknown'}`);
    return r;
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
  ipcMain.handle('zapret:startService', async () => {
    logger.info('Starting zapret service');
    const r = await service.startService();
    r?.ok ? logger.info('zapret service started') : logger.warn(`zapret service start failed: ${r?.reason || 'unknown'}`, 'app');
    return r;
  });
  ipcMain.handle('zapret:stopService', async () => {
    logger.info('Stopping zapret service');
    const r = await service.stopService();
    r?.ok ? logger.info('zapret service stopped') : logger.warn(`zapret service stop failed: ${r?.reason || 'unknown'}`, 'app');
    return r;
  });
  ipcMain.handle('zapret:killWinws', async () => {
    logger.info('Killing winws processes');
    return service.killWinws();
  });

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
  ipcMain.handle('lists:stats', (_e, key) => lists.stats(rootDir(), key));
  ipcMain.handle('lists:op', (_e, key, op, payload) => lists.listOp(rootDir(), key, op, payload || {}));

  ipcMain.handle('fakes:list', () => fakes.listFakes(rootDir()));
  ipcMain.handle('fakes:set', (_e, kind, fakeFile) => fakes.setFake(rootDir(), kind, fakeFile));

  ipcMain.handle('discord:clearCache', () => discord.clearDiscordCache());

  ipcMain.handle('dns:set', async () => dns.setDnsFix());
  ipcMain.handle('dns:reset', async () => dns.resetDns());

  ipcMain.handle('diag:run', () => diagnostics.runDiagnostics(rootDir()));

  ipcMain.handle('updates:checkVersion', () => updates.checkVersion(rootDir()));
  ipcMain.handle('updates:installZapret', async (_e, targetDir, localZip, opts) => {
    logger.info(`zapret install requested → ${targetDir}${localZip ? ' (local zip)' : ''}`);
    const r = await updates.installZapret(targetDir, (f) => emitToWindow('install:progress', f), (s) => emitToWindow('install:stage', s), {
      localZip: localZip || null,
      // Installing zapret into the panel's own folder gets wiped by the next
      // panel update, so it is refused unless the caller passes force.
      panelDir: app.getAppPath(),
      preferBundled: !!(opts && opts.preferBundled),
    });
    if (r.ok) {
      logger.info(`zapret installed: ${r.version} (${r.source || 'network'})${r.merged ? ' [merged]' : ''}`);
      // The pre-swap security gate in installZapret already refused red
      // distributions, so no post-swap rollback is needed here.
      config.set('zapretRoot', targetDir);
      refreshTrayState();
    } else {
      logger.error(`zapret install failed: ${r.reason}${r.message ? ` — ${r.message}` : ''}`);
    }
    return r;
  });
  ipcMain.handle('updates:updateIpset', () => updates.updateIpset(rootDir()));
  ipcMain.handle('updates:updateHosts', (_e, opts) => updates.updateHosts(rootDir(), opts || {}));

  // ---- engine (bol-van/zapret) ----
  ipcMain.handle('engine:releases', () => engine.listEngineReleases());
  ipcMain.handle('engine:current', () => ({ version: engine.currentEngineVersion(rootDir()) }));
  ipcMain.handle('engine:update', async (_e, tag) => {
    const root = rootDir();
    if (!root) return { ok: false, reason: 'no_root' };
    emitToWindow('install:stage', 'resolve');
    const r = await engine.updateEngineToTag(root, tag, {
      onProgress: (f) => emitToWindow('install:progress', f),
      // Plain string stages: the setup UI maps these onto setup_stage_* keys.
      onStage: (stage) => emitToWindow('install:stage', stage),
    });
    logger[r.ok ? 'info' : 'error'](`Engine update → ${tag}: ${r.ok ? r.copied.length + ' files' : r.reason}`);
    return r;
  });

  // ---- Telegram MTProto -> WebSocket proxy ----
  ipcMain.handle('tg:status', () => tgProxy.getStats());
  ipcMain.handle('tg:start', async (_e, opts) => {
    const settings = {
      port: Number(config.get('tgProxyPort', 1443)),
      host: '127.0.0.1',
      secretHex: config.get('tgProxySecret', null),
      ...(opts || {}),
    };
    if (settings.secret) {
      config.set('tgProxySecret', settings.secret);
      delete settings.secret;
    }
    config.set('tgProxyPort', settings.port);
    const r = await tgProxy.start(settings);
    logger[r.ok ? 'info' : 'error'](`Telegram proxy ${r.ok ? `started on ${settings.host}:${settings.port}` : `failed: ${r.reason || 'error'}`}`);
    return r;
  });
  ipcMain.handle('tg:stop', async () => {
    const r = await tgProxy.stop();
    logger.info('Telegram proxy stopped');
    return r;
  });
  ipcMain.handle('tg:restart', async (_e, opts) => {
    const r = await tgProxy.restart({ port: Number(config.get('tgProxyPort', 1443)), ...(opts || {}) });
    logger.info('Telegram proxy restarted');
    return r;
  });
  ipcMain.handle('tg:regenerate', () => tgProxy.regenerateSecret());
  ipcMain.handle('tg:open', () => {
    const link = tgProxy.proxyLink();
    shell.openExternal(link);
    return link;
  });

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

  // ---- Live logs ----
  ipcMain.handle('logs:buffer', () => logger.getBuffer());
  ipcMain.handle('logs:clear', () => {
    logger.clear();
    return true;
  });
  ipcMain.handle('logs:path', () => logger.logPath());
  ipcMain.handle('logs:showFile', () => {
    const f = logger.logPath();
    if (f && fs.existsSync(f)) shell.showItemInFolder(f);
    return !!f;
  });
  ipcMain.handle('logs:export', async (_e, filter) => {
    const stamp = new Date().toISOString().slice(0, 10);
    const res = await dialog.showSaveDialog(win, {
      defaultPath: `ydp-logs-${stamp}.log`,
      filters: [{ name: 'Log files', extensions: ['log', 'txt'] }],
    });
    if (res.canceled || !res.filePath) return { ok: false, reason: 'canceled' };
    return logger.exportLog(res.filePath, filter || {});
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
  } else if (act && act.type === 'install-zapret' && act.dir) {
    // Resumed after a UAC restart: same install, now with admin rights.
    logger.info(`Resuming zapret install after elevation → ${act.dir}`);
    const r = await updates.installZapret(
      act.dir,
      (frac) => emitToWindow('install:progress', frac),
      (stage) => emitToWindow('install:stage', stage),
      { localZip: act.localZip || null }
    );
    if (r.ok) {
      config.set('zapretRoot', act.dir);
      logger.info(`zapret installed after elevation: ${r.version}`);
    } else {
      logger.error(`zapret install failed after elevation: ${r.reason}`);
    }
    emitToWindow('install:result', r);
  }
  // 'run-tests' is executed by the renderer (it owns the UI flow)
}

app.whenReady().then(() => {
  config.init(app.getPath('userData'));
  logger.init(app.getPath('userData'));
  // Live log stream: the logger batches entries and pushes them to the window.
  logger.setSink((batch) => emitToWindow('log:lines', batch));
  logger.info(`Panel ${app.getVersion()} started (admin=${isAdminSync()})`);
  tgProxy.setLogger((msg, level) => (level === 'error' || level === 'warn' ? logger.warn(msg, 'tg-proxy') : logger.info(msg, 'tg-proxy')));
  // The proxy secret is generated once and reused, otherwise Telegram would
  // have to be reconfigured after every panel restart.
  tgProxy.ensureSecret();
  config.set('tgProxySecret', tgProxy.ensureSecret().toString('hex'));
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
      refreshTrayState();
    },
    onRestart: async () => {
      const st = await status.getStatus(rootDir());
      if (st.activeMode === 'off') return;
      await service.stopService();
      await service.killWinws();
      const last = config.get('lastStrategyFile', null);
      if (last && fs.existsSync(last)) {
        if (config.get('preferServiceMode', true)) await service.installStrategyAsService(rootDir(), last);
        else await service.launchStrategyProcess(rootDir(), last);
      }
      logger.info('Bypass restarted from tray');
      refreshTrayState();
    },
    onStrategy: async (name) => {
      const root = rootDir();
      const list = root ? strategy.listStrategies(root) : [];
      const target = list.find((s) => s.name === name);
      if (!target) return;
      const preferService = config.get('preferServiceMode', true);
      const r = preferService
        ? await service.installStrategyAsService(root, target.file)
        : await service.launchStrategyProcess(root, target.file);
      logger[r.ok ? 'info' : 'error'](`Tray strategy switch → ${name}: ${r.ok ? 'ok' : r.reason}`);
      if (r.ok) config.set('lastStrategyFile', target.file);
      refreshTrayState();
    },
    onGameFilter: async (mode) => {
      const root = rootDir();
      if (!root) return;
      const r = filters.writeGameFilter(root, { mode });
      logger[r.ok ? 'info' : 'error'](`Tray game filter → ${mode}: ${r.ok ? 'ok' : r.reason}`);
      refreshTrayState();
    },
    onIpset: async (mode) => {
      const root = rootDir();
      if (!root) return;
      const r = filters.writeIpsetMode(root, mode);
      logger[r.ok ? 'info' : 'error'](`Tray IPSet → ${mode}: ${r.ok ? 'ok' : r.reason}`);
      refreshTrayState();
    },
    onNavigate: navigateTo,
    onTool: (tool) => {
      if (tool === 'log') {
        const f = logger.logPath();
        if (f && fs.existsSync(f)) shell.showItemInFolder(f);
        return;
      }
      if (tool === 'diag') navigateTo('diagnostics');
    },
    onQuit: () => {
      quitting = true;
      app.quit();
    },
    i18n: trayI18n(),
  });
  refreshTrayState();
  setInterval(refreshTrayState, 30000);
});

// Tray menu strings, rebuilt when the UI language changes.
function trayI18n() {
  const ru = config.get('lang', 'ru') === 'ru';
  if (!ru) {
    return {
      running: 'ON', stopped: 'OFF',
      trayStart: 'Start', trayStop: 'Stop', trayRestart: 'Restart',
      trayShow: 'Show', trayQuit: 'Quit',
      trayStrategies: 'Strategies', trayTuning: 'Tuning', trayNavigate: 'Go to',
      trayGameFilter: 'Game Filter', trayIpset: 'IPSet', trayTools: 'Tools',
      trayOpenLog: 'Open log file', trayDiagnostics: 'Run diagnostics',
      trayMore: 'More…', trayNoStrategies: 'No strategies found',
      foreignBlocked: 'Foreign zapret — take over in the panel',
      gfOff: 'Off', gfAll: 'TCP + UDP', gfTcp: 'TCP only', gfUdp: 'UDP only',
      ipsetNone: 'none', ipsetLoaded: 'loaded', ipsetAny: 'any',
      pages: [['dashboard', 'Dashboard'], ['strategies', 'Strategies'], ['filters', 'Filters'],
        ['lists', 'Lists'], ['updates', 'Updates'], ['diagnostics', 'Diagnostics'],
        ['logs', 'Logs'], ['settings', 'Settings']],
    };
  }
  return {
    running: 'ВКЛ', stopped: 'ВЫКЛ',
    trayStart: 'Запустить', trayStop: 'Остановить', trayRestart: 'Перезапустить',
    trayShow: 'Показать', trayQuit: 'Выход',
    trayStrategies: 'Стратегии', trayTuning: 'Тюнинг', trayNavigate: 'Перейти',
    trayGameFilter: 'Игровой фильтр', trayIpset: 'IPSet', trayTools: 'Инструменты',
    trayOpenLog: 'Открыть файл логов', trayDiagnostics: 'Запустить диагностику',
    trayMore: 'Ещё…', trayNoStrategies: 'Стратегии не найдены',
    foreignBlocked: 'Чужой zapret — перехватите в панели',
    gfOff: 'Выключен', gfAll: 'TCP + UDP', gfTcp: 'Только TCP', gfUdp: 'Только UDP',
    ipsetNone: 'none', ipsetLoaded: 'loaded', ipsetAny: 'any',
    pages: [['dashboard', 'Обход'], ['strategies', 'Стратегии'], ['filters', 'Фильтры'],
      ['lists', 'Списки'], ['updates', 'Обновления'], ['diagnostics', 'Диагностика'],
      ['logs', 'Логи'], ['settings', 'Настройки']],
  };
}

app.on('before-quit', () => {
  quitting = true;
  destroyTray();
});

app.on('window-all-closed', () => {
  if (process.platform !== 'win32') app.quit();
});
