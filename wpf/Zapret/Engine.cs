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
  /// <summary>Direct link to the downloadable zip of this release.</summary>
  public string assetUrl { get; set; }
  public string assetName { get; set; }
  public long assetSize { get; set; }
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
        /// The newest published release together with the zip asset, so the UI
        /// can offer a real "download and install" instead of only listing tags.
        /// </summary>
        public static EngineRelease LatestRelease()
        {
            try
            {
                string json = Fetch(ReleasesApi);
                var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"(?<tag>[^\"]+)\"");
                if (!tag.Success) return null;
                var release = new EngineRelease
                {
                    tag = tag.Groups["tag"].Value,
                    name = tag.Groups["tag"].Value.TrimStart('v'),
                    url = "https://github.com/Flowseal/zapret-discord-youtube/releases/tag/"
                          + tag.Groups["tag"].Value,
                };

                // Only the newest release matters, so stop before the second one.
                var second = Regex.Match(json.Substring(tag.Index + tag.Length),
                    "\"tag_name\"\\s*:\\s*\"", RegexOptions.None);
                string head = second.Success ? json.Substring(0, tag.Index + tag.Length + second.Index) : json;

                foreach (Match asset in Regex.Matches(head,
                    "\"name\"\\s*:\\s*\"(?<name>[^\"]+\\.zip)\".*?"
                  + "\"browser_download_url\"\\s*:\\s*\"(?<url>[^\"]+)\"",
                    RegexOptions.Singleline))
                {
                    release.assetName = asset.Groups["name"].Value;
                    release.assetUrl = asset.Groups["url"].Value.Replace("\\/", "/");
                    // The position of "size" inside the asset object is not
                    // guaranteed, so look around the match instead of after it.
                    int from = Math.Max(0, asset.Index - 800);
                    int to = Math.Min(head.Length, asset.Index + asset.Length + 800);
                    var window = head.Substring(from, to - from);
                    var sizeMatch = Regex.Match(window, "\"size\"\\s*:\\s*(?<size>[0-9]+)");
                    if (sizeMatch.Success)
                    {
                        long.TryParse(sizeMatch.Groups["size"].Value, out long size);
                        release.assetSize = size;
                    }
                    break;
                }
                return release;
            }
            catch (Exception ex)
            {
                Logger.Warn("Не удалось получить последний релиз zapret: " + ex.Message, "engine");
                return null;
            }
        }

        /// <summary>Download a release asset to disk, reporting progress.</summary>
        public static bool Download(string url, string destination, Action<int> onProgress = null)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = "YoutubeDiscordPanel";
                request.Timeout = 30000;
                request.AllowAutoRedirect = true;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var input = response.GetResponseStream())
                {
                    long total = response.ContentLength;
                    string temp = destination + ".part";
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                            done += read;
                            if (onProgress != null && total > 0)
                                onProgress((int)(done * 100 / total));
                        }
                    }
                    // Only publish a complete download: a truncated .part must
                    // never be mistaken for an installable archive.
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(temp, destination);
                }
                if (onProgress != null) onProgress(100);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Ошибка загрузки: " + ex.Message, "engine");
                try { if (File.Exists(destination + ".part")) File.Delete(destination + ".part"); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Version marker written after a successful install, so the panel can
        /// tell the user whether an update is available instead of guessing from
        /// file dates.
        /// </summary>
        public static string VersionMarkerPath(string rootDir)
        {
            return Path.Combine(rootDir, "ydp-engine-version.txt");
        }

        public static string InstalledVersion(string rootDir)
        {
            try
            {
                string path = VersionMarkerPath(rootDir);
                if (!File.Exists(path)) return null;
                string value = File.ReadAllText(path).Trim();
                return value.Length == 0 ? null : value;
            }
            catch { return null; }
        }

        public static void WriteInstalledVersion(string rootDir, string version)
        {
            try { File.WriteAllText(VersionMarkerPath(rootDir), version ?? ""); }
            catch { /* the marker is a convenience, not a requirement */ }
        }

        private static string Fetch(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "YoutubeDiscordPanel";
            request.Timeout = 15000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                return reader.ReadToEnd();
        }

        /// <summary>
        /// Install the bundled archive (or a local zip the user picked) into the
        /// zapret root. A missing service and a stopped driver are handled
        /// first, because WinDivert64.sys stays locked while the bypass runs.
        /// </summary>
        /// <param name="version">
        /// Release tag to remember in the version marker, so the panel can tell
        /// later whether an update is pending.
        /// </param>
        public static EngineResult Install(string rootDir, string zipPath, string version = null)
        {
            string pendingVersion = version;
            if (string.IsNullOrWhiteSpace(rootDir)) return new EngineResult { ok = false, reason = "no_root" };
            if (!Directory.Exists(rootDir)) return new EngineResult { ok = false, reason = "root_missing" };
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return new EngineResult { ok = false, reason = "zip_missing" };

            // Whole install under the bypass gate: extraction swaps bin\winws.exe
            // and the driver files, and that must not interleave with a sweep,
            // a connect or the watchdog tearing winws down.
            ServiceOps.BypassGate.Wait();
            try { return InstallLocked(rootDir, zipPath, pendingVersion); }
            finally { ServiceOps.BypassGate.Release(); }
        }

        private static EngineResult InstallLocked(string rootDir, string zipPath, string pendingVersion)
        {
            Logger.Info("Установка движка из " + Path.GetFileName(zipPath), "engine");
            // Clear the old marker first: if the install dies halfway we must not
            // keep claiming the previous version is fully installed.
            WriteInstalledVersion(rootDir, null);
            // Service first, then any winws it may have left behind: an orphan holds
            // the WinDivert filter and makes the next service start fail with
            // exit code 1. Drivers go last, and only as preparation for
            // overwriting the .sys files.
            ServiceOps.StopServiceAndWinws();
            bool driversStopped = ServiceOps.StopDrivers();

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
                        // Refuse to escape the root via "..". Path.Combine does not
                        // resolve the segments, so the raw combined string can still
                        // carry them past a naive prefix check - canonicalize both
                        // sides and require the root to be a real directory prefix
                        // (root + separator), not a string prefix that "C:\zapret"
                        // shares with "C:\zapretEVIL".
                        if (relative.Split(Path.DirectorySeparatorChar).Any(s => s == "..")) continue;
                        string fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        string fullTarget = Path.GetFullPath(target);
                        if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;

                        string dir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        entry.ExtractToFile(target, true);
                        copied.Add(relative);
                    }
                }
                Logger.Info("Движок обновлён, файлов: " + copied.Count, "engine");
                // Record which release produced this install, unless the caller
                // already knows (the "update" button passes its version).
                if (!string.IsNullOrEmpty(pendingVersion)) WriteInstalledVersion(rootDir, pendingVersion);
                // A driver still loaded means the new .sys may only take effect
                // after a reboot - say so instead of leaving a silent surprise.
                var result = new EngineResult { ok = true, copied = copied.ToArray() };
                if (!driversStopped) result.message = "Драйвер WinDivert остался загружен: перезагрузите компьютер, чтобы применился новый движок.";
                return result;
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