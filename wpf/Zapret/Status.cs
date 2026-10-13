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
        /// </summary>
        public static string ResolveRoot(string configured)
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
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "zapret-discord-youtube");
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
            foreach (var ch in dir)
            {
                if (char.IsWhiteSpace(ch)) { issues.Add("spaces"); break; }
            }
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