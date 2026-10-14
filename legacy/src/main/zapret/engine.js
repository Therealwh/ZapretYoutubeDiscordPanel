'use strict';
// Engine (bol-van/zapret) updates.
//
// The Flowseal package ships its own winws build; this panel can also move the
// engine to any upstream bol-van/zapret tag. Safety rules:
//   * ONLY files from an explicit allowlist are ever written;
//   * ACTIVE_*.bin (user-tunable fake payloads) are never overwritten;
//   * the bypass is stopped before copying, and a backup of the touched files
//     is kept so a bad engine can be rolled back;
//   * the current engine version is recorded in bin/engine-version.txt.
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');

// The only executables/driver files an engine update may replace.
const ENGINE_BIN_FILES = [
  'winws.exe',
  'WinDivert.dll',
  'WinDivert64.sys',
  'cygwin1.dll',
  'mdig.exe',
  'ip2net.exe',
  'killall.exe',
];

const ENGINE_VERSION_FILE = 'engine-version.txt';
const GITHUB_RELEASES_API = 'https://api.github.com/repos/bol-van/zapret/releases';

// A real tag looks like v72.13 / v2.0.rc1 — reject anything else so a
// malformed API response can never point us at a non-release asset.
function isValidEngineTag(tag) {
  return /^v\d[\w.-]{0,31}$/.test(String(tag || ''));
}

function fetchJson(url, timeoutMs = 12000) {
  return new Promise((resolve, reject) => {
    const https = require('node:https');
    const req = https.get(url, { headers: { 'User-Agent': 'YoutubeDiscordPanel', Accept: 'application/vnd.github+json' }, timeout: timeoutMs }, (res) => {
      if (res.statusCode !== 200) {
        res.resume();
        reject(new Error(`http_${res.statusCode}`));
        return;
      }
      let body = '';
      res.setEncoding('utf8');
      res.on('data', (c) => {
        body += c;
        if (body.length > 2_000_000) {
          req.destroy();
          reject(new Error('too_large'));
        }
      });
      res.on('end', () => {
        try { resolve(JSON.parse(body)); } catch (e) { reject(e); }
      });
    });
    req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
    req.on('error', reject);
  });
}

function downloadFile(url, dest, onProgress, timeoutMs = 180000) {
  return new Promise((resolve, reject) => {
    const https = require('node:https');
    const tmp = `${dest}.part`;
    const file = fs.createWriteStream(tmp);
    let received = 0;
    const req = https.get(url, { headers: { 'User-Agent': 'YoutubeDiscordPanel' }, timeout: timeoutMs }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        file.close();
        try { fs.unlinkSync(tmp); } catch { /* ignore */ }
        downloadFile(res.headers.location, dest, onProgress, timeoutMs).then(resolve, reject);
        return;
      }
      if (res.statusCode !== 200) {
        file.close();
        try { fs.unlinkSync(tmp); } catch { /* ignore */ }
        reject(new Error(`http_${res.statusCode}`));
        return;
      }
      const total = Number(res.headers['content-length'] || 0);
      res.on('data', (c) => {
        received += c.length;
        if (onProgress && total) onProgress(received / total);
      });
      res.pipe(file);
      file.on('finish', () => file.close(() => {
        try { fs.renameSync(tmp, dest); resolve(dest); } catch (e) { reject(e); }
      }));
    });
    req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
    req.on('error', (e) => {
      file.close();
      try { fs.unlinkSync(tmp); } catch { /* ignore */ }
      reject(e);
    });
  });
}

function currentEngineVersion(rootDir) {
  try {
    return fs.readFileSync(path.join(rootDir, 'bin', ENGINE_VERSION_FILE), 'utf8').trim() || null;
  } catch {
    return null;
  }
}

// Newest release tag from bol-van/zapret.
async function listEngineReleases() {
  const json = await fetchJson(`${GITHUB_RELEASES_API}?per_page=30`);
  return (Array.isArray(json) ? json : [])
    .filter((r) => r && r.tag_name && isValidEngineTag(r.tag_name) && !r.draft)
    .map((r) => ({ tag: r.tag_name, name: r.name || r.tag_name, publishedAt: r.published_at || null }));
}

function findEngineAsset(release, tag) {
  const assets = (release && release.assets) || [];
  const wanted = new RegExp(`^zapret-${String(tag).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\.zip$`, 'i');
  return assets.find((a) => a && a.name && wanted.test(a.name)) || null;
}

// Full tag payload (needed for its assets).
async function fetchRelease(tag) {
  if (!isValidEngineTag(tag)) throw new Error('bad_tag');
  const json = await fetchJson(`${GITHUB_RELEASES_API}/tags/${encodeURIComponent(tag)}`);
  return json;
}

// Stop the bypass before swapping the driver, so files are not locked.
function stopBypass() {
  return new Promise((resolve) => {
    const ps = spawn('powershell', ['-NoProfile', '-Command',
      "net stop zapret 2>$null; taskkill /IM winws.exe /F 2>$null; exit 0"],
    { windowsHide: true });
    ps.on('exit', () => resolve());
    ps.on('error', () => resolve());
    const t = setTimeout(() => { try { ps.kill(); } catch { /* ignore */ } resolve(); }, 20000);
    if (typeof t.unref === 'function') t.unref();
  });
}

// Expand the release zip into a temp dir. Returns the engine root (the dir
// that directly contains bin/ and files/).
async function expandZip(zipPath, workDir) {
  fs.mkdirSync(workDir, { recursive: true });
  const ps = `$ErrorActionPreference='Stop'; Expand-Archive -LiteralPath '${String(zipPath).replace(/'/g, "''")}' -DestinationPath '${String(workDir).replace(/'/g, "''")}' -Force;`;
  await new Promise((resolve, reject) => {
    const p = spawn('powershell', ['-NoProfile', '-Command', ps], { windowsHide: true });
    p.on('exit', (c) => (c === 0 ? resolve() : reject(new Error(`expand_failed_${c}`))));
    p.on('error', reject);
  });
  // zip may contain a single top-level folder
  const entries = fs.readdirSync(workDir, { withFileTypes: true });
  if (entries.length === 1 && entries[0].isDirectory()) return path.join(workDir, entries[0].name);
  return workDir;
}

// Pick the allowlisted files (and files/fake/*.bin) out of the expanded tree.
function collectEngineFiles(srcRoot) {
  const found = [];
  const binDir = path.join(srcRoot, 'bin');
  if (fs.existsSync(binDir)) {
    for (const name of fs.readdirSync(binDir)) {
      const lower = name.toLowerCase();
      // ACTIVE_* payloads are user-tunable: never touch them.
      if (lower.startsWith('active_')) continue;
      if (!ENGINE_BIN_FILES.includes(name)) continue;
      found.push({ from: path.join(binDir, name), to: name });
    }
  }
  const fakeDir = path.join(srcRoot, 'files', 'fake');
  if (fs.existsSync(fakeDir)) {
    for (const name of fs.readdirSync(fakeDir)) {
      if (!/\.bin$/i.test(name)) continue;
      if (name.toLowerCase().startsWith('active_')) continue;
      found.push({ from: path.join(fakeDir, name), to: path.join('files', 'fake', name) });
    }
  }
  return found;
}

/**
 * Update the engine to `tag`.
 * @returns {{ok:boolean, tag?:string, copied?:string[], backup?:string, reason?:string}}
 */
async function updateEngineToTag(rootDir, tag, { onProgress, onStage } = {}) {
  if (!isValidEngineTag(tag)) return { ok: false, reason: 'bad_tag' };
  const binDir = path.join(rootDir, 'bin');
  if (!fs.existsSync(path.join(binDir, 'winws.exe'))) return { ok: false, reason: 'no_engine' };

  onStage?.('fetch');
  const release = await fetchRelease(tag);
  const asset = findEngineAsset(release, tag);
  if (!asset) return { ok: false, reason: 'no_asset' };

  const workDir = path.join(rootDir, '_engine_update');
  const zipPath = path.join(workDir, `zapret-${tag}.zip`);
  try {
    onStage?.('download');
    await downloadFile(asset.browser_download_url, zipPath, onProgress);

    onStage?.('extract');
    const srcRoot = await expandZip(zipPath, path.join(workDir, 'src'));
    const files = collectEngineFiles(srcRoot);
    if (!files.length) return { ok: false, reason: 'no_files' };

    onStage?.('stop');
    await stopBypass();

    // Backup the files we are about to overwrite.
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const backupDir = path.join(rootDir, '_backup', `engine-${stamp}`);
    let backedUp = 0;
    for (const f of files) {
      const dst = path.join(binDir, f.to);
      if (!fs.existsSync(dst)) continue;
      const bk = path.join(backupDir, f.to);
      fs.mkdirSync(path.dirname(bk), { recursive: true });
      fs.copyFileSync(dst, bk);
      backedUp += 1;
    }

    onStage?.('install');
    const copied = [];
    for (const f of files) {
      const dst = path.join(binDir, f.to);
      fs.mkdirSync(path.dirname(dst), { recursive: true });
      fs.copyFileSync(f.from, dst);
      copied.push(f.to);
    }
    fs.writeFileSync(path.join(binDir, ENGINE_VERSION_FILE), `${tag}\n`, 'utf8');

    return { ok: true, tag, copied, backup: backedUp ? backupDir : null };
  } catch (e) {
    return { ok: false, reason: e.message };
  } finally {
    try {
      if (fs.existsSync(workDir)) fs.rmSync(workDir, { recursive: true, force: true });
    } catch { /* best effort */ }
  }
}

module.exports = {
  ENGINE_BIN_FILES,
  ENGINE_VERSION_FILE,
  isValidEngineTag,
  listEngineReleases,
  fetchRelease,
  findEngineAsset,
  currentEngineVersion,
  collectEngineFiles,
  updateEngineToTag,
  expandZip,
  stopBypass,
};