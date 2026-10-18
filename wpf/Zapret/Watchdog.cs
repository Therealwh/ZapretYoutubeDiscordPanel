using System;
using System.Net.NetworkInformation;
using System.Threading;
using Ydp.Services;

namespace Ydp.Zapret
{
    /// <summary>
    /// Bypass health monitor: quietly checks, while the bypass is supposed to be
    /// on, that Discord/YouTube actually answer.
    ///
    /// Two independent behaviours:
    ///  - Notify (on by default): a tray balloon when the bypass is up but the
    ///    sites stopped answering, so the user is not left guessing why Discord
    ///    will not load.
    ///  - Recover (opt-in, off by default): automatically re-connect — remembered
    ///    strategy first, then a full search.
    ///
    /// Also re-checks immediately when Windows reports the network changed
    /// (switching Wi-Fi/ethernet can leave a working strategy stopped).
    /// </summary>
    public static class BypassWatchdog
    {
        public const string KeyEnabled = "watchdogEnabled";        // auto-recover
        public const string KeyNotify = "watchdogNotify";          // tray notification
        private const int IntervalMs = 45000;
        // 4 consecutive failures (~3 min) before touching the connection.
        // It was 2 (~90 s), which was short enough that a transient network
        // blip made the panel reconnect a perfectly working bypass and cut
        // Telegram for everyone on the machine. The health probe now also runs
        // every interval just to tint the tray icon, so the streak sees far more
        // probes than before and has to be correspondingly more patient.
        private const int RequiredFails = 4;

        private static Thread _thread;
        private static volatile bool _stop;
        private static int _recovering;
        private static int _fails;
        private static int _notified;                             // one balloon per outage

        /// <summary>Auto-reconnect when the bypass stops working (off by default).</summary>
        public static bool Enabled
        {
            get { return string.Equals(Config.Get(KeyEnabled, "off"), "on", StringComparison.OrdinalIgnoreCase); }
            set { Config.Set(KeyEnabled, value ? "on" : "off"); }
        }

        /// <summary>Show a tray notification when the bypass is up but not working (on by default).</summary>
        public static bool Notify
        {
            get { return !string.Equals(Config.Get(KeyNotify, "on"), "off", StringComparison.OrdinalIgnoreCase); }
            set { Config.Set(KeyNotify, value ? "on" : "off"); }
        }

        public static void Start()
        {
            // _thread was left set by a previous Stop(), so a restart would
            // silently no-op and the panel would lose its health monitor until
            // restart. Clear it so Start really starts.
            _thread = null;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "BypassWatchdog" };
            _thread.Start();
            // A new network (different Wi-Fi/ethernet) can leave a working strategy
            // stopped — check as soon as Windows says the network changed.
            NetworkChange.NetworkAddressChanged += (s, e) =>
            {
                Logger.Info("Сеть изменилась — проверяю обход", "watchdog");
                // Let the new network settle before probing.
                var t = new Thread(() => { Thread.Sleep(6000); CheckNow("смена сети"); })
                { IsBackground = true, Name = "BypassWatchdogNet" };
                t.Start();
            };
        }

        public static void Stop()
        {
            _stop = true;
        }

        /// <summary>Run a health check right now, off the calling thread.</summary>
        public static void CheckNow(string reason)
        {
            var t = new Thread(() => { try { Tick(reason); } catch { } })
            { IsBackground = true, Name = "BypassWatchdogNow" };
            t.Start();
        }

        private static void Loop()
        {
            while (!_stop)
            {
                try { Thread.Sleep(IntervalMs); } catch { return; }
                if (_stop) break;
                try { Tick("периодическая проверка"); }
                catch (Exception ex) { Logger.Warn("Watchdog: ошибка проверки: " + ex.Message, "watchdog"); }
            }
        }

        private static void Tick(string reason)
        {
            // Always probe: this is what keeps the tray icon and the dashboard
            // verdict current. Whether we notify and whether we auto-recover are
            // separate, opt-in decisions made further down.
            // One action at a time; a recovery can take minutes.
            if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0) return;
            try
            {
                var root = Status.ResolveRoot(Config.Get("zapretRoot", null));
                if (string.IsNullOrEmpty(root)) return;

                var st = Status.GetStatus(root);
                // Nothing to protect while the bypass is off; reflect that in the tray.
                if (st.activeMode == "off") { BypassHealth.MarkOff(); Reset(); return; }

                // Stay out of the way while a test or a connect is in progress:
                // their transient reachability is not a dead bypass.
                if (Tester.SweepActive || AutoConnect.IsConnecting) { Reset(); return; }

                // One probe feeds the tray icon and this decision alike.
                // Probe returns null when another probe is already in flight (the
                // manual "Проверить обход" button, another watchdog tick). That is
                // NOT evidence the bypass works: counting it as healthy reset the
                // failure streak and could mask a real outage forever, so leave the
                // streak untouched and try again on the next tick.
                var health = BypassHealth.Probe(root, 4);
                if (health == null) return;
                if (health.ok) { Reset(); return; }

                int fails = Interlocked.Increment(ref _fails);
                if (fails < RequiredFails)
                {
                    Logger.Info("Обход включён, но цели не отвечают (" + fails + "/" + RequiredFails + ", " + reason + ")", "watchdog");
                    return;
                }

                // Broken. Tell the user once…
                if (Notify && Interlocked.CompareExchange(ref _notified, 1, 0) == 0)
                {
                    Logger.Warn("Обход включён, но Discord/YouTube не отвечают", "watchdog");
                    App.Tray?.Notify("Обход не работает",
                        "Discord или YouTube не отвечают, хотя обход включён. Проверьте сеть или переподключите обход.");
                }

                // …and, if the user opted in, actually recover.
                Interlocked.Exchange(ref _fails, 0);
                if (!Enabled) return;
                Logger.Warn("Автовосстановление: переподключаю (" + reason + ")", "watchdog");
                var res = AutoConnect.Connect(root, m => Logger.Info("Автовосстановление: " + m, "watchdog"), null, null);
                Logger.Info(res.ok
                    ? "Автовосстановление: переподключился на " + res.strategy
                    : "Автовосстановление: не удалось (" + (res.reason ?? "ошибка") + ")", "watchdog");
                Reset();
            }
            finally { Interlocked.Exchange(ref _recovering, 0); }
        }

        private static void Reset()
        {
            Interlocked.Exchange(ref _fails, 0);
            Interlocked.Exchange(ref _notified, 0);
        }
    }
}
