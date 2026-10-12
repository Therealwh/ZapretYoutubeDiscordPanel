'use strict';
// Tests for the Cloudflare relay transport (the fallback that makes Telegram
// work on networks where kws relays and DC addresses are both blocked).
const { test } = require('node:test');
const assert = require('node:assert');
const cf = require('../src/main/zapret/tg-cf');

test('normalizeDomains keeps only plain hostnames', () => {
  const list = cf.normalizeDomains('a.com\n\n B.COM \nhttp://bad\n#c\nx.y.dev\nnot_a_domain\n');
  assert.deepStrictEqual(list, ['a.com', 'b.com', 'x.y.dev']);
});

test('normalizeDomains drops garbage and keeps the rest', () => {
  assert.deepStrictEqual(cf.normalizeDomains(''), []);
  assert.deepStrictEqual(cf.normalizeDomains('123'), []);
});

test('the seed pool is non-empty and valid', () => {
  assert.ok(cf.SEED_DOMAINS.length >= 5, 'offline fallback pool must not be empty');
  for (const d of cf.SEED_DOMAINS) assert.match(d, /^[a-z0-9.-]+\.[a-z]{2,}$/);
});

test('readCached falls back to the seed list when there is no cache', () => {
  const list = cf.readCached('C:/Temp/opencode/does-not-exist-xyz');
  assert.ok(list.length >= 5);
});

test('refreshDomains resolves to a usable list even with no network', async () => {
  const list = await cf.refreshDomains('C:/Temp/opencode/tgcf-test');
  assert.ok(Array.isArray(list) && list.length >= 5, 'must always yield a usable pool');
  for (const d of list) assert.match(d, /^[a-z0-9.-]+\.[a-z]{2,}$/);
});

test('CF relay posts to /api and emits the reply', async () => {
  // Point the transport at a local stub that answers like the worker relay.
  const http2 = require('node:http2');
  const server = http2.createServer();
  server.on('stream', (stream, headers) => {
    assert.strictEqual(headers[':path'], cf.CF_PATH, 'must use the /api endpoint');
    assert.strictEqual(headers[':method'], 'POST');
    const chunks = [];
    stream.on('data', (c) => chunks.push(c));
    stream.on('end', () => {
      const received = Buffer.concat(chunks);
      stream.respond({ ':status': 200, 'content-type': 'application/octet-stream' });
      // Echo the packet back so the transport has something to emit.
      stream.end(received);
    });
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;

  const { CfRelay } = cf;
  const relay = new CfRelay(`127.0.0.1:${port}`);
  const originalConnect = require('node:http2').connect;
  // http2.connect("https://host") cannot carry a port in the authority, so
  // exercise the relay against a locally patched connector instead.
  require('node:http2').connect = (authority, opts) =>
    originalConnect(`http://127.0.0.1:${port}`, opts);
  try {
    const reply = await new Promise((resolve, reject) => {
      relay.once('message', resolve);
      relay.once('error', reject);
      relay.send(Buffer.from([1, 2, 3, 4]));
      setTimeout(() => reject(new Error('no reply')), 8000);
    });
    assert.deepStrictEqual([...reply], [1, 2, 3, 4]);
  } finally {
    require('node:http2').connect = originalConnect;
    relay.destroy();
    server.close();
  }
});

test('openCfRelay returns null when every domain fails', async () => {
  const relay = await cf.openCfRelay(['definitely-not-a-real-domain-xyz.invalid'], () => {});
  assert.strictEqual(relay, null);
}, { timeout: 30000 });