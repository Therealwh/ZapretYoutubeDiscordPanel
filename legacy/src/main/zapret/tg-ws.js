'use strict';
// Minimal RFC 6455 client: enough to carry MTProto binary frames to
// Telegram's kws<dc>.web.telegram.org/apiws endpoint.
// Ported in spirit from Flowseal/tg-ws-proxy proxy/raw_websocket.py.
const tls = require('node:tls');
const crypto = require('node:crypto');
const { EventEmitter } = require('node:events');

const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
const OPCODE_CONT = 0x0;
const OPCODE_BINARY = 0x2;
const OPCODE_CLOSE = 0x8;
const OPCODE_PING = 0x9;
const OPCODE_PONG = 0xa;

class RawWebSocket extends EventEmitter {
  constructor(socket) {
    super();
    this.socket = socket;
    this.buf = Buffer.alloc(0);
    this.frag = Buffer.alloc(0);
    this.fragOpcode = 0;
    this.closed = false;

    socket.on('data', (chunk) => {
      this.buf = this.buf.length ? Buffer.concat([this.buf, chunk]) : chunk;
      try {
        this._drain();
      } catch (e) {
        this.destroy(e);
      }
    });
    socket.on('error', (e) => this.destroy(e));
    socket.on('close', () => {
      if (!this.closed) {
        this.closed = true;
        this.emit('close');
      }
    });
  }

  static connect(host, domain, path = '/apiws', timeoutMs = 10000) {
    return new Promise((resolve, reject) => {
      const key = crypto.randomBytes(16).toString('base64');
      const socket = tls.connect(
        {
          host,
          port: 443,
          servername: domain,
          rejectUnauthorized: true,
        },
        () => {
          const req = [
            `GET ${path} HTTP/1.1`,
            `Host: ${domain}`,
            'Upgrade: websocket',
            'Connection: Upgrade',
            `Sec-WebSocket-Key: ${key}`,
            'Sec-WebSocket-Version: 13',
            'Sec-WebSocket-Protocol: binary',
            '',
            '',
          ].join('\r\n');
          socket.write(req);
        }
      );
      socket.setNoDelay(true);

      // Bound the WHOLE dial (DNS + TCP + TLS + upgrade). Without this a
      // blocked host keeps the socket pending for minutes, which is exactly
      // what happens on the networks this proxy exists for.
      let settled = false;
      const finish = (err) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        if (err) {
          try { socket.destroy(); } catch { /* ignore */ }
          reject(err);
        }
      };
      const timer = setTimeout(() => finish(new Error('ws_handshake_timeout')), timeoutMs);
      if (typeof timer.unref === 'function') timer.unref();
      socket.on('error', (e) => finish(e));
      // eslint-disable-next-line no-unused-vars

      let acc = Buffer.alloc(0);
      const onData = (chunk) => {
        acc = Buffer.concat([acc, chunk]);
        const idx = acc.indexOf('\r\n\r\n');
        if (idx === -1) return;
        clearTimeout(timer);
        socket.removeListener('data', onData);
        const head = acc.subarray(0, idx).toString('latin1');
        const lines = head.split('\r\n');
        const status = Number((lines[0].split(' ')[1] || 0));
        if (status !== 101) {
          socket.destroy();
          reject(new Error(`ws_handshake_${status}`));
          return;
        }
        const expected = crypto.createHash('sha1').update(key + GUID).digest('base64');
        const accept = (lines.find((l) => /^sec-websocket-accept:/i.test(l)) || '').split(':')[1] || '';
        if (accept.trim() !== expected) {
          socket.destroy();
          reject(new Error('ws_accept_mismatch'));
          return;
        }
        const rest = acc.subarray(idx + 4);
        const ws = new RawWebSocket(socket);
        if (rest.length) {
          ws.buf = rest;
          try {
            ws._drain();
          } catch (e) {
            ws.destroy(e);
          }
        }
        resolve(ws);
      };
      socket.on('data', onData);
      socket.on('error', (e) => {
        clearTimeout(timer);
        reject(e);
      });
    });
  }

  _drain() {
    for (;;) {
      const frame = this._readFrame();
      if (!frame) return;
      const { fin, opcode, payload } = frame;
      if (opcode === OPCODE_PING) {
        this._send(OPCODE_PONG, payload);
        continue;
      }
      if (opcode === OPCODE_PONG) continue;
      if (opcode === OPCODE_CLOSE) {
        this.destroy();
        this.emit('close');
        return;
      }
      if (opcode === OPCODE_CONT) {
        this.frag = Buffer.concat([this.frag, payload]);
      } else {
        this.frag = Buffer.from(payload);
        this.fragOpcode = opcode;
      }
      if (fin) {
        const data = this.frag;
        this.frag = Buffer.alloc(0);
        if (this.fragOpcode === OPCODE_BINARY) this.emit('message', data);
      }
    }
  }

  // Returns one decoded frame, or null when more bytes are needed.
  _readFrame() {
    const b = this.buf;
    if (b.length < 2) return null;
    const fin = (b[0] & 0x80) !== 0;
    const opcode = b[0] & 0x0f;
    const masked = (b[1] & 0x80) !== 0;
    let len = b[1] & 0x7f;
    let offset = 2;
    if (len === 126) {
      if (b.length < offset + 2) return null;
      len = b.readUInt16BE(offset);
      offset += 2;
    } else if (len === 127) {
      if (b.length < offset + 8) return null;
      const big = b.readBigUInt64BE(offset);
      if (big > BigInt(Number.MAX_SAFE_INTEGER)) throw new Error('ws_frame_too_big');
      len = Number(big);
      offset += 8;
    }
    let mask = null;
    if (masked) {
      if (b.length < offset + 4) return null;
      mask = b.subarray(offset, offset + 4);
      offset += 4;
    }
    if (b.length < offset + len) return null;
    const payload = Buffer.from(b.subarray(offset, offset + len));
    if (mask) {
      for (let i = 0; i < payload.length; i += 1) payload[i] ^= mask[i % 4];
    }
    this.buf = b.subarray(offset + len);
    return { fin, opcode, payload };
  }

  _send(opcode, payload) {
    if (this.closed || this.socket.destroyed) return;
    const len = payload.length;
    const mask = crypto.randomBytes(4);
    let header;
    if (len < 126) {
      header = Buffer.alloc(6);
      header[1] = 0x80 | len;
    } else if (len < 65536) {
      header = Buffer.alloc(8);
      header[1] = 0x80 | 126;
      header.writeUInt16BE(len, 2);
    } else {
      header = Buffer.alloc(14);
      header[1] = 0x80 | 127;
      header.writeBigUInt64BE(BigInt(len), 2);
    }
    header[0] = 0x80 | opcode; // FIN + opcode
    mask.copy(header, header.length - 4);
    const masked = Buffer.from(payload);
    for (let i = 0; i < masked.length; i += 1) masked[i] ^= mask[i % 4];
    this.socket.write(Buffer.concat([header, masked]));
  }

  send(data) {
    this._send(OPCODE_BINARY, Buffer.isBuffer(data) ? data : Buffer.from(data));
  }

  destroy(err) {
    if (this.closed) return;
    this.closed = true;
    try { this.socket.destroy(); } catch { /* already gone */ }
    if (err) this.emit('error', err);
    this.emit('close');
  }
}

module.exports = { RawWebSocket, OPCODE_BINARY, OPCODE_CLOSE };