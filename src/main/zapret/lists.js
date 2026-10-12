'use strict';
const fs = require('node:fs');
const path = require('node:path');

// User-editable lists (auto-created on first run by service.bat; we do the same)
const USER_LISTS = {
  general: 'list-general-user.txt',
  exclude: 'list-exclude-user.txt',
  ipsetExclude: 'ipset-exclude-user.txt',
};

const DEFAULTS = {
  'list-general-user.txt': '# Never leave this file empty\r\ndomain.example.abc\r\n',
  'list-exclude-user.txt': 'domain.example.abc\r\n',
  'ipset-exclude-user.txt': '203.0.113.113/32\r\n',
};

function ensureUserLists(rootDir) {
  const lists = path.join(rootDir, 'lists');
  fs.mkdirSync(lists, { recursive: true });
  const created = [];
  for (const [key, name] of Object.entries(USER_LISTS)) {
    const file = path.join(lists, name);
    if (!fs.existsSync(file)) {
      fs.writeFileSync(file, DEFAULTS[name] || '', 'utf8');
      created.push(key);
    }
  }
  return created;
}

function readList(rootDir, key) {
  const name = USER_LISTS[key];
  if (!name) return { ok: false, reason: 'unknown_list' };
  if (key === 'ipsetAll') {
    /* handled below for read-only view */
  }
  try {
    return { ok: true, content: fs.readFileSync(path.join(rootDir, 'lists', name), 'utf8') };
  } catch {
    return { ok: false, reason: 'not_found' };
  }
}

function writeList(rootDir, key, content) {
  const name = USER_LISTS[key];
  if (!name) return { ok: false, reason: 'unknown_list' };
  try {
    fs.writeFileSync(path.join(rootDir, 'lists', name), content, 'utf8');
    return { ok: true };
  } catch {
    return { ok: false, reason: 'write_failed' };
  }
}

// Read-only viewer for ipset-all.txt
function readIpsetAll(rootDir) {
  try {
    return { ok: true, content: fs.readFileSync(path.join(rootDir, 'lists', 'ipset-all.txt'), 'utf8') };
  } catch {
    return { ok: false, reason: 'not_found' };
  }
}

// ---------- smart list helpers ----------
const MAX_BYTES = 2 * 1024 * 1024;

// Split a hostlist into entries, dropping comments/blank lines.
// Leading '^' (the "match subdomains" marker used by zapret) is preserved.
function parseEntries(content) {
  return String(content || '')
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l && !l.startsWith('#') && !l.startsWith(';'));
}

// Counters shown next to the editor: total entries, duplicates, comments.
function listStats(content) {
  const rawLines = String(content || '').split(/\r?\n/);
  const entries = parseEntries(content);
  const comments = rawLines.filter((l) => {
    const t = l.trim();
    return t && (t.startsWith('#') || t.startsWith(';'));
  }).length;
  const seen = new Set();
  let duplicates = 0;
  for (const e of entries) {
    const k = e.toLowerCase().replace(/^\^/, '');
    if (seen.has(k)) duplicates++;
    else seen.add(k);
  }
  return { total: entries.length, duplicates, comments, bytes: Buffer.byteLength(String(content || ''), 'utf8') };
}

// Remove duplicates (case-insensitive, '^' ignored for comparison) keeping
// first occurrence and preserving each entry's original '^' marker.
function dedupeEntries(content) {
  const out = [];
  const seen = new Set();
  let removed = 0;
  for (const raw of String(content || '').split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#') || line.startsWith(';')) {
      out.push(raw);
      continue;
    }
    const key = line.toLowerCase().replace(/^\^/, '');
    if (seen.has(key)) {
      removed++;
      continue;
    }
    seen.add(key);
    out.push(raw);
  }
  return { content: out.join('\n').replace(/\n+$/, '\n'), removed };
}

// Sort entries alphabetically, keeping comment lines at the top in place.
function sortEntries(content) {
  const lines = String(content || '').split(/\r?\n/);
  const comments = [];
  const entries = [];
  const tail = [];
  for (const raw of lines) {
    const line = raw.trim();
    if (!line) continue;
    if (line.startsWith('#') || line.startsWith(';')) comments.push(raw.trim());
    else entries.push({ raw, sortKey: line.toLowerCase().replace(/^\^/, '') });
  }
  entries.sort((a, b) => (a.sortKey < b.sortKey ? -1 : a.sortKey > b.sortKey ? 1 : 0));
  return { content: [...comments, ...entries.map((e) => e.raw)].join('\n') + '\n', count: entries.length };
}

// Append one entry (validated), avoiding duplicates.
function addEntry(content, entry) {
  // Reject anything that is not a single token — silently keeping the first
  // word of "not a domain!" would insert garbage into the filter list.
  const clean = String(entry ?? '').trim();
  if (!clean) return { ok: false, reason: 'empty' };
  if (/\s/.test(clean)) return { ok: false, reason: 'invalid' };
  if (clean.length > 253) return { ok: false, reason: 'too_long' };
  // hostlist: domain / ^domain / IP / CIDR
  if (!/^\^?[\w.-]+$/.test(clean) && !/^\^?\d{1,3}(\.\d{1,3}){3}(\/\d{1,2})?$/.test(clean) && !/^\^?[\da-f:]+(\/\d{1,3})?$/i.test(clean)) {
    return { ok: false, reason: 'invalid' };
  }
  const entries = parseEntries(content);
  const exists = entries.some((e) => e.toLowerCase().replace(/^\^/, '') === clean.toLowerCase().replace(/^\^/, ''));
  if (exists) return { ok: false, reason: 'duplicate', content };
  const base = String(content || '');
  const prefix = base && !base.endsWith('\n') ? `${base}\n` : base;
  return { ok: true, content: `${prefix}${clean}\n`, added: clean };
}

// Apply a smart operation to a user list and persist it.
function listOp(rootDir, key, op, payload) {
  const name = USER_LISTS[key];
  if (!name) return { ok: false, reason: 'unknown_list' };
  const file = path.join(rootDir, 'lists', name);
  let content = '';
  try {
    content = fs.readFileSync(file, 'utf8');
  } catch {
    return { ok: false, reason: 'not_found' };
  }
  let result;
  switch (op) {
    case 'dedupe':
      result = dedupeEntries(content);
      break;
    case 'sort':
      result = sortEntries(content);
      break;
    case 'add':
      result = addEntry(content, payload && payload.entry);
      if (!result.ok) return { ...result, stats: listStats(content) };
      break;
    case 'remove':
      result = removeEntry(content, payload && payload.entry);
      if (!result.ok) return { ...result, stats: listStats(content) };
      break;
    case 'stripPlaceholder':
      result = { content: stripPlaceholder(content) };
      break;
    default:
      return { ok: false, reason: 'unknown_op' };
  }
  const next = result.content;
  if (Buffer.byteLength(next, 'utf8') > MAX_BYTES) {
    return { ok: false, reason: 'too_big' };
  }
  try {
    fs.writeFileSync(file, next, 'utf8');
    return { ok: true, content: next, stats: listStats(next), removed: result.removed, added: result.added, count: result.count };
  } catch {
    return { ok: false, reason: 'write_failed' };
  }
}

function removeEntry(content, entry) {
  const clean = String(entry || '').trim();
  if (!clean) return { ok: false, reason: 'empty' };
  const target = clean.toLowerCase().replace(/^\^/, '');
  const out = [];
  let removed = 0;
  for (const raw of String(content || '').split(/\r?\n/)) {
    const line = raw.trim();
    if (line && !line.startsWith('#') && !line.startsWith(';')) {
      if (line.toLowerCase().replace(/^\^/, '') === target) {
        removed++;
        continue;
      }
    }
    out.push(raw);
  }
  if (!removed) return { ok: false, reason: 'not_found' };
  return { ok: true, content: out.join('\n').replace(/\n+$/, '\n'), removed };
}

// Remove the "domain.example.abc" seed line that must never be empty.
function stripPlaceholder(content) {
  const out = String(content || '')
    .split(/\r?\n/)
    .filter((l) => !/^\^?domain\.example\.abc$/i.test(l.trim()));
  return out.join('\n').replace(/\n+$/, '\n');
}

function stats(rootDir, key) {
  const name = USER_LISTS[key];
  if (!name) return { ok: false, reason: 'unknown_list' };
  try {
    return { ok: true, stats: listStats(fs.readFileSync(path.join(rootDir, 'lists', name), 'utf8')) };
  } catch {
    return { ok: false, reason: 'not_found' };
  }
}

module.exports = {
  USER_LISTS, ensureUserLists, readList, writeList, readIpsetAll,
  parseEntries, listStats, dedupeEntries, sortEntries, addEntry, removeEntry,
  stripPlaceholder, listOp, stats, MAX_BYTES,
};
