'use strict';
// MTProto obfuscated2 crypto for the Telegram WebSocket bridge.
//
// Ported from Flowseal/tg-ws-proxy (proxy/tg_ws_proxy.py):
//   _try_handshake()       -> parseHandshake()
//   _generate_relay_init() -> generateRelayInit()
//   _build_crypto_ctx()     -> buildCryptoCtx()
//
// The proxy never touches MTProto's inner (IGE) layer: it strips the client's
// outer obfuscated2 encryption, reads the plaintext length header, re-wraps
// the opaque inner blob with its own obfuscated2 key and ships it over the
// WebSocket. That is why plain AES-256-CTR is enough — Node has it natively.
//
// Protocol constants (proxy/utils.py):
const HANDSHAKE_LEN = 64;
const SKIP_LEN = 8;
const PREKEY_LEN = 32;
const KEY_LEN = 32;
const IV_LEN = 16;
const PROTO_TAG_POS = 56;
const DC_IDX_POS = 60;

const PROTO_TAG_ABRIDGED = Buffer.from([0xef, 0xef, 0xef, 0xef]);
const PROTO_TAG_INTERMEDIATE = Buffer.from([0xee, 0xee, 0xee, 0xee]);
const PROTO_TAG_SECURE = Buffer.from([0xdd, 0xdd, 0xdd, 0xdd]);

const PROTO_ABRIDGED_INT = 0xefefefef;
const PROTO_INTERMEDIATE_INT = 0xeeeeeeee;
const PROTO_PADDED_INTERMEDIATE_INT = 0xdddddddd;

// Random first-4/continuation-4 bytes that would make the handshake look like
// HTTP methods — Telegram rejects those (utils.py RESERVED_*).
const RESERVED_FIRST_BYTES = new Set([0xef]);
const RESERVED_STARTS = new Set(['HEAD', 'POST', 'GET ']);
const RESERVED_CONTINUE = Buffer.from([0x00, 0x00, 0x00, 0x00]);

const ZERO_64 = Buffer.alloc(64);
const crypto = require('node:crypto');

/**
 * Stateful AES-256-CTR stream.
 *
 * Node's built-in 'aes-256-ctr' cipher object does NOT carry its keystream
 * offset across update() calls, which silently breaks a persistent obfuscation
 * stream (every chunk would be encrypted as if it started at counter zero).
 * So the counter block is tracked here and the keystream is produced with
 * AES-256-ECB, which makes the stream position explicit and correct.
 */
function ctr(key, iv) {
  if (key.length !== KEY_LEN) throw new Error('aes-256-ctr needs a 32-byte key');
  if (iv.length !== IV_LEN) throw new Error('aes-256-ctr needs a 16-byte counter block');
  const ecb = crypto.createCipheriv('aes-256-ecb', key, null);
  const ctrBlock = Buffer.from(iv);
  let offset = 0; // bytes already consumed from the current block

  return (data) => {
    const input = Buffer.isBuffer(data) ? data : Buffer.from(data);
    const out = Buffer.allocUnsafe(input.length);
    let written = 0;
    while (written < input.length) {
      // Encrypt the CURRENT counter block first (the stream starts at IV),
      // then advance once the whole block has been consumed.
      const ks = ecb.update(ctrBlock);
      const n = Math.min(BLOCK - offset, input.length - written);
      for (let i = 0; i < n; i += 1) out[written + i] = input[written + i] ^ ks[offset + i];
      written += n;
      offset += n;
      if (offset === BLOCK) {
        offset = 0;
        let carry = 1;
        for (let i = BLOCK - 1; i >= 0 && carry; i -= 1) {
          const sum = ctrBlock[i] + carry;
          ctrBlock[i] = sum & 0xff;
          carry = sum >> 8;
        }
      }
    }
    return out;
  };
}

const BLOCK = 16;

/**
 * Parse the 64-byte obfuscated2 handshake.
 * @returns {{dcId:number, isMedia:boolean, protoTag:Buffer, decPrekeyIv:Buffer}|null}
 */
function parseHandshake(handshake, secret) {
  const hs = Buffer.from(handshake);
  if (hs.length < HANDSHAKE_LEN) return null;
  const decPrekeyIv = hs.subarray(SKIP_LEN, SKIP_LEN + PREKEY_LEN + IV_LEN);
  const decPrekey = decPrekeyIv.subarray(0, PREKEY_LEN);
  const decIv = decPrekeyIv.subarray(PREKEY_LEN);

  const decKey = crypto.createHash('sha256').update(Buffer.concat([decPrekey, secret])).digest();
  const plain = ctr(decKey, decIv)(hs);

  const protoTag = plain.subarray(PROTO_TAG_POS, PROTO_TAG_POS + 4);
  if (!isKnownProtoTag(protoTag)) return null;

  const dcIdx = plain.readInt16LE(DC_IDX_POS);
  return {
    dcId: Math.abs(dcIdx),
    isMedia: dcIdx < 0,
    protoTag: Buffer.from(protoTag),
    decPrekeyIv: Buffer.from(decPrekeyIv),
  };
}

function isKnownProtoTag(tag) {
  return tag.equals(PROTO_TAG_ABRIDGED) || tag.equals(PROTO_TAG_INTERMEDIATE) || tag.equals(PROTO_TAG_SECURE);
}

function protoIntOf(tag) {
  if (tag.equals(PROTO_TAG_ABRIDGED)) return PROTO_ABRIDGED_INT;
  if (tag.equals(PROTO_TAG_INTERMEDIATE)) return PROTO_INTERMEDIATE_INT;
  return PROTO_PADDED_INTERMEDIATE_INT;
}

/**
 * Build the handshake we present to the upstream DC. The 8 bytes that carry
 * (proto_tag, dc_id, random) are encrypted with the keystream that the random
 * prefix itself produces, so the recipient can recover them.
 */
function generateRelayInit(protoTag, dcIdx) {
  for (;;) {
    const rnd = crypto.randomBytes(HANDSHAKE_LEN);
    if (RESERVED_FIRST_BYTES.has(rnd[0])) continue;
    if (RESERVED_STARTS.has(rnd.subarray(0, 4).toString('latin1'))) continue;
    if (rnd.subarray(4, 8).equals(RESERVED_CONTINUE)) continue;

    const encKey = rnd.subarray(SKIP_LEN, SKIP_LEN + PREKEY_LEN);
    const encIv = rnd.subarray(SKIP_LEN + PREKEY_LEN, SKIP_LEN + PREKEY_LEN + IV_LEN);

    // Full keystream over the random block.
    const encryptedFull = ctr(encKey, encIv)(rnd);
    const keystreamTail = Buffer.alloc(8);
    for (let i = 0; i < 8; i += 1) keystreamTail[i] = encryptedFull[56 + i] ^ rnd[56 + i];

    const dcBytes = Buffer.alloc(2);
    dcBytes.writeInt16LE(dcIdx, 0);
    const tailPlain = Buffer.concat([protoTag, dcBytes, crypto.randomBytes(2)]);
    const encryptedTail = Buffer.alloc(8);
    for (let i = 0; i < 8; i += 1) encryptedTail[i] = tailPlain[i] ^ keystreamTail[i];

    const result = Buffer.from(rnd);
    encryptedTail.copy(result, PROTO_TAG_POS);
    return result;
  }
}

/**
 * Build the four re-encryption streams.
 * Client side uses the secret-hashed key with a REVERSED prekey+iv (echo mode);
 * relay side uses the raw prekey (standard obfuscation, no secret).
 */
function buildCryptoCtx(clientDecPrekeyIv, secret, relayInit) {
  const cltDecPrekey = clientDecPrekeyIv.subarray(0, PREKEY_LEN);
  const cltDecIv = clientDecPrekeyIv.subarray(PREKEY_LEN);
  const cltDecKey = crypto.createHash('sha256').update(Buffer.concat([cltDecPrekey, secret])).digest();

  const cltEncPrekeyIv = Buffer.from(clientDecPrekeyIv).reverse();
  const cltEncKey = crypto
    .createHash('sha256')
    .update(Buffer.concat([cltEncPrekeyIv.subarray(0, PREKEY_LEN), secret]))
    .digest();
  const cltEncIv = cltEncPrekeyIv.subarray(PREKEY_LEN);

  const cltDec = ctr(cltDecKey, cltDecIv);
  const cltEnc = ctr(cltEncKey, cltEncIv);

  const relayEncKey = relayInit.subarray(SKIP_LEN, SKIP_LEN + PREKEY_LEN);
  const relayEncIv = relayInit.subarray(SKIP_LEN + PREKEY_LEN, SKIP_LEN + PREKEY_LEN + IV_LEN);
  const relayDecPrekeyIv = Buffer.from(relayInit.subarray(SKIP_LEN, SKIP_LEN + PREKEY_LEN + IV_LEN)).reverse();
  const relayDecKey = relayDecPrekeyIv.subarray(0, KEY_LEN);
  const relayDecIv = relayDecPrekeyIv.subarray(KEY_LEN);

  const tgEnc = ctr(relayEncKey, relayEncIv);
  const tgDec = ctr(relayDecKey, relayDecIv);

  // Skip the 64 handshake bytes in the two directions that already exchanged
  // one: client->relay (we consumed the client's handshake) and relay->client
  // (we are about to send ours). The opposite directions start fresh.
  cltDec(ZERO_64);
  tgEnc(ZERO_64);

  return { cltDec, cltEnc, tgEnc, tgDec };
}

/**
 * Split a TCP stream into individual MTProto transport packets so each can be
 * one WebSocket frame. Lengths come from the DECRYPTED (outer) plaintext, whose
 * header is never encrypted by the inner layer.
 * Ported from bridge.py MsgSplitter.
 */
class MsgSplitter {
  constructor(protoInt) {
    this.proto = protoInt;
    this.cipherBuf = [];
    this.plainBuf = [];
    this.disabled = false;
  }

  push(chunk, plain) {
    if (!chunk.length) return [];
    if (this.disabled) return [Buffer.from(chunk)];
    this.cipherBuf.push(chunk);
    this.plainBuf.push(plain);

    const cipher = this.cipherBuf.length === 1 ? this.cipherBuf[0] : Buffer.concat(this.cipherBuf);
    const plainAll = this.plainBuf.length === 1 ? this.plainBuf[0] : Buffer.concat(this.plainBuf);
    // Remember where we are so partial input can continue next call.
    this.cipherBuf = [cipher];
    this.plainBuf = [plainAll];

    const parts = [];
    let offset = 0;
    while (offset < cipher.length) {
      const packetLen = this._nextPacketLen(cipher, plainAll, offset, cipher.length - offset);
      if (packetLen === null) break;
      if (packetLen <= 0) {
        parts.push(cipher.subarray(offset));
        offset = cipher.length;
        this.disabled = true;
        break;
      }
      parts.push(cipher.subarray(offset, offset + packetLen));
      offset += packetLen;
    }

    this.cipherBuf = [cipher.subarray(offset)];
    this.plainBuf = [plainAll.subarray(offset)];
    return parts.map((p) => Buffer.from(p));
  }

  flush() {
    if (!this.cipherBuf.length || !this.cipherBuf[0].length) return [];
    const tail = Buffer.concat(this.cipherBuf);
    this.cipherBuf = [];
    this.plainBuf = [];
    return [tail];
  }

  _nextPacketLen(cipher, plain, offset, avail) {
    if (avail <= 0) return null;
    if (this.proto === PROTO_ABRIDGED_INT) return this._abridgedLen(plain, offset, avail);
    if (this.proto === PROTO_INTERMEDIATE_INT || this.proto === PROTO_PADDED_INTERMEDIATE_INT) {
      return this._intermediateLen(plain, offset, avail);
    }
    return 0; // unknown protocol: forward verbatim
  }

  _abridgedLen(plain, offset, avail) {
    const first = plain[offset];
    let payloadLen;
    let headerLen;
    if (first === 0x7f || first === 0xff) {
      if (avail < 4) return null;
      payloadLen = plain.readUInt16LE(offset + 1) * 4;
      headerLen = 4;
    } else {
      payloadLen = (first & 0x7f) * 4;
      headerLen = 1;
    }
    if (payloadLen <= 0) return 0;
    const packetLen = headerLen + payloadLen;
    return avail < packetLen ? null : packetLen;
  }

  _intermediateLen(plain, offset, avail) {
    if (avail < 4) return null;
    const payloadLen = plain.readUInt32LE(offset) & 0x7fffffff;
    if (payloadLen <= 0) return 0;
    const packetLen = 4 + payloadLen;
    return avail < packetLen ? null : packetLen;
  }
}

module.exports = {
  HANDSHAKE_LEN, SKIP_LEN, PREKEY_LEN, KEY_LEN, IV_LEN, PROTO_TAG_POS, DC_IDX_POS,
  PROTO_TAG_ABRIDGED, PROTO_TAG_INTERMEDIATE, PROTO_TAG_SECURE,
  PROTO_ABRIDGED_INT, PROTO_INTERMEDIATE_INT, PROTO_PADDED_INTERMEDIATE_INT,
  parseHandshake, generateRelayInit, buildCryptoCtx, protoIntOf, isKnownProtoTag,
  MsgSplitter, ctr,
};