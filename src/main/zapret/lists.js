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

module.exports = { USER_LISTS, ensureUserLists, readList, writeList, readIpsetAll };
