using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ydp.Tg
{
    public sealed class CtStream : IDisposable
    {
        private const int Block = 16;
        private readonly Aes _aes;
        private readonly ICryptoTransform _transform;
        private readonly byte[] _counter;
        private int _offset = Block;
        private byte[] _keystream;

        /// <summary>
        /// Stateful AES-256-CTR keystream.
        ///
        /// System.Security.Cryptography's CTR implementation is not usable for a
        /// persistent obfuscation stream: it resets the counter for every
        /// TransformBlock call unless the same transform object is fed a
        /// continuous stream, and building the stream block-by-block here makes
        /// the stream position explicit instead of implicit.
        /// </summary>
        public CtStream(byte[] key, byte[] iv)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(key));
            if (iv == null || iv.Length != 16) throw new ArgumentException("iv must be 16 bytes", nameof(iv));
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = key;
            _transform = _aes.CreateEncryptor();
            _counter = (byte[])iv.Clone();
            _keystream = new byte[Block];
        }

public byte[] Update(byte[] data, int offset, int count)
        {
            var output = new byte[count];
            int written = 0;
            while (written < count)
            {
                if (_offset == Block)
                {
                    _transform.TransformBlock(_counter, 0, Block, _keystream, 0);
                    _offset = 0;
                    AdvanceCounter();
                }
                int take = Math.Min(Block - _offset, count - written);
                // XOR the plaintext with the keystream block, walking both the
                // input and the partially consumed keystream block.
                int dest = written;
                int src = offset + written;
                int ks = _offset;
                for (int i = 0; i < take; i++)
                {
                    output[dest + i] = (byte)(data[src + i] ^ _keystream[ks + i]);
                }
                _offset += take;
                written += take;
            }
            return output;
        }

        public byte[] Update(byte[] data)
        {
            return Update(data, 0, data.Length);
        }

        private void AdvanceCounter()
        {
            for (int i = Block - 1; i >= 0; i--)
            {
                if (++_counter[i] != 0) break;
            }
        }

        public void Dispose()
        {
            _transform.Dispose();
            _aes.Dispose();
        }
    }

    public sealed class ParsedHandshake
    {
        public int DcId { get; set; }
        public bool IsMedia { get; set; }
        public byte[] ProtoTag { get; set; }
        public byte[] DecPrekeyIv { get; set; }
    }

    public sealed class CryptoContext : IDisposable
    {
        public CtStream ClientDecrypt { get; set; }   // client -> us
        public CtStream ClientEncrypt { get; set; }   // us -> client
        public CtStream TgEncrypt { get; set; }       // us -> Telegram
        public CtStream TgDecrypt { get; set; }       // Telegram -> us

        public void Dispose()
        {
            ClientDecrypt?.Dispose();
            ClientEncrypt?.Dispose();
            TgEncrypt?.Dispose();
            TgDecrypt?.Dispose();
        }
    }

    /// <summary>
    /// MTProto obfuscated2 crypto, ported from Flowseal/tg-ws-proxy
    /// (proxy/tg_ws_proxy.py). The proxy never touches MTProto's inner IGE
    /// layer: it strips the client's outer obfuscated2 encryption, reads the
    /// length header and re-wraps the opaque blob with its own key.
    /// </summary>
    public static class TgCrypto
    {
        public const int HandshakeLen = 64;
        public const int SkipLen = 8;
        public const int PrekeyLen = 32;
        public const int KeyLen = 32;
        public const int IvLen = 16;
        public const int ProtoTagPos = 56;
        public const int DcIdxPos = 60;

        public static readonly byte[] ProtoTagAbridged = { 0xef, 0xef, 0xef, 0xef };
        public static readonly byte[] ProtoTagIntermediate = { 0xee, 0xee, 0xee, 0xee };
        public static readonly byte[] ProtoTagSecure = { 0xdd, 0xdd, 0xdd, 0xdd };

        private static readonly byte[] Zero64 = new byte[HandshakeLen];

        private static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        private static byte[] Sha256(byte[] a, byte[] b)
        {
            using (var sha = SHA256.Create())
            {
                sha.TransformBlock(a, 0, a.Length, null, 0);
                sha.TransformFinalBlock(b, 0, b.Length);
                return sha.Hash;
            }
        }

        private static bool KnownProtoTag(byte[] tag)
        {
            return Same(tag, ProtoTagAbridged) || Same(tag, ProtoTagIntermediate) || Same(tag, ProtoTagSecure);
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static bool IsIntermediate(byte[] tag) => Same(tag, ProtoTagIntermediate);
        public static bool IsAbridged(byte[] tag) => Same(tag, ProtoTagAbridged);
        public static bool IsSecure(byte[] tag) => Same(tag, ProtoTagSecure);

        /// <summary>Parse the 64-byte obfuscated2 handshake, or return null.</summary>
        public static ParsedHandshake ParseHandshake(byte[] handshake, byte[] secret)
        {
            if (handshake == null || handshake.Length < HandshakeLen || secret == null || secret.Length != 16) return null;

            var decPrekeyIv = new byte[PrekeyLen + IvLen];
            Buffer.BlockCopy(handshake, SkipLen, decPrekeyIv, 0, decPrekeyIv.Length);
            var decPrekey = new byte[PrekeyLen];
            var decIv = new byte[IvLen];
            Buffer.BlockCopy(decPrekeyIv, 0, decPrekey, 0, PrekeyLen);
            Buffer.BlockCopy(decPrekeyIv, PrekeyLen, decIv, 0, IvLen);

            byte[] plain;
            using (var stream = new CtStream(Sha256(decPrekey, secret), decIv))
            {
                plain = stream.Update(handshake, 0, HandshakeLen);
            }

            var protoTag = new byte[4];
            Buffer.BlockCopy(plain, ProtoTagPos, protoTag, 0, 4);
            if (!KnownProtoTag(protoTag)) return null;

            int dcIdx = BitConverter.ToInt16(plain, DcIdxPos);
            return new ParsedHandshake
            {
                DcId = Math.Abs(dcIdx),
                IsMedia = dcIdx < 0,
                ProtoTag = protoTag,
                DecPrekeyIv = decPrekeyIv,
            };
        }

        private static readonly HashSet<int> ReservedFirstBytes = new HashSet<int> { 0xef };
        private static readonly string[] ReservedStarts = { "HEAD", "POST", "GET " };

        /// <summary>
        /// Build the handshake we present to the upstream DC. The 8 bytes that
        /// carry (proto_tag, dc_id, random) are encrypted with the keystream that
        /// the random prefix itself produces, so the recipient can recover them.
        /// </summary>
        public static byte[] GenerateRelayInit(byte[] protoTag, short dcIdx)
        {
            var random = new Random();
            while (true)
            {
                var rnd = new byte[HandshakeLen];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rnd);

                if (ReservedFirstBytes.Contains(rnd[0])) continue;
                string head = Encoding.ASCII.GetString(rnd, 0, 4);
                if (Array.IndexOf(ReservedStarts, head) >= 0) continue;
                if (rnd[4] == 0 && rnd[5] == 0 && rnd[6] == 0 && rnd[7] == 0) continue;

                var encKey = new byte[PrekeyLen];
                var encIv = new byte[IvLen];
                Buffer.BlockCopy(rnd, SkipLen, encKey, 0, PrekeyLen);
                Buffer.BlockCopy(rnd, SkipLen + PrekeyLen, encIv, 0, IvLen);

                byte[] encryptedFull;
                using (var stream = new CtStream(encKey, encIv))
                {
                    encryptedFull = stream.Update(rnd, 0, HandshakeLen);
                }

                var tailPlain = new byte[8];
                Buffer.BlockCopy(protoTag, 0, tailPlain, 0, 4);
                tailPlain[4] = (byte)(dcIdx & 0xff);
                tailPlain[5] = (byte)((dcIdx >> 8) & 0xff);
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(new[] { tailPlain[6], tailPlain[7] });

                var result = (byte[])rnd.Clone();
                for (int i = 0; i < 8; i++)
                {
                    byte keystream = (byte)(encryptedFull[ProtoTagPos + i] ^ rnd[ProtoTagPos + i]);
                    result[ProtoTagPos + i] = (byte)(tailPlain[i] ^ keystream);
                }
                return result;
            }
        }

        /// <summary>
        /// Build the four re-encryption streams.
        /// Client side uses the secret-hashed key with a REVERSED prekey+iv
        /// (echo mode); relay side uses the raw prekey (standard obfuscation).
        /// </summary>
        public static CryptoContext BuildCryptoContext(byte[] clientDecPrekeyIv, byte[] secret, byte[] relayInit)
        {
            var cltDecPrekey = new byte[PrekeyLen];
            var cltDecIv = new byte[IvLen];
            Buffer.BlockCopy(clientDecPrekeyIv, 0, cltDecPrekey, 0, PrekeyLen);
            Buffer.BlockCopy(clientDecPrekeyIv, PrekeyLen, cltDecIv, 0, IvLen);
            var cltDecKey = Sha256(cltDecPrekey, secret);

            var cltEncPrekeyIv = (byte[])clientDecPrekeyIv.Clone();
            Array.Reverse(cltEncPrekeyIv);
            var cltEncPrekey = new byte[PrekeyLen];
            var cltEncIv = new byte[IvLen];
            Buffer.BlockCopy(cltEncPrekeyIv, 0, cltEncPrekey, 0, PrekeyLen);
            Buffer.BlockCopy(cltEncPrekeyIv, PrekeyLen, cltEncIv, 0, IvLen);
            var cltEncKey = Sha256(cltEncPrekey, secret);

            var cltDecrypt = new CtStream(cltDecKey, cltDecIv);
            var cltEncrypt = new CtStream(cltEncKey, cltEncIv);

            var relayEncKey = new byte[PrekeyLen];
            var relayEncIv = new byte[IvLen];
            Buffer.BlockCopy(relayInit, SkipLen, relayEncKey, 0, PrekeyLen);
            Buffer.BlockCopy(relayInit, SkipLen + PrekeyLen, relayEncIv, 0, IvLen);

            var relayDecPrekeyIv = new byte[PrekeyLen + IvLen];
            Buffer.BlockCopy(relayInit, SkipLen, relayDecPrekeyIv, 0, relayDecPrekeyIv.Length);
            Array.Reverse(relayDecPrekeyIv);
            var relayDecKey = new byte[KeyLen];
            var relayDecIv = new byte[IvLen];
            Buffer.BlockCopy(relayDecPrekeyIv, 0, relayDecKey, 0, KeyLen);
            Buffer.BlockCopy(relayDecPrekeyIv, KeyLen, relayDecIv, 0, IvLen);

            var tgEncrypt = new CtStream(relayEncKey, relayEncIv);
            var tgDecrypt = new CtStream(relayDecKey, relayDecIv);

            // Fast-forward the two streams that already exchanged a handshake.
            cltDecrypt.Update(Zero64, 0, Zero64.Length);
            tgEncrypt.Update(Zero64, 0, Zero64.Length);

            return new CryptoContext
            {
                ClientDecrypt = cltDecrypt,
                ClientEncrypt = cltEncrypt,
                TgEncrypt = tgEncrypt,
                TgDecrypt = tgDecrypt,
            };
        }

        public static byte[] NewSecret()
        {
            var secret = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
            return secret;
        }
    }
}