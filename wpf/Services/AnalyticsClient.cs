using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ydp.Services
{
    /// <summary>
    /// Optional daily usage analytics. Enabled by default; users can turn it off in Settings → Metrics.
    /// One installation can create at most one launch row per Moscow calendar day;
    /// the server enforces the same rule with a unique database key.
    /// </summary>
    public static class AnalyticsClient
    {
        private const string Endpoint = "https://status.zapretyoutubediscordpanel.top/api/event.php";
        private const string AnalyticsEnabledKey = "analyticsEnabled";
        private const string InstallIdKey = "analyticsInstallId";
        private const string LastSentDayKey = "analyticsLastSentMskDay";
        private static int _sending;
        private static readonly object TimerGate = new object();
        private static Timer _dailyTimer;

        public static bool IsEnabled
        {
            get { return string.Equals(Config.Get(AnalyticsEnabledKey, "yes"), "yes", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// Start daily activity tracking. A one-shot timer checks at Moscow midnight
        /// and retries every five minutes only while today's event has not been accepted.
        /// It remains alive while the app is minimized to the tray.
        /// </summary>
        public static void StartTracking()
        {
            if (!IsEnabled)
            {
                StopTracking();
                return;
            }

            TrackDailyLaunch();
            ScheduleNextCheck();
        }

        public static void SetEnabled(bool enabled)
        {
            Config.Set(AnalyticsEnabledKey, enabled ? "yes" : "no");
            if (enabled) StartTracking();
            else StopTracking();
        }

        private static void StopTracking()
        {
            lock (TimerGate)
            {
                if (_dailyTimer != null)
                    _dailyTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private static void ScheduleNextCheck()
        {
            if (!IsEnabled) return;

            string today = MoscowToday();
            bool counted = string.Equals(
                Config.Get(LastSentDayKey, string.Empty), today, StringComparison.Ordinal);

            // Once today's event has been accepted, wait for the next Moscow midnight.
            // If it failed (or we're crossing midnight), retry in five minutes.
            TimeSpan due = counted ? TimeUntilNextMoscowMidnight() : TimeSpan.FromMinutes(5);
            if (due < TimeSpan.FromSeconds(1)) due = TimeSpan.FromSeconds(1);

            lock (TimerGate)
            {
                if (_dailyTimer == null)
                    _dailyTimer = new Timer(DailyTimerTick, null, due, Timeout.InfiniteTimeSpan);
                else
                    _dailyTimer.Change(due, Timeout.InfiniteTimeSpan);
            }
        }

        private static void DailyTimerTick(object state)
        {
            if (!IsEnabled)
            {
                StopTracking();
                return;
            }

            TrackDailyLaunch();
            ScheduleNextCheck();
        }

        private static TimeSpan TimeUntilNextMoscowMidnight()
        {
            DateTime utcNow = DateTime.UtcNow;
            TimeZoneInfo zone = MoscowTimeZone();
            DateTime localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
            DateTime nextLocalMidnight = DateTime.SpecifyKind(
                localNow.Date.AddDays(1), DateTimeKind.Unspecified);
            DateTime nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextLocalMidnight, zone);
            TimeSpan remaining = nextUtc - utcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
        }

        /// <summary>
        /// Send one event per Moscow calendar day. The local date marker only
        /// suppresses redundant requests; the server's UNIQUE(install_id, date)
        /// constraint is the authoritative deduplication mechanism.
        /// </summary>
        public static void TrackDailyLaunch()
        {
            if (!IsEnabled) return;

            string moscowDay = MoscowToday();
            if (string.Equals(Config.Get(LastSentDayKey, string.Empty), moscowDay, StringComparison.Ordinal))
                return;

            if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;

            Task.Run(() =>
            {
                try
                {
                    SendDailyLaunch(moscowDay);
                }
                catch
                {
                    // Analytics must never affect startup or panel functionality.
                    // Do not set the day marker: a later normal launch can retry.
                }
                finally
                {
                    Interlocked.Exchange(ref _sending, 0);
                }
            });
        }

        private static void SendDailyLaunch(string clientDay)
        {
            string installId = GetInstallId();
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string body = "{\"install_id\":\"" + installId + "\",\"app_version\":\"" + EscapeJson(version) + "\"}";
            byte[] bytes = Encoding.UTF8.GetBytes(body);

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(Endpoint);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Accept = "application/json";
            request.UserAgent = "ZapretYoutubeDiscordPanel-Analytics";
            request.Timeout = 5000;
            request.ReadWriteTimeout = 5000;
            request.ContentLength = bytes.Length;

            using (var stream = request.GetRequestStream())
                stream.Write(bytes, 0, bytes.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                int status = (int)response.StatusCode;
                if (status < 200 || status >= 300)
                    throw new WebException("Analytics endpoint returned HTTP " + status);
            }

            // Save only after the server accepted the event. If the request fails,
            // the same day can be retried on the next ordinary app opening.
            Config.Set(LastSentDayKey, clientDay);
        }

        private static string GetInstallId()
        {
            string value = Config.Get(InstallIdKey, string.Empty);
            Guid parsed;
            if (Guid.TryParse(value, out parsed))
                return parsed.ToString("D");

            value = Guid.NewGuid().ToString("D");
            Config.Set(InstallIdKey, value);
            return value;
        }

        internal static string MoscowDateKey(DateTime utcNow)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
                utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            TimeZoneInfo zone = MoscowTimeZone();
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static TimeZoneInfo MoscowTimeZone()
        {
            try
            {
                // Windows time-zone identifier for Moscow (UTC+03:00).
                return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
            }
            catch
            {
                // Fixed UTC+03:00 fallback for Windows images without that zone entry.
                return TimeZoneInfo.CreateCustomTimeZone("Moscow UTC+3", TimeSpan.FromHours(3),
                    "Moscow UTC+3", "Moscow UTC+3");
            }
        }

        private static string MoscowToday()
        {
            return MoscowDateKey(DateTime.UtcNow);
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
