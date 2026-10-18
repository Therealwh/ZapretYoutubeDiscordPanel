using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Ydp.Zapret
{
    public sealed class TuneStepResult
    {
        public string label { get; set; }
        public bool ok { get; set; }
        public int discord { get; set; }
        public int youtube { get; set; }
        public string detail { get; set; }
        public int score { get { return discord + youtube; } }
    }

    public sealed class TuneOutcome
    {
        public List<TuneStepResult> steps { get; set; } = new List<TuneStepResult>();
        public TuneStepResult winner { get; set; }
        public string winnerFile { get; set; }
        public string restoredTo { get; set; }
        public bool aborted { get; set; }
    }

    /// <summary>
    /// blockcheck-style search over attack combinations.
    ///
    /// The strategy tester can only tell whether a READY strategy works, which
    /// leaves the user hunting through .bat files one by one. This does the
    /// hunting: it writes a short list of attack combinations known to unblock
    /// TLS, applies each through the same service/process path as any strategy,
    /// probes Discord and YouTube through it, and keeps the one that answered.
    ///
    /// The winner is saved as a real .bat, so it shows up in the strategy list,
    /// can be pinned and can be opened in the DPI expert for further tuning -
    /// the search produces a strategy, not a one-off.
    ///
    /// The candidate list is deliberately short. A full sweep of the flag space
    /// would take hours; these are the combinations that actually carry traffic.
    /// </summary>
    public static class AutoTuner
    {
        public const string TempName = "autotune-candidate.bat";
        public const string WinnerPrefix = "Мой обход (авто";

        // desync, fooling, repeats, ttl/autottl, split_tls
        private static readonly string[][] Candidates =
        {
            new[] { "fake",           "md5sig", "6", "1:3-20", "sniext" },
            new[] { "fake,split2",    "md5sig", "6", "1:3-20", "sniext" },
            new[] { "fake,split2",    "badseq", "6", "1:3-20", "sniext" },
            new[] { "fake,split2",    "none",   "11", "4",      "sniext" },
            new[] { "disorder2",      "md5sig", "6", "2",      "sniext" },
            new[] { "split2",         "md5sig", "6", "1:3-20", "sni"    },
            new[] { "fake,tamper",    "md5sig", "6", "2",      "sniext" },
            new[] { "fake,hopbyhop",  "md5sig", "6", "2",      "sniext" },
            new[] { "syndata,split2", "md5sig", "6", "1:3-20", "sniext" },
            new[] { "fakeknown",      "md5sig", "6", "2",      "sniext" },
        };

        public static int CandidateCount { get { return Candidates.Length; } }

        public static string CandidateLabel(int i)
        {
            var c = Candidates[i];
            return "desync=" + c[0] + ", fooling=" + c[1] + ", repeats=" + c[2]
                 + ", ttl=" + c[3] + ", split_tls=" + c[4];
        }

        public static DpiPlan PlanFor(int i)
        {
            var c = Candidates[i];
            var plan = new DpiPlan { name = "autotune" };
            plan.Set("--dpi-desync", c[0]);
            plan.Set("--dpi-desync-fooling", c[1]);
            plan.Set("--dpi-desync-repeats", c[2]);
            if (c[3].Contains(":")) plan.Set("--dpi-desync-autottl", c[3]);
            else plan.Set("--dpi-desync-ttl", c[3]);
            plan.Set("--dpi-desync-split-tls", c[4]);
            plan.Set("--filter-l3", "ipv4");
            return plan;
        }

        /// <summary>
        /// Run the search. progress(i, total, label) fires before each
        /// candidate. shouldStop is consulted with the results so far; return
        /// true to stop early (used when a candidate fully works).
        /// </summary>
        public static TuneOutcome Search(
            string rootDir,
            Action<int, int, string> progress,
            Func<List<TuneStepResult>, bool> shouldStop,
            Action<string> log,
            Func<bool> cancelRequested)
        {
            var outcome = new TuneOutcome();
            string tempPath = Path.Combine(rootDir, TempName);
            string previous = null;
            bool preferService = !string.Equals(
                Config.Get(AutoConnect.KeyPreferService, "true"), "false", StringComparison.OrdinalIgnoreCase);

            try
            {
                previous = Status.GetStatus(rootDir).serviceStrategy;
            }
            catch { }

            try
            {
                for (int i = 0; i < Candidates.Length; i++)
                {
                    if (cancelRequested != null && cancelRequested())
                    {
                        outcome.aborted = true;
                        break;
                    }
                    if (progress != null) progress(i, Candidates.Length, CandidateLabel(i));

                    var step = new TuneStepResult { label = CandidateLabel(i) };
                    try
                    {
                        File.WriteAllText(tempPath, DpiStrategy.WriteBat(PlanFor(i), rootDir, TempName));

                        var applied = preferService
                            ? Services.ServiceOps.InstallStrategyAsService(rootDir, TempName)
                            : Services.ServiceOps.LaunchStrategyProcess(rootDir, TempName);

                        if (applied != null && applied.ok)
                        {
                            // The service needs a moment to pick the new binPath
                            // up before the probe means anything.
                            Thread.Sleep(3000);
                            var reach = Tester.CheckReachability(rootDir, 6);
                            if (reach.groups != null)
                            {
                                step.discord = reach.groups.discord != null ? reach.groups.discord.okTargets : 0;
                                step.youtube = reach.groups.youtube != null ? reach.groups.youtube.okTargets : 0;
                                step.ok = Tester.IsWorking(reach.groups);
                                step.detail = step.ok
                                    ? "Discord и YouTube отвечают"
                                    : "Discord: " + step.discord + ", YouTube: " + step.youtube;
                            }
                        }
                        else
                        {
                            step.detail = "не удалось применить: "
                                + (applied == null ? "?" : (applied.reason ?? applied.message ?? "?"));
                        }
                    }
                    catch (Exception ex)
                    {
                        step.detail = "ошибка: " + ex.Message;
                    }

                    outcome.steps.Add(step);
                    if (log != null)
                        log("autotune " + (i + 1) + "/" + Candidates.Length + ": "
                            + (step.ok ? "РАБОТАЕТ" : "нет") + " — " + step.label);

                    if (step.ok && shouldStop != null && shouldStop(outcome.steps)) break;
                }
            }
            finally
            {
                outcome.winner = AutoTuner.Best(outcome.steps);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

                if (outcome.winner != null)
                {
                    // Keep the winner as a permanent, editable strategy.
                    try
                    {
                        string name = WinnerPrefix + " " + DateTime.Now.ToString("HHmm") + ").bat";
                        string winnerPath = Path.Combine(rootDir, name);
                        var idx = Candidates.ToList().FindIndex(
                            c => CandidateLabel(Candidates.ToList().IndexOf(c)) == outcome.winner.label);
                        File.WriteAllText(winnerPath, DpiStrategy.WriteBat(PlanFor(Math.Max(0, idx)), rootDir, name));
                        outcome.winnerFile = name;
                        if (preferService)
                            Services.ServiceOps.InstallStrategyAsService(rootDir, name);
                        else
                            Services.ServiceOps.LaunchStrategyProcess(rootDir, name);
                        Services.ServiceOps.WriteServiceStrategy(name);
                        if (log != null) log("autotune: сохранена рабочая стратегия " + name);
                    }
                    catch (Exception ex)
                    {
                        if (log != null) log("autotune: не удалось сохранить победителя: " + ex.Message);
                    }
                }
                else if (!outcome.aborted && !string.IsNullOrEmpty(previous))
                {
                    // Nothing worked: leave the machine as we found it.
                    try
                    {
                        if (preferService) Services.ServiceOps.InstallStrategyAsService(rootDir, previous);
                        else Services.ServiceOps.LaunchStrategyProcess(rootDir, previous);
                        Services.ServiceOps.WriteServiceStrategy(previous);
                        outcome.restoredTo = previous;
                    }
                    catch { }
                }
                else
                {
                    outcome.restoredTo = previous;
                }
            }

            return outcome;
        }

        /// <summary>The best candidate, or null when nothing worked.</summary>
        public static TuneStepResult Best(List<TuneStepResult> results)
        {
            if (results == null || results.Count == 0) return null;
            return results.Where(r => r.ok).OrderByDescending(r => r.score).FirstOrDefault();
        }
    }
}