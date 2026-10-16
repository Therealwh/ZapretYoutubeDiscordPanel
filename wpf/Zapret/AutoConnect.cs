using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using Ydp.Services;

namespace Ydp.Zapret
{
    public sealed class ConnectResult
    {
        public bool ok { get; set; }
        public bool already { get; set; }
        public bool restored { get; set; }
        public bool fallback { get; set; }
        public string strategy { get; set; }
        public string mode { get; set; }
        public string reason { get; set; }
        public int candidatesTried { get; set; }
    }

    /// <summary>
    /// "Подключить рабочий обход" + memory of what worked.
    ///
    /// Order of business:
    ///   1. If something is already running, verify it TWICE before touching it —
    ///      a single flaky curl probe must not kill a working bypass.
    ///   2. Otherwise search the strategies that worked before on this machine
    ///      first, then the rest, and connect the first where Discord AND
    ///      YouTube answer.
    ///   3. If nothing passes, restore the previous winner rather than leaving
    ///      the user with no bypass at all.
    /// Every strategy that works is remembered, which is what makes the boot-time
    /// auto-apply possible.
    /// </summary>
    public static class AutoConnect
    {
        public const string KeyLastWinner = "lastStrategyFile";
        public const string KeyKnownWorking = "knownWorkingFiles";
        public const string KeyPreferService = "preferServiceMode";
        public const string KeyAutoApply = "autoApplyOnLogin";
        public const int MaxRemembered = 5;

        public static List<string> KnownWorking()
        {
            string raw = Config.Get(KeyKnownWorking, string.Empty);
            var files = raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
            return files.Where(File.Exists).ToList();
        }

        public static void RememberWorking(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return;
            var list = KnownWorking();
            list.RemoveAll(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, file);
            if (list.Count > MaxRemembered) list.RemoveRange(MaxRemembered, list.Count - MaxRemembered);
            Config.Set(KeyKnownWorking, string.Join("|", list));
        }

        public static string LastWinner()
        {
            var file = Config.Get(KeyLastWinner, null);
            return !string.IsNullOrWhiteSpace(file) && File.Exists(file) ? file : null;
        }

        // ---- last-known-good timestamp, per strategy ------------------------
        // The bundled tester is not deterministic, so a strategy that passed
        // yesterday may fail today. Showing "last time this was seen working"
        // lets the user pick a likely winner without re-running the whole sweep.
        // Keyed by the .bat file name so it survives moves of the zapret folder.

        public const string LastGoodPrefix = "lastGood:";

        /// <summary>Stamp a strategy as "working right now".</summary>
        public static void MarkGoodNow(string strategyFileOrName)
        {
            string name = NormalizeStrategyName(strategyFileOrName);
            if (string.IsNullOrEmpty(name)) return;
            Config.Set(LastGoodPrefix + name, DateTime.UtcNow.ToString("o"));
        }

        /// <summary>When a strategy last passed a check, or null if never.</summary>
        public static DateTime? LastGood(string strategyFileOrName)
        {
            string name = NormalizeStrategyName(strategyFileOrName);
            if (string.IsNullOrEmpty(name)) return null;
            string raw = Config.Get(LastGoodPrefix + name, null);
            DateTime parsed;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTime.TryParse(raw, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out parsed))
                return parsed.ToLocalTime();
            return null;
        }

        /// <summary>
        /// Russian relative label for the strategy list: "только что",
        /// "5 мин назад", "вчера", "3 дн. назад", "12 окт".
        /// </summary>
        public static string LastGoodLabel(string strategyFileOrName)
        {
            var when = LastGood(strategyFileOrName);
            if (when == null) return string.Empty;
            var span = DateTime.Now - when.Value;
            if (span < TimeSpan.FromMinutes(1)) return "только что";
            if (span < TimeSpan.FromHours(1)) return ((int)span.TotalMinutes) + " мин назад";
            if (span < TimeSpan.FromHours(24)) return ((int)span.TotalHours) + " ч назад";
            if (span < TimeSpan.FromDays(2)) return "вчера";
            if (span < TimeSpan.FromDays(30)) return ((int)span.TotalDays) + " дн. назад";
            return when.Value.ToString("d MMM");
        }

        // ---- pinned strategies ------------------------------------------------
        // A pinned strategy is one the user wants tried first whenever the
        // bypass is brought up — their own go-to config, regardless of what the
        // sweep happened to pick last. Stored as .bat names.

        public const string KeyPinned = "pinnedStrategies";

        public static List<string> Pinned()
        {
            string raw = Config.Get(KeyPinned, string.Empty);
            return raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        }

        public static bool IsPinned(string strategyName)
        {
            if (string.IsNullOrWhiteSpace(strategyName)) return false;
            return Pinned().Any(p => string.Equals(p, strategyName, StringComparison.OrdinalIgnoreCase));
        }

        public static void TogglePin(string strategyName)
        {
            if (string.IsNullOrWhiteSpace(strategyName)) return;
            var list = Pinned();
            if (list.Any(p => string.Equals(p, strategyName, StringComparison.OrdinalIgnoreCase)))
                list.RemoveAll(p => string.Equals(p, strategyName, StringComparison.OrdinalIgnoreCase));
            else
                list.Add(strategyName);
            Config.Set(KeyPinned, string.Join("|", list));
        }

        private static string NormalizeStrategyName(string fileOrName)
        {
            if (string.IsNullOrWhiteSpace(fileOrName)) return null;
            string n = Path.GetFileName(fileOrName.Trim());
            // strip a .bat/.cmd extension if present so a full path and a bare
            // name map to the same key
            string ext = Path.GetExtension(n);
            if (!string.IsNullOrEmpty(ext)
                && (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)))
                n = Path.GetFileNameWithoutExtension(n);
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }

        private static bool PreferService()
        {
            var value = Config.Get(KeyPreferService, "true");
            return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Bring the bypass up from a remembered strategy instead of re-running
        /// the whole search.
        ///
        /// The power switch on the dashboard used to call the full sweep on every
        /// ON, so a user with a perfectly good remembered strategy still watched
        /// the toggle stay off for minutes while all 22 configs were probed.
        /// This installs the remembered strategy (last winner first, then the
        /// other known-good files), confirms it with ONE quick probe, and returns
        /// as soon as it works. If nothing remembered works any more the bypass
        /// is torn down and null is returned, so the caller falls back to the
        /// full search.
        /// </summary>
        private static ConnectResult TryRememberedFirst(string rootDir, Action<string> log)
        {
            var candidates = new List<string>();
            // Pinned strategies go first: the user marked them as the ones to
            // try before anything else, ahead of the last winner.
            var pinned = Pinned();
            if (pinned.Count > 0)
            {
                foreach (var s in Strategy.ListStrategies(rootDir))
                {
                    if (pinned.Any(p => string.Equals(p, s.name, StringComparison.OrdinalIgnoreCase)))
                        candidates.Add(s.file);
                }
            }
            var last = LastWinner();
            if (last != null && !candidates.Contains(last, StringComparer.OrdinalIgnoreCase)) candidates.Add(last);
            foreach (var f in KnownWorking())
                if (!candidates.Contains(f, StringComparer.OrdinalIgnoreCase)) candidates.Add(f);
            if (candidates.Count == 0) return null;

            foreach (var file in candidates)
            {
                string label = Path.GetFileNameWithoutExtension(file);
                log?.Invoke("Пробую запомненную стратегию: " + label);
                var applied = PreferService()
                    ? ServiceOps.InstallStrategyAsService(rootDir, file)
                    : ServiceOps.LaunchStrategyProcess(rootDir, file);
                if (!applied.ok) continue;

                // Installed means winws came up, not that it actually bypasses;
                // one probe is the difference between "on" and "on but still
                // blocked". But a freshly started service is still loading its
                // lists: probing the instant it reports RUNNING made a perfectly
                // good remembered strategy look dead and sent the toggle into the
                // full search. Give it a beat, then probe patiently.
                System.Threading.Thread.Sleep(2000);
                var verify = Tester.VerifyStrategy(rootDir, attempts: 3, timeoutSec: 6);
                if (verify.ok)
                {
                    Config.Set(KeyLastWinner, file);
                    Config.Set("processStrategyName", Path.GetFileName(file));
                    RememberWorking(file);
                    MarkGoodNow(file);
                    return new ConnectResult
                    {
                        ok = true,
                        strategy = label,
                        mode = applied.mode,
                        candidatesTried = 1,
                    };
                }

                // This one no longer works on this network: drop it and keep
                // going, so a stale memory never leaves the bypass half-up.
                ServiceOps.KillWinws();
                ServiceOps.StopService();
                log?.Invoke(label + " больше не подходит — ищу лучше");
            }
            return null;
        }

        /// <summary>What to do when no remembered strategy passes its check.</summary>
        public enum RememberedFailureAction
        {
            /// <summary>Give the remembered strategies another chance.</summary>
            Retry,
            /// <summary>Stop trying and run the full search.</summary>
            Search,
            /// <summary>Leave the bypass off and end the attempt.</summary>
            Cancel,
        }

        public static ConnectResult Connect(string rootDir, Action<string> log, Action<Tester.StrategyRunResult, int, int> progress, Func<RememberedFailureAction> onRememberedFailure = null)
        {
            // One connect at a time. The background watchdog and a manual click
            // can otherwise race on the same service name: one stops the other's
            // winws and the user is left with no bypass.
            if (System.Threading.Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
            {
                log?.Invoke("Подключение уже выполняется — пропускаю повторный запуск");
                return new ConnectResult { ok = false, reason = "already_connecting" };
            }
            try { return ConnectCore(rootDir, log, progress, onRememberedFailure); }
            finally { System.Threading.Interlocked.Exchange(ref _connecting, 0); }
        }

        private static int _connecting;

        /// <summary>True while a connect (manual or watchdog) is in progress.</summary>
        public static bool IsConnecting { get { return System.Threading.Volatile.Read(ref _connecting) > 0; } }

        private static ConnectResult ConnectCore(string rootDir, Action<string> log, Action<Tester.StrategyRunResult, int, int> progress, Func<RememberedFailureAction> onRememberedFailure)
        {
            var result = new ConnectResult();

            // 1. Is what is running actually working?
            var status = Status.GetStatus(rootDir);
            if (status.activeMode != "off")
            {
                string currentName = Config.Get("processStrategyName", null)
                    ?? status.serviceStrategy
                    ?? "текущая";
                var verify = Tester.VerifyStrategy(rootDir, attempts: 2);
                if (verify.ok)
                {
                    var remembered = LastWinner();
                    if (remembered != null) RememberWorking(remembered);
                    MarkGoodNow(status.serviceStrategy ?? currentName);
                    return new ConnectResult { ok = true, already = true, strategy = currentName, mode = status.activeMode };
                }
                log?.Invoke($"Текущая стратегия \"{currentName}\" не подтвердилась за {verify.attempts} попытки — ищу лучше");
                ServiceOps.KillWinws();
                ServiceOps.StopService();
            }

            // 2. Try the remembered strategy FIRST, without running the sweep.
            // The power switch used to re-search every strategy on every ON,
            // even when a good one was already remembered - the switch sat
            // "off" for minutes while it probed all 22 configs. Apply the
            // remembered one directly, confirm it with a single quick probe,
            // and only fall through to the full search if it no longer works.
            var fast = TryRememberedFirst(rootDir, log);
            if (fast != null) return fast;

            // Every remembered strategy failed its quick check. The old code
            // dropped straight into the full search, which probes every config
            // and can sit "off" for minutes with no way out. When a UI is there
            // to ask, let the user decide: retry the memory, run the full
            // search, or cancel and stay off. With no prompt (headless paths)
            // this keeps the old behaviour and searches right away.
            //
            // "Повторить" re-runs the whole remembered set every time, so an
            // uncapped loop turned one press into an endless round through the
            // same strategies. Cap it: after a few fruitless retries, fall
            // through to the full search on our own.
            const int maxRetries = 2;
            int retries = 0;
            while (onRememberedFailure != null)
            {
                var action = onRememberedFailure();
                if (action == RememberedFailureAction.Cancel)
                    return new ConnectResult { ok = false, reason = "user_cancelled", candidatesTried = 0 };
                if (action == RememberedFailureAction.Search)
                    break; // fall through to the full search below

                if (retries++ >= maxRetries)
                {
                    log?.Invoke("Запомненная стратегия не сработала несколько раз подряд — запускаю полный поиск");
                    break;
                }

                // Retry: hand the remembered strategies another chance. If one
                // works now, the bypass is up and the search is skipped.
                var again = TryRememberedFirst(rootDir, log);
                if (again != null) return again;
            }

            // 3. Search, remembering what worked before.
            var known = KnownWorking();
            var sweep = Tester.RunStrategyTests(rootDir, known, stopOnFirst: true, onProgress: progress);

            if (!string.IsNullOrEmpty(sweep.winnerFile))
            {
                Config.Set(KeyLastWinner, sweep.winnerFile);
                Config.Set("processStrategyName", sweep.winner);
                RememberWorking(sweep.winnerFile);
                result.candidatesTried = sweep.results.Count;

                if (PreferService())
                {
                    var installed = ServiceOps.InstallStrategyAsService(rootDir, sweep.winnerFile);
                    if (installed.ok)
                    {
                        return new ConnectResult { ok = true, strategy = sweep.winner, mode = "service", candidatesTried = sweep.results.Count };
                    }
                    // The winning winws is ALREADY running as a process — keep it
                    // rather than reporting a failure and leaving no bypass.
                    if (Win.ProcessRunning("winws.exe"))
                    {
                        log?.Invoke($"Стратегия рабочая, но службу установить не удалось ({installed.reason}) — оставляю процесс");
                        return new ConnectResult { ok = true, strategy = sweep.winner, mode = "process-kept", reason = installed.reason, candidatesTried = sweep.results.Count };
                    }
                    return new ConnectResult { ok = false, strategy = sweep.winner, mode = "service", reason = installed.reason, candidatesTried = sweep.results.Count };
                }

                if (Win.ProcessRunning("winws.exe"))
                {
                    return new ConnectResult { ok = true, strategy = sweep.winner, mode = "process-kept", candidatesTried = sweep.results.Count };
                }
                var launched = ServiceOps.LaunchStrategyProcess(rootDir, sweep.winnerFile);
                return new ConnectResult { ok = launched.ok, strategy = sweep.winner, mode = "process", reason = launched.reason, candidatesTried = sweep.results.Count };
            }

            // 3. Nothing perfect: the best available strategy.
            var best = sweep.results.Where(r => !r.failed)
                .OrderByDescending(r => r.score.ok).FirstOrDefault();
            if (best != null)
            {
                Config.Set(KeyLastWinner, best.file);
                Config.Set("processStrategyName", best.name);
                RememberWorking(best.file);
                var applied = PreferService()
                    ? ServiceOps.InstallStrategyAsService(rootDir, best.file)
                    : ServiceOps.LaunchStrategyProcess(rootDir, best.file);
                if (!applied.ok && PreferService())
                {
                    var proc = ServiceOps.LaunchStrategyProcess(rootDir, best.file);
                    if (proc.ok) return new ConnectResult { ok = true, strategy = best.name, mode = "process", fallback = true, candidatesTried = sweep.results.Count };
                }
                return new ConnectResult { ok = applied.ok, strategy = best.name, fallback = true, reason = applied.reason, candidatesTried = sweep.results.Count };
            }

            // 4. Everything failed outright: bring back the previous winner.
            var previous = LastWinner();
            if (previous != null && !sweep.aborted)
            {
                log?.Invoke("Ничего не подошло — возвращаю прежний обход");
                var restored = PreferService()
                    ? ServiceOps.InstallStrategyAsService(rootDir, previous)
                    : ServiceOps.LaunchStrategyProcess(rootDir, previous);
                if (restored.ok)
                {
                    Config.Set("processStrategyName", Path.GetFileName(previous));
                    return new ConnectResult { ok = true, restored = true, strategy = Path.GetFileName(previous), candidatesTried = sweep.results.Count };
                }
            }
            return new ConnectResult { ok = false, reason = "none_works", candidatesTried = sweep.results.Count };
        }

        /// <summary>
        /// Boot/login path: no UI, no questions. Remembered strategy first, and
        /// the current bypass is only replaced when it actually fails.
        /// </summary>
        public static ConnectResult ApplyRememberedOnStartup(string rootDir, Action<string> log)
        {
            if (!AutoStart.AutoApplyEnabled()) return new ConnectResult { ok = false, reason = "auto_apply_off" };

            var status = Status.GetStatus(rootDir);
            if (status.activeMode != "off")
            {
                var verify = Tester.VerifyStrategy(rootDir, attempts: 1, timeoutSec: 5);
                if (verify.ok) return new ConnectResult { ok = true, already = true, strategy = Config.Get("processStrategyName", "текущая") };
            }

            var remembered = KnownWorking();
            if (remembered.Count == 0) return new ConnectResult { ok = false, reason = "nothing_remembered" };

            foreach (var file in remembered)
            {
                var applied = PreferService()
                    ? ServiceOps.InstallStrategyAsService(rootDir, file)
                    : ServiceOps.LaunchStrategyProcess(rootDir, file);
                if (applied.ok)
                {
                    log?.Invoke("Применена запомненная стратегия: " + Path.GetFileName(file));
                    return new ConnectResult { ok = true, strategy = Path.GetFileName(file), mode = applied.mode };
                }
            }
            return new ConnectResult { ok = false, reason = "remembered_failed" };
        }
    }

    /// <summary>
    /// Autostart plumbing. The zapret SERVICE itself is start=auto, so once a
    /// working strategy is installed as a service it comes up with Windows and
    /// the panel never has to run. This class covers the other case — the panel
    /// starting at login to re-apply the remembered strategy as a process, or
    /// simply to be there.
    /// </summary>
    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "YoutubeDiscordPanel";
        private const string FlagAutoApply = "--autoconnect";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Start the panel at login, optionally applying the remembered strategy first.</summary>
        public static bool SetEnabled(bool enabled, bool withAutoApply)
        {
            try
            {
                string exe = ExePath();
                if (exe == null) return false;
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return false;
                    if (enabled)
                    {
                        string value = "\"" + exe + "\"";
                        if (withAutoApply) value += " " + FlagAutoApply;
                        key.SetValue(ValueName, value);
                        Config.Set(AutoConnect.KeyAutoApply, withAutoApply ? "true" : "false");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                        Config.Set(AutoConnect.KeyAutoApply, "false");
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Make sure the login item points at THIS executable.
        ///
        /// The entry is written by the installer, which may have been run with a
        /// different folder than the one the panel ended up in. A stale entry is
        /// invisible in the UI but silently means the bypass never comes back
        /// after a reboot, so it is repaired here on every start.
        /// </summary>
        public static bool EnsureAutoStartEntry()
        {
            string exe = ExePath();
            if (string.IsNullOrEmpty(exe)) return false;
            string subKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            string current = Win.ReadRegistryString("HKCU", subKey, "YoutubeDiscordPanel");
            // Respect the user's opt-out: the installer DELETES this value when
            // autostart was declined at setup. Repairing a missing value here
            // resurrected the entry on every start, which is exactly how
            // "I unchecked autostart" turned into autostart being enabled.
            // Only an EXISTING entry pointing elsewhere gets repaired.
            if (string.IsNullOrEmpty(current)) return false;
            string wanted = "\"" + exe + "\" --autoconnect";
            if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return false;

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey))
                {
                    if (key == null) return false;
                    key.SetValue("YoutubeDiscordPanel", wanted);
                }
                Logger.Info("Запись автозапуска исправлена: " + (current ?? "не было") + " -> " + wanted, "autoconnect");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("Не удалось исправить автозапуск: " + ex.Message, "autoconnect");
                return false;
            }
        }

        public static bool AutoApplyEnabled()
        {
            // On by default: the whole point of the panel is that the bypass is
            // up after a reboot without anyone opening anything. A fresh install
            // used to come up in "panel only" mode, which looked like the
            // program had not done its job.
            return string.Equals(Config.Get(AutoConnect.KeyAutoApply, "true"), "true", StringComparison.OrdinalIgnoreCase);
        }

        public static string ExePath()
        {
            try
            {
                string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when the process was started by the login item.</summary>
        public static bool StartedWithAutoApply()
        {
            try
            {
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (string.Equals(arg, FlagAutoApply, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { /* ignore */ }
            return false;
        }
    }
}