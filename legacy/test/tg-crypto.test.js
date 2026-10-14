'use strict';
// Tests for the MTProto obfuscated2 core (port of Flowseal/tg-ws-proxy).
const { test } = require('node:test');
const assert = require('node:assert');
const crypto = require('node:crypto');
const tg = require('../src/main/zapret/tg-crypto');

const SECRET = crypto.randomBytes(16);

// Build a client handshake exactly as an obfuscated2 client would, so we can
// verify parseHandshake() recovers the same values (round-trip against spec).
//
// obfuscated2 layout (64 bytes): 8 random skip bytes, then prekey(32)+iv(16)
// IN CLEARTEXT, then 8 encrypted bytes carrying proto_tag + dc_id + 2 random.
function makeClientHandshake(dcIdx, protoTag, secret, seed) {
  const hs = seed || crypto.randomBytes(64);
  const pk = hs.subarray(8, 40);
  const iv = hs.subarray(40, 56);
  const key = crypto.createHash('sha256').update(Buffer.concat([pk, secret])).digest();
  // keystream over the whole 64-byte block; the tail is what gets encrypted.
  const keystream = tg.ctr(key, iv)(hs);
  const ksTail = Buffer.alloc(8);
  for (let i = 0; i < 8; i += 1) ksTail[i] = keystream[56 + i] ^ hs[56 + i];
  const tailPlain = Buffer.concat([protoTag, (() => { const b = Buffer.alloc(2); b.writeInt16LE(dcIdx, 0); return b; })(), crypto.randomBytes(2)]);
  for (let i = 0; i < 8; i += 1) hs[56 + i] = tailPlain[i] ^ ksTail[i];
  return { handshake: Buffer.from(hs), prekeyIv: Buffer.concat([pk, iv]) };
}

test('parseHandshake recovers dc id, media flag and proto tag', () => {
  for (const [dc, media] of [[2, false], [4, false], [2, true], [203, false]]) {
    const protoTag = tg.PROTO_TAG_ABRIDGED;
    const { handshake } = makeClientHandshake(media ? -dc : dc, protoTag, SECRET);
    const r = tg.parseHandshake(handshake, SECRET);
    assert.ok(r, 'handshake must parse');
    assert.strictEqual(r.dcId, dc);
    assert.strictEqual(r.isMedia, media);
    assert.ok(r.protoTag.equals(protoTag));
  }
});

test('parseHandshake accepts intermediate and secure proto tags', () => {
  for (const tag of [tg.PROTO_TAG_INTERMEDIATE, tg.PROTO_TAG_SECURE]) {
    const { handshake } = makeClientHandshake(3, tag, SECRET);
    assert.ok(tg.parseHandshake(handshake, SECRET));
  }
});

test('parseHandshake rejects a non-obfuscated stream', () => {
  const junk = crypto.randomBytes(64);
  assert.strictEqual(tg.parseHandshake(junk, SECRET), null);
});

test('parseHandshake rejects a handshake encrypted with the wrong secret', () => {
  const { handshake } = makeClientHandshake(2, tg.PROTO_TAG_ABRIDGED, SECRET);
  const other = crypto.randomBytes(16);
  assert.strictEqual(tg.parseHandshake(handshake, other), null);
});

test('parseHandshake rejects a short buffer', () => {
  assert.strictEqual(tg.parseHandshake(Buffer.alloc(10), SECRET), null);
});

test('generateRelayInit yields a handshake the DC can read back', () => {
  for (const dc of [1, 2, 5, 203, -2]) {
    const init = tg.generateRelayInit(tg.PROTO_TAG_ABRIDGED, dc);
    assert.strictEqual(init.length, tg.HANDSHAKE_LEN);
    // Read it back with the RAW key (relay side uses no secret).
    const key = init.subarray(tg.SKIP_LEN, tg.SKIP_LEN + tg.PREKEY_LEN);
    const iv = init.subarray(tg.SKIP_LEN + tg.PREKEY_LEN, tg.SKIP_LEN + tg.PREKEY_LEN + tg.IV_LEN);
    const plain = tg.ctr(key, iv)(init);
    assert.ok(plain.subarray(tg.PROTO_TAG_POS, tg.PROTO_TAG_POS + 4).equals(tg.PROTO_TAG_ABRIDGED));
    assert.strictEqual(plain.readInt16LE(tg.DC_IDX_POS), dc);
  }
});

test('generateRelayInit avoids reserved HTTP-looking prefixes', () => {
  const continue4 = Buffer.from([0x00, 0x00, 0x00, 0x00]);
  for (let i = 0; i < 50; i += 1) {
    const init = tg.generateRelayInit(tg.PROTO_TAG_ABRIDGED, 2);
    assert.notStrictEqual(init[0], 0xef);
    assert.doesNotMatch(init.subarray(0, 4).toString('latin1'), /^(HEAD|POST|GET )/);
    assert.ok(!init.subarray(4, 8).equals(continue4));
  }
});

test('generateRelayInit produces a fresh prekey each time', () => {
  const a = tg.generateRelayInit(tg.PROTO_TAG_ABRIDGED, 2);
  const b = tg.generateRelayInit(tg.PROTO_TAG_ABRIDGED, 2);
  assert.ok(!a.equals(b));
});

test('buildCryptoCtx streams line up with an independent peer cipher', () => {
  const { prekeyIv } = makeClientHandshake(2, tg.PROTO_TAG_ABRIDGED, SECRET);
  const relayInit = tg.generateRelayInit(tg.PROTO_TAG_ABRIDGED, 2);
  const ctx = tg.buildCryptoCtx(prekeyIv, SECRET, relayInit);

  // Peer view of the client->relay direction: same key/iv, advanced past the
  // 64 handshake bytes. Both sides must stay byte-for-byte in lockstep.
  const peerKey = crypto.createHash('sha256').update(Buffer.concat([prekeyIv.subarray(0, tg.PREKEY_LEN), SECRET])).digest();
  const peerIv = prekeyIv.subarray(tg.PREKEY_LEN);
  const payload = crypto.randomBytes(96);

  // Reference: Node's own cipher, skipping the handshake the same way.
  const native = crypto.createCipheriv('aes-256-ctr', peerKey, peerIv);
  native.update(Buffer.alloc(64));
  const expected = native.update(payload);

  assert.ok(ctx.cltDec(payload).equals(expected), 'client stream must be advanced by 64 bytes');
});

test('buildCryptoCtx relay stream is advanced past the handshake', () => {
  const { prekeyIv } = makeClientHandshake(2, tg.PROTO_TAG_ABRIDGED, SECRET);
  const relayInit = tg.generateRelayInit(tg.PROTO_TAG_ABRIDGED, 2);
  const ctx = tg.buildCryptoCtx(prekeyIv, SECRET, relayInit);

  const relayKey = relayInit.subarray(tg.SKIP_LEN, tg.SKIP_LEN + tg.PREKEY_LEN);
  const relayIv = relayInit.subarray(tg.SKIP_LEN + tg.PREKEY_LEN, tg.SKIP_LEN + tg.PREKEY_LEN + tg.IV_LEN);
  const payload = crypto.randomBytes(48);

  const native = crypto.createCipheriv('aes-256-ctr', relayKey, relayIv);
  native.update(Buffer.alloc(64));
  assert.ok(ctx.tgEnc(payload).equals(native.update(payload)), 'relay stream must be advanced by 64 bytes');
});

test('buildCryptoCtx does not repeat keystream across calls', () => {
  const { prekeyIv } = makeClientHandshake(2, tg.PROTO_TAG_ABRIDGED, SECRET);
  const relayInit = tg.generateRelayInit(tg.PROTO_TAG_ABRIDGED, 2);
  const ctx = tg.buildCryptoCtx(prekeyIv, SECRET, relayInit);
  const payload = crypto.randomBytes(32);
  const once = Buffer.from(ctx.tgEnc(payload));
  // same plaintext again must give DIFFERENT ciphertext (counter advanced)
  assert.ok(!once.equals(ctx.tgEnc(payload)));
});

test('buildCryptoCtx uses reversed prekey for the client-encrypt direction', () => {
  const { prekeyIv } = makeClientHandshake(2, tg.PROTO_TAG_ABRIDGED, SECRET);
  const relayInit = tg.generateRelayInit(tg.PROTO_TAG_ABRIDGED, 2);
  const ctx = tg.buildCryptoCtx(prekeyIv, SECRET, relayInit);

  const reversed = Buffer.from(prekeyIv).reverse();
  const expectedKey = crypto
    .createHash('sha256')
    .update(Buffer.concat([reversed.subarray(0, tg.PREKEY_LEN), SECRET]))
    .digest();
  const payload = crypto.randomBytes(32);
  const expected = tg.ctr(expectedKey, reversed.subarray(tg.PREKEY_LEN))(payload);
  assert.ok(ctx.cltEnc(payload).equals(expected));
});

test('ctr() matches Node native AES-256-CTR for a single shot', () => {
  const key = crypto.randomBytes(32);
  const iv = crypto.randomBytes(16);
  for (const len of [1, 15, 16, 17, 100, 1000]) {
    const data = crypto.randomBytes(len);
    const native = crypto.createCipheriv('aes-256-ctr', key, iv).update(data);
    assert.ok(tg.ctr(key, iv)(data).equals(native), `length ${len} must match native CTR`);
  }
});

test('ctr() keeps stream position across separate calls', () => {
  const key = crypto.randomBytes(32);
  const iv = crypto.randomBytes(16);
  const whole = crypto.randomBytes(200);
  const native = crypto.createCipheriv('aes-256-ctr', key, iv);
  const expected = native.update(whole);
  const stream = tg.ctr(key, iv);
  const out = Buffer.concat([stream(whole.subarray(0, 7)), stream(whole.subarray(7, 64)), stream(whole.subarray(64))]);
  assert.ok(out.equals(expected), 'chunked encryption must equal one-shot encryption');
});

test('ctr() matches native CTR when a handshake offset is skipped', () => {
  // obfuscated2 always skips 64 bytes before the data stream; both sides must
  // land on the same keystream position afterwards.
  const key = crypto.randomBytes(32);
  const iv = crypto.randomBytes(16);
  const payload = crypto.randomBytes(64);
  const mine = tg.ctr(key, iv);
  mine(Buffer.alloc(64));
  const theirs = crypto.createCipheriv('aes-256-ctr', key, iv);
  theirs.update(Buffer.alloc(64));
  assert.ok(mine(payload).equals(theirs.update(payload)));
});

test('ctr() rejects wrong key/iv sizes', () => {
  assert.throws(() => tg.ctr(crypto.randomBytes(16), crypto.randomBytes(16)));
  assert.throws(() => tg.ctr(crypto.randomBytes(32), crypto.randomBytes(8)));
});

// ---- MsgSplitter ----

function abridged(len4) {
  const n = len4 / 4;
  if (n <= 127) return Buffer.from([n]);
  const b = Buffer.alloc(4);
  b[0] = 0x7f;
  b.writeUInt16LE(n, 1);
  return b;
}

test('MsgSplitter splits abridged packets', () => {
  const s = new tg.MsgSplitter(tg.PROTO_ABRIDGED_INT);
  const p1 = abridged(8);
  const d1 = Buffer.alloc(8);
  const p2 = abridged(16);
  const d2 = Buffer.alloc(16);
  const parts = s.push(Buffer.concat([p1, d1, p2, d2]), Buffer.concat([p1, d1, p2, d2]));
  assert.strictEqual(parts.length, 2);
  assert.strictEqual(parts[0].length, 9);
  assert.strictEqual(parts[1].length, 17);
});

test('MsgSplitter waits for a complete packet', () => {
  const s = new tg.MsgSplitter(tg.PROTO_ABRIDGED_INT);
  const p = abridged(8);
  const whole = Buffer.concat([p, Buffer.alloc(8)]);
  const head = whole.subarray(0, 4);
  assert.strictEqual(s.push(head, head).length, 0, 'incomplete packet must not be emitted');
  const rest = whole.subarray(4);
  const parts = s.push(rest, rest);
  assert.strictEqual(parts.length, 1);
  assert.ok(parts[0].equals(whole));
});

test('MsgSplitter handles the long abridged form', () => {
  const s = new tg.MsgSplitter(tg.PROTO_ABRIDGED_INT);
  const len = 1024;
  const hdr = abridged(len);
  assert.strictEqual(hdr.length, 4, 'long form uses a 4-byte header');
  const whole = Buffer.concat([hdr, Buffer.alloc(len)]);
  const parts = s.push(whole, whole);
  assert.strictEqual(parts.length, 1);
  assert.strictEqual(parts[0].length, whole.length);
});

test('MsgSplitter splits intermediate packets', () => {
  const s = new tg.MsgSplitter(tg.PROTO_INTERMEDIATE_INT);
  const mk = (len) => {
    const b = Buffer.alloc(4);
    b.writeUInt32LE(len, 0);
    return Buffer.concat([b, Buffer.alloc(len)]);
  };
  const whole = Buffer.concat([mk(12), mk(20)]);
  const parts = s.push(whole, whole);
  assert.strictEqual(parts.length, 2);
  assert.strictEqual(parts[0].length, 16);
  assert.strictEqual(parts[1].length, 24);
});

test('MsgSplitter masks the high bit of intermediate lengths', () => {
  const s = new tg.MsgSplitter(tg.PROTO_INTERMEDIATE_INT);
  const hdr = Buffer.alloc(4);
  hdr.writeUInt32LE(0x80000008, 0); // padding flag set
  const whole = Buffer.concat([hdr, Buffer.alloc(8)]);
  const parts = s.push(whole, whole);
  assert.strictEqual(parts.length, 1);
  assert.strictEqual(parts[0].length, 12);
});

test('MsgSplitter passes unknown protocols through verbatim', () => {
  const s = new tg.MsgSplitter(0x11223344);
  const data = crypto.randomBytes(64);
  const parts = s.push(data, data);
  assert.strictEqual(parts.length, 1);
  assert.ok(parts[0].equals(data));
});

test('MsgSplitter flush returns the unparsed tail', () => {
  const s = new tg.MsgSplitter(tg.PROTO_ABRIDGED_INT);
  const p = abridged(64);
  const head = Buffer.concat([p, Buffer.alloc(2)]); // 2 bytes missing
  s.push(head, head);
  const tail = s.flush();
  assert.strictEqual(tail.length, 1);
  assert.ok(tail[0].equals(head));
  assert.strictEqual(s.flush().length, 0);
});