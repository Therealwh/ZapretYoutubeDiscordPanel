'use strict';
// Tests for the smart list helpers (dedupe / sort / stats / add / remove).
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const lists = require('../src/main/zapret/lists');

function tmpRoot() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ydp-lists-'));
  fs.mkdirSync(path.join(dir, 'lists'), { recursive: true });
  return dir;
}

function write(root, name, content) {
  fs.writeFileSync(path.join(root, 'lists', name), content, 'utf8');
}

test('parseEntries drops comments and blanks, keeps ^ marker', () => {
  const e = lists.parseEntries('# c\n\n^discord.com\n  discord.com  \n; semi\n');
  assert.deepStrictEqual(e, ['^discord.com', 'discord.com']);
});

test('listStats counts entries, duplicates, comments', () => {
  const s = lists.listStats('# head\nExample.COM\nexample.com\n^foo.net\n');
  assert.strictEqual(s.total, 3);
  assert.strictEqual(s.duplicates, 1);
  assert.strictEqual(s.comments, 1);
});

test('dedupeEntries removes case-insensitive duplicates, keeps first', () => {
  const r = lists.dedupeEntries('# head\nExample.COM\nexample.com\nfoo.net\n');
  assert.strictEqual(r.removed, 1);
  assert.match(r.content, /Example\.COM/);
  assert.doesNotMatch(r.content.split('\n').slice(-1)[0], /^example\.com/);
  assert.match(r.content, /foo\.net/);
});

test('dedupeEntries treats ^domain and domain as duplicates', () => {
  const r = lists.dedupeEntries('^discord.com\ndiscord.com\n');
  assert.strictEqual(r.removed, 1);
});

test('sortEntries sorts alphabetically and keeps comments first', () => {
  const r = lists.sortEntries('# top\nzebra.com\nalpha.com\n^mid.com\n');
  const lines = r.content.trim().split('\n');
  assert.strictEqual(lines[0], '# top');
  assert.deepStrictEqual(lines.slice(1), ['alpha.com', '^mid.com', 'zebra.com']);
  assert.strictEqual(r.count, 3);
});

test('addEntry validates and rejects duplicates', () => {
  const ok = lists.addEntry('a.com\n', 'b.com');
  assert.strictEqual(ok.ok, true);
  assert.match(ok.content, /b\.com/);

  const dup = lists.addEntry('a.com\n', 'A.com');
  assert.strictEqual(dup.ok, false);
  assert.strictEqual(dup.reason, 'duplicate');

  const bad = lists.addEntry('', 'not a domain!');
  assert.strictEqual(bad.ok, false);
  assert.strictEqual(bad.reason, 'invalid');

  const empty = lists.addEntry('', '   ');
  assert.strictEqual(empty.reason, 'empty');
});

test('addEntry accepts IP, CIDR and ^ prefix', () => {
  assert.strictEqual(lists.addEntry('', '8.8.8.8').ok, true);
  assert.strictEqual(lists.addEntry('', '10.0.0.0/8').ok, true);
  assert.strictEqual(lists.addEntry('', '^cloudflare.com').ok, true);
});

test('addEntry appends after content without trailing newline', () => {
  const r = lists.addEntry('a.com', 'b.com');
  assert.strictEqual(r.content, 'a.com\nb.com\n');
});

test('removeEntry deletes matching entry ignoring ^ and case', () => {
  const r = lists.removeEntry('# c\n^Discord.com\nkeep.com\n', 'discord.com');
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.removed, 1);
  assert.match(r.content, /keep\.com/);
  assert.doesNotMatch(r.content, /discord\.com/i);

  const miss = lists.removeEntry('a.com\n', 'zzz.com');
  assert.strictEqual(miss.ok, false);
  assert.strictEqual(miss.reason, 'not_found');
});

test('stripPlaceholder removes the seed domain', () => {
  const r = lists.stripPlaceholder('# Never leave this file empty\r\ndomain.example.abc\r\nreal.com\r\n');
  assert.doesNotMatch(r, /domain\.example\.abc/);
  assert.match(r, /real\.com/);
});

test('listOp persists dedupe and returns stats', () => {
  const root = tmpRoot();
  write(root, 'list-general-user.txt', '# c\na.com\nA.com\nb.com\n');
  const r = lists.listOp(root, 'general', 'dedupe');
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.removed, 1);
  assert.strictEqual(r.stats.total, 2);
  const onDisk = fs.readFileSync(path.join(root, 'lists', 'list-general-user.txt'), 'utf8');
  assert.strictEqual(onDisk, r.content);
});

test('listOp add writes entry to disk', () => {
  const root = tmpRoot();
  write(root, 'list-general-user.txt', 'a.com\n');
  const r = lists.listOp(root, 'general', 'add', { entry: 'new.com' });
  assert.strictEqual(r.ok, true);
  assert.match(fs.readFileSync(path.join(root, 'lists', 'list-general-user.txt'), 'utf8'), /new\.com/);
});

test('listOp rejects unknown list and unknown op', () => {
  const root = tmpRoot();
  assert.strictEqual(lists.listOp(root, 'nope', 'dedupe').reason, 'unknown_list');
  write(root, 'list-general-user.txt', 'a.com\n');
  assert.strictEqual(lists.listOp(root, 'general', 'wat').reason, 'unknown_op');
});

test('listOp returns not_found for a missing list', () => {
  const root = tmpRoot();
  assert.strictEqual(lists.listOp(root, 'general', 'dedupe').reason, 'not_found');
});

test('listOp enforces the 2 MB limit', () => {
  const root = tmpRoot();
  const big = `${'a'.repeat(10)}\n`.repeat(Math.ceil(lists.MAX_BYTES / 11) + 10);
  write(root, 'list-general-user.txt', big);
  const r = lists.listOp(root, 'general', 'sort');
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.reason, 'too_big');
});

test('stats reads counts from disk', () => {
  const root = tmpRoot();
  write(root, 'list-general-user.txt', 'a.com\na.com\n');
  const r = lists.stats(root, 'general');
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.stats.total, 2);
  assert.strictEqual(r.stats.duplicates, 1);
});