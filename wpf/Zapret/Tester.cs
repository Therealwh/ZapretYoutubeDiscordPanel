using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        /// <summary>Name of the target the verdict came from (e.g. YouTubeWeb).</summary>
        public string decisiveName { get; set; }
        /// <summary>That target was actually present in the run.</summary>
        public bool hasDecisive { get; set; }
        public bool decisiveOk { get; set; }
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

        /// <summary>
        /// Per-target probe tokens in the same shape the bundled zapret tester
        /// prints them: HTTP / TLS1.2 / TLS1.3, each marked OK, SSL, UNSUP or
        /// ERR.
        ///
        /// The tester deliberately does not decide whether a strategy "works" -
        /// it shows the evidence and lets the human judge. An invented score
        /// hides the one thing that matters here: SSL and ERR next to a green
        /// count still means the DPI got through.
        /// </summary>
        public static string TokenSummary(TargetResult t)
        {
            if (t == null) return "";
            if (t.kind == "ping") return "ping " + (t.result ?? "n/a");
            var parts = new List<string>();
            foreach (var label in new[] { "HTTP", "TLS1.2", "TLS1.3" })
            {
                string v;
                if (!t.results.TryGetValue(label, out v)) v = "ERR";
                parts.Add(label + ":" + v);
            }
            return string.Join("  ", parts);
        }

        /// <summary>
        /// Worst result across a target's probes, as a state the UI can colour.
        /// Same palette as the bundled tester: OK green, UNSUP/TIMEOUT yellow,
        /// SSL/ERROR red.
        /// </summary>
        public static string TokenState(TargetResult t)
        {
            if (t == null) return "warn";
            if (t.kind == "ping") return t.result == "Timeout" ? "warn" : "good";
            var values = t.results.Values.ToList();
            if (values.Count == 0) return "warn";
            if (values.Any(v => v == "SSL" || v == "DNS" || v == "REFUSED" || v.StartsWith("ERR"))) return "bad";
            if (values.Any(v => v == "UNSUP" || v == "TIMEOUT")) return "warn";
            return "good";
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

            // The verdict is decided by the DECISIVE target, not by "any target
            // answered".
            //
            // YouTube's group is four targets: the main page, youtu.be, image
            // hosts and the video redirect. The image and redirect hosts live on
            // Google addresses that usually stay reachable even when the main
            // page is blocked, so "okTargets > 0" reported YouTube as working
            // while www.youtube.com was dead in DNS - which is why no strategy
            // ever turned red and why the sweep could "confirm" a broken one.
            // Same shape for Discord. Falls back to the old any-target rule only
            // when the decisive target is absent (custom target files).
            ApplyDecisive(groups.discord, results, "DiscordMain");
            ApplyDecisive(groups.youtube, results, "YouTubeWeb");

            groups.discord.works = groups.discord.hasDecisive
                ? groups.discord.decisiveOk
                : groups.discord.okTargets > 0;
            groups.youtube.decisiveName = "YouTubeWeb";
            groups.youtube.works = groups.youtube.hasDecisive
                ? groups.youtube.decisiveOk
                : groups.youtube.okTargets > 0;
            groups.youtube.state = groups.youtube.works ? "ok" : "dead";
            return groups;
        }

        /// <summary>
        /// Resolve a group's verdict from its decisive target and remember what
        /// that target was, so the UI can say WHICH host failed instead of only
        /// that the service did.
        /// </summary>
        private static void ApplyDecisive(GroupStat group, IEnumerable<TargetResult> results, string decisiveName)
        {
            group.decisiveName = decisiveName;
            TargetResult decisive = null;
            foreach (var t in results)
            {
                if (t == null || t.kind != "url") continue;
                if (string.Equals(t.name, decisiveName, StringComparison.OrdinalIgnoreCase)) { decisive = t; break; }
            }
            if (decisive == null) return;
            group.hasDecisive = true;
            group.decisiveOk = decisive.results != null && decisive.results.Values.Any(v => v == "OK");
        }

        /// <summary>
        /// One line explaining why a strategy was judged unusable, naming the
        /// host that actually failed rather than just "nothing works".
        /// </summary>
        public static string DescribeGroups(Groups groups)
        {
            if (groups == null) return "нет данных о проверке";
            var parts = new List<string>();
            if (groups.discord != null && !groups.discord.works)
                parts.Add("Discord: " + HostLabel(groups.discord));
            if (groups.youtube != null && !groups.youtube.works)
                parts.Add("YouTube: " + HostLabel(groups.youtube));
            if (parts.Count == 0) return "ничего не разблокировано";
            return "не разблокирует — " + string.Join("; ", parts);
        }

        private static string HostLabel(GroupStat g)
        {
            if (g == null) return "нет данных";
            if (g.hasDecisive && !string.IsNullOrEmpty(g.decisiveName))
                return g.decisiveName + " не отвечает";
            return "ни одна цель не отвечает (" + g.okTargets + "/" + g.total + ")";
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
            string stderr;
            return RunProcess(exe, args, timeoutMs, utf8, out stderr);
        }

        /// <summary>
        /// Run a process and capture both streams.
        ///
        /// The two ReadToEndAsync calls run concurrently on purpose: reading
        /// stdout to the end and only then stderr deadlocks whenever the child
        /// fills the stderr pipe buffer while we are still blocked on stdout.
        /// </summary>
        private static string RunProcess(string exe, string args, int timeoutMs, bool utf8, out string stderr)
        {
            stderr = null;
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
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { /* ignore */ }
                        stderr = SafeTaskResult(errTask);
                        return SafeTaskResult(outTask) ?? string.Empty;
                    }
                    stderr = SafeTaskResult(errTask);
                    return (SafeTaskResult(outTask) ?? string.Empty).Trim();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeTaskResult(System.Threading.Tasks.Task<string> task)
        {
            try { return task.Result; }
            catch { return null; }
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
            string stderr;
            string output = RunProcess("curl.exe", args, (timeoutSec + 2) * 1000, false, out stderr);
            if (string.IsNullOrEmpty(output)) return ClassifyCurlError(stderr);

            // curl prints the -w line first; --show-error appends the reason.
            var firstLine = output.Split('\n')[0].Trim();
            var parts = firstLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string code = parts.Length > 0 ? parts[0] : "";
            if (parts.Length > 1 && parts[1] != "0.0.0.0") remoteIp = parts[1];

            // 000 means no HTTP response arrived at all: connection refused,
            // timeout, TLS handshake killed by the DPI. Counting it as OK is how
            // a fully blocked target used to pass as reachable, so any code that
            // is not a real HTTP status is classified from the error text.
            if (code.Length == 3 && code != "000" && code.All(char.IsDigit)) return "OK";
            return ClassifyCurlError((stderr ?? "") + " " + output);
        }

        /// <summary>
        /// Map curl's error text to the same labels the bundled zapret tester
        /// prints: DNS, SSL, UNSUP, TIMEOUT, REFUSED, ERR.
        /// </summary>
        private static string ClassifyCurlError(string errorText)
        {
            string lower = (errorText ?? "").ToLowerInvariant();
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

        /// <summary>
        /// Probe several targets at the same time.
        ///
        /// The bundled zapret tester uses a runspace pool for this, and the
        /// reason is plain: every blocked target burns the whole timeout waiting
        /// for something that will never answer, so probing one after another
        /// makes a full sweep take an order of magnitude longer than it needs to
        /// for no extra information.
        /// </summary>
        public static List<TargetResult> ProbeTargets(IList<KeyValuePair<string, string>> targets, int timeoutSec)
        {
            var results = new TargetResult[targets.Count];
            if (targets.Count == 0) return new List<TargetResult>();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(8, Math.Max(4, Environment.ProcessorCount)),
            };
            Parallel.For(0, targets.Count, options, i =>
                results[i] = ProbeTarget(targets[i].Key, targets[i].Value, timeoutSec));
            return new List<TargetResult>(results);
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
            // Probed in parallel: with the bypass down every blocked target sits
            // there for the full timeout, and doing that one at a time is what
            // made the sweep look like it had frozen before it even started.
            var collected = ProbeTargets(targets, timeoutSec);
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

            /// <summary>The strategy never got winws running, as opposed to
            /// running and not unblocking anything. The UI words those
            /// differently: "did not start" is a broken config, "does not
            /// unblock" is a config that simply does not fit this network.</summary>
            public bool launchFailed { get; set; }

            /// <summary>
            /// The probes were cut short by an abort.
            ///
            /// A half-measured strategy must not be presented as a normal verdict:
            /// it has fewer probes than the rest of the sweep, so a green score on
            /// it would not mean the same thing as a green score on a full run.
            /// </summary>
            public bool partial { get; set; }
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

            /// <summary>
            /// Reachability with NO bypass running, measured before the sweep.
            ///
            /// Without it a green row proves nothing: on a network where the
            /// targets answer anyway, every strategy would look equally good.
            /// With the baseline a user can see what the test actually changed.
            /// </summary>
            /// <summary>
        /// What answered with no bypass at all.
public Groups baseline { get; set; }
        }

        private static readonly object AbortGate = new object();
        private static volatile bool _aborted;

        /// <summary>
        /// True while a full/selected sweep is running. The bypass watchdog uses
        /// it to stay quiet during a test: the sweep deliberately cycles through
        /// every config, so reachability is chaotic and must not be read as "the
        /// bypass died".
        /// </summary>
        public static volatile bool SweepActive;

        public static void Abort() { _aborted = true; }

        /// <summary>
        /// Clear the abort flag at the moment the UI starts scheduling a sweep.
        ///
        /// Clearing it inside the sweep method has a hole: the UI enables the
        /// stop button before the Task.Run actually gets scheduled, so an Abort
        /// landing in that window was wiped by the reset and the sweep ran to
        /// the end. Resetting on the UI thread up front removes the window.
        /// </summary>
        public static void ResetAbort() { _aborted = false; }

        /// <summary>
        /// Kill a process and its children.
        ///
        /// Process.Kill only takes down the process itself: the tester's cmd
        /// wrapper and any curl it spawned survive and keep running. taskkill /T
        /// walks the tree, which .NET Framework's Process API cannot do.
        /// </summary>
        private static void KillProcessTree(System.Diagnostics.Process proc)
        {
            try { RunProcess("taskkill.exe", "/PID " + proc.Id + " /T /F", 10000); }
            catch { try { proc.Kill(); } catch { } }
        }

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

            // Every candidate starts from a clean machine: the service entry is removed
            // so nothing can inherit the previous arguments, winws is gone and
            // the driver unloaded. Without this the next candidate is measured
            // while the previous one is still bypassing, and everything is green.
            ServiceOps.ResetBypass();

            var result = new SweepResult();

            // Baseline first: what the targets answer with no bypass at all.
            try
            {
// No baseline pass before the sweep. The bundled tester does not
            // measure one either: it prints the raw probe tokens per config and
            // leaves the reading to a human. A "what answers with no bypass"
            // banner on top of that only produced the confusing claim that
            // everything was already open, and it cost a full extra round of
            // timeouts before the first strategy even started.
            onProgress?.Invoke(null, 0, strategies.Count);
            }
            catch { /* the sweep is still useful without the baseline */ }

            for (int i = 0; i < strategies.Count; i++)
            {
                if (_aborted) break;
                var strat = strategies[i];
                onProgress?.Invoke(null, i, strategies.Count);

                ServiceOps.ResetBypass();

                // Started as a plain process, the same way the bundled
                // "test zapret.ps1" does and the same way a user double-clicks
                // general*.bat to try a strategy by hand. Installing a Windows
                // service for every candidate costs a service create/delete per
                // strategy and is the slowest and most failure-prone part of the
                // run; the service only matters for the one strategy that is
                // finally chosen and installed on demand.
                var start = ServiceOps.LaunchStrategyProcess(rootDir, strat.file);
                if (!start.ok || !ServiceOps.WaitWinws(false, 8000))
                {
                    result.results.Add(new StrategyRunResult
                    {
                        name = strat.name,
                        file = strat.file,
                        failed = true,
                        failReason = start.ok ? "winws_not_started" : (start.reason ?? "not_started"),
                        score = new Score(),
                    });
                    ServiceOps.KillWinws();
                    continue;
                }

var targets = OrderTargets(LoadTargets(rootDir));
                List<TargetResult> collected;
                if (_aborted) collected = new List<TargetResult>();
                else collected = ProbeTargets(targets, timeoutSec);

                // The bypass has to still be alive once the probes are done.
                // If winws died or was never up, the probes measured the plain
                // network and nothing about this strategy, so it cannot be
                // counted as working no matter how many probes came back OK.
                if (!ServiceOps.IsWinwsRunning())
                {
                    result.results.Add(new StrategyRunResult
                    {
                        name = strat.name,
                        file = strat.file,
                        failed = true,
                        launchFailed = true,
                        failReason = "winws_не_запустился",
                        score = new Score(),
                        targets = collected,
                    });
                    onProgress?.Invoke(result.results[result.results.Count - 1], i, strategies.Count);
                    ServiceOps.KillWinws();
                    if (_aborted) break;
                    continue;
                }

                var score = ScoreResults(collected);
                var groups = GroupStats(collected);
                bool works = IsWorking(groups);
                var runResult = new StrategyRunResult
                {
                    name = strat.name,
                    file = strat.file,
                    score = score,
                    groups = groups,
                    targets = new List<TargetResult>(collected),
                    partial = _aborted,
                    // The tester DOES judge the config now.
                    //
                    // It used to leave `failed` false for every strategy that
                    // merely started, so a full sweep painted them all green even
                    // when they unblocked nothing - the row colour came from the
                    // per-target dots, which stayed yellow ("Discord ok, YouTube
                    // partially"). With the decisive-target verdict that verdict
                    // is trustworthy, so a strategy that does not unblock Discord
                    // AND YouTube is marked failed, with the reason.
                    failed = !works,
                    failReason = works ? null : DescribeGroups(groups),
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

                // After an abort the bypass belongs to whoever asked for the stop.
                // ResetBypass here would race "Подключить сейчас", which installs
                // the picked strategy at the same moment, and whichever loses the
                // race leaves the user without the strategy they just clicked on.
                if (_aborted) break;

                ServiceOps.KillWinws();
                ServiceOps.WaitWinws(true, 8000);
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

        // ---- original zapret tester -----------------------------------------

        // Report shapes shared by the tester's console stream and its saved
        // test_results_*.txt, so both paths read identically.
        private static readonly System.Text.RegularExpressions.Regex ReConfigMark =
            new System.Text.RegularExpressions.Regex(@"^\s*\[(\d+)/(\d+)\]\s*(.+)$");
        private static readonly System.Text.RegularExpressions.Regex ReConfigFile =
            new System.Text.RegularExpressions.Regex("^Config: (.+?) \\(Type: (\\w+)\\)$");
        private static readonly System.Text.RegularExpressions.Regex ReTarget =
            new System.Text.RegularExpressions.Regex("^  (.+?) : (.*?) \\| Ping: (.+)$");
        private static readonly System.Text.RegularExpressions.Regex ReToken =
            new System.Text.RegularExpressions.Regex("(HTTP|TLS1\\.2|TLS1\\.3):(OK|ERROR|SSL|UNSUP)");
        private static readonly System.Text.RegularExpressions.Regex ReBest =
            new System.Text.RegularExpressions.Regex("^Best strategy: (.+)$");

        // Console format: the name column is padded to a fixed width, so there
        // is no "name : tokens" separator — the tokens simply start after a run
        // of two or more spaces. The saved .txt file, in contrast, joins them
        // with " : ". Two shapes, one parser.
        private static readonly System.Text.RegularExpressions.Regex ReTargetConsole =
            new System.Text.RegularExpressions.Regex(@"^\s*(?<name>.+?)\s{2,}(?<tokens>(?:HTTP|TLS1\.2|TLS1\.3):.+?)(?:\s*\|\s*Ping:\s*(?<ping>.*))?$");
        private static readonly System.Text.RegularExpressions.Regex ReTargetConsolePing =
            new System.Text.RegularExpressions.Regex(@"^\s*(?<name>.+?)\s{2,}Ping:\s*(?<ping>.+)$");

        /// <summary>
        /// One console/report line about a single target. Handles both shapes:
        /// file ("  Name : HTTP:OK ... | Ping: 6 ms") and console
        /// ("  Name    HTTP:OK ... | Ping: 6 ms", ping-only rows too).
        /// </summary>
        public static TargetResult ParseTargetLine(string line)
        {
            var t = new TargetResult();
            string http, ping;

            var m = ReTarget.Match(line);
            if (m.Success)
            {
                t.name = m.Groups[1].Value.Trim();
                http = m.Groups[2].Value;
                ping = m.Groups[3].Value.Trim();
            }
            else
            {
                m = ReTargetConsole.Match(line);
                if (m.Success)
                {
                    t.name = m.Groups["name"].Value.Trim();
                    http = m.Groups["tokens"].Value;
                    ping = m.Groups["ping"].Success ? m.Groups["ping"].Value.Trim() : null;
                }
                else
                {
                    m = ReTargetConsolePing.Match(line);
                    if (!m.Success) return null;
                    t.name = m.Groups["name"].Value.Trim();
                    http = "";
                    ping = m.Groups["ping"].Value.Trim();
                }
            }

            t.url = t.name;
            var words = ReToken.Matches(http ?? "");
            if (words.Count == 0)
            {
                t.kind = "ping";
                t.result = ping;
            }
            else
            {
                t.kind = "url";
                if (ping != null) t.result = ping;
                foreach (System.Text.RegularExpressions.Match w in words)
                    t.results[w.Groups[1].Value] = w.Groups[2].Value;
            }
            return t;
        }

        /// <summary>
        /// Run the zapret bundle's own tester (utils\test zapret.ps1) and parse
        /// the report it writes.
        ///
        /// This is deliberately NOT a re-implementation. An earlier in-process
        /// sweep reproduced the probe mechanics but got the state wrong: probes
        /// ran while the previous strategy's bypass was still up, so every
        /// config looked identical. The bundled script owns the whole cycle -
        /// stop winws, start the .bat, wait for it, probe in parallel, restore
        /// what was running - and refuses to run at all while a zapret service
        /// is installed, which is exactly the isolation it is supposed to
        /// enforce. Delegating to it means the panel shows literally what the
        /// reference tool shows.
        /// </summary>
        /// <summary>
        /// Map a config label printed by the tester ("general (ALT2).bat") to the
        /// real .bat file inside the zapret root. The tester prints a title, not a
        /// path; passing that title straight to ParseStrategyFile resolved it against
        /// the panel's own working directory and failed with parse_failed.
        /// </summary>
        public static string ResolveStrategyFile(string rootDir, string label)
        {
            if (string.IsNullOrEmpty(label)) return label;
            if (Path.IsPathRooted(label)) return label;
            string name = label.Trim();
            string root = rootDir ?? string.Empty;
            try
            {
                string exact = Path.Combine(root, name);
                if (File.Exists(exact)) return exact;
                // The tester sometimes prints the title without the extension.
                if (!name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    string withExt = Path.Combine(root, name + ".bat");
                    if (File.Exists(withExt)) return withExt;
                }
                // Case-insensitive match against the actual files on disk.
                foreach (var f in Directory.GetFiles(root, "*.bat"))
                    if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                        return f;
            }
            catch { /* best effort: fall through to the plain combine */ }
            return Path.Combine(root, name);
        }

        /// <param name="onLine">Live console line from the script, for the status bar.</param>
        /// <summary>
        /// Builds the keystrokes for the bundled tester's interactive menu.
        ///
        /// The script asks three questions: test type (1 = standard),
        /// run mode (1 = all configs, 2 = selected configs) and, only for
        /// "selected", the numbers (e.g. "1,3,5-7", '0' = all). This feeds
        /// those answers non-interactively and ends with a throwaway line for
        /// Wait-AnyKey/Read-Host.
        ///
        /// The selection is sanitized down to digits, commas, dashes and spaces
        /// before it reaches the script's stdin: a stray character would make the
        /// script's number regex reject the whole line and drop into its "try
        /// again" loop, stalling the run.
        /// </summary>
        private static string BuildTestMenuInput(string selection)
        {
            var cleaned = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(selection))
                foreach (var c in selection)
                    if (char.IsDigit(c) || c == ',' || c == '-' || c == ' ')
                        cleaned.Append(c);

            var picked = cleaned.ToString().Trim().Trim(',', '-', ' ');
            // No usable numbers survived: fall back to "all configs" so a bad
            // entry can never leave the tester waiting on its selection prompt.
            if (string.IsNullOrEmpty(picked) || !picked.Any(char.IsDigit))
                return "1\r\n1\r\nx\r\n";

            // 1 = standard, 2 = selected configs, then the numbers.
            return "1\r\n2\r\n" + picked + "\r\nx\r\n";
        }

        public static SweepResult RunOriginalTester(string rootDir, Action<string> onLine, Action<StrategyRunResult, int, int> onProgress, string selection = null)
        {
            // Not reset here on purpose: the UI clears the flag via ResetAbort()
            // before scheduling this task, and an Abort landing between scheduling
            // and entering the method must survive.
            var result = new SweepResult();

            string script = Path.Combine(rootDir, "utils", "test zapret.ps1");
            if (!File.Exists(script))
            {
                Logger.Warn("Оригинальный тестер не найден: " + script, "tester");
                return result;
            }

            // The script refuses to run while the zapret service is installed
            // ("Remove the service before running tests"), and a clean start is
            // what makes each config measurement independent anyway.
            ServiceOps.ResetBypass();

            string resultsDir = Path.Combine(rootDir, "utils", "test results");
            var started = DateTime.Now.AddSeconds(-2);

            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; & '\""
                + script + "\"'\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = rootDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            System.Diagnostics.Process proc;
            try { proc = System.Diagnostics.Process.Start(psi); }
            catch (Exception ex)
            {
                Logger.Warn("Не удалось запустить тестер zapret: " + ex.Message, "tester");
                return result;
            }
            if (proc == null) return result;
            string winnerLine = null;

            using (proc)
            {
                // Standard tests → (All configs | Selected configs → numbers). The
                // trailing line is consumed by Wait-AnyKey, which falls back to
                // Read-Host when the console is not interactive. The stream stays
                // open on purpose: closing it would turn every later Read-Host into
                // an endless menu loop.
                try
                {
                    proc.StandardInput.Write(BuildTestMenuInput(selection));
                    proc.StandardInput.Flush();
                }
                catch { /* the script exited before asking anything */ }

                // The script prints each config's target table to the console as
                // soon as that config is done. Parsing the stream means the UI
                // shows config 1 in full detail within the first minute — with
                // its connect button — instead of after all 22 configs.
                int total = 0, index = -1;
                string currentName = null;
                var currentTargets = new List<TargetResult>();

                var flush = new Action(() =>
                {
                    if (string.IsNullOrEmpty(currentName))
                    {
                        currentTargets = new List<TargetResult>();
                        return;
                    }
                    // A config with zero target rows means winws never came up:
                    // the script prints the header, tries to start, and moves on.
                    // That is a real verdict the user needs to see, not something
                    // to silently drop.
                    if (currentTargets.Count == 0)
                    {
                        result.results.Add(new StrategyRunResult
                        {
                            name = currentName,
                            file = ResolveStrategyFile(rootDir, currentName),
                            failed = true,
                            failReason = "winws не поднялся",
                            score = new Score(),
                        });
                        onProgress?.Invoke(result.results[result.results.Count - 1], Math.Max(0, index), Math.Max(total, result.results.Count));
                        currentName = null;
                        currentTargets = new List<TargetResult>();
                        return;
                    }
                    var run = new StrategyRunResult
                    {
                        name = currentName,
                        file = ResolveStrategyFile(rootDir, currentName),
                        targets = new List<TargetResult>(currentTargets),
                        // An abort in the middle of the probes leaves this config
                        // with fewer rows than the rest; marking it partial keeps
                        // the UI from offering a connect on a truncated run.
                        partial = _aborted,
                    };
                    run.groups = GroupStats(run.targets);
                    run.score = ScoreResults(run.targets);
                    result.results.Add(run);
                    onProgress?.Invoke(run, Math.Max(0, index), Math.Max(total, result.results.Count));
                    currentName = null;
                    currentTargets = new List<TargetResult>();
                });

                proc.OutputDataReceived += (_, e) =>
                {
                    var line = e.Data;
                    if (string.IsNullOrEmpty(line)) return;
                    onLine?.Invoke(line);

                    // "Best strategy" is printed after the last config, while
                    // currentName is still set, so it has to be checked before
                    // the target-table block or it would never be seen.
                    var bm = ReBest.Match(line.Trim());
                    if (bm.Success) { winnerLine = bm.Groups[1].Value.Trim(); return; }

                    var m = ReConfigMark.Match(line);
                    if (m.Success)
                    {
                        flush();
                        // Bounded parse: a garbage line with a huge number must
                        // not throw OverflowException inside the reader callback.
                        index = int.TryParse(m.Groups[1].Value, out var idx) ? idx - 1 : 0;
                        total = int.TryParse(m.Groups[2].Value, out var tot) ? tot : 0;
                        currentName = m.Groups[3].Value.Trim();
                        return;
                    }
                    if (currentName != null)
                    {
                        var target = ParseTargetLine(line);
                        if (target != null) currentTargets.Add(target);
                    }
                };
                proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) onLine?.Invoke(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                // The script prints its own "several minutes" warning: 22
                // configs with per-target probes is minutes, not seconds.
                var deadline = DateTime.Now.AddMinutes(45);
                while (!proc.HasExited)
                {
                    if (_aborted)
                    {
                        KillProcessTree(proc);
                        ServiceOps.KillWinws();
                        result.aborted = true;
                        onLine?.Invoke("[ПАНЕЛЬ] Проверка остановлена пользователем");
                        break;
                    }
                    if (DateTime.Now > deadline)
                    {
                        KillProcessTree(proc);
                        ServiceOps.KillWinws();
                        onLine?.Invoke("[ПАНЕЛЬ] Тестер не уложился в 45 минут и был остановлен");
                        break;
                    }
                    Thread.Sleep(400);
                }

                // Drain the async readers: after HasExited goes true, buffered
                // output lines can still arrive on the pool threads. A BOUNDED
                // wait — an unbounded one turned "stop" into a frozen UI when a
                // stubborn child kept a pipe open. The tree kill above already
                // did the real work; if the handle is still wedged, force it
                // and move on.
                try
                {
                    if (!proc.WaitForExit(15000)) KillProcessTree(proc);
                }
                catch { }

                // Whatever the script printed last still needs its row.
                flush();

                ServiceOps.KillWinws();
                ServiceOps.WaitWinws(true, 8000);
            }

            if (result.aborted) return result;

            // The report file is now only consulted for the tester's own
            // "Best strategy" pick; the rows themselves came from the stream.
            var report = NewestResultFile(resultsDir, started);
            if (report != null)
            {
                try
                {
                    foreach (var raw in File.ReadAllLines(report))
                    {
                        var m = ReBest.Match(raw.Trim());
                        if (m.Success) { result.winner = m.Groups[1].Value.Trim(); break; }
                    }
                }
                catch { /* winner stays empty; rows are already in place */ }
            }
            if (result.winner == null) result.winner = winnerLine;
            SaveResults(rootDir, result, result.winner);
            return result;
        }

        /// <summary>Newest report file the tester wrote since the run began.</summary>
        private static string NewestResultFile(string dir, DateTime notOlderThan)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;
                return new DirectoryInfo(dir)
                    .GetFiles("test_results_*.txt")
                    .Where(f => f.LastWriteTime >= notOlderThan)
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// Read the tester's saved report: per-config target lines with raw probe
        /// tokens and its "Best strategy" pick.
        /// </summary>
        public static SweepResult ParseOriginalReport(string file)
        {
            var sweep = new SweepResult();
            StrategyRunResult current = null;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.TrimEnd();
                var m = ReConfigFile.Match(line);
                if (m.Success)
                {
                    current = new StrategyRunResult { name = m.Groups[1].Value.Trim(), file = m.Groups[1].Value.Trim() };
                    sweep.results.Add(current);
                    continue;
                }
                if (current != null)
                {
                    var t = ParseTargetLine(line);
                    if (t != null) { current.targets.Add(t); continue; }
                }
                m = ReBest.Match(line);
                if (m.Success) sweep.winner = m.Groups[1].Value.Trim();
            }

            foreach (var r in sweep.results)
            {
                r.groups = GroupStats(r.targets);
                r.score = ScoreResults(r.targets);
            }
            return sweep;
        }

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