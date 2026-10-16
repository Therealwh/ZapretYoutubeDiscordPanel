using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Ydp.Services;

namespace Ydp.Services
{
    public sealed class UpdateInfo
    {
        public bool available { get; set; }
        public string current { get; set; }
        public string latest { get; set; }
        public string url { get; set; }
        public string notes { get; set; }
    }

    public sealed class UpdateResult
    {
        public bool ok { get; set; }
        public string reason { get; set; }
        public string message { get; set; }
    }

    /// <summary>
    /// Panel self-update from GitHub Releases.
    ///
    /// The panel is a single exe with no runtime to install, so the update is
    /// just "download the new exe and swap it in". The swap is done by a short
    /// cmd that waits for this process to exit - copying over a running exe is
    /// refused by Windows, and killing ourselves mid-write would corrupt it.
    /// </summary>
    public static class PanelUpdater
    {
        public const string ApiUrl = "https://api.github.com/repos/Therealwh/ZapretYoutubeDiscordPanel/releases/latest";

        private static UpdateInfo _state = new UpdateInfo();

        public static UpdateInfo State => _state;

        public static string CurrentVersion()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version.Major + "." + version.Minor + "." + version.Build;
        }

        private static int ParseVersion(string text)
        {
            var match = Regex.Match(text ?? "", @"(\d+)\.(\d+)\.(\d+)");
            if (!match.Success) return 0;
            int major = int.Parse(match.Groups[1].Value);
            int minor = int.Parse(match.Groups[2].Value);
            int patch = int.Parse(match.Groups[3].Value);
            return major * 10000 + minor * 100 + patch;
        }

        public static UpdateInfo Check()
        {
            _state.current = CurrentVersion();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(ApiUrl);
                request.UserAgent = "YoutubeDiscordPanel";
                request.Timeout = 15000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                    var notes = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    var url = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/Therealwh/ZapretYoutubeDiscordPanel/releases/tag/[^\"]+)\"");
                    if (tag.Success)
                    {
                        _state.latest = tag.Groups[1].Value;
                        _state.url = url.Success ? url.Groups[1].Value : "https://github.com/Therealwh/ZapretYoutubeDiscordPanel/releases";
                        _state.notes = notes.Success ? Regex.Unescape(notes.Groups[1].Value) : null;
                        _state.available = ParseVersion(_state.latest) > ParseVersion(_state.current);
                    }
                }
            }
            catch (Exception ex)
            {
                _state.available = false;
                Logger.Warn("Проверка обновлений не удалась: " + ex.Message, "updater");
            }
            return _state;
        }

        /// <summary>
        /// Download the release exe and swap it in after this process exits.
        /// The asset is preferred by name; the first .exe asset is the fallback.
        /// </summary>
        public static UpdateResult Install()
        {
            Check();
            if (!_state.available) return new UpdateResult { ok = false, reason = "no_update" };
            if (string.IsNullOrEmpty(_state.url)) return new UpdateResult { ok = false, reason = "no_url" };

            try
            {
                string target = AppDomain.CurrentDomain.BaseDirectory + "YoutubeDiscordPanel.exe";
                string download = Path.Combine(Path.GetTempPath(),
                    "ydp-update-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string assetUrl = FindAssetUrl(_state.url) ?? _state.url;
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "YoutubeDiscordPanel");
                    client.DownloadFile(assetUrl, download);
                }

                long size = new FileInfo(download).Length;
                if (size < 20 * 1024)
                {
                    return new UpdateResult { ok = false, reason = "asset_too_small", message = size + " байт" };
                }

                // Wait for this process to be GONE before copying — Windows refuses
                // to overwrite a running exe. A fixed delay is used on purpose:
                // polling with `tasklist /fi "PID eq N"` is unreliable here, because
                // on a non-English (ru) Windows the localized tasklist output can
                // make `/fi` reject the argument and the whole wait loop collapse.
                // We already stop the Telegram proxy and winws right before, and we
                // exit immediately after — so a 6-second grace is generous and
                // safe. Once we are gone, cmd copies the new exe and starts it;
                // the new instance then finds 127.0.0.1:1443 free.
                // start "" is required so the path with spaces is not taken as
                // the window title.
                string command = string.Format(
                    "ping -n 7 127.0.0.1 >nul & copy /y \"{0}\" \"{1}\" >nul & start \"\" \"{1}\"",
                    download, target);
                var startInfo = new ProcessStartInfo("cmd.exe", "/c " + command)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                // Release the Telegram proxy port and winws in THIS process before
                // it exits. Without this the old instance keeps 127.0.0.1:1443
                // while the new one starts, and the new proxy can never bind —
                // Telegram then reconnects to a dead port. Stopping here frees
                // the port immediately, ahead of the restart.
                try { Ydp.Tg.TgProxy.Stop(); } catch { /* best effort */ }
                try { ServiceOps.KillWinws(); } catch { /* best effort */ }

                Process.Start(startInfo);

                Logger.Info("Запущено обновление панели до " + _state.latest, "updater");
                // The panel must actually LEAVE. Without this the old process
                // survives the update, keeps 127.0.0.1:1443, and the new
                // instance cannot bind the Telegram proxy. Kill any child
                // helpers first so nothing lingers holding a port.
                // Environment.Exit (not Kill) so config is flushed and the
                // process leaves cleanly; Windows then releases 1443 for us.
                Environment.Exit(0);
                return new UpdateResult { ok = true, message = _state.latest };
            }
            catch (Exception ex)
            {
                Logger.Error("Обновление панели не удалось: " + ex.Message, "updater");
                return new UpdateResult { ok = false, reason = "install_failed", message = ex.Message };
            }
        }

        private static string FindAssetUrl(string releasePage)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/Therealwh/ZapretYoutubeDiscordPanel/releases/latest");
                request.UserAgent = "YoutubeDiscordPanel";
                request.Timeout = 15000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    var matches = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.exe)\"");
                    foreach (Match match in matches)
                    {
                        string url = match.Groups[1].Value.Replace("\\/", "/");
                        if (url.Contains("Setup")) continue;
                        return url;
                    }
                }
            }
            catch
            {
                // Not fatal: the release page URL still works for manual download.
            }
            return null;
        }
    }
}