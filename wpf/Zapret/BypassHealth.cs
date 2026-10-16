using System;
using System.Threading;
using Ydp.Services;

namespace Ydp.Zapret
{
    /// <summary>
    /// The single source of truth for "is the bypass actually working right
    /// now". The old "Обход включён" badge only reflected that the Windows
    /// service was RUNNING, which is not the same as Discord/YouTube actually
    /// loading. One probe feeds the dashboard verdict, the manual check button
    /// and the tray icon colour, so they can never disagree.
    /// </summary>
    public sealed class HealthResult
    {
        public bool bypassOn;      // a strategy is installed/running
        public bool ok;            // both Discord and YouTube answer
        public bool discord;
        public bool youtube;
        public string verdict;     // human-readable, per target

        /// <summary>ok → green, on-but-not-ok → red, off → grey.</summary>
        public string State { get { return !bypassOn ? "off" : (ok ? "ok" : "err"); } }
    }

    public static class BypassHealth
    {
        private static int _probing;

        /// <summary>Last known result; null before the first probe.</summary>
        public static HealthResult Last { get; private set; }

        /// <summary>Raised (any thread) whenever a fresh result is available.</summary>
        public static event Action<HealthResult> Changed;

        /// <summary>
        /// Probe Discord/YouTube with the current strategy. Returns null when a
        /// probe is already in flight (the caller can just read <see cref="Last"/>).
        /// </summary>
        public static HealthResult Probe(string root, int timeoutSec = 6)
        {
            if (string.IsNullOrEmpty(root)) return null;
            if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0) return null;
            try
            {
                var st = Status.GetStatus(root);
                var res = new HealthResult { bypassOn = st.activeMode != "off" };
                if (res.bypassOn)
                {
                    var check = Tester.CheckReachability(root, timeoutSec);
                    var g = check.groups;
                    res.discord = g != null && g.discord != null && g.discord.works;
                    res.youtube = g != null && g.youtube != null && g.youtube.state == "ok";
                    res.ok = res.discord && res.youtube;
                }
                res.verdict = Describe(res);
                Last = res;
                try { App.Tray?.SetState(res.State, res.verdict); } catch { }
                try { Changed?.Invoke(res); } catch { }
                Logger.Info("Проверка обхода: " + res.verdict, "health");
                return res;
            }
            finally { Interlocked.Exchange(ref _probing, 0); }
        }

        /// <summary>Record "bypass is off" without probing (no need to hit the net).</summary>
        public static void MarkOff()
        {
            if (Last != null && !Last.bypassOn) return;
            var res = new HealthResult { bypassOn = false, verdict = "Обход выключен" };
            Last = res;
            try { App.Tray?.SetState("off", res.verdict); } catch { }
            try { Changed?.Invoke(res); } catch { }
        }

        public static string Describe(HealthResult r)
        {
            if (r == null) return "Обход ещё не проверялся";
            if (!r.bypassOn) return "Обход выключен — включите тумблер";
            if (r.ok) return "Обход работает — Discord и YouTube отвечают";
            if (!r.discord && !r.youtube) return "Обход не работает — не отвечают ни Discord, ни YouTube";
            if (!r.discord) return "Обход частично: Discord не отвечает, YouTube отвечает";
            return "Обход частично: YouTube не отвечает, Discord отвечает";
        }
    }
}
