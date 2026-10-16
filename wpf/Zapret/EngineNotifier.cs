using System;
using System.Threading;
using System.Windows.Threading;
using Ydp.Services;

namespace Ydp.Zapret
{
    /// <summary>
    /// Watches the zapret engine for new releases. The panel already tracked
    /// its own updates; the engine was only checkable by opening the Updates
    /// tab. This raises a tray balloon when a newer zapret is out, so a working
    /// panel can be kept current without hunting.
    ///
    /// Every 6 hours on purpose: the GitHub releases API is rate-limited, and
    /// an engine release does not appear by the minute.
    /// </summary>
    public static class EngineNotifier
    {
        private static DispatcherTimer _timer;
        private static int _checking;
        private static string _lastNotified;

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _timer.Tick += (s, e) => Check();
            _timer.Start();
            // First check shortly after launch, off the UI thread.
            var t = new System.Threading.Thread(() =>
            {
                Thread.Sleep(8000);
                Check();
            })
            { IsBackground = true, Name = "EngineNotifier" };
            t.Start();
        }

        public static void Check()
        {
            if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
            try
            {
                var root = Status.ResolveRoot(Config.Get("zapretRoot", null));
                if (string.IsNullOrEmpty(root)) return;

                var latest = Engine.LatestRelease();
                if (latest == null || string.IsNullOrEmpty(latest.tag)) return;
                var installed = Engine.InstalledVersion(root);
                if (IsNewer(latest.tag, installed))
                {
                    if (latest.tag != _lastNotified)
                    {
                        _lastNotified = latest.tag;
                        string what = string.IsNullOrEmpty(installed) ? "установлен не тот движок" : "у вас " + installed;
                        App.Tray?.Notify("Доступно обновление zapret — " + latest.tag,
                            "Сейчас " + what + ". Откройте панель → «Обновления» → «Обновить zapret».");
                        Logger.Info("Доступно обновление движка zapret: " + what + " → " + latest.tag, "engine");
                    }
                }
                else
                {
                    _lastNotified = null; // a manual update cleared the banner
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Проверка обновлений zapret не удалась: " + ex.Message, "engine");
            }
            finally { Interlocked.Exchange(ref _checking, 0); }
        }

        /// <summary>True when <paramref name="latest"/> is a newer version than
        /// <paramref name="installed"/>. Non-numeric tags compare as strings.</summary>
        private static bool IsNewer(string latest, string installed)
        {
            if (string.IsNullOrWhiteSpace(installed)) return true;
            var a = Parse(latest);
            var b = Parse(installed);
            if (a == null || b == null) return false;
            return a > b;
        }

        private static Version Parse(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim().TrimStart('v', 'V');
            int dash = v.IndexOfAny(new[] { '-', ' ' });
            if (dash > 0) v = v.Substring(0, dash);
            Version ver;
            return Version.TryParse(v, out ver) ? ver : null;
        }
    }
}
