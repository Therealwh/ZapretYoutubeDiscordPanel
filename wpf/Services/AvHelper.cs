// Antivirus helper: detects which AV products watch the process, explains the
// false-positive (WinDivert hooks packets, so every AV flags it), and offers a
// one-click Defender exclusion. Third-party AVs cannot be configured from
// code on purpose - we only show the folder path to whitelist.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ydp.Services
{
    public static class AvHelper
    {
        public class AvState
        {
            public string product;      // display name
            public bool isDefender;
            public bool panelExcluded;  // Defender only: our folder in exclusions
        }

        /// <summary>Enumerate registered AV products via SecurityCenter2 WMI.
        /// On Windows Home that class exists; on some SKUs it does not - fall
        /// back to a Defender-only answer.</summary>
        public static List<AvState> Detect()
        {
            var result = new List<AvState>();
            try
            {
                var wmi = new System.Management.ManagementObjectSearcher(
                    @"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
                foreach (var o in wmi.Get())
                {
                    var name = (o["displayName"] as string ?? "").Trim();
                    if (name.Length == 0) continue;
                    int state;
                    int.TryParse(o["productState"]?.ToString(), out state);
                    // productState hex: byte 2 nibble 2 = enabled/disabled (0x10 = on)
                    bool enabled = ((state >> 12) & 0xF) == 1;
                    result.Add(new AvState
                    {
                        product = name + (enabled ? "" : " (выключен)"),
                        isDefender = name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0,
                        panelExcluded = false,
                    });
                }
            }
            catch
            {
                // SecurityCenter2 unavailable: assume Defender (it is always there on 10/11)
                result.Add(new AvState { product = "Microsoft Defender", isDefender = true, panelExcluded = false });
            }
            if (result.Count == 0)
                result.Add(new AvState { product = "Microsoft Defender", isDefender = true, panelExcluded = false });
            return result;
        }

        public static bool IsDefenderExcluding(string directory)
        {
            try
            {
                var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "powershell.exe",
                    "-NoProfile -Command \"(Get-MpPreference).ExclusionPath -contains '\" + directory + \"'\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                });
                string output = ps.StandardOutput.ReadToEnd().Trim();
                ps.WaitForExit(20000);
                return output == "True";
            }
            catch { return false; }
        }

        /// <summary>Add the panel root (and zapret root) to Defender exclusions.
        /// Requires an elevated process; the panel already runs as admin.</summary>
        public static string AddDefenderExclusions(params string[] directories)
        {
            var paths = (directories ?? new string[0]).Where(Directory.Exists).Distinct().ToArray();
            if (paths.Length == 0) return "Нет папок для исключения.";
            var args = "-NoProfile -Command \"\"";
            var parts = paths.Select(p => "Add-MpPreference -ExclusionPath '\" + p + \"'");
            args += string.Join("; ", parts) + "\"\"";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit(60000);
                    return p.ExitCode == 0
                        ? "Готово: папки добавлены в исключения Defender."
                        : "Не удалось (код " + p.ExitCode + "). Добавьте вручную: Безопасность Windows → Защита от вирусов → Исключения.";
                }
            }
            catch (Exception ex)
            {
                return "Ошибка: " + ex.Message + ". Добавьте папку вручную в исключения Defender.";
            }
        }

        /// <summary>The folders an AV is most likely to touch.</summary>
        public static string[] FoldersToWhitelist(string zapretRoot)
        {
            var list = new List<string>();
            var panel = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (panel.Length > 0 && Directory.Exists(panel)) list.Add(panel);
            if (!string.IsNullOrEmpty(zapretRoot) && Directory.Exists(zapretRoot)) list.Add(zapretRoot);
            return list.ToArray();
        }
    }
}
