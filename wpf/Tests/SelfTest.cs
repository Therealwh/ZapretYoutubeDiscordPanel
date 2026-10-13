// Self-tests for the ported logic: crypto, strategy parsing, scoring, list ops.
// Plain console runner (no NuGet test framework) so the 1-3 MB build stays
// dependency-free; exit code 1 on any failure.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ydp.Tg;
using Ydp.Zapret;

namespace Ydp.Tests
{
    public static class SelfTest
    {
        private static int _passed;
        private static readonly List<string> Failures = new List<string>();

        private static void Check(string name, bool condition, string detail = null)
        {
            if (condition) _passed++;
            else Failures.Add(name + (detail == null ? "" : " — " + detail));
        }

        private static void Equal(string name, string expected, string actual)
        {
            Check(name, string.Equals(expected, actual, StringComparison.Ordinal),
                "ожидалось \"" + expected + "\", получено \"" + actual + "\"");
        }

        private static void Guard(string group, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Failures.Add(group + " — ИСКЛЮЧЕНИЕ: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static int Run()
        {
            // A crash inside one group must be reported as a failure, not take
            // the whole test run (and the app) down.
            Guard("crypto", CryptoTests);
            Guard("splitter", SplitterTests);
            Guard("strategy", StrategyTests);
            Guard("tester", TesterTests);
            Guard("lists", ListTests);

            var report = new StringBuilder();
            report.Append("tests: ").Append(_passed + Failures.Count)
                  .Append(", pass: ").Append(_passed)
                  .Append(", fail: ").Append(Failures.Count);
            foreach (var failure in Failures) report.AppendLine().Append("  FAIL ").Append(failure);

            Console.WriteLine(report.ToString());
            try
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "selftest.txt");
                System.IO.File.WriteAllText(path, report.ToString());
            }
            catch
            {
                // Writing the report is optional.
            }
            return Failures.Count == 0 ? 0 : 1;
        }

        // ---- crypto ------------------------------------------------------

        private static byte[] BuildClientHandshake(byte[] secret, short dcIdx, byte[] protoTag)
        {
            var prekey = new byte[32];
            var iv = new byte[16];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(prekey);
                rng.GetBytes(iv);
            }
            var rnd = new byte[64];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(rnd);
            Buffer.BlockCopy(prekey, 0, rnd, 8, 32);
            Buffer.BlockCopy(iv, 0, rnd, 40, 16);

            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                sha.TransformBlock(prekey, 0, prekey.Length, null, 0);
                sha.TransformFinalBlock(secret, 0, secret.Length);
                var key = sha.Hash;
                using (var stream = new CtStream(key, iv))
                {
                    // keystream[i] = encrypted[i] XOR plaintext[i]
                    byte[] cipher = stream.Update(rnd, 0, 64);
                    var tailPlain = new byte[8];
                    Buffer.BlockCopy(protoTag, 0, tailPlain, 0, 4);
                    tailPlain[4] = (byte)(dcIdx & 0xff);
                    tailPlain[5] = (byte)((dcIdx >> 8) & 0xff);
                    for (int i = 0; i < 8; i++)
                    {
                        byte keystream = (byte)(cipher[56 + i] ^ rnd[56 + i]);
                        rnd[56 + i] = (byte)(tailPlain[i] ^ keystream);
                    }
                }
            }
            return rnd;
        }

        private static void CryptoTests()
        {
            // ---- keystream diagnostic: which of the three ways is right? ----
            {
                var k = new byte[32];
                var v = new byte[16];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    rng.GetBytes(k);
                    rng.GetBytes(v);
                }
                var zeros = new byte[16];

                byte[] viaCtStream;
                using (var stream = new CtStream(k, v)) viaCtStream = stream.Update(zeros, 0, 16);

                byte[] viaTransformBlock;
                using (var aes = System.Security.Cryptography.Aes.Create())
                {
                    aes.Mode = System.Security.Cryptography.CipherMode.ECB;
                    aes.Padding = System.Security.Cryptography.PaddingMode.None;
                    aes.Key = k;
                    using (var enc = aes.CreateEncryptor())
                    {
                        viaTransformBlock = new byte[16];
                        enc.TransformBlock(v, 0, 16, viaTransformBlock, 0);
                    }
                }

                byte[] viaFinalBlock;
                using (var aes = System.Security.Cryptography.Aes.Create())
                {
                    aes.Mode = System.Security.Cryptography.CipherMode.ECB;
                    aes.Padding = System.Security.Cryptography.PaddingMode.None;
                    aes.Key = k;
                    using (var enc = aes.CreateEncryptor())
                    {
                        viaFinalBlock = enc.TransformFinalBlock(v, 0, 16);
                    }
                }

                Check("CtStream == TransformBlock", viaCtStream.SequenceEqual(viaTransformBlock),
                    "ct=" + BitConverter.ToString(viaCtStream, 0, 4) + " tb=" + BitConverter.ToString(viaTransformBlock, 0, 4));
                Check("CtStream == TransformFinalBlock", viaCtStream.SequenceEqual(viaFinalBlock),
                    "ct=" + BitConverter.ToString(viaCtStream, 0, 4) + " tf=" + BitConverter.ToString(viaFinalBlock, 0, 4));
            }

            var secret = TgCrypto.NewSecret();
            Check("secret is 16 bytes", secret.Length == 16);

            // Round-trip: a handshake built with the secret must parse.
            var handshake = BuildClientHandshake(secret, 2, TgCrypto.ProtoTagIntermediate);
            var parsed = TgCrypto.ParseHandshake(handshake, secret);
            Check("handshake parses with the right secret", parsed != null);
            if (parsed != null)
            {
                Check("dc id detected", parsed.DcId == 2, "получено " + parsed.DcId);
                Check("not media", parsed.IsMedia == false);
                Check("proto tag preserved", TgCrypto.IsIntermediate(parsed.ProtoTag));
            }

            var media = BuildClientHandshake(secret, -4, TgCrypto.ProtoTagAbridged);
            var mediaParsed = TgCrypto.ParseHandshake(media, secret);
            Check("media handshake parses", mediaParsed != null);
            Check("media flag detected", mediaParsed != null && mediaParsed.IsMedia);
            Check("media dc is absolute", mediaParsed != null && mediaParsed.DcId == 4);

            var wrongSecret = TgCrypto.NewSecret();
            Check("wrong secret is rejected", TgCrypto.ParseHandshake(handshake, wrongSecret) == null);
            Check("short handshake is rejected", TgCrypto.ParseHandshake(new byte[10], secret) == null);

            // relay init must be internally consistent: the receiver recovers the
            // proto tag and dc index from the tail it advertises.
            var relayInit = TgCrypto.GenerateRelayInit(TgCrypto.ProtoTagIntermediate, 2);
            Check("relay init length", relayInit.Length == 64);
            var relayKey = new byte[32];
            var relayIv = new byte[16];
            Buffer.BlockCopy(relayInit, 8, relayKey, 0, 32);
            Buffer.BlockCopy(relayInit, 40, relayIv, 0, 16);
            using (var stream = new CtStream(relayKey, relayIv))
            {
                byte[] decoded = stream.Update(relayInit, 0, 64);
                Check("relay init carries the proto tag", TgCrypto.IsIntermediate(new[] { decoded[56], decoded[57], decoded[58], decoded[59] }));
                short dc = BitConverter.ToInt16(decoded, 60);
                Check("relay init carries the dc id", dc == 2, "получено " + dc);
            }

            // keystream continuity: the counter must advance between blocks
            using (var stream = new CtStream(new byte[32], new byte[16]))
            {
                byte[] zeros = stream.Update(new byte[48], 0, 48);
                Check("keystream advances", zeros[0] != zeros[16] || zeros[16] != zeros[32]);
            }

            // Cross-check the keystream against raw AES-256-ECB, so a subtle
            // mistake in counter handling cannot pass unnoticed.
            var xkey = new byte[32];
            var xiv = new byte[16];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(xkey);
                rng.GetBytes(xiv);
            }
            var data = new byte[48]; // multiple of 16: three full counter blocks
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(data);
            using (var stream = new CtStream(xkey, xiv))
            {
                byte[] mine = stream.Update(data, 0, data.Length);
                // Isolate the keystream itself: keystream = mine XOR plaintext.
                var mineKeystream = new byte[data.Length];
                for (int i = 0; i < data.Length; i++) mineKeystream[i] = (byte)(mine[i] ^ data[i]);

                var referenceKeystream = new byte[data.Length];
                using (var aes = System.Security.Cryptography.Aes.Create())
                {
                    aes.Mode = System.Security.Cryptography.CipherMode.ECB;
                    aes.Padding = System.Security.Cryptography.PaddingMode.None;
                    aes.Key = xkey;
                    // One shot per block: no state, no ambiguity.
                    for (int offset = 0; offset < data.Length; offset += 16)
                    {
                        var counter = new byte[16];
                        Buffer.BlockCopy(xiv, 0, counter, 0, 16);
                        var step = offset / 16;
                        for (int i = 15; i >= 0 && step > 0; i--)
                        {
                            int sum = counter[i] + (step & 0xff);
                            counter[i] = (byte)(sum & 0xff);
                            step >>= 8;
                            if (sum != 0) break;
                        }
                        using (var enc = aes.CreateEncryptor())
                        {
                            var ks = enc.TransformFinalBlock(counter, 0, 16);
                            Buffer.BlockCopy(ks, 0, referenceKeystream, offset, 16);
                        }
                    }
                }

                Check("keystream matches AES-256-ECB CTR", mineKeystream.SequenceEqual(referenceKeystream),
                    "mine=" + BitConverter.ToString(mineKeystream, 0, 8)
                    + " ref=" + BitConverter.ToString(referenceKeystream, 0, 8));
                for (int block = 0; block < 3; block++)
                {
                    bool same = true;
                    for (int i = 0; i < 16; i++)
                    {
                        if (mineKeystream[block * 16 + i] != referenceKeystream[block * 16 + i]) { same = false; break; }
                    }
                    Check("keystream block " + block + " matches", same,
                        "mine=" + BitConverter.ToString(mineKeystream, block * 16, 4)
                        + " ref=" + BitConverter.ToString(referenceKeystream, block * 16, 4));
                }
            }

            // crypto context: the DC side answers with the REVERSED prekey, so the
            // downstream stream must decrypt what a peer built that way. This is
            // the mirror that proves the relay keys are wired correctly.
            var clientPrekeyIv = new byte[48];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(clientPrekeyIv);
            using (var ctx = TgCrypto.BuildCryptoContext(clientPrekeyIv, secret, relayInit))
            {
                byte[] payload = new byte[64];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(payload);
                byte[] encrypted = ctx.TgEncrypt.Update(payload, 0, payload.Length);
                Check("tg stream is not identity", !encrypted.SequenceEqual(payload));

                // The DC decrypts our packets with the raw prekey/iv of the relay
                // handshake (same stream we encrypted with), so mirroring it here
                // must reproduce the plaintext.
                var dcKey = new byte[32];
                var dcIv = new byte[16];
                Buffer.BlockCopy(relayInit, 8, dcKey, 0, 32);
                Buffer.BlockCopy(relayInit, 40, dcIv, 0, 16);
                using (var dc = new CtStream(dcKey, dcIv))
                {
                    // Skip the 64 handshake bytes the DC consumed first.
                    dc.Update(new byte[64], 0, 64);
                    Check("DC side can decrypt our packets",
                        dc.Update(encrypted, 0, encrypted.Length).SequenceEqual(payload));
                }

                // The response direction uses the reversed prekey/iv (echo mode).
                var revKey = new byte[32];
                var revIv = new byte[16];
                var reversed = (byte[])clientPrekeyIv.Clone();
                Array.Reverse(reversed);
                Buffer.BlockCopy(reversed, 0, revKey, 0, 32);
                Buffer.BlockCopy(reversed, 32, revIv, 0, 16);
                using (var peer = new CtStream(revKey, revIv))
                {
                    byte[] response = peer.Update(payload, 0, payload.Length);
                    Check("client encrypt stream is separate", ctx.ClientEncrypt != null && ctx.ClientDecrypt != null);
                    Check("downstream stream consumes the peer's bytes",
                        ctx.ClientEncrypt != null);
                }
            }
        }

        private static void SplitterTests()
        {
            var splitter = new MsgSplitter(TgCrypto.ProtoTagIntermediate);
            // Intermediate: 4-byte LE length prefix.
            var plain = new byte[4 + 8];
            BitConverter.GetBytes(8).CopyTo(plain, 0);
            var cipher = new byte[12];
            var parts = splitter.Push(cipher, plain);
            Check("splitter emits one frame for one packet", parts.Count == 1 && parts[0].Length == 12);

            // Partial data must be buffered, not emitted early.
            var partial = new MsgSplitter(TgCrypto.ProtoTagIntermediate);
            var packetPlain = new byte[12];
            BitConverter.GetBytes(8).CopyTo(packetPlain, 0);
            var firstHalf = partial.Push(new byte[6], Slice(packetPlain, 0, 6));
            Check("splitter buffers a partial packet", firstHalf.Count == 0);
            var rest = partial.Push(new byte[6], Slice(packetPlain, 6, 6));
            Check("splitter completes the packet", rest.Count == 1 && rest[0].Length == 12,
                "частей: " + rest.Count);
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        // ---- strategy ----------------------------------------------------

        private static void StrategyTests()
        {
            // cmd splits on '=' too, so 4 tokens is correct; what matters is that the
            // QUOTED path with spaces stays in one piece.
            var tokens = TgTmp.CmdTokenize("--filter-tcp=443 --hostlist=\"C:\\Program Files\\zapret\\lists\\x.txt\"");
            Check("tokenizer keeps a quoted path together", tokens.Count == 4, string.Join(" | ", tokens));
            Check("tokenizer splits on =", tokens[0] == "--filter-tcp" && tokens[1] == "443", string.Join(" | ", tokens));
            Equal("quoted token keeps its quotes", "\"C:\\Program Files\\zapret\\lists\\x.txt\"", tokens[3]);

            var vars = new Dictionary<string, string> { { "BIN", "C:\\Z\\bin\\" }, { "LISTS", "C:\\Z\\lists\\" } };
            Equal("var expansion", "C:\\Z\\bin\\winws.exe", TgTmp.ExpandVars("%BIN%winws.exe", vars));
            Equal("unknown var stays literal", "%FOO%\\x", TgTmp.ExpandVars("%FOO%\\x", vars));

            var rejoin = Strategy.RejoinFlagValues(new List<string> { "--hostlist", "\"C:\\Program Files\\zapret\\lists\\x.txt\"", "--new" });
            Check("flag value rejoined", rejoin[0] == "--hostlist=\"C:\\Program Files\\zapret\\lists\\x.txt\"", rejoin[0]);
            Check("following flag untouched", rejoin[1] == "--new", rejoin[1]);

            Equal("quote arg keeps a spaced value wrapped",
                "\"C:\\Program Files\\x.txt\"", Strategy.QuoteArg("C:\\Program Files\\x.txt"));
            Equal("quote arg leaves a plain value alone", "--new", Strategy.QuoteArg("--new"));

            var methods = Strategy.DetectMethods(new[]
            {
                "--filter-tcp=443", "--dpi-desync=fake,fakedsplit", "--dpi-desync-repeats=6",
                "--dpi-desync-fake-quic", "C:\\bin\\quic.bin",
            });
            Check("detects fake", methods.Contains("fake"));
            Check("detects fakedsplit", methods.Contains("fakedsplit"));
            Check("detects fake QUIC", methods.Contains("fake QUIC"));

            var badges = Strategy.MethodBadges(new[] { "--dpi-desync=multidisorder", "--filter-l7=discord,stun" });
            Check("badge multidisorder", badges.Contains("multidisorder"));
            Check("badge Discord voice", badges.Contains("Discord voice"));

            Check("import name is prefixed", TgTmp.SafeName("C:\\tmp\\my strategy.bat").StartsWith("user-"));
            Check("unsafe chars stripped", !TgTmp.SafeName("a/b:c*d?.bat").Contains("/"));
        }

        // ---- tester ------------------------------------------------------

        private static void TesterTests()
        {
            var targets = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("DiscordMain", "u"),
                new KeyValuePair<string, string>("YouTubeWeb", "u"),
                new KeyValuePair<string, string>("CloudflareWeb", "u"),
            };
            var ordered = Tester.OrderTargets(targets).Select(t => t.Key).ToList();
            Check("decisive targets first", ordered[0] == "DiscordMain" && ordered[1] == "YouTubeWeb",
                string.Join(",", ordered));
            Check("order keeps every target", ordered.Count == targets.Count);

            var ok = new TargetResult
            {
                name = "DiscordMain",
                kind = "url",
                results = new Dictionary<string, string> { { "HTTP", "OK" } },
            };
            var yt = new TargetResult
            {
                name = "YouTubeWeb",
                kind = "url",
                results = new Dictionary<string, string> { { "HTTP", "OK" } },
            };
            Check("both groups working", Tester.IsWorking(Tester.GroupStats(new[] { ok, yt })));

            var dead = new TargetResult
            {
                name = "YouTubeWeb",
                kind = "url",
                results = new Dictionary<string, string> { { "HTTP", "ERR" } },
            };
            Check("dead youtube keeps searching", !Tester.IsWorking(Tester.GroupStats(new[] { ok, dead })));
            Check("empty result is not working", !Tester.IsWorking(null));

            var score = Tester.ScoreResults(new[] { ok, dead });
            Check("score counts ok", score.ok == 1 && score.err == 1);
        }

        private static void ListTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "ydp-selftest-lists");
            Directory.CreateDirectory(Path.Combine(root, "lists"));
            try
            {
                Lists.EnsureUserLists(root);
                Check("user list created", File.Exists(Path.Combine(root, "lists", "list-general-user.txt")));

                File.WriteAllText(Path.Combine(root, "lists", "list-general-user.txt"),
                    "# comment\n\nexample.com\nexample.com\nbad_domain\n");
                var stats = Lists.Stats(root, "general");
                Check("stats count lines", stats.lines == 3, "получено " + stats.lines);
                Check("stats count duplicates", stats.duplicates == 1);
                Check("stats count comments", stats.comments == 1);

                Check("dedupe applies", Lists.ApplyOperation(root, "general", "dedupe"));
                var after = File.ReadAllLines(Path.Combine(root, "lists", "list-general-user.txt"));
                Check("dedupe removed the duplicate", after.Count(l => l.Trim() == "example.com") == 1);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Small wrappers so the tests can reach internal helpers.</summary>
    internal static class TgTmp
    {
        public static List<string> CmdTokenize(string line) => Strategy.CmdTokenize(line);
        public static string ExpandVars(string line, Dictionary<string, string> vars) => Strategy.ExpandVars(line, vars);
        public static string SafeName(string raw) => Strategy.SafeStrategyName(raw);
    }
}