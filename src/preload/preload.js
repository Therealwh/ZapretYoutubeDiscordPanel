'use strict';
const { contextBridge, ipcRenderer } = require('electron');

const api = {
  appInfo: () => ipcRenderer.invoke('app:info'),
  getConfig: () => ipcRenderer.invoke('config:get'),
  setConfig: (kv) => ipcRenderer.invoke('config:set', kv),

  discoverZapret: () => ipcRenderer.invoke('zapret:discover'),
  validateRoot: (dir) => ipcRenderer.invoke('zapret:validateRoot', dir),
  chooseDir: () => ipcRenderer.invoke('dialog:chooseDir'),
  restartElevated: (pendingAction) => ipcRenderer.invoke('admin:restartElevated', pendingAction),
  elevateCommands: (cmds) => ipcRenderer.invoke('admin:elevateCommands', cmds),

  getStatus: () => ipcRenderer.invoke('zapret:getStatus'),
  listStrategies: () => ipcRenderer.invoke('zapret:listStrategies'),
  importStrategy: () => ipcRenderer.invoke('strategy:import'),
  deleteStrategy: (f) => ipcRenderer.invoke('strategy:delete', f),
  isImportedName: (name) => String(name || '').toLowerCase().startsWith('user-'),
  installService: (f) => ipcRenderer.invoke('zapret:installService', f),
  launchProcess: (f) => ipcRenderer.invoke('zapret:launchProcess', f),
  removeServices: () => ipcRenderer.invoke('zapret:removeServices'),
  startService: () => ipcRenderer.invoke('zapret:startService'),
  stopService: () => ipcRenderer.invoke('zapret:stopService'),
  killWinws: () => ipcRenderer.invoke('zapret:killWinws'),

  getFilters: () => ipcRenderer.invoke('filters:get'),
  setGameFilter: (gf) => ipcRenderer.invoke('filters:setGame', gf),
  setIpsetMode: (m) => ipcRenderer.invoke('filters:setIpset', m),

  ensureLists: () => ipcRenderer.invoke('lists:ensure'),
  readList: (key) => ipcRenderer.invoke('lists:read', key),
  writeList: (key, content) => ipcRenderer.invoke('lists:write', key, content),
  readIpsetAll: () => ipcRenderer.invoke('lists:readIpsetAll'),
  listStats: (key) => ipcRenderer.invoke('lists:stats', key),
  listOp: (key, op, payload) => ipcRenderer.invoke('lists:op', key, op, payload),

  listFakes: () => ipcRenderer.invoke('fakes:list'),
  setFake: (kind, file) => ipcRenderer.invoke('fakes:set', kind, file),

  clearDiscordCache: () => ipcRenderer.invoke('discord:clearCache'),
  setDnsFix: () => ipcRenderer.invoke('dns:set'),
  resetDns: () => ipcRenderer.invoke('dns:reset'),
  runDiagnostics: () => ipcRenderer.invoke('diag:run'),

  checkVersion: () => ipcRenderer.invoke('updates:checkVersion'),
  installZapret: (dir, localZip, opts) => ipcRenderer.invoke('updates:installZapret', dir, localZip, opts),
  chooseZip: () => ipcRenderer.invoke('dialog:chooseZip'),
  runningInfo: () => ipcRenderer.invoke('zapret:runningInfo'),
  ownership: () => ipcRenderer.invoke('zapret:ownership'),
  takeoverForeign: () => ipcRenderer.invoke('zapret:takeover'),
  adoptPortable: () => ipcRenderer.invoke('zapret:adoptPortable'),
  updateIpset: () => ipcRenderer.invoke('updates:updateIpset'),
  updateHosts: (opts) => ipcRenderer.invoke('updates:updateHosts', opts),

  engineReleases: () => ipcRenderer.invoke('engine:releases'),
  engineCurrent: () => ipcRenderer.invoke('engine:current'),
  engineUpdate: (tag) => ipcRenderer.invoke('engine:update', tag),

  logBuffer: () => ipcRenderer.invoke('logs:buffer'),
  clearLogs: () => ipcRenderer.invoke('logs:clear'),
  logPath: () => ipcRenderer.invoke('logs:path'),
  showLogFile: () => ipcRenderer.invoke('logs:showFile'),
  exportLogs: (filter) => ipcRenderer.invoke('logs:export', filter),

  runTests: () => ipcRenderer.invoke('tester:run'),
  lastTestResults: () => ipcRenderer.invoke('tester:lastResults'),
  abortTests: () => ipcRenderer.invoke('tester:abort'),
  autoConnect: () => ipcRenderer.invoke('autoconnect:run'),

  tgStatus: () => ipcRenderer.invoke('tg:status'),
  tgStart: (opts) => ipcRenderer.invoke('tg:start', opts),
  tgStop: () => ipcRenderer.invoke('tg:stop'),
  tgRestart: (opts) => ipcRenderer.invoke('tg:restart', opts),
  tgRegenerate: () => ipcRenderer.invoke('tg:regenerate'),
  tgOpen: () => ipcRenderer.invoke('tg:open'),

  updaterState: () => ipcRenderer.invoke('updater:state'),
  updaterCheck: () => ipcRenderer.invoke('updater:check'),
  updaterDownload: () => ipcRenderer.invoke('updater:download'),
  updaterInstall: () => ipcRenderer.invoke('updater:install'),

  openExternal: (url) => ipcRenderer.invoke('shell:openExternal', url),
  showItemInFolder: (p) => ipcRenderer.invoke('shell:showItem', p),
  windowAction: (a) => ipcRenderer.invoke('app:windowAction', a),

  on: (channel, cb) => {
    const allowed = ['tester:event', 'install:progress', 'install:stage', 'updater:event', 'zapret:update', 'log:lines', 'app:navigate', 'install:result'];
    if (allowed.includes(channel)) {
      const listener = (_e, payload) => cb(payload);
      ipcRenderer.on(channel, listener);
      return () => ipcRenderer.removeListener(channel, listener);
    }
    return null;
  },
};

contextBridge.exposeInMainWorld('api', api);
