'use strict';
const path = require('node:path');
const { exists, isDir, processImagePath } = require('../util');

const INSTALL_URL_PAGE = 'https://github.com/Flowseal/zapret-discord-youtube/releases/latest';

// Known-good root layout markers
function looksLikeZapretRoot(dir) {
  return isDir(path.join(dir, 'bin')) && exists(path.join(dir, 'bin', 'winws.exe')) && isDir(path.join(dir, 'lists'));
}

// Recommended install locations to scan on first run
function candidateRoots() {
  const out = ['C:\\zapret', 'C:\\zapret-discord-youtube', 'C:\\Program Files\\zapret-discord-youtube'];
  if (process.env.LOCALAPPDATA) {
    out.push(path.join(process.env.LOCALAPPDATA, 'zapret-discord-youtube'));
  }
  return out;
}

function discoverZapretRoot(configured) {
  if (configured && looksLikeZapretRoot(configured)) return configured;
  for (const c of candidateRoots()) {
    if (looksLikeZapretRoot(c)) return c;
  }
  return null;
}

// Locate the zapret root from a RUNNING winws.exe image path. Catches
// non-standard folders (e.g. C:\zapret-discord-youtube-1.10.2) that the
// candidate scan misses. Returns root dir or null.
async function discoverFromRunningProcess() {
  const exePath = await processImagePath('winws.exe');
  if (!exePath) return null;
  const m = exePath.match(/^(.+)[\\/]bin[\\/]winws\.exe$/i);
  return m ? m[1] : null;
}

// Problems that affect zapret operation (mirrors service.bat diagnostics concern)
function pathIssues(dir) {
  const issues = [];
  if (/[\u0430-\u044F\u0410-\u042F\u0451\u0401]/.test(dir)) issues.push('cyrillic');
  if (/\s/.test(dir)) issues.push('spaces');
  if (dir.toLowerCase().includes('onedrive')) issues.push('onedrive');
  return issues;
}

function validateRoot(dir) {
  if (!isDir(dir)) return { ok: false, reason: 'not_found' };
  if (!isDir(path.join(dir, 'bin'))) return { ok: false, reason: 'no_bin' };
  if (!exists(path.join(dir, 'bin', 'winws.exe'))) return { ok: false, reason: 'no_winws' };
  if (!isDir(path.join(dir, 'lists'))) return { ok: false, reason: 'no_lists' };
  return { ok: true, issues: pathIssues(dir) };
}

module.exports = { looksLikeZapretRoot, discoverZapretRoot, discoverFromRunningProcess, validateRoot, pathIssues, INSTALL_URL_PAGE };
