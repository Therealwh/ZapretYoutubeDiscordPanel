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
  const relay = await cf.openCfRelay(
    ['definitely-not-a-real-domain-xyz.invalid'],
    () => {},
    Buffer.from([0xef, 0x48, 0xad, 0x89, 1, 2, 3, 4])
  );
  assert.strictEqual(relay, null);
}, { timeout: 30000 });

test('openCfRelay refuses to guess without a real probe packet', async () => {
  const logs = [];
  assert.strictEqual(cf.openCfRelay(['a.example'], (m) => logs.push(m)) instanceof Promise, true);
  assert.strictEqual(await cf.openCfRelay(['a.example'], (m) => logs.push(m)), null);
  assert.strictEqual(await cf.openCfRelay(['a.example'], (m) => logs.push(m), Buffer.alloc(0)), null);
  assert.ok(logs.every((l) => l.includes('probe packet missing')), 'must log why it gave up');
});

test('openCfRelay probes with a real packet and skips dead domains first', async () => {
  // Stub worker that only answers when it receives a non-empty body, exactly
  // like the real relay — this is what an empty probe packet got wrong.
  const http2 = require('node:http2');
  const server = http2.createServer();
  const bodies = [];
  server.on('stream', (stream) => {
    const chunks = [];
    stream.on('data', (c) => chunks.push(c));
    stream.on('end', () => {
      const received = Buffer.concat(chunks);
      bodies.push(received);
      if (!received.length) {
        stream.respond({ ':status': 400 });
        stream.end();
        return;
      }
      stream.respond({ ':status': 200, 'content-type': 'application/octet-stream' });
      stream.end(received);
    });
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;

  const logs = [];
  const originalConnect = http2.connect;
  const originalHttpsRequest = require('node:https').request;
  // Route every authority (dead domain and live stub) to the local H2 server,
  // and make the dead domain fail at the H2 layer only.
  http2.connect = (authority, opts) => {
    const dead = String(authority).includes('dead-domain-xyz.invalid');
    if (dead) {
      const { EventEmitter } = require('node:events');
      const fake = new EventEmitter();
      fake.close = () => {};
      process.nextTick(() => fake.emit('error', new Error('ENOTFOUND')));
      return fake;
    }
    return originalConnect(`http://127.0.0.1:${port}`, opts);
  };
  require('node:https').request = (opts, cb) => {
    if (String(opts.hostname || '').includes('dead-domain-xyz.invalid')) {
      const { EventEmitter } = require('node:events');
      const fake = new EventEmitter();
      fake.on = fake.on.bind(fake);
      fake.setTimeout = () => {};
      process.nextTick(() => fake.emit('error', new Error('ENOTFOUND')));
      return fake;
    }
    return originalHttpsRequest(opts, cb);
  };

  try {
    const relay = await cf.openCfRelay(
      ['dead-domain-xyz.invalid', `127.0.0.1:${port}`],
      (msg, level) => logs.push(`${level}:${msg}`),
      Buffer.from([0xef, 0x48, 0xad, 0x89, 1, 2, 3, 4])
    );
    assert.ok(relay, 'a live domain must be selected even if the first one fails');
    assert.strictEqual(relay.domain, `127.0.0.1:${port}`);
    assert.ok(logs.some((l) => l.includes('dead-domain-xyz.invalid') && l.startsWith('warn:')), 'dead domain must be logged');
    assert.ok(logs.some((l) => l.includes('CF relay ready')), 'ready line must be logged');
    assert.equal(bodies.length, 1);
    assert.ok(bodies[0].length > 0, 'probe must carry a real packet, never an empty body');
    relay.destroy();
  } finally {
    http2.connect = originalConnect;
    require('node:https').request = originalHttpsRequest;
    server.close();
  }
}, { timeout: 30000 });

test('loadSecret keeps the persisted secret across restarts', () => {
  const proxy = require('../src/main/zapret/tg-proxy');
  const stored = '0123456789abcdef0123456789abcdef';
  assert.strictEqual(proxy.loadSecret(stored), true);
  const first = proxy.ensureSecret().toString('hex');
  assert.strictEqual(first, stored, 'ensureSecret must not invent a new secret');
  // A fresh start (empty in-memory state) reloaded from config keeps it stable.
  assert.strictEqual(proxy.loadSecret(first), true);
  assert.strictEqual(proxy.ensureSecret().toString('hex'), stored);
  assert.ok(proxy.proxyLink().includes(stored), 'tg:// link must carry the same secret');
});

test('loadSecret rejects garbage instead of silently rotating', () => {
  const proxy = require('../src/main/zapret/tg-proxy');
  const before = proxy.ensureSecret().toString('hex');
  assert.strictEqual(proxy.loadSecret('not-hex'), false);
  assert.strictEqual(proxy.loadSecret('aabb'), false);
  assert.strictEqual(proxy.loadSecret(null), false);
  assert.strictEqual(proxy.loadSecret(''), false);
  assert.strictEqual(proxy.ensureSecret().toString('hex'), before, 'secret must be untouched');
});

test('regenerateSecret returns a persistable secret that differs', () => {
  const proxy = require('../src/main/zapret/tg-proxy');
  const before = proxy.regenerateSecret();
  const after = proxy.regenerateSecret();
  assert.match(after.secretHex, /^[0-9a-f]{32}$/);
  assert.notStrictEqual(before.secretHex, after.secretHex);
  assert.strictEqual(proxy.loadSecret(after.secretHex), true);
});