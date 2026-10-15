using System;
using System.Collections.Generic;
using System.IO;
using Ydp.Services;

namespace Ydp.Zapret
{
    public sealed class ServiceInfoView
    {
        public bool exists { get; set; }
        public bool running { get; set; }
        public string state { get; set; }
        public string name { get; set; }
    }

    public sealed class StatusSnapshot
    {
        public string activeMode { get; set; } = "off";
        public ServiceInfoView service { get; set; }
        public ServiceInfoView winDivert { get; set; }
        public bool winwsProcess { get; set; }
        public string winwsProcessPath { get; set; }
        public string serviceStrategy { get; set; }
        public string rootDir { get; set; }
        public List<ServiceInfoView> conflicts { get; set; } = new List<ServiceInfoView>();
    }

    /// <summary>
    /// Compose the dashboard status from direct API queries.
    ///
    /// Mirrors the Node implementation 1:1 (same fields, same fallbacks,
    /// WinDivert checked under both service names plus the kernel driver) so the
    /// renderer needs no changes and saved results stay comparable.
    /// </summary>
    public static class Status
    {
        public const string ZapretServiceName = "zapret";
        public const string StrategyRegistryValue = "zapret-discord-youtube";

        public static StatusSnapshot GetStatus(string rootDir)
        {
            var zapret = Win.QueryService(ZapretServiceName);
            var wd1 = Win.QueryService("WinDivert");
            var wd2 = Win.QueryService("WinDivert64");
            var winws = Win.ProcessRunning("winws.exe");
            var winwsPath = Win.ProcessImagePath("winws.exe");
            var strategy = Win.ReadRegistryString(
                "HKLM", @"System\CurrentControlSet\Services\zapret", StrategyRegistryValue);

            ServiceInfo winDivert = wd1.Exists ? wd1 : (wd2.Exists ? wd2 : null);

            var snapshot = new StatusSnapshot
            {
                service = ToView(zapret),
                winDivert = winDivert == null ? null : ToView(winDivert),
                winwsProcess = winws,
                winwsProcessPath = winwsPath,
                serviceStrategy = strategy,
                rootDir = rootDir,
            };

            if (zapret.Running) snapshot.activeMode = "service";
            else if (winws) snapshot.activeMode = "process";

            if (snapshot.activeMode == "off")
            {
                foreach (var name in Win.ConflictServiceNames())
                {
                    var info = Win.QueryService(name);
                    if (info.Exists) snapshot.conflicts.Add(ToView(info));
                }
            }
            return snapshot;
        }

        private static ServiceInfoView ToView(ServiceInfo info)
        {
            return new ServiceInfoView
            {
                exists = info.Exists,
                running = info.Running,
                state = info.State,
                name = info.Name,
            };
        }

        /// <summary>
        /// The zapret root: the configured one when valid, otherwise the first
        /// known-good location (C:\Zapret first — the layout zapret's own
        /// scripts expect).
        ///
        /// Memoized for a few seconds: when the folder is NOT configured this
        /// probes several drives, and the dashboard calls it every second —
        /// on the UI thread that used to be a periodic stutter.
        /// </summary>
        private static readonly object RootGate = new object();
        private static string _cacheKey;
        private static string _cacheValue;
        private static DateTime _cacheUntil;

        public static string ResolveRoot(string configured)
        {
            var key = configured ?? "";
            lock (RootGate)
            {
                if (_cacheUntil > DateTime.UtcNow && _cacheKey == key) return _cacheValue;
            }
            var value = ResolveRootSlow(configured);
            lock (RootGate)
            {
                _cacheKey = key;
                _cacheValue = value;
                _cacheUntil = DateTime.UtcNow.AddSeconds(3);
            }
            return value;
        }

        private static string ResolveRootSlow(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && LooksLikeZapretRoot(configured)) return configured;
            foreach (var candidate in CandidateRoots())
            {
                if (LooksLikeZapretRoot(candidate)) return candidate;
            }
            return null;
        }

        public static bool LooksLikeZapretRoot(string dir)
        {
            try
            {
                return Directory.Exists(Path.Combine(dir, "bin"))
                    && File.Exists(Path.Combine(dir, "bin", "winws.exe"))
                    && Directory.Exists(Path.Combine(dir, "lists"));
            }
            catch
            {
                return false;
            }
        }

        public static IEnumerable<string> CandidateRoots()
        {
yield return @"C:\Zapret";
  yield return @"C:\zapret";
  yield return @"C:\zapret-discord-youtube";

  // The panel itself may live under Program Files, and zapret is commonly
  // unpacked next to it. The old list knew only about C:\Zapret, so an install
  // anywhere else looked "missing" forever and the nag dialog kept returning.
  var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
  if (!string.IsNullOrEmpty(programFiles))
  {
    yield return Path.Combine(programFiles, "Zapret");
    yield return Path.Combine(programFiles, "zapret-discord-youtube");
  }
  var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
  if (!string.IsNullOrEmpty(programFilesX86))
  {
    yield return Path.Combine(programFilesX86, "Zapret");
  }

  // A Zapret folder next to the panel executable, whichever drive that is.
  string appDir = null;
  try
  {
    appDir = System.IO.Path.GetDirectoryName(
        System.Reflection.Assembly.GetExecutingAssembly().Location);
  }
  catch { /* an unknown app location is not a reason to fail */ }
  if (!string.IsNullOrEmpty(appDir))
  {
    yield return Path.Combine(appDir, "Zapret");
    string parent = System.IO.Path.GetDirectoryName(appDir.TrimEnd('\\'));
    if (!string.IsNullOrEmpty(parent)) yield return Path.Combine(parent, "Zapret");
  }

  var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  if (!string.IsNullOrEmpty(localAppData))
  {
    yield return Path.Combine(localAppData, "zapret-discord-youtube");
    yield return Path.Combine(localAppData, "Zapret");
  }

  // Any drive root that has it: the installer may have been pointed at D:.
  foreach (var drive in DriveInfo.GetDrives())
  {
    string root = null;
    bool ready = false;
    try { ready = drive.IsReady; root = drive.RootDirectory.FullName; }
    catch { /* an unreadable or removed drive is simply skipped */ }
    if (!ready || string.IsNullOrEmpty(root)) continue;
    yield return System.IO.Path.Combine(root, "Zapret");
    yield return System.IO.Path.Combine(root, "zapret");
  }
  }

        /// <summary>Validate a candidate root and report the known problem classes.</summary>
        public static RootValidation ValidateRoot(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return new RootValidation { ok = false, reason = "not_found" };
            if (!Directory.Exists(Path.Combine(dir, "bin"))) return new RootValidation { ok = false, reason = "no_bin" };
            if (!File.Exists(Path.Combine(dir, "bin", "winws.exe"))) return new RootValidation { ok = false, reason = "no_winws" };
            if (!Directory.Exists(Path.Combine(dir, "lists"))) return new RootValidation { ok = false, reason = "no_lists" };
            var issues = new List<string>();
            foreach (var ch in dir)
            {
                int code = ch;
                if ((code >= 0x0430 && code <= 0x044F) || (code >= 0x0410 && code <= 0x042F) || code == 0x0451 || code == 0x0401)
                {
                    issues.Add("cyrillic");
                    break;
                }
            }
            // Spaces are NOT an issue any more: the SCM call and the direct spawn
            // both quote their arguments, verified against
            // "D:\Program Files\AZ Tools\zapret" by Tests/PathTest.cs. Reporting
            // them pushed users towards a C:\Zapret copy for no reason.
            if (dir.IndexOf("onedrive", StringComparison.OrdinalIgnoreCase) >= 0) issues.Add("onedrive");
            return new RootValidation { ok = true, issues = issues.ToArray() };
        }
    }

    public sealed class RootValidation
    {
        public bool ok { get; set; }
        public string reason { get; set; }
        public string[] issues { get; set; } = new string[0];
    }
}