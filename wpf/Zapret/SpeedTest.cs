using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ydp.Zapret
{
    public sealed class SpeedResult
    {
        public bool ok { get; set; }
        public double mbps { get; set; }
        public double seconds { get; set; }
        public long bytes { get; set; }
        public string verdict { get; set; }
        public string url { get; set; }
        public string error { get; set; }
    }

    /// <summary>
    /// Measures download speed through whatever is currently routing traffic.
    ///
    /// The point is not the absolute number but the comparison: the same file is
    /// measured again with the bypass off, and if the numbers differ a lot the
    /// bypass is costing throughput even though Discord and YouTube open. That
    /// is the failure mode users cannot see today - "works" and "fast" are
    /// different questions and the panel only answered the first one.
    /// </summary>
    public static class SpeedTest
    {
        public const string ConfigKey = "speedTestUrl";
        public const string DefaultUrl =
            "https://speed.cloudflare.com/__down?bytes=20000000";

        public static string Url
        {
            get { return Config.Get(ConfigKey, DefaultUrl); }
            set { Config.Set(ConfigKey, value); }
        }

        public static SpeedResult Measure(int timeoutSec = 25)
        {
            string url = Url;
            var result = new SpeedResult { url = url };
            if (string.IsNullOrWhiteSpace(url))
            {
                result.error = "не задан адрес для замера";
                return result;
            }

            var sw = Stopwatch.StartNew();
            string outp, err;
            // -w speed_download is bytes/s averaged over the transfer; -o NUL keeps
            // nothing on disk. --http1.1 so the measurement does not depend on a
            // QUIC path the bypass may not cover.
            string args = string.Format(
                "-s -L --http1.1 -o NUL --max-time {0} -w \"%{{speed_download}}|%{{size_download}}\" \"{1}\"",
                timeoutSec, url);
            try
            {
                outp = Run("curl.exe", args, (timeoutSec + 8) * 1000, out err);
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
                return result;
            }
            sw.Stop();

            var m = Regex.Match(outp ?? string.Empty, @"^([0-9.]+)\|([0-9.]+)\s*$");
            if (!m.Success)
            {
                result.error = string.IsNullOrWhiteSpace(err) ? "замер не удался" : FirstLine(err);
                return result;
            }

            double bytesPerSec, bytes;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out bytesPerSec)
                || !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out bytes))
            {
                result.error = "не разобран ответ curl";
                return result;
            }

            result.ok = bytesPerSec > 0;
            result.mbps = bytesPerSec * 8.0 / 1000000.0;
            result.seconds = sw.Elapsed.TotalSeconds;
            result.bytes = (long)bytes;
            result.verdict = Verdict(result.mbps);
            return result;
        }

        /// <summary>
        /// Threshold from experience with DPI-throttled links rather than a
        /// promise: under 1 Mbit/s video is unwatchable, over 20 the bypass is
        /// not the bottleneck.
        /// </summary>
        private static string Verdict(double mbps)
        {
            if (mbps < 1) return "Очень медленно — видео будет вставать";
            if (mbps < 5) return "Медленно — возможно, провайдер режет скорость";
            if (mbps < 20) return "Рабочий скорости, выше было бы лучше";
            return "Хорошая скорость";
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "замер не удался";
            var i = s.IndexOf('\n');
            return (i > 0 ? s.Substring(0, i) : s).Trim();
        }

        private static string Run(string exe, string args, int timeoutMs, out string stderr)
        {
            stderr = null;
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) return string.Empty;
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return string.Empty;
                }
                stderr = Safe(errTask);
                return (Safe(outTask) ?? string.Empty).Trim();
            }
        }

        private static string Safe(Task<string> t)
        {
            try { return t.Result; } catch { return null; }
        }
    }
}