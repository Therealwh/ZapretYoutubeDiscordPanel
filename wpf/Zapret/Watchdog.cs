using System;
using System.Threading;
using Ydp.Services;

namespace Ydp.Zapret
{
    /// <summary>
    /// Bypass watchdog: quietly checks, while the bypass is supposed to be on,
    /// that Discord/YouTube actually answer. If they stop answering for a few
    /// consecutive checks it re-connects on its own — remembered strategy
    /// first, and only then the full search — and writes what it did to the log.
    ///
    /// Off by default: re-connecting on its own changes running state, so the
    /// user opts in from Settings.
    /// </summary>
    public static class BypassWatchdog
    {
        public const string KeyEnabled = "watchdogEnabled";
        private const int IntervalMs = 45000;
        private const int RequiredFails = 2;

        private static Thread _thread;
        private static volatile bool _stop;
        private static int _recovering;
        private static int _fails;

        public static bool Enabled
        {
            get { return string.Equals(Config.Get(KeyEnabled, "off"), "on", StringComparison.OrdinalIgnoreCase); }
            set { Config.Set(KeyEnabled, value ? "on" : "off"); }
        }

        public static void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "BypassWatchdog" };
            _thread.Start();
        }

        public static void Stop()
        {
            _stop = true;
        }

        private static void Loop()
        {
            while (!_stop)
            {
                try { Thread.Sleep(IntervalMs); } catch { return; }
                if (_stop) break;
                try { Tick(); }
                catch (Exception ex) { Logger.Warn("Watchdog: ошибка проверки: " + ex.Message, "watchdog"); }
            }
        }

        private static void Tick()
        {
            if (!Enabled) return;
            // One recovery at a time; the recovery itself can take minutes.
            if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0) return;
            try
            {
                var root = Status.ResolveRoot(Config.Get("zapretRoot", null));
                if (string.IsNullOrEmpty(root)) return;

                var st = Status.GetStatus(root);
                // Nothing to protect while the bypass is off.
                if (st.activeMode == "off") { Interlocked.Exchange(ref _fails, 0); return; }

                // Stay out of the way while a test or a connect is in progress:
                // their transient reachability is not a dead bypass.
                if (Tester.SweepActive || AutoConnect.IsConnecting) { Interlocked.Exchange(ref _fails, 0); return; }

                var check = Tester.CheckReachability(root, 4);
                if (Tester.IsWorking(check.groups)) { Interlocked.Exchange(ref _fails, 0); return; }

                if (Interlocked.Increment(ref _fails) < RequiredFails)
                {
                    Logger.Info("Watchdog: обход включён, но цели не отвечают (" + Interlocked.CompareExchange(ref _fails, 0, 0) + "/" + RequiredFails + ")", "watchdog");
                    return;
                }

                Interlocked.Exchange(ref _fails, 0);
                Logger.Warn("Watchdog: обход включён, но Discord/YouTube не отвечают — переподключаю", "watchdog");
                var res = AutoConnect.Connect(root, m => Logger.Info("Watchdog: " + m, "watchdog"), null, null);
                Logger.Info(res.ok
                    ? "Watchdog: переподключился на " + res.strategy
                    : "Watchdog: переподключение не удалось (" + (res.reason ?? "ошибка") + ")", "watchdog");
            }
            finally { Interlocked.Exchange(ref _recovering, 0); }
        }
    }
}
