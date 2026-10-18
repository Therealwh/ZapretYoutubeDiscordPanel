using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Ydp.Zapret
{
    public sealed class WhyStep
    {
        public string title { get; set; }
        public string state { get; set; }   // good | warn | bad | skip
        public string detail { get; set; }
    }

    public sealed class WhyReport
    {
        public List<WhyStep> steps { get; set; } = new List<WhyStep>();
        public string verdict { get; set; }
        public bool bypassOn { get; set; }
    }

    /// <summary>
    /// Explains WHY the bypass is not working, as an ordered chain: is the
    /// bypass on, does DNS answer with a real address, does TCP 443 connect,
    /// does the TLS handshake complete. The first step that fails is the
    /// answer; the steps after it are not interesting and are skipped.
    ///
    /// Without this the diagnostics page can only say "not reachable", which
    /// leaves the user guessing between three very different problems.
    /// </summary>
    public static class Why
    {
        private static readonly string[] ProbeHosts = { "discord.com", "www.youtube.com" };

        public static WhyReport Run(string rootDir, int timeoutSec = 6)
        {
            var r = new WhyReport();

            // 1) is anything running at all
            var st = Status.GetStatus(rootDir);
            r.bypassOn = st.activeMode != "off";
            r.steps.Add(new WhyStep
            {
                title = "Обход запущен",
                state = r.bypassOn ? "good" : "bad",
                detail = r.bypassOn
                    ? "Стратегия: " + (st.serviceStrategy ?? "процесс winws")
                    : "winws не запущен — включите тумблер «Обход»",
            });
            if (!r.bypassOn)
            {
                r.verdict = "Обход выключен. Пока его не включили, ломать нечему.";
                return r;
            }

            // 2) DNS: a stub resolver (0.0.0.0 / 127.0.0.1 / private range for a
            // public name) means the block happens before any packet leaves.
            var dns = new Dictionary<string, string>();
            var dnsBad = new List<string>();
            foreach (var host in ProbeHosts)
            {
                string ip;
                string why = ResolveHost(host, out ip);
                dns[host] = ip;
                if (why != null) dnsBad.Add(host + ": " + why);
            }
            r.steps.Add(new WhyStep
            {
                title = "DNS отвечает настоящим адресом",
                state = dnsBad.Count == 0 ? "good" : "bad",
                detail = dnsBad.Count == 0
                    ? string.Join(", ", ProbeHosts.Select(h => h + " → " + dns[h]))
                    : string.Join("; ", dnsBad),
            });
            if (dnsBad.Count > 0)
            {
                r.verdict = "Проблема в DNS: " + dnsBad[0]
                    + ". Обход не поможет, пока резолвер отдаёт заглушку — смените DNS в Настройках.";
                return r;
            }

            // 3) TCP connect
            var tcpBad = new List<string>();
            foreach (var host in ProbeHosts)
            {
                string why = TcpConnect(dns[host], 443, 3000);
                if (why != null) tcpBad.Add(host + ": " + why);
            }
            r.steps.Add(new WhyStep
            {
                title = "TCP 443 принимает соединение",
                state = tcpBad.Count == 0 ? "good" : "bad",
                detail = tcpBad.Count == 0 ? "Оба адреса отвечают на 443" : string.Join("; ", tcpBad),
            });
            if (tcpBad.Count > 0)
            {
                r.verdict = "TCP до 443 не проходит: " + tcpBad[0]
                    + ". Это уже похоже на блокировку по IP — помогает смена стратегии, а не DNS.";
                return r;
            }

            // 4) TLS handshake through the bypass - the step DPI actually attacks
            var tlsBad = new List<string>();
            foreach (var host in ProbeHosts)
            {
                var t = Tester.ProbeTarget(host, "https://" + host + "/", timeoutSec);
                var state = Tester.TokenState(t);
                if (state != "good") tlsBad.Add(host + " (" + state + ")");
            }
            r.steps.Add(new WhyStep
            {
                title = "TLS-хендшейк проходит через обход",
                state = tlsBad.Count == 0 ? "good" : "bad",
                detail = tlsBad.Count == 0
                    ? "Оба рукопожатия завершились"
                    : "Не проходит: " + string.Join(", ", tlsBad),
            });
            if (tlsBad.Count > 0)
            {
                r.verdict = "TCP работает, TLS нет — значит режут именно DPI по TLS Client Hello. "
                    + "Нужна другая стратегия обхода: запустите «Подбор и тест стратегий».";
                return r;
            }

            r.verdict = "Все проверки пройдены — обход работает и DNS, и TCP, и TLS в порядке. "
                + "Если YouTube всё равно тормозит, дело в скорости: замерьте её на главной.";
            return r;
        }

        /// <summary>Returns null when the address is usable, otherwise the reason.</summary>
        private static string ResolveHost(string host, out string ip)
        {
            ip = null;
            try
            {
                var entry = Dns.GetHostEntry(host);

                // A blocked name often comes back with only a poisoned AAAA and no
                // A record at all. Naming the sinkhole is far more useful than
                // "no A record": the user then knows the resolver is being fed
                // fakes rather than the network being slow.
                var v6 = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
                var v4 = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (v4 == null && v6 != null)
                {
                    ip = v6.ToString();
                    if (IsSinkholeV6(v6)) return "резолвер отдал подменный адрес " + ip;
                    return "нет A-записи, только IPv6 " + ip;
                }
                if (v4 == null) return "нет A-записи";
                ip = v4.ToString();
                if (ip == "0.0.0.0" || ip.StartsWith("127.")) return "резолвер отдал заглушку " + ip;
                if (IsPrivate(v4)) return "резолвер отдал внутренний адрес " + ip;
                return null;
            }
            catch (Exception ex)
            {
                return "ошибка DNS: " + ex.Message;
            }
        }

        /// <summary>
        /// fdea::/16 is the block range RKN hands out for filtered names; seeing it
        /// means the answer came from the filter, not from a real host.
        /// </summary>
        private static bool IsSinkholeV6(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 0xFD && b[1] == 0xEA;
        }

        private static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        private static string TcpConnect(string ip, int port, int timeoutMs)
        {
            if (string.IsNullOrEmpty(ip)) return "неизвестный адрес";
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return "таймаут " + timeoutMs + " мс";
                    c.EndConnect(ar);
                    return null;
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return "соединение не установлено: " + inner;
            }
        }
    }
}