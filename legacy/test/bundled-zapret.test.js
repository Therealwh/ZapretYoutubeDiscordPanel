'use strict';
// Tests for the bundled zapret payload (offline-first install) and the guard
// that keeps zapret out of the panel's own installation folder.
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const updates = require('../src/main/zapret/updates');

test('findBundledZip locates the shipped zapret archive', () => {
  const found = updates.findBundledZip();
  assert.ok(found, 'the panel must ship a zapret zip');
  assert.ok(found.path.endsWith('.zip'));
  assert.ok(fs.existsSync(found.path), 'bundled archive must exist on disk');
  assert.match(found.name, /^zapret-discord-youtube-[\d.]+\.zip$/);
  assert.match(found.version, /^[\d.]+$/, 'version must come from the file name');
});

test('bundled archive passes the same security gate as a download', async () => {
  const found = updates.findBundledZip();
  const sec = await updates.verifyReleaseSecurity(null, found.path);
  assert.strictEqual(sec.level, 'green', 'bundled copy must be a verified upstream build');
  assert.strictEqual(sec.ok, true);
});

test('preferBundled installs without touching the network', async () => {
  const orig = updates.latestReleaseAsset;
  let networkUsed = false;
  updates.latestReleaseAsset = async () => {
    networkUsed = true;
    return { ok: false, reason: 'should_not_happen' };
  };
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ydp-bundled-'));
  try {
    const res = await updates.installZapret(path.join(dir, 'zapret'), () => {}, () => {}, { preferBundled: true });
    assert.strictEqual(res.ok, true, res.reason || res.message || '');
    assert.strictEqual(networkUsed, false, 'bundled install must not call the network');
    assert.strictEqual(res.source, 'bundled');
    assert.ok(fs.existsSync(path.join(dir, 'zapret', 'bin', 'winws.exe')));
    assert.ok(fs.existsSync(path.join(dir, 'zapret', 'service.bat')));
  } finally {
    updates.latestReleaseAsset = orig;
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('updates page path prefers the network for newer versions', async () => {
  const orig = updates.latestReleaseAsset;
  let networkUsed = false;
  updates.latestReleaseAsset = async () => {
    networkUsed = true;
    return { ok: false, reason: 'no_asset' };
  };
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ydp-net-'));
  try {
    // No preferBundled: an update must always ask GitHub for the newest build.
    const res = await updates.installZapret(path.join(dir, 'zapret'), () => {}, () => {}, {});
    assert.strictEqual(networkUsed, true, 'update must query GitHub');
    assert.strictEqual(res.ok, false);
    assert.strictEqual(res.reason, 'no_asset');
  } finally {
    updates.latestReleaseAsset = orig;
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('assertNotPanelDir refuses the panel folder and accepts a separate one', () => {
  const panel = 'C:\\Program Files\\YoutubeDiscordPanel';
  assert.strictEqual(updates.assertNotPanelDir(panel, panel).reason, 'target_is_panel_dir');
  assert.strictEqual(updates.assertNotPanelDir('C:\\Program Files\\YoutubeDiscordPanel\\zapret', panel).reason, 'target_is_panel_dir');
  // A parent of the panel dir is equally unsafe.
  assert.strictEqual(updates.assertNotPanelDir('C:\\Program Files', panel).reason, 'target_is_panel_dir');
  assert.strictEqual(updates.assertNotPanelDir('C:\\zapret', panel).ok, true);
  assert.strictEqual(updates.assertNotPanelDir('C:\\zapret', null).ok, true);
});

test('installZapret refuses the panel folder before downloading', async () => {
  const orig = updates.latestReleaseAsset;
  let networkUsed = false;
  updates.latestReleaseAsset = async () => {
    networkUsed = true;
    return { ok: false, reason: 'no_asset' };
  };
  try {
    const res = await updates.installZapret('C:\\Program Files\\YoutubeDiscordPanel', () => {}, () => {}, { panelDir: 'C:\\Program Files\\YoutubeDiscordPanel' });
    assert.strictEqual(res.ok, false);
    assert.strictEqual(res.reason, 'target_is_panel_dir');
    assert.strictEqual(networkUsed, false);
  } finally {
    updates.latestReleaseAsset = orig;
  }
});

test('updating an existing install does not fail on the drive root', async () => {
  // The swap step used to `mkdir` the PARENT unconditionally; on a drive root
  // (C:\) that always throws EPERM, so every update to C:\Zapret aborted with
  // "target_unwritable" even though the folder was writable.
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ydp-root-'));
  const service = require('../src/main/zapret/service');
  const orig = service.killWinws;
  const origStop = service.stopService;
  const origDrivers = service.stopDrivers;
  const origRemove = service.removeServices;
  service.killWinws = async () => {};
  service.stopService = async () => {};
  service.stopDrivers = async () => {};
  service.removeServices = async () => {};
  const target = path.join(dir, 'zapret');
  fs.mkdirSync(target); // pre-existing folder forces the swap path
  try {
    const res = await updates.installZapret(target, () => {}, () => {}, { preferBundled: true, allowExisting: true });
    assert.strictEqual(res.ok, true, `update must succeed: ${res.reason || ''} ${res.message || ''}`);
    assert.ok(fs.existsSync(path.join(target, 'bin', 'winws.exe')));
    assert.ok(fs.existsSync(path.join(target, 'service.bat')));
  } finally {
    service.killWinws = orig;
    service.stopService = origStop;
    service.stopDrivers = origDrivers;
    service.removeServices = origRemove;
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('install refuses to overwrite the panel folder on a fresh install', async () => {
  const orig = updates.latestReleaseAsset;
  let networkUsed = false;
  updates.latestReleaseAsset = async () => {
    networkUsed = true;
    return { ok: false, reason: 'no_asset' };
  };
  try {
    const res = await updates.installZapret('C:\\Program Files\\YoutubeDiscordPanel', () => {}, () => {}, {
      panelDir: 'C:\\Program Files\\YoutubeDiscordPanel',
      allowExisting: false,
    });
    assert.strictEqual(res.ok, false);
    assert.strictEqual(res.reason, 'target_is_panel_dir');
    assert.strictEqual(networkUsed, false);
  } finally {
    updates.latestReleaseAsset = orig;
  }
});

test('assertTargetWritable reports a missing root permission early', () => {
  // Z: does not exist here, so creating it must fail fast without a download.
  const res = updates.assertTargetWritable('Z:\\zapret');
  assert.strictEqual(res.ok, false);
  assert.strictEqual(res.reason, 'target_unwritable');
});

test('assertTargetWritable accepts the default drive-root path (issue: mkdir C:\\: EPERM)', () => {
  // C:\zapret is the wizard's default suggestion. Probing it used to fail with
  // `mkdir C:\: EPERM` because mkdir on an existing drive root always throws.
  const res = updates.assertTargetWritable('C:\\zapret');
  assert.strictEqual(res.ok, true, `default install path must be accepted: ${res.message || ''}`);
});

test('assertTargetWritable leaves no folder behind', () => {
  const target = path.join(os.tmpdir(), `ydp-no-side-effect-${process.pid}`);
  assert.strictEqual(fs.existsSync(target), false);
  updates.assertTargetWritable(target);
  assert.strictEqual(fs.existsSync(target), false, 'a permission check must not create folders');
});

test('assertTargetWritable accepts an existing writable folder', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ydp-exist-'));
  try {
    assert.strictEqual(updates.assertTargetWritable(path.join(dir, 'zapret')).ok, true);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('assertTargetWritable accepts a writable temp folder', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ydp-w-'));
  try {
    assert.strictEqual(updates.assertTargetWritable(path.join(dir, 'zapret')).ok, true);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});