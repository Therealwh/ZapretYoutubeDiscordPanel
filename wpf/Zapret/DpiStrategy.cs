using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ydp.Zapret
{
    /// <summary>One editable zapret knob.</summary>
    public sealed class DpiFlag
    {
        public string id { get; set; }          // "--dpi-desync"
        public string label { get; set; }       // Russian label
        public string hint { get; set; }        // what it actually does
        public string group { get; set; }
        /// <summary>Pick one of these (null = free text).</summary>
        public string[] values { get; set; }
        public bool multi { get; set; }         // comma list allowed
        public string def { get; set; }

        public bool isChoice { get { return values != null && values.Length > 0; } }
    }

    /// <summary>
    /// The flag catalog behind the DPI expert.
    ///
    /// Until now a strategy was an opaque .bat: the panel could list it, test it
    /// and apply it, but a user who wanted a different attack had to hand-edit
    /// winws arguments. These are the real zapret flags with plain-language
    /// labels, so a strategy can be assembled in the UI and saved as a .bat the
    /// rest of the panel already understands.
    ///
    /// Grouped the way they are actually used: what to desync, how to fool, and
    /// per-service blocks.
    /// </summary>
    public static class DpiCatalog
    {
        public const string GroupAttack = "Атака (dpi-desync)";
        public const string GroupFool = "Обманка (fooling)";
        public const string GroupFake = "Поддельные пакеты";
        public const string GroupSplit = "Разрыв пакета";
        public const string GroupTtl = "TTL и фрагментация";
        public const string GroupWindows = "Фильтры и списки";

        public static readonly List<DpiFlag> Flags = new List<DpiFlag>
        {
            new DpiFlag
            {
                id = "--dpi-desync", group = GroupAttack,
                label = "Режим десинхронизации",
                hint = "Как ломается TLS-хендшейк. split2 и fake — самые универсальные, "
                     + "disorder2 — для агрессивного DPI, hopbyhop/destopt — для фрагментации.",
                values = new[]
                {
                    "synack", "syndata", "fake", "fakeknown", "rst", "rstack", "hopbyhop",
                    "destopt", "ipfrag1", "disorder", "disorder2", "split", "split2",
                    "ipfrag2", "udplen", "tamper",
                },
                multi = true, def = "fake",
            },
            new DpiFlag
            {
                id = "--dpi-desync-fooling", group = GroupFool,
                label = "Способ обмана",
                hint = "Как фальшивый пакет не долетает до сервера. md5sig — самый чистый, "
                     + "badseq/badsum — для DPI, которые проверяют заголовки.",
                values = new[] { "none", "md5sig", "badseq", "badsum", "datanoack", "hopbyhop", "hopbyhop2" },
                def = "md5sig",
            },
            new DpiFlag
            {
                id = "--dpi-desync-repeats", group = GroupAttack,
                label = "Повторов фейкового пакета", hint = "Сколько раз отправить подделку. Больше — надёжнее, но шумнее.",
                def = "6",
            },
            new DpiFlag
            {
                id = "--dpi-desync-split-pos", group = GroupSplit,
                label = "Позиция разрыва TLS", hint = "Смещение в Client Hello, где режется пакет. Обычно 1-2, реже до 64.",
                def = "1",
            },
            new DpiFlag
            {
                id = "--dpi-desync-split-tls", group = GroupSplit,
                label = "Что именно режем в TLS", hint = "sniext режет после Client Hello, sni — по самому имени хоста.",
                values = new[] { "sni", "sniext" }, def = "sniext",
            },
            new DpiFlag
            {
                id = "--dpi-desync-split-http-req", group = GroupSplit,
                label = "Где режем HTTP", hint = "Для сайтов без TLS. method — по строке запроса, host — по имени.",
                values = new[] { "method", "host" },
            },
            new DpiFlag
            {
                id = "--dpi-desync-split-seqovl", group = GroupSplit,
                label = "Наложение поверх оригинала", hint = "Сколько байт фейкового пакета наложить на настоящий.",
                def = "1",
            },
            new DpiFlag
            {
                id = "--dpi-desync-ttl", group = GroupTtl,
                label = "TTL фейкового пакета", hint = "Чтобы подделка не дошла до сервера. 1-4 — почти всегда достаточно.",
                def = "2",
            },
            new DpiFlag
            {
                id = "--dpi-desync-autottl", group = GroupTtl,
                label = "TTL автоматически", hint = "delta:min-max. Позволяет zapret подбирать TTL самому.",
                def = "1:3-20",
            },
            new DpiFlag
            {
                id = "--dpi-desync-udplen-increment", group = GroupAttack,
                label = "+ байт к UDP", hint = "Увеличение UDP-пакета — основной приём против QUIC.",
                def = "10",
            },
            new DpiFlag
            {
                id = "--dpi-desync-udplen-pattern", group = GroupAttack,
                label = "Заполнение хвоста UDP", hint = "Паттерн, которым добирают UDP до нужного размера.",
                def = "0xDEADBEEF",
            },
            new DpiFlag
            {
                id = "--dpi-desync-fake-tls", group = GroupFake,
                label = "Файл поддельного TLS", hint = "Свой Client Hello для фейкового пакета. Пусто — встроенный.",
            },
            new DpiFlag
            {
                id = "--dpi-desync-fake-quic", group = GroupFake,
                label = "Файл поддельного QUIC", hint = "Свой QUIC Initial. Пусто — встроенный.",
            },
            new DpiFlag
            {
                id = "--filter-tcp", group = GroupWindows,
                label = "Порты TCP", hint = "Список портов или диапазон, к которым применяется обход. Пусто — все.",
            },
            new DpiFlag
            {
                id = "--filter-udp", group = GroupWindows,
                label = "Порты UDP", hint = "То же для UDP. Пусто — все.",
            },
            new DpiFlag
            {
                id = "--filter-l3", group = GroupWindows,
                label = "Версии IP", hint = "Какие IP обрабатывать.",
                values = new[] { "ipv4", "ipv6", "ipv4,ipv6" }, def = "ipv4",
            },
            new DpiFlag
            {
                id = "--hostlist", group = GroupWindows,
                label = "Список хостов", hint = "Файл со списком доменов. Пусто — бить по всему трафику.",
            },
            new DpiFlag
            {
                id = "--hostlist-exclude", group = GroupWindows,
                label = "Исключения", hint = "Чего НЕ трогать, например ваши локальные сервисы.",
            },
        };

        public static IEnumerable<string> Groups
        {
            get { return Flags.Select(f => f.group).Distinct(); }
        }

        public static DpiFlag ById(string id)
        {
            return Flags.FirstOrDefault(f => string.Equals(f.id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class DpiPlan
    {
        public string name { get; set; }
        public Dictionary<string, string> values { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Get(string id)
        {
            string v;
            return values.TryGetValue(id, out v) ? v : null;
        }

        public void Set(string id, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) values.Remove(id);
            else values[id] = value.Trim();
        }

        public DpiPlan Clone()
        {
            var p = new DpiPlan { name = name };
            foreach (var kv in values) p.values[kv.Key] = kv.Value;
            return p;
        }
    }

    public static class DpiStrategy
    {
        /// <summary>Args for winws from a plan, in a stable order.</summary>
        public static List<string> ToArgs(DpiPlan plan)
        {
            var args = new List<string>();
            foreach (var flag in DpiCatalog.Flags)
            {
                string v = plan.Get(flag.id);
                if (string.IsNullOrWhiteSpace(v)) continue;
                args.Add(flag.id + "=" + v);
            }
            return args;
        }

        /// <summary>
        /// Write the plan as a .bat the rest of the panel already understands:
        /// a single winws.exe line with --new separated blocks, which is the
        /// shape Strategy.ParseStrategyFile reads.
        /// </summary>
        public static string WriteBat(DpiPlan plan, string rootDir, string fileName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("rem Generated by YoutubeDiscordPanel - DPI expert");
            sb.AppendLine("cd /d \"%~dp0\"");
            sb.AppendLine();
            sb.AppendLine("start \"\" /min \"%~dp0bin\\winws.exe\" ^");

            var blocks = SplitBlocks(plan, rootDir);
            for (int i = 0; i < blocks.Count; i++)
            {
                bool last = i == blocks.Count - 1;
                sb.AppendLine("  " + string.Join(" ", blocks[i]) + (last ? "" : " --new ^"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Per-service blocks: TCP 443 for YouTube with the hostlist, TCP 443 for
        /// Discord, plus QUIC/UDP so video streams are not the first thing to
        /// break. Mirrors how the shipped strategies are shaped, which is why a
        /// plan built here behaves like a known-good .bat instead of a toy.
        /// </summary>
        private static List<List<string>> SplitBlocks(DpiPlan plan, string rootDir)
        {
            var shared = new List<string>();
            string desync = plan.Get("--dpi-desync");
            string fooling = plan.Get("--dpi-desync-fooling");
            string repeats = plan.Get("--dpi-desync-repeats");
            string splitPos = plan.Get("--dpi-desync-split-pos");
            string splitTls = plan.Get("--dpi-desync-split-tls");
            string seqovl = plan.Get("--dpi-desync-split-seqovl");
            string ttl = plan.Get("--dpi-desync-ttl");
            string autottl = plan.Get("--dpi-desync-autottl");
            string fakeTls = plan.Get("--dpi-desync-fake-tls");
            string fakeQuic = plan.Get("--dpi-desync-fake-quic");
            string filterL3 = plan.Get("--filter-l3") ?? "ipv4";
            string exclude = plan.Get("--hostlist-exclude");
            string hostAll = plan.Get("--hostlist");

            Func<string, string> quote = f => f == null ? null : "\"" + Path.Combine(rootDir ?? "", f) + "\"";

            // Every desync sub-option is emitted as its OWN --dpi-desync-* flag,
            // not folded into one comma list.
            //
            // The shipped strategies do exactly this (--dpi-desync=fake
            // --dpi-desync-repeats=6 ...), and it is not cosmetic: the panel's
            // own parser tokenizes on commas AND '=' and rejoins values with
            // commas, so "--dpi-desync=fake,split2,fooling=md5sig" comes back
            // as "fake,split2,fooling,md5sig" - the '=' is gone and zapret no
            // longer sees a value for fooling. Separate flags survive the round
            // trip untouched, so a strategy saved here applies exactly as shown.
            var attack = new List<string>();
            if (!string.IsNullOrWhiteSpace(desync)) attack.Add("--dpi-desync=" + desync);
            if (!string.IsNullOrWhiteSpace(fooling)) attack.Add("--dpi-desync-fooling=" + fooling);
            if (!string.IsNullOrWhiteSpace(repeats)) attack.Add("--dpi-desync-repeats=" + repeats);
            if (!string.IsNullOrWhiteSpace(splitPos)) attack.Add("--dpi-desync-split-pos=" + splitPos);
            if (!string.IsNullOrWhiteSpace(splitTls)) attack.Add("--dpi-desync-split-tls=" + splitTls);
            if (!string.IsNullOrWhiteSpace(seqovl)) attack.Add("--dpi-desync-split-seqovl=" + seqovl);
            if (!string.IsNullOrWhiteSpace(autottl)) attack.Add("--dpi-desync-autottl=" + autottl);
            else if (!string.IsNullOrWhiteSpace(ttl)) attack.Add("--dpi-desync-ttl=" + ttl);

            string splitHttp = plan.Get("--dpi-desync-split-http-req");
            if (!string.IsNullOrWhiteSpace(splitHttp)) attack.Add("--dpi-desync-split-http-req=" + splitHttp);

            string udpInc = plan.Get("--dpi-desync-udplen-increment");
            string udpPat = plan.Get("--dpi-desync-udplen-pattern");

            // block 1: YouTube TCP
            shared.Add("--wf-l3=" + filterL3);
            shared.Add("--filter-tcp=" + (plan.Get("--filter-tcp") ?? "443"));
            if (hostAll != null) shared.Add("--hostlist=" + quote(hostAll));
            if (exclude != null) shared.Add("--hostlist-exclude=" + quote(exclude));
            shared.AddRange(attack);
            if (fakeTls != null) shared.Add("--dpi-desync-fake-tls=" + quote(fakeTls));
            var blocks = new List<List<string>> { new List<string>(shared) };

            // block 2: Discord TCP
            var discord = new List<string>
            {
                "--filter-tcp=443",
                "--hostlist=" + quote("lists\\list-discord.txt"),
            };
            discord.AddRange(attack);
            if (fakeTls != null) discord.Add("--dpi-desync-fake-tls=" + quote(fakeTls));
            blocks.Add(discord);

            // block 3: QUIC / voice
            var quic = new List<string>
            {
                "--filter-udp=" + (plan.Get("--filter-udp") ?? "443"),
            };
            if (hostAll != null) quic.Add("--hostlist=" + quote(hostAll));
            quic.AddRange(attack);
            if (!string.IsNullOrWhiteSpace(udpInc)) quic.Add("--dpi-desync-udplen-increment=" + udpInc);
            if (!string.IsNullOrWhiteSpace(udpPat)) quic.Add("--dpi-desync-udplen-pattern=" + udpPat);
            if (fakeQuic != null) quic.Add("--dpi-desync-fake-quic=" + quote(fakeQuic));
            blocks.Add(quic);

            return blocks;
        }

        /// <summary>
        /// Read flags back out of an existing .bat so a shipped strategy can be
        /// opened in the expert and tweaked instead of started from scratch.
        /// Unknown arguments are kept verbatim: dropping them would silently
        /// change what the strategy does.
        /// </summary>
        public static DpiPlan FromArgs(IEnumerable<string> args, string name)
        {
            var plan = new DpiPlan { name = name };
            var list = (args ?? Enumerable.Empty<string>()).ToList();

            for (int i = 0; i < list.Count; i++)
            {
                string token = (list[i] ?? string.Empty).Trim();
                if (token.Length == 0 || !token.StartsWith("--", StringComparison.Ordinal)) continue;

                // Two shapes reach here. A file written as "--dpi-desync=fake"
                // yields "--dpi-desync=fake", but the panel's own parser splits on
                // '=' and rejoins values with commas, so the SAME file comes back
                // as "--dpi-desync" followed by "fake". Both have to be read, or
                // opening a saved strategy in the expert shows every knob empty.
                string key = token;
                string val = null;
                int eq = token.IndexOf('=');
                if (eq > 0)
                {
                    key = token.Substring(0, eq);
                    val = token.Substring(eq + 1);
                }
                else if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    val = list[i + 1];
                    i++;
                }

                var flag = DpiCatalog.ById(key);
                if (flag == null) continue;   // unknown flags are simply not editable here
                if (val == null) continue;

                // Desync may be a compound token: pull the sub-options we model
                // separately out of it so they are not buried in one value.
                if (flag.id == "--dpi-desync")
                {
                    var keep = new List<string>();
                    foreach (var part in val.Split(','))
                    {
                        var kv = part.Split(new[] { '=' }, 2);
                        var sub = DpiCatalog.ById("--dpi-desync-" + kv[0].Trim().ToLowerInvariant());
                        if (sub != null && kv.Length == 2) plan.Set(sub.id, kv[1]);
                        else keep.Add(part);
                    }
                    if (keep.Count > 0) plan.Set(flag.id, string.Join(",", keep));
                }
                else
                {
                    plan.Set(flag.id, val);
                }
            }
            return plan;
        }
    }
}