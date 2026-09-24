'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

const updates = require('../src/main/zapret/updates');

function tmpZip(name, bytes) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ydp-sec-test-'));
  const file = path.join(dir, name);
  fs.writeFileSync(file, Buffer.from(bytes || 'PK\u0003\u0004 fake zip payload for tests'));
  return file;
}

test('verifyReleaseSecurity: missing zip => red, blocked', async () => {
  const res = await updates.verifyReleaseSecurity('u', path.join(os.tmpdir(), 'ydp-nope-999.zip'));
  assert.strictEqual(res.level, 'red');
  assert.strictEqual(res.ok, false);
});

test('verifyReleaseSecurity: garbage zip => hash unknown, sig fail (sys_missing) => red', async () => {
  const f = tmpZip('garbage.zip', 'this is not a zip');
  const res = await updates.verifyReleaseSecurity('u', f);
  const hash = res.checks.find((c) => c.id === 'hash');
  assert.strictEqual(hash.verdict, 'unknown');
  assert.strictEqual(res.level, 'red');
  assert.strictEqual(res.ok, false);
});

test('sha256File matches node crypto', async () => {
  const f = tmpZip('h.zip', 'hello');
  const { createHash } = require('node:crypto');
  const expected = createHash('sha256').update('hello').digest('hex');
  assert.strictEqual(await updates.sha256File(f), expected);
});

test('installZapret rejects before swap when security gate is red (missing zip sim)', async () => {
  // Resolve succeeds but the downloaded file is garbage -> gate must block.
  const origResolve = updates.latestReleaseAsset;
  const origDl = updates.downloadFile;
  updates.latestReleaseAsset = async () => ({
    ok: true,
    version: '9.9.9-test',
    name: 'zapret-discord-youtube-9.9.9-test.zip',
    url: 'https://example.invalid/does-not-exist.zip',
    size: 1,
  });
  updates.downloadFile = async (_url, dest) => {
    fs.writeFileSync(dest, 'not a zip at all');
    return dest;
  };
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ydp-inst-'));
  try {
    const res = await updates.installZapret(path.join(dir, 'zapret'));
    assert.strictEqual(res.ok, false);
    assert.strictEqual(res.reason, 'security_rejected');
    assert.strictEqual(res.security.level, 'red');
  } finally {
    updates.latestReleaseAsset = origResolve;
    updates.downloadFile = origDl;
    fs.rmSync(dir, { recursive: true, force: true });
  }
});