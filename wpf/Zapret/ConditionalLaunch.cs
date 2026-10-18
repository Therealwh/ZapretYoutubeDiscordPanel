// Conditional launch: user rules of the form
//   when <process>.exe is running  ->  make sure strategy <X> is active
//   when <process>.exe has exited  ->  stop the bypass (optional)
// A single background watcher evaluates rules every few seconds. Zero UI
// frameworks: rules live in config.json as "rule:<n>" entries.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Ydp.Services;

namespace Ydp.Zapret
{
    public static class ConditionalLaunch
    {
        public class Rule
        {
            public string process;      // "steam.exe" (case-insensitive, no path)
            public string strategyFile; // full path or name relative to root
            public bool stopWhenClosed; // stop the bypass when the process exits
        }

        private static Timer _timer;
        private static readonly object _lock = new object();
        private static volatile bool _evaluating;

        public static string KeyEnabled = "condLaunchEnabled";
        public static string KeyRules = "condLaunchRules"; // "proc1|strategy1|1;proc2|strategy2|0"

        public static List<Rule> ReadRules()
        {
            var raw = Config.Get(KeyRules, null);
            var list = new List<Rule>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var chunk in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = chunk.Split('|');
                if (parts.Length < 2) continue;
                list.Add(new Rule
                {
                    process = (parts[0] ?? "").Trim().ToLowerInvariant(),
                    strategyFile = (parts[1] ?? "").Trim(),
                    stopWhenClosed = parts.Length > 2 && parts[2].Trim() == "1",
                });
            }
            return list;
        }

        public static void WriteRules(IEnumerable<Rule> rules)
        {
            var chunks = (rules ?? Enumerable.Empty<Rule>())
                .Where(r => !string.IsNullOrWhiteSpace(r.process) && !string.IsNullOrWhiteSpace(r.strategyFile))
                .Select(r => r.process.Trim().ToLowerInvariant() + "|" + r.strategyFile.Trim() + "|" + (r.stopWhenClosed ? "1" : "0"));
            Config.Set(KeyRules, string.Join(";", chunks));
        }

        public static bool IsEnabled()
        {
            return !string.Equals(Config.Get(KeyEnabled, "false"), "false", StringComparison.OrdinalIgnoreCase);
        }

        public static void SetEnabled(bool enabled)
        {
            Config.Set(KeyEnabled, enabled ? "true" : "false");
            if (enabled) EnsureStarted();
        }

        /// <summary>Is any of the named processes running right now?</summary>
        public static bool IsProcessRunning(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            var want = processName.Trim().ToLowerInvariant();
            if (!want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) want += ".exe";
            try
            {
                // GetProcessesByName takes the name WITHOUT ".exe"
                var stem = want.Substring(0, want.Length - 4);
                return Process.GetProcessesByName(stem).Length > 0;
            }
            catch { return false; }
        }

        public static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_timer != null) return;
                // 5 s cadence: fast enough to feel instant, slow enough to cost nothing.
                _timer = new Timer(_ => Evaluate(), null, 5000, 5000);
            }
        }

        /// <summary>One watcher pass. Safe to call from tests directly.</summary>
        public static void Evaluate()
        {
            if (_evaluating) return;
            if (!IsEnabled()) return;
            var rules = ReadRules();
            if (rules.Count == 0) return;

            lock (_lock)
            {
                if (_evaluating) return;
                _evaluating = true;
            }
            try
            {
                var root = Status.ResolveRoot(Config.Get("zapretRoot", null));
                if (string.IsNullOrEmpty(root)) return;

                foreach (var rule in rules)
                {
                    var running = IsProcessRunning(rule.process);
                    if (running)
                    {
                        // Already bypassing with the wanted strategy? Leave it alone.
                        var current = Config.Get("processStrategyName", null);
                        var want = System.IO.Path.GetFileNameWithoutExtension(rule.strategyFile);
                        var active = ServiceOps.IsWinwsRunning()
                                     && current != null
                                     && string.Equals(current, want, StringComparison.OrdinalIgnoreCase);
                        if (!active)
                        {
                            Logger.Info("Условный запуск: " + rule.process + " работает → стратегия " + want, "cond");
                            if (string.Equals(Config.Get(AutoConnect.KeyPreferService, "true"), "true", StringComparison.OrdinalIgnoreCase))
                                ServiceOps.InstallStrategyAsService(root, rule.strategyFile);
                            else
                                ServiceOps.LaunchStrategyProcess(root, rule.strategyFile);
                        }
                    }
                    else if (rule.stopWhenClosed && ServiceOps.IsWinwsRunning())
                    {
                        // Only stop if the bypass currently runs THIS rule's strategy:
                        // do not tear down something the user switched to manually.
                        var current = Config.Get("processStrategyName", null);
                        var want = System.IO.Path.GetFileNameWithoutExtension(rule.strategyFile);
                        if (current != null && string.Equals(current, want, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Info("Условный запуск: " + rule.process + " закрыт → остановка обхода", "cond");
                            ServiceOps.ResetBypass();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Условный запуск: " + ex.Message, "cond");
            }
            finally
            {
                lock (_lock) { _evaluating = false; }
            }
        }
    }
}

