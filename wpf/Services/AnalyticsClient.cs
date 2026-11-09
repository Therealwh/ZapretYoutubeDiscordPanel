using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Ydp.Services
{
    /// <summary>
    /// Optional, consent-based, privacy-minimal daily usage analytics.
    /// One installation can create at most one launch row per Moscow calendar day;
    /// the server enforces the same rule with a unique database key.
    /// </summary>
    public static class AnalyticsClient
    {
        private const string Endpoint = "https://status.zapretyoutubediscordpanel.top/api/event.php";
        private const string ConsentKey = "analyticsConsent";
        private const string InstallIdKey = "analyticsInstallId";
        private const string LastSentDayKey = "analyticsLastSentMskDay";
        private static int _sending;

        public static bool IsEnabled
        {
            get { return string.Equals(Config.Get(ConsentKey, "no"), "yes", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Ask once before sending anything. No is the safe default.</summary>
        public static void EnsureConsentAndTrack()
        {
            string consent = Config.Get(ConsentKey, string.Empty);
            if (!string.Equals(consent, "yes", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(consent, "no", StringComparison.OrdinalIgnoreCase))
            {
                var answer = MessageBox.Show(
                    "Разрешить отправку обезличенной статистики Zapret Panel?\n\n"
                    + "Передаются только случайный ID установки, версия программы и факт активности — "
                    + "не более одного раза за календарные сутки по московскому времени. "
                    + "Повторные открытия в тот же день не увеличивают счётчик.\n\n"
                    + "Имя компьютера, имя пользователя Windows, содержимое логов, посещаемые сайты "
                    + "и сетевой трафик не собираются. В базе статистики IP-адреса не сохраняются. "
                    + "Отказ не влияет на работу программы. Настройку можно изменить в разделе «Настройки».",
                    "Обезличенная статистика Zapret Panel",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information,
                    MessageBoxResult.No);
                consent = answer == MessageBoxResult.Yes ? "yes" : "no";
                Config.Set(ConsentKey, consent);
            }

            if (string.Equals(consent, "yes", StringComparison.OrdinalIgnoreCase))
                TrackDailyLaunch();
        }

        public static void SetEnabled(bool enabled)
        {
            Config.Set(ConsentKey, enabled ? "yes" : "no");
            if (enabled) TrackDailyLaunch();
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

        private static string MoscowToday()
        {
            DateTime utcNow = DateTime.UtcNow;
            try
            {
                // Windows time-zone identifier for Moscow (UTC+03:00).
                TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch
            {
                return utcNow.AddHours(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
