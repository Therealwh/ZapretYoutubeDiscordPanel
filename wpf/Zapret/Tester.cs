using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Ydp.Services;

namespace Ydp.Zapret
{
    public sealed class TargetResult
    {
        public string name { get; set; }
        public string kind { get; set; }
        public string url { get; set; }
        public Dictionary<string, string> results { get; set; } = new Dictionary<string, string>();
        public string result { get; set; }

        /// <summary>
        /// The address the connection actually reached, as reported by curl's
        /// remote_ip. Users want to know WHERE the attempt landed, and a DNS
        /// lookup would be a guess rather than the truth.
        /// </summary>
        public string ip { get; set; }

        /// <summary>True when at least one probe variant answered.</summary>
        public bool ok
        {
            get
            {
                if (kind == "ping") return result != "Timeout" && result != null;
                return results != null && results.Values.Any(v => v == "OK");
            }
        }

        /// <summary>Compact per-variant summary for the UI, e.g. "HTTP ok · TLS1.2 ERR".</summary>
        public string probes
        {
            get
            {
                if (kind == "ping") return result ?? "";
                if (results == null) return "";
                return string.Join(" · ", results.Select(p => p.Key + " " + p.Value));
            }
        }
    }

    public sealed class GroupStat
    {
        public int okTargets { get; set; }
        public int total { get; set; }
        public int probesOk { get; set; }
        public int probesAll { get; set; }
        public bool works { get; set; }
        public string state { get; set; }
    }

    public sealed class Groups
    {
        public GroupStat discord { get; set; } = new GroupStat();
        public GroupStat youtube { get; set; } = new GroupStat();
        public GroupStat other { get; set; } = new GroupStat();
    }

    public sealed class Score
    {
        public int ok { get; set; }
        public int err { get; set; }
        public int unsup { get; set; }
        public int pingOk { get; set; }
        public int pingFail { get; set; }
        public int total { get; set; }
    }

    /// <summary>
    /// Port of utils/test (zapret.ps1): launch each strategy, probe the targets
    /// from utils/targets.txt with curl (HTTP/1.1, TLS1.2, TLS1.3) plus ping,
    /// then score.
    ///
    /// Two behaviour changes over the Node version, both deliberate:
    ///  - the deciding targets (DiscordMain, YouTubeWeb) are probed FIRST and the
    ///    run stops as soon as both groups answer, so a working strategy is
    ///    confirmed in one batch instead of walking all 11 targets;
    ///  - a strategy that passes is remembered (see AutoConnect), so the next
    ///    search starts there instead of from the first general*.bat.
    /// </summary>
    public static class Tester
    {
        public static readonly Dictionary<string, string> DefaultTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "DiscordMain", "https://discord.com" },
            { "DiscordGateway", "https://gateway.discord.gg" },
            { "DiscordCDN", "https://cdn.discordapp.com" },
            { "YouTubeWeb", "https://www.youtube.com" },
            { "YouTubeImage", "https://i.ytimg.com" },
            { "CloudflareWeb", "https://www.cloudflare.com" },
            { "CloudflareDNS1111", "PING:1.1.1.1" },
        };

        // One Discord + one YouTube target decide whether a strategy works.
        public static readonly string[] DecisiveTargets = { "DiscordMain", "YouTubeWeb" };

        public static Dictionary<string, string> LoadTargets(string rootDir)
        {
            try
            {
                string path = Path.Combine(rootDir, "utils", "targets.txt");
                if (!File.Exists(path)) return DefaultTargets;
                var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(path))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*(\w+)\s*=\s*""(.+)""\s*$");
                    if (m.Success) parsed[m.Groups[1].Value] = m.Groups[2].Value;
                }
                return parsed.Count > 0 ? parsed : DefaultTargets;
            }
            catch
            {
                return DefaultTargets;
            }
        }

        /// <summary>Decisive targets first so one batch is usually enough.</summary>
        public static List<KeyValuePair<string, string>> OrderTargets(IEnumerable<KeyValuePair<string, string>> targets)
        {
            var list = targets.ToList();
            var decisive = list.Where(t => DecisiveTargets.Contains(t.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            var rest = list.Where(t => !DecisiveTargets.Contains(t.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            return decisive.Concat(rest).ToList();
        }

        public static bool IsWorking(Groups groups)
        {
            return groups != null && groups.discord != null && groups.discord.works
                && groups.youtube != null && groups.youtube.state == "ok";
        }

        public static Groups GroupStats(IEnumerable<TargetResult> results)
        {
            var groups = new Groups
            {
                discord = new GroupStat(),
                youtube = new GroupStat(),
                other = new GroupStat(),
            };
            foreach (var t in results)
            {
                if (t.kind != "url") continue;
                GroupStat g;
                if (t.name.StartsWith("discord", StringComparison.OrdinalIgnoreCase)) g = groups.discord;
                else if (t.name.StartsWith("youtube", StringComparison.OrdinalIgnoreCase)) g = groups.youtube;
                else g = groups.other;
                g.total++;
                int okProbes = t.results.Values.Count(v => v == "OK");
                g.probesOk += okProbes;
                g.probesAll += t.results.Count;
                if (okProbes > 0) g.okTargets++;
            }
            groups.discord.works = groups.discord.okTargets > 0;
            groups.youtube.state = groups.youtube.okTargets > 0 ? "ok" : "dead";
            groups.youtube.works = groups.youtube.state == "ok";
            return groups;
        }

        public static Score ScoreResults(IEnumerable<TargetResult> results)
        {
            var score = new Score();
            foreach (var t in results)
            {
                if (t.kind == "url")
                {
                    foreach (var value in t.results.Values)
                    {
                        if (value == "OK") score.ok++;
                        else if (value == "UNSUP") score.unsup++;
                        else score.err++;
                    }
                }
                else if (t.kind == "ping")
                {
                    if (t.result != "Timeout") score.pingOk++;
                    else score.pingFail++;
                }
            }
            score.total = score.ok + score.err + score.unsup;
            return score;
        }

        // ---- probes ------------------------------------------------------

        private static string RunProcess(string exe, string args, int timeoutMs, bool utf8 = false)
        {
            try
            {
                var startInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = utf8 ? Encoding.UTF8 : Encoding.Default,
                };
                using (var p = Process.Start(startInfo))
                {
                    if (p == null) return string.Empty;
                    var stdout = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { /* ignore */ }
                        return stdout ?? string.Empty;
                    }
                    return (stdout ?? string.Empty).Trim();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// One curl probe: HEAD with specific TLS/http options.
        /// remote_ip is captured too, so the UI can show which address was
        /// actually reached instead of only "it worked".
        /// </summary>
        private static string CurlProbe(string url, string extraArgs, int timeoutSec, out string remoteIp)
        {
            remoteIp = null;
            string args = string.Format(
                "-I -s -m {0} --connect-timeout {1} -o NUL -w \"%{{http_code}} %{{remote_ip}}\" --show-error {2} \"{3}\"",
                timeoutSec, Math.Min(3, timeoutSec), extraArgs, url);
            string output = RunProcess("curl.exe", args, (timeoutSec + 2) * 1000);
            if (string.IsNullOrEmpty(output)) return "ERR";

            // curl writes the -w line first; --show-error appends any message.
            var firstLine = output.Split('\n')[0].Trim();
            var parts = firstLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string code = parts.Length > 0 ? parts[0] : "";
            if (parts.Length > 1 && parts[1] != "0.0.0.0") remoteIp = parts[1];

            if (code.Length >= 3 && code.Substring(0, 3).All(char.IsDigit)) return "OK";
            string lower = output.ToLowerInvariant();
            if (lower.Contains("could not resolve host")) return "DNS";
            if (lower.Contains("certificate") || lower.Contains("ssl")) return "SSL";
            if (lower.Contains("unsupported") || lower.Contains("not supported") || lower.Contains("unrecognized") || lower.Contains("schannel")) return "UNSUP";
            if (lower.Contains("timed out") || lower.Contains("timeout")) return "TIMEOUT";
            if (lower.Contains("refused") || lower.Contains("failed to connect")) return "REFUSED";
            return "ERR";
        }

        private static string PingHost(string host, out string resolvedIp)
        {
            resolvedIp = null;
            try
            {
                var addresses = System.Net.Dns.GetHostAddresses(host);
                var first = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (first != null) resolvedIp = first.ToString();
            }
            catch { /* ping may still work without a resolved address */ }

            string output = RunProcess("ping.exe", "-n 1 -w 1000 " + host, 4000);
            var m = System.Text.RegularExpressions.Regex.Match(output, @"time[<=](\d+)ms", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + " ms";
            if (output.Contains("TTL=")) return "OK";
            return "Timeout";
        }

        public static TargetResult ProbeTarget(string name, string value, int timeoutSec)
        {
            if (value != null && value.StartsWith("PING:", StringComparison.OrdinalIgnoreCase))
            {
                string ip;
                string pingResult = PingHost(value.Substring(5).Trim(), out ip);
                return new TargetResult { name = name, kind = "ping", result = pingResult, ip = ip, url = value.Substring(5).Trim() };
            }
            var target = new TargetResult { name = name, kind = "url", url = value };
            var variants = new[]
            {
                new { Label = "HTTP", Args = "--http1.1" },
                new { Label = "TLS1.2", Args = "--tlsv1.2 --tls-max 1.2" },
                new { Label = "TLS1.3", Args = "--tlsv1.3 --tls-max 1.3" },
            };
            foreach (var variant in variants)
            {
                string ip;
                target.results[variant.Label] = CurlProbe(value, variant.Args, timeoutSec, out ip);
                if (ip != null) target.ip = ip;
            }
            return target;
        }

        // ---- reachability -------------------------------------------------

        public sealed class Reachability
        {
            public Groups groups { get; set; }
            public List<TargetResult> targets { get; set; } = new List<TargetResult>();
        }

        /// <summary>Probe reachability without touching the running bypass.</summary>
        public static Reachability CheckReachability(string rootDir, int timeoutSec = 6)
        {
            var targets = OrderTargets(LoadTargets(rootDir));
            var collected = new List<TargetResult>();
            for (int i = 0; i < targets.Count; i += 4)
            {
                var batch = targets.Skip(i).Take(4).ToList();
                foreach (var entry in batch) collected.Add(ProbeTarget(entry.Key, entry.Value, timeoutSec));
                // Both groups answered: the rest cannot change the verdict.
                if (IsWorking(GroupStats(collected))) break;
            }
            return new Reachability { groups = GroupStats(collected), targets = collected };
        }

        public sealed class VerifyResult
        {
            public bool ok { get; set; }
            public Reachability check { get; set; }
            public int attempts { get; set; }
        }

        /// <summary>
        /// Verify the bypass that is ALREADY running, without touching it. A
        /// single flaky curl probe must not throw away a working setup, so a
        /// failed verdict is retried before the caller starts searching.
        /// </summary>
        public static VerifyResult VerifyStrategy(string rootDir, int attempts = 2, int timeoutSec = 6)
        {
            Reachability check = null;
            for (int i = 1; i <= attempts; i++)
            {
                check = CheckReachability(rootDir, timeoutSec);
                if (IsWorking(check.groups)) return new VerifyResult { ok = true, check = check, attempts = i };
                if (i < attempts) Thread.Sleep(800);
            }
            return new VerifyResult { ok = false, check = check, attempts = attempts };
        }

        // ---- full sweep -----------------------------------------------------

        public sealed class StrategyRunResult
        {
            public string name { get; set; }
            public string file { get; set; }
            public bool failed { get; set; }
            public string failReason { get; set; }
            public Score score { get; set; }
            public Groups groups { get; set; }

            /// <summary>
            /// Per-target detail of this run, so the UI can show which address
            /// answered and which did not instead of only a score.
            /// </summary>
            public List<TargetResult> targets { get; set; } = new List<TargetResult>();
        }

        public sealed class SweepResult
        {
            public List<StrategyRunResult> results { get; set; } = new List<StrategyRunResult>();
            public string winner { get; set; }
            public string winnerFile { get; set; }
            public bool aborted { get; set; }
        }

        private static readonly object AbortGate = new object();
        private static volatile bool _aborted;

        public static void Abort() { _aborted = true; }

        /// <summary>
        /// Run strategies in order and connect the first one where Discord AND
        /// YouTube answer. Strategies that worked before (firstFiles) run first.
        /// </summary>
        public static SweepResult RunStrategyTests(string rootDir, IEnumerable<string> firstFiles, bool stopOnFirst, Action<StrategyRunResult, int, int> onProgress, int timeoutSec = 6)
        {
            _aborted = false;
            var strategies = Strategy.ListStrategies(rootDir);
            var wanted = new HashSet<string>(firstFiles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (wanted.Count > 0)
            {
                strategies = strategies.Where(s => wanted.Contains(s.file))
                    .Concat(strategies.Where(s => !wanted.Contains(s.file)))
                    .ToList();
            }

            ServiceOps.KillWinws();
            ServiceOps.WaitWinws(true, 8000);

            var result = new SweepResult();
            for (int i = 0; i < strategies.Count; i++)
            {
                if (_aborted) break;
                var strat = strategies[i];
                onProgress?.Invoke(null, i, strategies.Count);

                ServiceOps.KillWinws();
                ServiceOps.WaitWinws(true, 8000);

                var start = ServiceOps.LaunchStrategyProcess(rootDir, strat.file);
                if (!start.ok)
                {
                    result.results.Add(new StrategyRunResult
                    {
                        name = strat.name,
                        file = strat.file,
                        failed = true,
                        failReason = start.reason ?? "not_started",
                        score = new Score(),
                    });
                    continue;
                }

                Thread.Sleep(500);
                var targets = OrderTargets(LoadTargets(rootDir));
                var collected = new List<TargetResult>();
                for (int j = 0; j < targets.Count && !_aborted; j += 4)
                {
                    var batch = targets.Skip(j).Take(4).ToList();
                    foreach (var entry in batch) collected.Add(ProbeTarget(entry.Key, entry.Value, timeoutSec));
                    if (stopOnFirst && IsWorking(GroupStats(collected))) break;
                }

                var score = ScoreResults(collected);
                var groups = GroupStats(collected);
                var runResult = new StrategyRunResult
                {
                    name = strat.name,
                    file = strat.file,
                    score = score,
                    groups = groups,
                    targets = new List<TargetResult>(collected),
                };
                result.results.Add(runResult);
                onProgress?.Invoke(runResult, i, strategies.Count);

                if (stopOnFirst && IsWorking(groups))
                {
                    // Leave the winner running, exactly like the Node version.
                    result.winner = strat.name;
                    result.winnerFile = strat.file;
                    SaveResults(rootDir, result, strat.name);
                    return result;
                }
                ServiceOps.KillWinws();
            }

            var ok = result.results.Where(r => !r.failed).ToList();
            var best = ok.OrderByDescending(r => r.score.ok).ThenByDescending(r => r.score.pingOk).FirstOrDefault();
            if (best != null)
            {
                result.winner = best.name;
                result.winnerFile = best.file;
            }
            result.aborted = _aborted;
            SaveResults(rootDir, result, result.winner);
            return result;
        }

        // ---- saved results ---------------------------------------------------

        public static void SaveResults(string rootDir, SweepResult run, string winner)
        {
            try
            {
                string dir = Path.Combine(rootDir, "utils", "test results");
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var sb = new StringBuilder();
                sb.Append("{\n  \"at\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");
                sb.Append("  \"winner\": ").Append(Json(winner)).Append(",\n  \"results\": [\n");
                for (int i = 0; i < run.results.Count; i++)
                {
                    var r = run.results[i];
                    sb.Append("    { \"name\": ").Append(Json(r.name))
                      .Append(", \"failed\": ").Append(r.failed ? "true" : "false")
                      .Append(", \"failReason\": ").Append(Json(r.failReason))
                      .Append(", \"score\": { \"ok\": ").Append(r.score.ok)
                      .Append(", \"err\": ").Append(r.score.err)
                      .Append(", \"unsup\": ").Append(r.score.unsup)
                      .Append(", \"pingOk\": ").Append(r.score.pingOk)
                      .Append(", \"pingFail\": ").Append(r.score.pingFail).Append(" } }");
                    if (i < run.results.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("  ]\n}\n");
                File.WriteAllText(Path.Combine(dir, "test_results_" + stamp + ".json"), sb.ToString());
            }
            catch
            {
                // Persisting results must never break the run.
            }
        }

        private static string Json(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var ch in value)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        public sealed class LastResults
        {
            public string winner { get; set; }
            public string at { get; set; }
            public string file { get; set; }
            public List<StrategyRunResult> results { get; set; } = new List<StrategyRunResult>();
        }

        public static LastResults LoadLastResults(string rootDir)
        {
            try
            {
                string dir = Path.Combine(rootDir, "utils", "test results");
                if (!Directory.Exists(dir)) return new LastResults();
                var file = Directory.GetFiles(dir, "test_results_*.json")
                    .OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
                if (file == null) return new LastResults();
                string raw = File.ReadAllText(file);
                var result = new LastResults { file = Path.GetFileName(file) };
                var winner = System.Text.RegularExpressions.Regex.Match(raw, @"""winner""\s*:\s*""((?:[^""\\]|\\.)*)""");
                if (winner.Success) result.winner = winner.Groups[1].Value;
                var at = System.Text.RegularExpressions.Regex.Match(raw, @"""at""\s*:\s*""([^""]*)""");
                if (at.Success) result.at = at.Groups[1].Value;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, @"""name""\s*:\s*""((?:[^""\\]|\\.)*)"""))
                {
                    result.results.Add(new StrategyRunResult { name = m.Groups[1].Value });
                }
                return result;
            }
            catch
            {
                return new LastResults();
            }
        }
    }
}