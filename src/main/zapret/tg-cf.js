'use strict';
// Cloudflare relay transport for the Telegram proxy.
//
// Why this exists: on many networks BOTH the WebSocket relays
// (kws<dc>.web.telegram.org) and the raw DC addresses are unreachable, while
// Cloudflare-fronted worker domains still work. Flowseal/tg-ws-proxy uses the
// same trick (proxy/cf_h2.py): every MTProto packet is POSTed to
//     POST https://<worker-domain>/api
// and the response body is the reply packet.
//
// Ported from Flowseal/tg-ws-proxy (MIT/GPL-3.0 project by the same author
// as zapret-discord-youtube); the domain list is refreshed from that repo.
const https = require('node:https');
const http2 = require('node:http2');
const fs = require('node:fs');
const path = require('node:path');
const { EventEmitter } = require('node:events');

const DOMAINS_URL =
  'https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt';

// Seed list so the proxy still works if the refresh fails (offline / blocked).
const SEED_DOMAINS = [
  'virkgj.com', 'vmmzovy.com', 'mkuosckvso.com', 'zaewayzmplad.com', 'twdmbzcm.com',
  'awzwsldi.com', 'clngqrflngqin.com', 'tjacxbqtj.com', 'bxaxtxmrw.com', 'dmohrsgmohcrwb.com',
  'vwbmtmoi.com', 'khgrre.com',
];

const CF_PATH = '/api';
const TIMEOUT_MS = 15000;
const REFRESH_FILE = 'cf-domains.json';

function normalizeDomains(raw) {
  return String(raw || '')
    .split(/\r?\n/)
    .map((l) => l.trim().toLowerCase())
    .filter((l) => /^[a-z0-9.-]+\.[a-z]{2,}$/.test(l));
}

// Refresh the pool from upstream; failures keep the previous list.
function refreshDomains(userDataDir, onLog) {
  return new Promise((resolve) => {
    const req = https.get(
      DOMAINS_URL,
      { headers: { 'User-Agent': 'YoutubeDiscordPanel' }, timeout: 10000 },
      (res) => {
        if (res.statusCode !== 200) {
          res.resume();
          onLog?.(`CF domain refresh failed: HTTP ${res.statusCode}`, 'warn');
          resolve(readCached(userDataDir));
          return;
        }
        let body = '';
        res.setEncoding('utf8');
        res.on('data', (c) => {
          body += c;
          if (body.length > 200000) req.destroy();
        });
        res.on('end', () => {
          const list = normalizeDomains(body);
          if (list.length) {
            try {
              fs.writeFileSync(path.join(userDataDir, REFRESH_FILE), JSON.stringify(list), 'utf8');
            } catch { /* cache is optional */ }
            onLog?.(`CF relay pool refreshed: ${list.length} domains`, 'info');
            resolve(list);
          } else {
            resolve(readCached(userDataDir));
          }
        });
      }
    );
    req.on('timeout', () => {
      req.destroy();
      resolve(readCached(userDataDir));
    });
    req.on('error', () => resolve(readCached(userDataDir)));
  });
}

function readCached(userDataDir) {
  try {
    const raw = JSON.parse(fs.readFileSync(path.join(userDataDir, REFRESH_FILE), 'utf8'));
    const list = normalizeDomains(raw.join('\n'));
    if (list.length) return list;
  } catch { /* no cache */ }
  return SEED_DOMAINS.slice();
}

// One packet = one POST. Returns the reply body.
//
// HTTP/2 is used first because the worker relays are H2-only in practice (the
// reference implementation treats an HTTP/1.1 response as an error); the
// HTTP/1.1 path stays as a fallback for relays that accept it.
function postPacket(domain, payload, timeoutMs = TIMEOUT_MS) {
  return postPacketH2(domain, payload, timeoutMs).catch(() => postPacketH1(domain, payload, timeoutMs));
}

function postPacketH2(domain, payload, timeoutMs) {
  return new Promise((resolve, reject) => {
    const client = http2.connect(`https://${domain}`, { timeout: timeoutMs });
    const done = (fn, arg) => {
      try { client.close(); } catch { /* already closed */ }
      fn(arg);
    };
    client.on('error', (e) => done(reject, e));
    const req = client.request({
      ':method': 'POST',
      ':path': CF_PATH,
      'content-type': 'application/octet-stream',
      'accept-encoding': 'identity',
      'user-agent': 'YoutubeDiscordPanel',
    });
    const timer = setTimeout(() => {
      try { req.close(); } catch { /* ignore */ }
      done(reject, new Error('cf_h2_timeout'));
    }, timeoutMs);
    if (typeof timer.unref === 'function') timer.unref();

    const chunks = [];
    let status = 0;
    let headersDone = false;
    req.on('response', (headers) => {
      headersDone = true;
      status = Number(headers[':status'] || 0);
    });
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      clearTimeout(timer);
      const body = Buffer.concat(chunks);
      if (status !== 200 || !body.length) return done(reject, new Error(`cf_h2_${status || 'empty'}`));
      done(resolve, body);
    });
    req.on('error', (e) => {
      clearTimeout(timer);
      done(reject, e);
    });
    req.end(payload);
  });
}

function postPacketH1(domain, payload, timeoutMs) {
  return new Promise((resolve, reject) => {
    const req = https.request(
      {
        hostname: domain,
        port: 443,
        path: CF_PATH,
        method: 'POST',
        headers: {
          'Content-Type': 'application/octet-stream',
          'Content-Length': payload.length,
          'Accept-Encoding': 'identity',
          'User-Agent': 'YoutubeDiscordPanel',
          Host: domain,
        },
        servername: domain,
        timeout: timeoutMs,
      },
      (res) => {
        if (res.statusCode !== 200) {
          res.resume();
          reject(new Error(`cf_http_${res.statusCode}`));
          return;
        }
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => {
          const body = Buffer.concat(chunks);
          if (!body.length) reject(new Error('cf_empty'));
          else resolve(body);
        });
        res.on('error', reject);
      }
    );
    req.on('timeout', () => {
      req.destroy();
      reject(new Error('cf_timeout'));
    });
    req.on('error', reject);
    req.end(payload);
  });
}

/**
 * CF transport: transparently POSTs packets and emits replies.
 * Same interface as the WebSocket transport (send + 'message' event).
 */
class CfRelay extends EventEmitter {
  constructor(domain) {
    super();
    this.domain = domain;
    this.closed = false;
    this.kind = 'cf';
  }

  send(payload) {
    if (this.closed) return;
    postPacket(this.domain, payload).then(
      (reply) => {
        if (!this.closed) this.emit('message', reply);
      },
      (err) => {
        if (this.closed) return;
        this.emit('error', err);
        this.emit('close');
      }
    );
  }

  destroy() {
    if (this.closed) return;
    this.closed = true;
    this.emit('close');
  }
}

// Try domains in order until one answers.
async function openCfRelay(domains, onLog) {
  for (const domain of domains) {
    try {
      await postPacket(domain, Buffer.alloc(0), 8000);
      onLog?.(`CF relay ready: ${domain}`, 'info');
      return new CfRelay(domain);
    } catch (e) {
      onLog?.(`CF ${domain} failed (${e.message})`, 'warn');
    }
  }
  return null;
}

module.exports = { CfRelay, openCfRelay, refreshDomains, normalizeDomains, readCached, SEED_DOMAINS, CF_PATH };