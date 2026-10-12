'use strict';
// Telegram MTProto -> WebSocket proxy (127.0.0.1:1443 by default).
//
// Flow per client connection (port of Flowseal/tg-ws-proxy):
//   1. read the 64-byte obfuscated2 handshake, extract auth_key, DC id, media
//      flag and transport protocol;
//   2. open a WebSocket to kws<dc>.web.telegram.org/apiws (media: kws<dc>-1);
//   3. send our own obfuscated2 handshake built for that DC;
//   4. client ciphertext -> strip client obfuscation -> read plaintext length
//      -> re-wrap with the relay obfuscation -> one WS frame per MTProto packet;
//      and the reverse for the downlink.
//
// The inner MTProto layer is never decrypted here, so plain AES-256-CTR (see
// tg-crypto.js) is all the crypto this bridge needs.
const net = require('node:net');
const crypto = require('node:crypto');
const { RawWebSocket } = require('./tg-ws');
const tg = require('./tg-crypto');

const DEFAULT_PORT = 1443;
const DEFAULT_HOST = '127.0.0.1';
const WS_PATH = '/apiws';

// Telegram DC addresses used as a direct-TCP fallback when WebSocket is cut.
const DC_DEFAULT_IPS = {
  1: '149.154.175.50',
  2: '149.154.167.51',
  3: '149.154.175.100',
  4: '149.154.167.91',
  5: '149.154.171.5',
  203: '91.105.192.100',
};

function wsDomains(dc, isMedia) {
  const id = dc === 203 ? 2 : dc; // 203 rides on DC2 relays
  if (!isMedia) return [`kws${id}.web.telegram.org`];
  return [`kws${id}-1.web.telegram.org`, `kws${id}.web.telegram.org`];
}

const state = {
  running: false,
  port: DEFAULT_PORT,
  host: DEFAULT_HOST,
  secret: null,
  server: null,
  connections: 0,
  bytesUp: 0,
  bytesDown: 0,
  lastError: null,
  startedAt: null,
};

let log = () => {};

function setLogger(fn) {
  log = typeof fn === 'function' ? fn : () => {};
}

function genSecret() {
  return crypto.randomBytes(16);
}

function ensureSecret() {
  if (!state.secret) state.secret = genSecret();
  return state.secret;
}

function getStats() {
  return {
    running: state.running,
    host: state.host,
    port: state.port,
    connections: state.connections,
    bytesUp: state.bytesUp,
    bytesDown: state.bytesDown,
    lastError: state.lastError,
    startedAt: state.startedAt,
    proxyUrl: proxyLink(),
  };
}

// tg:// link for Telegram Desktop ("Add proxy" prefills host/port/secret).
function proxyLink() {
  const hex = state.secret ? state.secret.toString('hex') : '';
  return `tg://proxy?server=${state.host}&port=${state.port}&secret=${hex}`;
}

function start(opts = {}) {
  if (state.running) return { ok: true, alreadyRunning: true, ...getStats() };
  state.port = Number(opts.port) || DEFAULT_PORT;
  state.host = opts.host || DEFAULT_HOST;
  if (opts.secretHex) {
    try {
      const buf = Buffer.from(String(opts.secretHex), 'hex');
      if (buf.length === 16) state.secret = buf;
    } catch { /* keep generated secret */ }
  }
  const secret = ensureSecret();

  const server = net.createServer((socket) => {
    state.connections += 1;
    handleClient(socket, secret).catch((e) => {
      state.lastError = String((e && e.message) || e);
      log(`client error: ${state.lastError}`, 'error');
      try { socket.destroy(); } catch { /* already gone */ }
    });
  });

  server.on('error', (e) => {
    state.lastError = String((e && e.message) || e);
    state.running = false;
    log(`proxy server error: ${state.lastError}`, 'error');
  });

  return new Promise((resolve) => {
    server.listen(state.port, state.host, () => {
      state.server = server;
      state.running = true;
      state.lastError = null;
      state.startedAt = new Date().toISOString();
      log(`Telegram proxy listening on ${state.host}:${state.port}`, 'info');
      resolve({ ok: true, ...getStats() });
    });
  });
}

function stop() {
  return new Promise((resolve) => {
    if (!state.server) {
      state.running = false;
      resolve({ ok: true, running: false });
      return;
    }
    const srv = state.server;
    state.server = null;
    state.running = false;
    srv.close(() => resolve({ ok: true, running: false }));
    // Drop lingering sockets so close() is not blocked by keep-alive clients.
    try { srv.unref(); } catch { /* ignore */ }
  });
}

async function restart(opts) {
  await stop();
  return start(opts || {});
}

function regenerateSecret() {
  state.secret = genSecret();
  return { ok: true, proxyUrl: proxyLink() };
}

// Read exactly n bytes from a socket (mirrors asyncio readexactly).
function readExactly(socket, n, timeoutMs = 10000) {
  return new Promise((resolve, reject) => {
    let acc = Buffer.alloc(0);
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error('read_timeout'));
    }, timeoutMs);
    if (typeof timer.unref === 'function') timer.unref();

    const onData = (chunk) => {
      acc = Buffer.concat([acc, chunk]);
      if (acc.length >= n) {
        cleanup();
        resolve(acc.subarray(0, n));
      }
    };
    const onEnd = () => {
      cleanup();
      reject(new Error(acc.length ? 'eof_mid_handshake' : 'eof'));
    };
    const onErr = (e) => {
      cleanup();
      reject(e);
    };
    function cleanup() {
      clearTimeout(timer);
      socket.removeListener('data', onData);
      socket.removeListener('end', onEnd);
      socket.removeListener('close', onEnd);
      socket.removeListener('error', onErr);
    }
    socket.on('data', onData);
    socket.once('end', onEnd);
    socket.once('close', onEnd);
    socket.once('error', onErr);
  });
}

async function handleClient(socket, secret) {
  socket.setNoDelay(true);
  socket.on('error', () => { /* handled by the bridge teardown */ });

  // 1) handshake first, with the stream flowing so the listener actually gets it.
  let handshake;
  try {
    handshake = await readExactly(socket, tg.HANDSHAKE_LEN);
  } catch {
    try { socket.destroy(); } catch { /* ignore */ }
    return;
  }

  const parsed = tg.parseHandshake(handshake, secret);
  if (!parsed) {
    log('handshake parse failed (wrong secret or not MTProto)', 'warn');
    try { socket.destroy(); } catch { /* ignore */ }
    return;
  }

  // 2) hold the stream while the upstream dial is in flight. Once the listener
  // is removed the socket buffers again, so nothing can be lost here — this is
  // the "pause during dial" guard from the reference implementation.
  socket.pause();

  const dc = parsed.dcId;
  const tag = `DC${dc}${parsed.isMedia ? 'm' : ''}`;
  log(`${tag}: client handshake ok`, 'info');

  const relayInit = tg.generateRelayInit(parsed.protoTag, parsed.isMedia ? -dc : dc);
  const ctx = tg.buildCryptoCtx(parsed.decPrekeyIv, secret, relayInit);
  const splitter = new tg.MsgSplitter(tg.protoIntOf(parsed.protoTag));

  let ws = null;
  let lastErr = null;
  for (const domain of wsDomains(dc, parsed.isMedia)) {
    try {
      ws = await RawWebSocket.connect(domain, domain, WS_PATH);
      lastErr = null;
      break;
    } catch (e) {
      lastErr = e;
      log(`${tag}: ws ${domain} failed (${e.message})`, 'warn');
    }
  }
  if (!ws) {
    state.lastError = `ws_connect_failed: ${lastErr ? lastErr.message : 'no domain'}`;
    log(`${tag}: no upstream WebSocket available`, 'error');
    try { socket.destroy(); } catch { /* ignore */ }
    return;
  }

  log(`${tag}: WebSocket established`, 'info');
  // Our handshake must be the first frame towards the DC.
  ws.send(relayInit);

  let closed = false;
  const teardown = () => {
    if (closed) return;
    closed = true;
    try { ws.destroy(); } catch { /* ignore */ }
    try { socket.destroy(); } catch { /* ignore */ }
    log(`${tag}: closed`, 'info');
  };

  // client -> Telegram
  socket.on('data', (chunk) => {
    if (closed) return;
    try {
      const plain = ctx.cltDec(chunk);
      const reencrypted = ctx.tgEnc(plain);
      const parts = splitter.push(reencrypted, plain);
      if (!parts.length) return;
      for (const p of parts) ws.send(p);
      state.bytesUp += chunk.length;
    } catch (e) {
      log(`${tag}: upstream send failed (${e.message})`, 'error');
      teardown();
    }
  });
  socket.on('close', teardown);
  socket.on('error', teardown);
  // Everything buffered while the dial was in flight now flows.
  socket.resume();

  // Telegram -> client
  ws.on('message', (data) => {
    if (closed) return;
    try {
      const plain = ctx.tgDec(data);
      socket.write(ctx.cltEnc(plain));
      state.bytesDown += data.length;
    } catch (e) {
      log(`${tag}: downstream decrypt failed (${e.message})`, 'error');
      teardown();
    }
  });
  ws.on('close', teardown);
  ws.on('error', (e) => {
    log(`${tag}: ws error (${e.message})`, 'warn');
    teardown();
  });
}

module.exports = {
  start,
  stop,
  restart,
  getStats,
  setLogger,
  regenerateSecret,
  proxyLink,
  ensureSecret,
  wsDomains,
  DC_DEFAULT_IPS,
  DEFAULT_PORT,
  DEFAULT_HOST,
  readExactly,
};