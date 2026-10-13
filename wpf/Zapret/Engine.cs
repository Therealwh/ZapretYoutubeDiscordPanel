using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ydp.Services;

namespace Ydp.Zapret
{
    public sealed class EngineRelease
    {
        public string tag { get; set; }
        public string name { get; set; }
        public string url { get; set; }
        public DateTime? published { get; set; }
    }

    public sealed class EngineResult
    {
        public bool ok { get; set; }
        public string reason { get; set; }
        public string message { get; set; }
        public string[] copied { get; set; } = new string[0];
    }

    /// <summary>
    /// zapret engine updates.
    ///
    /// Offline-first: the application ships the official release archive as an
    /// embedded resource, so a first install works with no network at all, and
    /// only the "check for updates" button talks to GitHub afterwards.
    /// </summary>
    public static class Engine
    {
        public const string ReleasesApi = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases";
        public const string BundledResourceName = "Ydp.Resources.zapret.zip";

        /// <summary>The archive bundled inside the exe (release + local file).</summary>
        public static string BundledZipPath => System.IO.Path.Combine(AppDir(), "zapret-bundled.zip");

        private static string AppDir()
        {
            string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrEmpty(path) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(path);
        }

        public static bool BundledZipPresent()
        {
            try { return File.Exists(BundledZipPath); }
            catch { return false; }
        }

        public static long BundledZipSize()
        {
            try
            {
                var info = new FileInfo(BundledZipPath);
                return info.Exists ? info.Length : 0;
            }
            catch { return 0; }
        }

        public static List<EngineRelease> LatestReleases(int count = 5)
        {
            var releases = new List<EngineRelease>();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(ReleasesApi);
                request.UserAgent = "YoutubeDiscordPanel";
                request.Timeout = 15000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    foreach (Match match in Regex.Matches(json,
                        "\"tag_name\"\\s*:\\s*\"(?<tag>[^\"]+)\".*?\"html_url\"\\s*:\\s*\"(?<url>[^\"]+)\"",
                        RegexOptions.Singleline))
                    {
                        releases.Add(new EngineRelease
                        {
                            tag = match.Groups["tag"].Value,
                            url = match.Groups["url"].Value,
                            name = match.Groups["tag"].Value.TrimStart('v'),
                        });
                        if (releases.Count >= count) break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Не удалось получить список релизов: " + ex.Message, "engine");
            }
            return releases;
        }

        /// <summary>
        /// Install the bundled archive (or a local zip the user picked) into the
        /// zapret root. A missing service and a stopped driver are handled
        /// first, because WinDivert64.sys stays locked while the bypass runs.
        /// </summary>
        public static EngineResult Install(string rootDir, string zipPath)
        {
            if (string.IsNullOrWhiteSpace(rootDir)) return new EngineResult { ok = false, reason = "no_root" };
            if (!Directory.Exists(rootDir)) return new EngineResult { ok = false, reason = "root_missing" };
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return new EngineResult { ok = false, reason = "zip_missing" };

            Logger.Info("Установка движка из " + Path.GetFileName(zipPath), "engine");
            ServiceOps.KillWinws();
            ServiceOps.StopService();
            ServiceOps.StopDrivers();

            try
            {
                var copied = new List<string>();
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    // The official archive has a single top-level folder; strip it
                    // so files land directly in the zapret root.
                    string prefix = DetectPrefix(archive);
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                        string relative = prefix.Length > 0 && entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            ? entry.FullName.Substring(prefix.Length)
                            : entry.FullName;
                        relative = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                        if (relative.Length == 0) continue;

                        string target = Path.Combine(rootDir, relative);
                        // Refuse to escape the root via "..".
                        if (!target.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase)) continue;

                        string dir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        entry.ExtractToFile(target, true);
                        copied.Add(relative);
                    }
                }
                Logger.Info("Движок обновлён, файлов: " + copied.Count, "engine");
                return new EngineResult { ok = true, copied = copied.ToArray() };
            }
            catch (Exception ex)
            {
                Logger.Error("Ошибка установки движка: " + ex.Message, "engine");
                return new EngineResult { ok = false, reason = "extract_failed", message = ex.Message };
            }
        }

        private static string DetectPrefix(ZipArchive archive)
        {
            var first = archive.Entries.FirstOrDefault(e => !e.FullName.EndsWith("/", StringComparison.Ordinal));
            if (first == null) return string.Empty;
            int slash = first.FullName.IndexOf('/');
            if (slash <= 0) return string.Empty;
            string candidate = first.FullName.Substring(0, slash + 1);
            bool allShare = archive.Entries
                .Where(e => !e.FullName.EndsWith("/", StringComparison.Ordinal))
                .All(e => e.FullName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            return allShare ? candidate : string.Empty;
        }

        /// <summary>Download a GitHub release asset to a temp file.</summary>
        public static EngineResult Download(string url, Action<int, string> progress)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = "YoutubeDiscordPanel";
                request.Timeout = 30000;
                string target = Path.Combine(Path.GetTempPath(), "ydp-zapret-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    long total = response.ContentLength;
                    using (var input = response.GetResponseStream())
                    using (var output = File.Create(target))
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        int chunk;
                        while ((chunk = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, chunk);
                            read += chunk;
                            if (total > 0) progress?.Invoke((int)(read * 100 / total), "Загрузка");
                        }
                    }
                }
                progress?.Invoke(100, "Готово");
                return new EngineResult { ok = true, message = target };
            }
            catch (Exception ex)
            {
                return new EngineResult { ok = false, reason = "download_failed", message = ex.Message };
            }
        }

        /// <summary>Best-effort local version from the root folder name / a marker file.</summary>
        public static string CurrentVersion(string rootDir)
        {
            try
            {
                string marker = Path.Combine(rootDir, "utils", "zapret-version.txt");
                if (File.Exists(marker)) return File.ReadAllText(marker).Trim();
            }
            catch { /* fall through */ }
            return null;
        }
    }
}