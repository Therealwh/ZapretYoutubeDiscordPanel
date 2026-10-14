'use strict';
// Panel self-update via electron-updater + GitHub Releases.
// NSIS install auto-updates; portable builds can only notify (Windows
// portable executables cannot replace themselves) — we still show the
// "new version" banner with a link to the release page.
const { autoUpdater } = require('electron-updater');
const { app } = require('electron');

let emit = null;
let portableView = false;
let state = { status: 'idle', version: null, progress: 0 };

function isPortable() {
  // electron-builder sets PORTABLE_EXECUTABLE_DIR for the portable target
  return !!process.env.PORTABLE_EXECUTABLE_DIR;
}

function setState(status, extra = {}) {
  state = { ...state, status, ...extra };
  if (emit) emit('updater:event', { type: status, ...extra, portable: portableView });
}

function init(onEmit) {
  emit = onEmit;
  portableView = isPortable();
  autoUpdater.autoDownload = false;
  autoUpdater.autoInstallOnAppQuit = true;
  // We are unsigned: signature mismatch is only a warning, don't block.
  autoUpdater.verifyUpdateCodeSignature = false;
  autoUpdater.logger = console;

  autoUpdater.on('checking-for-update', () => setState('checking'));
  autoUpdater.on('update-available', (info) => {
    setState('available', { version: info.version, releaseNotes: typeof info.releaseNotes === 'string' ? info.releaseNotes : '' });
  });
  autoUpdater.on('update-not-available', (info) => {
    setState('not-available', { version: app.getVersion() });
  });
  autoUpdater.on('download-progress', (p) => {
    state.progress = p.percent;
    setState('progress', { percent: Math.round(p.percent) });
  });
  autoUpdater.on('update-downloaded', (info) => {
    setState('downloaded', { version: info.version });
  });
  autoUpdater.on('error', (e) => {
    setState('error', { message: String((e && e.message) || e) });
  });
}

function check() {
  setState('checking');
  return autoUpdater.checkForUpdates().then((r) => {
    const v = r && r.update && r.update.version;
    if (v && v === app.getVersion()) setState('not-available', { version: v });
    return { ok: true, current: app.getVersion(), latest: v || null, updateAvailable: !!v && v !== app.getVersion() };
  }).catch((e) => {
    const message = String((e && e.message) || e);
    setState('error', { message });
    return { ok: false, current: app.getVersion(), error: message };
  });
}

function download() {
  if (portableView) return Promise.resolve({ ok: false, reason: 'portable' });
  autoUpdater.downloadUpdate();
  return Promise.resolve({ ok: true });
}

function install() {
  if (portableView) return { ok: false, reason: 'portable' };
  autoUpdater.quitAndInstall(false, true);
  return { ok: true };
}

function getState() {
  return { ...state, current: app.getVersion(), portable: portableView };
}

module.exports = { init, check, download, install, getState };
