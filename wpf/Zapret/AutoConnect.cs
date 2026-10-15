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
            var last = LastWinner();
            if (last != null) candidates.Add(last);
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
                // one short probe is the difference between "on" and "on but
                // still blocked".
                var verify = Tester.VerifyStrategy(rootDir, attempts: 1, timeoutSec: 5);
                if (verify.ok)
                {
                    Config.Set(KeyLastWinner, file);
                    Config.Set("processStrategyName", Path.GetFileName(file));
                    RememberWorking(file);
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

        public static ConnectResult Connect(string rootDir, Action<string> log, Action<Tester.StrategyRunResult, int, int> progress)
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