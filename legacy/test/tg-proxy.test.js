'use strict';
// Tests for the Telegram proxy lifecycle and helpers (no network required).
const { test } = require('node:test');
const assert = require('node:assert');
const net = require('node:net');
const crypto = require('node:crypto');
const tgProxy = require('../src/main/zapret/tg-proxy');
const tg = require('../src/main/zapret/tg-crypto');

// The upstream dial to Telegram must never make the test suite depend on the
// network: an unreachable DC is a valid outcome here. The tests below only
// assert the handshake stage and always wait for the connection to settle.
async function waitForLog(logs, needle, timeoutMs) {
  const t0 = Date.now();
  for (;;) {
    if (logs.some((l) => l.includes(needle))) return true;
    if (Date.now() - t0 > timeoutMs) return false;
    await new Promise((r) => setTimeout(r, 100));
  }
}

test('wsDomains picks the right relay host per DC and media flag', () => {
  assert.deepStrictEqual(tgProxy.wsDomains(2, false), ['kws2.web.telegram.org']);
  assert.deepStrictEqual(tgProxy.wsDomains(2, true), ['kws2-1.web.telegram.org', 'kws2.web.telegram.org']);
  // DC203 has no WS relays of its own and rides on DC2.
  assert.deepStrictEqual(tgProxy.wsDomains(203, false), ['kws2.web.telegram.org']);
});

test('ensureSecret is stable until regenerated', () => {
  const a = tgProxy.ensureSecret();
  const b = tgProxy.ensureSecret();
  assert.ok(a.equals(b), 'secret must not change between calls');
  assert.strictEqual(a.length, 16);
});

test('regenerateSecret issues a new 16-byte secret', () => {
  const before = tgProxy.ensureSecret().toString('hex');
  tgProxy.regenerateSecret();
  const after = tgProxy.ensureSecret().toString('hex');
  assert.notStrictEqual(before, after);
  assert.strictEqual(Buffer.from(after, 'hex').length, 16);
});

test('proxyLink embeds host, port and secret for Telegram Desktop', () => {
  const link = tgProxy.proxyLink();
  assert.match(link, /^tg:\/\/proxy\?server=127\.0\.0\.1&port=\d+&secret=[0-9a-f]{32}$/);
});

test('start/stop toggles the running state and reports stats', async () => {
  const port = 14561;
  const started = await tgProxy.start({ port });
  assert.strictEqual(started.ok, true);
  assert.strictEqual(started.running, true);
  assert.strictEqual(started.port, port);

  const again = await tgProxy.start({ port });
  assert.strictEqual(again.alreadyRunning, true, 'second start must be a no-op');

  const stopped = await tgProxy.stop();
  assert.strictEqual(stopped.running, false);
  assert.strictEqual(tgProxy.getStats().running, false);
});

test('a real obfuscated2 handshake is accepted and the DC is detected', async () => {
  const port = 14562;
  // Hermetic: no upstream dial, so the test needs no DNS/TLS/Telegram and
  // leaves no socket behind that could fire a late ECONNRESET.
  tgProxy.setUpstreamDisabled(true);
  await tgProxy.start({ port });
  try {
    const secret = tgProxy.ensureSecret();
    const logs = [];
    tgProxy.setLogger((m) => logs.push(m));

    // Build the handshake the way Telegram Desktop does.
    const hs = crypto.randomBytes(64);
    const pk = hs.subarray(8, 40);
    const iv = hs.subarray(40, 56);
    const key = crypto.createHash('sha256').update(Buffer.concat([pk, secret])).digest();
    const ks = tg.ctr(key, iv)(hs);
    const ksTail = Buffer.alloc(8);
    for (let i = 0; i < 8; i += 1) ksTail[i] = ks[56 + i] ^ hs[56 + i];
    const dcBytes = Buffer.alloc(2);
    dcBytes.writeInt16LE(4, 0);
    const tail = Buffer.concat([tg.PROTO_TAG_ABRIDGED, dcBytes, crypto.randomBytes(2)]);
    for (let i = 0; i < 8; i += 1) hs[56 + i] = tail[i] ^ ksTail[i];

    const sock = net.connect(port, '127.0.0.1');
    await new Promise((res, rej) => { sock.once('connect', res); sock.once('error', rej); });
    // The proxy tears the connection down on its own; a late ECONNRESET here
    // must not surface as an unhandled error after the test finished.
    sock.on('error', () => {});
    sock.write(hs);

    // Wait until the proxy has parsed it and moved on to the upstream dial.
    const ok = await waitForLog(logs, 'client handshake ok', 6000);
    sock.destroy();
    assert.strictEqual(ok, true, 'handshake must be parsed and logged');
    assert.ok(
      logs.some((l) => l.includes('DC4')),
      `DC must be reported, got: ${logs.join(' | ')}`
    );
    // Let the (disabled) upstream attempt finish so no async work outlives the test.
    await waitForLog(logs, 'upstream dial disabled', 5000);
  } finally {
    tgProxy.setUpstreamDisabled(false);
    tgProxy.setLogger(null);
    await tgProxy.stop();
  }
});

test('a handshake with the wrong secret is rejected', async () => {
  const port = 14563;
  await tgProxy.start({ port });
  try {
    const logs = [];
    tgProxy.setLogger((m) => logs.push(m));

    const hs = crypto.randomBytes(64);
    const fake = crypto.randomBytes(16);
    const pk = hs.subarray(8, 40);
    const key = crypto.createHash('sha256').update(Buffer.concat([pk, fake])).digest();
    const ks = tg.ctr(key, hs.subarray(40, 56))(hs);
    const ksTail = Buffer.alloc(8);
    for (let i = 0; i < 8; i += 1) ksTail[i] = ks[56 + i] ^ hs[56 + i];
    const dcBytes = Buffer.alloc(2);
    dcBytes.writeInt16LE(2, 0);
    const tail = Buffer.concat([tg.PROTO_TAG_ABRIDGED, dcBytes, crypto.randomBytes(2)]);
    for (let i = 0; i < 8; i += 1) hs[56 + i] = tail[i] ^ ksTail[i];

    const sock = net.connect(port, '127.0.0.1');
    await new Promise((res, rej) => { sock.once('connect', res); sock.once('error', rej); });
    sock.on('error', () => {});
    sock.write(hs);

    const rejected = await waitForLog(logs, 'handshake parse failed', 6000);
    sock.destroy();
    assert.strictEqual(rejected, true, 'wrong secret must be rejected');
  } finally {
    tgProxy.setLogger(null);
    await tgProxy.stop();
  }
});

test('readExactly resolves with the requested byte count', async () => {
  const srv = net.createServer(async (sock) => {
    sock.write(Buffer.from([1, 2, 3]));
    setTimeout(() => sock.write(Buffer.from([4, 5, 6, 7])), 50);
  });
  await new Promise((r) => srv.listen(14564, '127.0.0.1', r));
  const client = net.connect(14564, '127.0.0.1');
  await new Promise((r) => client.once('connect', r));
  const out = await tgProxy.readExactly(client, 6);
  assert.strictEqual(out.length, 6);
  assert.deepStrictEqual([...out], [1, 2, 3, 4, 5, 6]);
  client.destroy();
  srv.close();
});

test('readExactly rejects when the peer closes early', async () => {
  const srv = net.createServer((sock) => sock.end());
  await new Promise((r) => srv.listen(14565, '127.0.0.1', r));
  const client = net.connect(14565, '127.0.0.1');
  await new Promise((r) => client.once('connect', r));
  await assert.rejects(() => tgProxy.readExactly(client, 64));
  client.destroy();
  srv.close();
});