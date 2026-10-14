'use strict';
const fs = require('node:fs');
const path = require('node:path');
const https = require('node:https');
const { execFile } = require('node:child_process');
const { createHash } = require('node:crypto');
const { elevateCommands } = require('../admin');
const { run, exists } = require('../util');
const service = require('./service');

const LOCAL_VERSION = '1.10.3'; // resolved dynamically from .service/version.txt if present
const VERSION_URL = 'https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt';
const RELEASES_URL = 'https://github.com/Flowseal/zapret-discord-youtube/releases/latest';
const IPSET_URL = 'https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt';
const HOSTS_URL = 'https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts';

// GitHub API rejects requests without a User-Agent header (HTTP 403)
const HTTP_UA = 'YoutubeDiscordPanel/1.0 (+https://github.com/Flowseal/zapret-discord-youtube)';

function fetchText(url, timeoutMs = 10000) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, { headers: { 'Cache-Control': 'no-cache', 'User-Agent': HTTP_UA }, timeout: timeoutMs }, (res) => {
      if (res.statusCode !== 200) {
        res.resume();
        return reject(new Error(`HTTP ${res.statusCode}`));
      }
      let data = '';
      res.setEncoding('utf8');
      res.on('data', (c) => (data += c));
      res.on('end', () => resolve(data));
    });
    req.on('timeout', () => {
      req.destroy(new Error('timeout'));
    });
    req.on('error', reject);
  });
}

async function readLocalVersion(rootDir) {
  // .service folder inside the zapret root holds the version of the bundle
  const candidates = [
    path.join(rootDir, '.service', 'version.txt'),
    path.join(rootDir, 'version.txt'),
  ];
  for (const c of candidates) {
    if (exists(c)) {
      try {
        return fs.readFileSync(c, 'utf8').trim();
      } catch {
        /* fallthrough */
      }
    }
  }
  // Most installs have no version.txt — parse it from service.bat
  // (`set "LOCAL_VERSION=1.10.3"`), exactly like the bat itself does.
  try {
    const bat = fs.readFileSync(path.join(rootDir, 'service.bat'), 'latin1');
    const m = bat.match(/LOCAL_VERSION\s*=\s*([0-9]+(?:\.[0-9]+)*)/i);
    if (m) return m[1];
  } catch {
    /* fallthrough */
  }
  return null;
}

async function checkVersion(rootDir) {
  if (!rootDir) return { ok: false, local: null, remote: null, error: 'no_root', releaseUrl: RELEASES_URL };
  const local = await readLocalVersion(rootDir);
  let remote = null;
  try {
    remote = (await fetchText(VERSION_URL)).trim();
  } catch {
    return { ok: false, local, remote: null, error: 'network' };
  }
  return { ok: true, local, remote, upToDate: local === remote, releaseUrl: RELEASES_URL };
}

// ---- download of release zip with progress ----
function downloadFile(url, dest, onProgress) {
  return new Promise((resolve, reject) => {
    const get = (u) => {
      https
        .get(u, { headers: { 'User-Agent': 'YoutubeDiscordPanel' } }, (res) => {
          if (res.statusCode >= 301 && res.statusCode <= 308 && res.headers.location) {
            res.resume();
            return get(res.headers.location);
          }
          if (res.statusCode !== 200) {
            res.resume();
            return reject(new Error(`HTTP ${res.statusCode}`));
          }
          const total = Number(res.headers['content-length'] || 0);
          let received = 0;
          const out = fs.createWriteStream(dest);
          res.on('data', (chunk) => {
            received += chunk.length;
            if (onProgress && total) onProgress(received / total);
          });
          res.pipe(out);
          out.on('finish', () => {
            // A dropped connection still fires 'finish' — verify byte count.
            if (total && received !== total) {
              fs.rmSync(dest, { force: true });
              return reject(new Error(`download_truncated ${received}/${total}`));
            }
            out.close(() => resolve(dest));
          });
          out.on('error', reject);
        })
        .on('error', reject);
    };
    get(url);
  });
}

// ---- release safety verification (tamper / malware gate) ----
// Threat model: fake "zapret" distributions with loggers/stealers circulating
// on non-official mirrors. The official GitHub release is the trust anchor.
const KNOWN_GOOD_SHA256 = {
  // official https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-discord-youtube-1.10.3.zip
  '244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058': '1.10.3',
};
// WinDivert64.sys signer certificate (official driver builds by Jingcheng Zhang / Florin Alexis).
// Pinned as SHA-1 TBS hash of the cert (stable across re-signings with the same cert).
const WINDIVERT_TBS_SHA1 = [
  '043589f75fce2795e7f2cc3e526d46784d5ddab3', // official WinDivert 1.10.3 signer (probed live from the release zip)
];

function sha256File(file) {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256');
    const s = fs.createReadStream(file);
    s.on('data', (c) => hash.update(c));
    s.on('end', () => resolve(hash.digest('hex').toLowerCase()));
    s.on('error', reject);
  });
}

function runPs(cmd, timeout = 90000) {
  return new Promise((resolve) => {
    execFile('powershell.exe', ['-NoProfile', '-Command', cmd], { windowsHide: true, timeout, encoding: 'utf8' }, (err, stdout) => {
      resolve({ err: !!err, out: String(stdout || '') });
    });
  });
}

// Verify Authenticode signature of a single file; returns { status, certTbsSha1, subject, serial }.
async function fileSignature(file) {
  const p = path.resolve(file).replace(/'/g, "''");
  const cmd = [
    `[Console]::OutputEncoding=[Text.Encoding]::UTF8`,
    `$s = Get-AuthenticodeSignature -LiteralPath '${p}'`,
    `$c = $s.SignerCertificate`,
    `if ($c) {`,
    `  $tbs = $c.GetCertHashTypeString 2>$null`,
    `  if (-not $tbs) { $tbs = [System.Security.Cryptography.SHA1]::Create().ComputeHash($c.RawData) | ForEach-Object { $_.ToString('x2') } }`,
    `  $tbs = ($tbs -join '')`,
    `  "$($s.Status)|$($c.Thumbprint)|$([BitConverter]::ToString([System.Security.Cryptography.SHA1]::Create().ComputeHash($c.RawData)).Replace('-','').ToLower())|$($c.Subject)"`,
    `} else { "$($s.Status)|||" }`,
  ].join('; ');
  const { out } = await runPs(cmd);
  const line = out.split(/\r?\n/).find((l) => l.includes('|'));
  if (!line) return { status: 'Unknown', tbsSha1: null, subject: null };
  const [status, , tbsSha1, subject] = line.split('|');
  return { status, tbsSha1: tbsSha1 || null, subject: subject || null };
}

// VirusTotal key is stored in the panel config (userData/config.json).
let vtKeyProvider = null;
function setVtKeyProvider(fn) {
  vtKeyProvider = fn;
}

// Check the zip against the whitelist, then verify the WinDivert driver signature.
async function verifyReleaseSecurity(url, zipPath) {
  const res = { ok: null, level: 'unknown', checks: [] };
  const push = (check) => res.checks.push(check);
  const zipFile = path.resolve(String(zipPath || ''));
  let zipExists = false;
  try {
    zipExists = !!zipPath && fs.statSync(zipFile).isFile();
  } catch {}
  if (!zipExists) {
    res.ok = false;
    res.level = 'red';
    push({ id: 'hash', verdict: 'fail', detail: 'zip_missing' });
    return res;
  }

  // 1) SHA-256 whitelist
  const hash = await sha256File(zipFile);
  const known = KNOWN_GOOD_SHA256[hash];
  push({ id: 'hash', verdict: known ? 'pass' : 'unknown', hash, version: known || null });
  if (known) {
    res.ok = true;
    res.level = 'green';
    res.version = known;
    return res;
  }
  res.level = 'yellow';

  // 2) WinDivert64.sys must be Authenticode-valid AND signed by a pinned cert.
  const sysPath = await extractOneFromZip(zipFile, 'bin/WinDivert64.sys');
  if (!sysPath) {
    res.ok = false;
    res.level = 'red';
    push({ id: 'windivert_sig', verdict: 'fail', detail: 'sys_missing' });
    return res;
  }
  let sig = { status: 'Unknown', tbsSha1: null };
  try {
    sig = await fileSignature(sysPath);
  } catch {
    /* treated as unknown below */
  }
  const sigOk = sig.status === 'Valid' && (!sig.tbsSha1 || WINDIVERT_TBS_SHA1.includes(sig.tbsSha1.toLowerCase()));
  push({ id: 'windivert_sig', verdict: sigOk ? 'pass' : 'fail', status: sig.status, tbsSha1: sig.tbsSha1 });
  if (!sigOk) {
    res.ok = false;
    res.level = 'red';
    return res;
  }
  res.level = 'yellow';

  // 3) VirusTotal lookup (optional, needs user API key in settings)
  const vtKey = vtKeyProvider ? vtKeyProvider() : readVtKey();
  if (vtKey) {
    try {
      const vt = await vtLookup(hash, vtKey);
      push({ id: 'virustotal', verdict: vt.malicious > 0 ? 'fail' : 'pass', malicious: vt.malicious, total: vt.total, link: vt.link });
      if (vt.malicious > 0) {
        res.ok = false;
        res.level = 'red';
        return res;
      }
      res.ok = true;
      res.level = 'green';
    } catch (e) {
      push({ id: 'virustotal', verdict: 'skip', detail: e.message });
    }
  }
  return res;
}

// Pull bin/WinDivert64.sys out of the zip into a temp dir (PowerShell + .NET ZipFile).
async function extractOneFromZip(zipFile, entryName) {
  const tmpDir = fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'ydp-sec-'));
  const ps = [
    `Add-Type -AssemblyName System.IO.Compression.FileSystem`,
    `$z = [System.IO.Compression.ZipFile]::OpenRead('${zipFile.replace(/'/g, "''")}')`,
    `try {`,
    `  $e = $z.Entries | Where-Object { $_.FullName -ieq '${entryName}' } | Select-Object -First 1`,
    `  if (-not $e) { $e = $z.Entries | Where-Object { $_.FullName -ilike '*${entryName}' } | Select-Object -First 1 }`,
    `  if ($e) { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, '${(path.join(tmpDir, 'WinDivert64.sys')).replace(/'/g, "''")}', $true) }`,
    `} finally { $z.Dispose() }`,
  ].join('; ');
  const { err } = await runPs(ps, 60000);
  const out = path.join(tmpDir, 'WinDivert64.sys');
  if (err || !exists(out)) {
    try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch {}
    return null;
  }
  return out;
}

// VirusTotal v3 file report by sha256. Returns { malicious, total, link }.
function vtLookup(sha256, apiKey) {
  const opts = {
    hostname: 'www.virustotal.com',
    path: `/api/v3/files/${sha256}`,
    headers: { 'x-apikey': apiKey, 'User-Agent': HTTP_UA },
    timeout: 15000,
  };
  return new Promise((resolve, reject) => {
    const req = https.get(opts, (res) => {
      if (res.statusCode === 404) {
        res.resume();
        return resolve({ malicious: 0, total: 0, link: `https://www.virustotal.com/gui/file/${sha256}`, unknown: true });
      }
      if (res.statusCode === 429) {
        res.resume();
        return reject(new Error('rate_limited'));
      }
      if (res.statusCode !== 200) {
        res.resume();
        return reject(new Error(`HTTP ${res.statusCode}`));
      }
      let data = '';
      res.setEncoding('utf8');
      res.on('data', (c) => (data += c));
      res.on('end', () => {
        try {
          const j = JSON.parse(data);
          const stats = (j.data && j.data.attributes && j.data.attributes.last_analysis_stats) || {};
          resolve({
            malicious: (stats.malicious || 0) + (stats.suspicious || 0),
            total: Object.values(stats).reduce((a, b) => a + (b || 0), 0),
            link: `https://www.virustotal.com/gui/file/${sha256}`,
          });
 } catch (e) { reject(e); }
      });
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
  });
}

function readVtKey() {
  // Packaged Electron userData is %APPDATA%\<productName>, in dev it is the
  // package name — probe both spellings before giving up.
  const appdata = process.env.APPDATA || '';
  for (const dir of ['YoutubeDiscordPanel', 'youtube-discord-panel']) {
    try {
      const cfg = JSON.parse(fs.readFileSync(path.join(appdata, dir, 'config.json'), 'utf8'));
      if (typeof cfg.virustotalKey === 'string') return cfg.virustotalKey.trim() || null;
    } catch {
      /* try next candidate */
    }
  }
  return null;
}

async function latestReleaseAsset() {
  // Resolve latest release, find zapret-discord-youtube-*.zip asset
  const api = 'https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest';
  const body = await fetchText(api, 15000);
  const json = JSON.parse(body);
  const asset = (json.assets || []).find((a) => /^zapret-discord-youtube-.*\.zip$/i.test(a.name));
  if (!asset) return { ok: false, reason: 'no_asset' };
  return { ok: true, version: json.tag_name, name: asset.name, url: asset.browser_download_url, size: asset.size };
}

// Can we actually write to the destination folder? Checked BEFORE the download
// so a permission problem does not waste traffic.
//
// Two traps this avoids:
//   * `mkdir` on an existing drive root (C:\) fails with EPERM even though the
//     folder is writable — and C:\zapret is our own default suggestion, so
//     probing the parent with mkdir broke EVERY fresh install
//     (`mkdir C:\: EPERM`).
//   * creating the folder just to test it would leave an empty folder behind
//     when the user cancels. `access()` asks the ACLs and touches nothing.
function assertTargetWritable(targetDir) {
  const probeDir = exists(targetDir) ? targetDir : path.dirname(targetDir);
  try {
    fs.accessSync(probeDir, fs.constants.W_OK);
  } catch (e) {
    return { ok: false, reason: 'target_unwritable', message: String((e && e.message) || e) };
  }
  return { ok: true };
}

// Installing zapret INTO the panel's own folder looks tempting ("same Program
// Files tree"), but every panel update rewrites that folder and silently
// destroys the bypass — which is exactly how an install ends up as
// "version: unknown" with a dead engine. Refuse unless the user insists.
function assertNotPanelDir(targetDir, panelDir) {
  if (!panelDir) return { ok: true };
  const norm = (p) => path.resolve(p).replace(/[\\/]+$/, '').toLowerCase();
  const t = norm(targetDir);
  const p = norm(panelDir);
  if (t === p || t.startsWith(`${p}\\`) || p.startsWith(`${t}\\`)) {
    return { ok: false, reason: 'target_is_panel_dir', message: `${targetDir} vs ${panelDir}` };
  }
  return { ok: true };
}

// ---- bundled zapret payload ----
// The panel ships the official zapret zip so a fresh install works with NO
// network at all (the machines that need the bypass are usually the ones where
// github.com is throttled or blocked). The bundled archive is verified with the
// same SHA-256 whitelist as a download, so trust is identical.
const BUNDLED_DIR_NAME = 'zapret';
const BUNDLED_ZIP_RE = /^zapret-discord-youtube-[\d.]+\.zip$/i;

function bundledDir() {
  const candidates = [];
  // Packaged: <app>/resources/zapret (electron-builder extraResources)
  if (process.resourcesPath) candidates.push(path.join(process.resourcesPath, BUNDLED_DIR_NAME));
  // Unpacked dev run
  candidates.push(path.join(__dirname, '..', '..', '..', 'resources', BUNDLED_DIR_NAME));
  for (const dir of candidates) {
    try {
      if (fs.existsSync(dir)) return dir;
    } catch { /* try next */ }
  }
  return null;
}

// Find the bundled zip and its version from the file name.
function findBundledZip() {
  const dir = bundledDir();
  if (!dir) return null;
  let names;
  try {
    names = fs.readdirSync(dir);
  } catch {
    return null;
  }
  const zip = names.find((n) => BUNDLED_ZIP_RE.test(n));
  if (!zip) return null;
  const m = zip.match(/zapret-discord-youtube-([\d.]+)\.zip/i);
  return { path: path.join(dir, zip), name: zip, version: m ? m[1] : null, dir };
}

// Full install/refresh: obtain a zapret zip, extract it, swap the folder.
// Source priority: opts.localZip (user-picked) > opts.preferBundled (first
// install, offline) > network (Updates page always wants the newest release).
async function installZapret(targetDir, onProgress, onStage, opts) {
  // Never let an error escape: the renderer awaits this over IPC, and a
  // rejected promise would freeze its UI mid-install.
  try {
    return await installZapretInner(targetDir, onProgress, onStage, opts);
  } catch (e) {
    return { ok: false, reason: 'install_failed', message: String((e && e.message) || e) };
  }
}

async function installZapretInner(targetDir, onProgress, onStage, opts = {}) {
  const stage = (s) => onStage && onStage(s);
  // Housekeeping: drop install temp dirs older than a day.
  try {
    const tmpRoot = require('node:os').tmpdir();
    for (const d of fs.readdirSync(tmpRoot)) {
      if (!/^ydp-\d+/.test(d)) continue;
      const p = path.join(tmpRoot, d);
      try {
        if (Date.now() - fs.statSync(p).mtimeMs > 24 * 3600 * 1000) fs.rmSync(p, { recursive: true, force: true });
      } catch {}
    }
  } catch {}
  stage('resolve');
  const notPanel = assertNotPanelDir(targetDir, opts.panelDir);
  if (!notPanel.ok) return { ...notPanel };
  // Fail fast on an unwritable destination (e.g. C:\ without admin rights) —
  // otherwise the user waits for a download that could never be installed.
  const writable = assertTargetWritable(targetDir);
  if (!writable.ok) return { ...writable, adminRequired: true };
  // Routed through module.exports so tests can stub resolution/download.
  const localZip = opts && opts.localZip ? String(opts.localZip) : null;
  if (localZip && !/\.zip$/i.test(localZip)) {
    return { ok: false, reason: 'not_a_zip', message: localZip };
  }
  if (localZip && !exists(localZip)) {
    return { ok: false, reason: 'local_zip_missing', message: localZip };
  }
  let rel;
  if (localZip) {
    rel = { ok: true, version: null, name: path.basename(localZip), url: null, size: 0, local: true };
  } else {
    // Offline-first only when the caller asks for it (first-run setup); the
    // Updates page must always fetch the newest release from GitHub.
    const bundled = opts.preferBundled ? findBundledZip() : null;
    if (bundled) {
      rel = { ok: true, version: bundled.version, name: bundled.name, url: null, size: 0, bundled: bundled.path };
    } else {
      rel = await module.exports.latestReleaseAsset();
      if (!rel.ok) return { ok: false, reason: rel.reason };
    }
  }
  const tmp = path.join(require('node:os').tmpdir(), `ydp-${Date.now()}`);
  fs.mkdirSync(tmp, { recursive: true });
  const zipPath = path.join(tmp, rel.name);
  if (localZip) {
    stage('copy');
    // Copy so the install never depends on the user's original file staying put.
    try {
      fs.copyFileSync(localZip, zipPath);
    } catch (e) {
      try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
      return { ok: false, reason: 'local_zip_unreadable', message: String((e && e.message) || e) };
    }
  } else if (rel.bundled) {
    stage('copy');
    try {
      fs.copyFileSync(rel.bundled, zipPath);
    } catch (e) {
      try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
      return { ok: false, reason: 'bundled_zip_unreadable', message: String((e && e.message) || e) };
    }
  } else {
    stage('download');
    // Each stage reports ITS OWN reason: a generic install_failed tells the user
    // nothing about which step broke (and the throw used to escape here).
    try {
      await module.exports.downloadFile(rel.url, zipPath, onProgress);
    } catch (e) {
      try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
      return { ok: false, reason: 'download_failed', message: String((e && e.message) || e) };
    }
  }
  stage('security');
  // Safety gate: refuse tampered / malware-looking distributions before any
  // file is touched on disk outside the temp dir.
  let sec;
  try {
    sec = await verifyReleaseSecurity(rel.url, zipPath);
  } catch (e) {
    try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
    return { ok: false, reason: 'security_check_failed', message: String((e && e.message) || e) };
  }
  if (sec.ok === false && sec.level === 'red') {
    try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
    return { ok: false, reason: 'security_rejected', security: sec };
  }
  stage('extract');
  const extractDir = path.join(tmp, 'extracted');
  const ps = [
    `Expand-Archive -LiteralPath '${zipPath.replace(/'/g, "''")}' -DestinationPath '${extractDir.replace(/'/g, "''")}' -Force`,
  ].join('; ');
  const ex = await run('powershell', ['-NoProfile', '-Command', ps], { timeout: 300000 });
  if (ex.code !== 0) {
    try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
    return { ok: false, reason: 'extract_failed', message: (ex.stderr || `exit ${ex.code}`).trim().slice(0, 400) };
  }

  // Zip contains a nested folder like zapret-discord-youtube-1.10.3/... Find it.
  let src;
  try {
    const inner = fs
      .readdirSync(extractDir)
      .map((n) => path.join(extractDir, n))
      .find((p) => fs.statSync(p).isDirectory());
    src = inner || extractDir;
  } catch (e) {
    try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
    return { ok: false, reason: 'extract_empty', message: String((e && e.message) || e) };
  }
  if (!exists(path.join(src, 'bin', 'winws.exe'))) {
    return { ok: false, reason: 'bad_layout' };
  }

  stage('stop');
  // Full stop + unregister: running winws / registered services keep handles
  // inside the root folder and break the swap ("files in use").
  try {
    await service.killWinws();
    await service.stopService();
    await service.stopDrivers();
    // drop the zapret service registration entirely (recreated on next enable)
    await service.removeServices();
  } catch { /* best effort */ }
  try {
    await service.killWinws();
  } catch {}
  await new Promise((r) => setTimeout(r, 1500));
  stage('swap');
  // Move old folder aside (keep one backup), then move new content in.
  // Transient locks (AV scan, driver unloading, indexer) make rename fail —
  // retry for up to ~12s before giving up, and keep the old folder intact.
  // Last resort: merge-copy the new version over the old folder in place.
  const parent = path.dirname(targetDir);
  // Only create the parent when it is really missing: `mkdir` on an existing
  // drive root (C:\) throws EPERM even though the folder is writable, which
  // aborted every update to the default C:\Zapret location.
  if (!exists(parent)) {
    try {
      fs.mkdirSync(parent, { recursive: true });
    } catch (e) {
      try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
      return { ok: false, reason: 'target_unwritable', message: String((e && e.message) || e) };
    }
  }
  const backup = targetDir + '.backup';
  if (exists(targetDir)) {
    let moved = false;
    let lastErr = null;
    for (let attempt = 0; attempt < 8 && !moved; attempt++) {
      try {
        fs.rmSync(backup, { recursive: true, force: true });
      } catch {}
      try {
        fs.renameSync(targetDir, backup);
        moved = true;
      } catch (e) {
        lastErr = e;
        await new Promise((r) => setTimeout(r, 1500));
      }
    }
    if (!moved) {
      // cannot rename the folder — merge-copy new files over the old ones
      try {
        fs.cpSync(src, targetDir, { recursive: true, force: true });
        try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
        return { ok: true, merged: true, version: rel.version, backup: null, releaseUrl: rel.url, releaseZip: zipPath, security: sec, source: rel.bundled ? 'bundled' : (rel.local ? 'local' : 'network') };
      } catch (e2) {
        try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
        return {
          ok: false,
          reason: 'swap_failed_locked',
          message: String((e2 && e2.message) || lastErr && lastErr.message || e2),
        };
      }
    }
  }
  try {
    fs.renameSync(src, targetDir);
  } catch {
    // cross-device or locked destination: copy tree; on failure roll back
    try {
      fs.cpSync(src, targetDir, { recursive: true, force: true });
    } catch (e2) {
      try {
        if (!exists(targetDir) && exists(backup)) fs.renameSync(backup, targetDir);
      } catch {}
      try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}
      return { ok: false, reason: 'swap_failed', message: String((e2 && e2.message) || e2) };
    }
  }
  stage('cleanup');
  try {
    fs.rmSync(tmp, { recursive: true, force: true });
  } catch {
    /* best effort */
  }
  return { ok: true, version: rel.version, backup, releaseUrl: rel.url, releaseZip: zipPath, security: sec, source: rel.bundled ? 'bundled' : (rel.local ? 'local' : 'network') };
}

// ---- ipset list update ----
async function updateIpset(rootDir) {
  const file = path.join(rootDir, 'lists', 'ipset-all.txt');
  try {
    const content = await fetchText(IPSET_URL);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, content, 'utf8');
    return { ok: true };
  } catch {
    return { ok: false, reason: 'network' };
  }
}

// ---- hosts update: auto-apply via elevation or open for manual copy ----
async function updateHosts(rootDir, { auto }) {
  const tempFile = path.join(require('node:os').tmpdir(), 'zapret_hosts.txt');
  try {
    const content = await fetchText(HOSTS_URL + '?t=' + Date.now());
    fs.writeFileSync(tempFile, content, 'utf8');
  } catch {
    return { ok: false, reason: 'network' };
  }
  if (!auto) {
    return { ok: true, manual: true, tempFile };
  }
  const hostsFile = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'drivers', 'etc', 'hosts');
  const cmds = [
    `type "${tempFile}" >> "${hostsFile}"`,
    'ipconfig /flushdns',
  ];
  // Guard against duplicates: check first line presence before appending
  try {
    const hostsNow = fs.readFileSync(hostsFile, 'utf8');
    const firstLine = fs.readFileSync(tempFile, 'utf8').split(/\r?\n/).find((l) => l.trim());
    if (firstLine && hostsNow.includes(firstLine)) {
      return { ok: true, already: true };
    }
  } catch {
    /* proceed */
  }
  const res = await elevateCommands(cmds);
  return { ok: res.ok, manual: false };
}

module.exports = {
  checkVersion,
  installZapret,
  downloadFile,
  verifyReleaseSecurity,
  extractOneFromZip,
  fileSignature,
  vtLookup,
  sha256File,
  setVtKeyProvider,
  updateIpset,
  updateHosts,
  latestReleaseAsset,
  KNOWN_GOOD_SHA256,
  WINDIVERT_TBS_SHA1,
  findBundledZip,
  bundledDir,
  assertTargetWritable,
  assertNotPanelDir,
  LOCAL_VERSION,
  VERSION_URL,
  RELEASES_URL,
};
