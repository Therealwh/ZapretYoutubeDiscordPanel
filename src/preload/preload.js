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

  listFakes: () => ipcRenderer.invoke('fakes:list'),
  setFake: (kind, file) => ipcRenderer.invoke('fakes:set', kind, file),

  clearDiscordCache: () => ipcRenderer.invoke('discord:clearCache'),
  runDiagnostics: () => ipcRenderer.invoke('diag:run'),

  checkVersion: () => ipcRenderer.invoke('updates:checkVersion'),
  installZapret: (dir) => ipcRenderer.invoke('updates:installZapret', dir),
  runningInfo: () => ipcRenderer.invoke('zapret:runningInfo'),
  updateIpset: () => ipcRenderer.invoke('updates:updateIpset'),
  updateHosts: (opts) => ipcRenderer.invoke('updates:updateHosts', opts),

  runTests: () => ipcRenderer.invoke('tester:run'),
  abortTests: () => ipcRenderer.invoke('tester:abort'),

  updaterState: () => ipcRenderer.invoke('updater:state'),
  updaterCheck: () => ipcRenderer.invoke('updater:check'),
  updaterDownload: () => ipcRenderer.invoke('updater:download'),
  updaterInstall: () => ipcRenderer.invoke('updater:install'),

  openExternal: (url) => ipcRenderer.invoke('shell:openExternal', url),
  showItemInFolder: (p) => ipcRenderer.invoke('shell:showItem', p),
  windowAction: (a) => ipcRenderer.invoke('app:windowAction', a),

  on: (channel, cb) => {
    const allowed = ['tester:event', 'install:progress', 'install:stage', 'updater:event'];
    if (allowed.includes(channel)) {
      const listener = (_e, payload) => cb(payload);
      ipcRenderer.on(channel, listener);
      return () => ipcRenderer.removeListener(channel, listener);
    }
    return null;
  },
};

contextBridge.exposeInMainWorld('api', api);
