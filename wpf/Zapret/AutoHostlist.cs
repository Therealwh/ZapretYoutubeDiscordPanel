using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ydp.Zapret
{
    public sealed class DomainProbe
    {
        public string service { get; set; }
        public string domain { get; set; }
        public string state { get; set; }   // good | warn | bad
        public string ip { get; set; }
        public string detail { get; set; }

        public bool blocked { get { return state == "bad" || state == "warn"; } }
    }

    /// <summary>
    /// Finds which service domains the current network actually breaks, so the
    /// hostlist can be filled with facts instead of guesses.
    ///
    /// This is NOT zapret's --hostlist-auto: that flag needs nfqws to watch
    /// redirected traffic. Here every candidate is probed through the running
    /// bypass with the same curl the tester uses, and the ones that fail are
    /// offered as hostlist entries. A domain that fails with the bypass on is
    /// genuinely not reachable right now, which is exactly what a hostlist is
    /// for.
    /// </summary>
    public static class AutoHostlist
    {
        /// <summary>
        /// Candidates per service. Kept deliberately small and stable: these are
        /// the domains that matter for the SNI-based hostlists the shipped
        /// strategies use, not an exhaustive crawl.
        /// </summary>
        public static readonly Dictionary<string, string[]> Candidates =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { CategorizedDomains.SectionDiscord, new[]
                    {
                        "discord.com", "discordapp.com", "discordapp.net",
                        "cdn.discordapp.com", "media.discordapp.net",
                        "images-ext-1.discordapp.net",
                    } },
                { CategorizedDomains.SectionYouTube, new[]
                    {
                        "youtube.com", "www.youtube.com", "i.ytimg.com",
                        "googlevideo.com", "youtubei.googleapis.com",
                        "yt3.ggpht.com",
                    } },
                { CategorizedDomains.SectionTelegram, new[]
                    {
                        "telegram.org", "web.telegram.org", "t.me",
                    } },
            };

        /// <summary>Probe every candidate of the given services in parallel.</summary>
        public static List<DomainProbe> Scan(IEnumerable<string> services, int timeoutSec = 5)
        {
            var list = new List<DomainProbe>();
            foreach (var service in services)
            {
                string[] domains;
                if (!Candidates.TryGetValue(service, out domains)) continue;
                foreach (var d in domains)
                {
                    var localService = service;
                    var domain = d;
                    list.Add(new DomainProbe { service = localService, domain = domain });
                }
            }

            var options = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(6, Environment.ProcessorCount),
            };
            Parallel.For(0, list.Count, options, i =>
            {
                var p = list[i];
                try
                {
                    var target = Tester.ProbeTarget(p.domain, "https://" + p.domain + "/", timeoutSec);
                    p.ip = target.ip;
                    p.detail = Describe(target);
                    p.state = Classify(target);
                }
                catch (Exception ex)
                {
                    p.state = "bad";
                    p.detail = ex.Message;
                }
            });
            return list;
        }

        /// <summary>
        /// Blocked or not, judged by whether ANY variant got through.
        ///
        /// Tester.TokenState cannot be reused here: it reports "warn" when a
        /// single probe is UNSUP, and TLS1.3=UNSUP just means the server does not
        /// speak 1.3. Using it marked every healthy domain as suspect and would
        /// have dumped all twelve into the hostlist, including the ones that
        /// load fine. For a hostlist only a domain that cannot be reached at all
        /// belongs in there.
        /// </summary>
        private static string Classify(TargetResult t)
        {
            if (t == null || t.results == null || t.results.Count == 0) return "warn";
            var values = t.results.Values.ToList();
            if (values.Any(v => v == "OK")) return "good";
            if (values.Any(v => v == "DNS" || v == "REFUSED" || v == "SSL" || v.StartsWith("ERR"))) return "bad";
            // Everything timed out, or only unsupported variants: reachable but
            // not usable right now.
            return "warn";
        }

        private static string Describe(TargetResult t)
        {
            if (t == null || t.results == null || t.results.Count == 0) return "нет ответа";
            var parts = t.results.Select(kv => kv.Key + "=" + kv.Value).ToArray();
            return string.Join(", ", parts);
        }

        /// <summary>
        /// Hostlist body for the domains that failed. Comments carry the reason
        /// so a later reader knows this was measured, not guessed.
        /// </summary>
        public static string BuildHostlist(IEnumerable<DomainProbe> probes, bool includeWarn)
        {
            var lines = new List<string>();
            lines.Add("# Домены, найденные автоматически (проверено " +
                      DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")");
            lines.Add("# Запуск: Настройки → Автопоиск заблокированных доменов");
            lines.Add("");
            foreach (var p in probes.Where(x => x.blocked && (includeWarn || x.state == "bad")))
            {
                lines.Add("# " + p.service + ": " + (x_ip(p)) );
                lines.Add(p.domain);
            }
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        private static string x_ip(DomainProbe p)
        {
            return string.IsNullOrEmpty(p.ip) ? p.detail : p.ip;
        }
    }
}