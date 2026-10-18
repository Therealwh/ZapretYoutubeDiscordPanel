using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Ydp.Services
{
    public sealed class ServiceInfo
    {
        public bool Exists { get; set; }
        public bool Running { get; set; }
        public string State { get; set; }
        public string Name { get; set; }
    }

    /// <summary>
    /// Windows queries without spawning helper processes.
    ///
    /// The previous Node/Electron implementation answered every status refresh by
    /// launching sc.exe (5x), tasklist, reg.exe, driverquery.exe, a PowerShell
    /// process for the image path and `net session` for the admin check — eight
    /// child processes per refresh, each of which showed up in Task Manager next
    /// to the panel. Here the same answers come from BCL APIs:
    /// ServiceController, Process, the registry hive and the current token.
    /// </summary>
    public static class Win
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, ref TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION
        {
            public int TokenIsElevated;
        }

        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        /// <summary>True when the process runs elevated (no `net session` spawn).</summary>
        public static bool IsAdmin()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    if (principal.IsInRole(WindowsBuiltInRole.Administrator)) return true;
                }
            }
            catch { /* fall through to the token query */ }
            try
            {
                IntPtr token;
                if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_QUERY, out token) == 0) return false;
                using (var safe = new SafeHandleWrapper(token))
                {
                    TOKEN_ELEVATION elevation = default(TOKEN_ELEVATION);
                    int len;
                    return GetTokenInformation(token, TokenElevation, ref elevation, Marshal.SizeOf(elevation), out len) && elevation.TokenIsElevated != 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private sealed class SafeHandleWrapper : IDisposable
        {
            private readonly IntPtr _handle;
            public SafeHandleWrapper(IntPtr handle) { _handle = handle; }
            public void Dispose()
            {
                if (_handle != IntPtr.Zero) CloseHandle(_handle);
            }
            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);
        }

        /// <summary>True when any process with this image name runs (no tasklist).</summary>
        public static bool ProcessRunning(string imageName)
        {
            string name = (imageName ?? string.Empty).Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (name.Length == 0) return false;
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { return false; }
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }

        /// <summary>Full image path of the first matching process (was a PowerShell + WMI query).</summary>
        public static string ProcessImagePath(string imageName)
        {
            string name = (imageName ?? string.Empty).Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { return null; }
            try
            {
                foreach (var p in procs)
                {
                    try { return p.MainModule.FileName; }
                    catch { /* access denied for this one, try the next */ }
                }
            }
            finally { foreach (var p in procs) p.Dispose(); }
            return null;
        }

        /// <summary>Adapters that can actually carry DNS settings.</summary>
        public sealed class DnsAdapter
        {
            public string Name { get; set; }
            public string Title { get; set; }
            public string CurrentDns { get; set; }
            public bool Dhcp { get; set; }
            public override string ToString() { return Title; }
        }

        /// <summary>
        /// List adapters worth changing.
        ///
        /// Loopback, tunnels and virtual adapters are skipped: setting DNS on
        /// those either does nothing or breaks unrelated software, and a user
        /// picking "VPN" from a list should not have the panel silently retarget
        /// its DNS.
        /// </summary>
        public static List<DnsAdapter> ListDnsAdapters()
        {
            var result = new List<DnsAdapter>();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;

                    string title = nic.Name;
                    string description = null;
                    try { description = nic.Description; } catch { }
                    if (!string.IsNullOrEmpty(description) && description.IndexOf("Tunnel", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (!string.IsNullOrEmpty(description) && description.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    // DnsAddresses live on the adapter, not on the unicast address.
                    var dnsServers = new List<string>();
                    try
                    {
                        foreach (var d in nic.GetIPProperties().DnsAddresses)
                        {
                            if (d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                dnsServers.Add(d.ToString());
                        }
                    }
                    catch { /* adapter without properties */ }

                    // DHCP clients also receive resolver addresses (the router),
                    // so "some DNS present" does not mean manual. The reliable
                    // distinction lives in the registry: a statically configured
                    // adapter has its NameServer value filled in.
                    bool dhcp = !HasStaticDnsInRegistry(nic.Id);

                    result.Add(new DnsAdapter
                    {
                        Name = nic.Name,
                        Title = string.IsNullOrEmpty(description) ? nic.Name : nic.Name + " (" + description + ")",
                        CurrentDns = dnsServers.Count > 0 ? string.Join(", ", dnsServers) : "не задан",
                        Dhcp = dhcp,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Не удалось получить список сетевых адаптеров: " + ex.Message, "net");
            }
            return result;
        }

        /// <summary>
        /// Does the adapter carry a statically configured resolver?
        ///
        /// Windows fills NameServer for manual DNS and leaves it empty when the
        /// addresses come from DHCP, which is the only reliable way to tell the
        /// two apart: the API-level DnsAddresses are populated in both cases.
        /// </summary>
        private static bool HasStaticDnsInRegistry(string adapterId)
        {
            if (string.IsNullOrEmpty(adapterId)) return false;
            try
            {
                var path = "SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\" + adapterId;
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path))
                {
                    if (key == null) return false;
                    var nameServer = (key.GetValue("NameServer") as string) ?? "";
                    return nameServer.Trim().Length > 0;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Quote an adapter name for a netsh command line. Adapter names are
        /// user- (and local-malware-) renamable and may contain quotes; a raw
        /// name="..." splice would let a name with a quote end the quoted token
        /// early and inject netsh verbs from the elevated panel.
        /// </summary>
        private static string NetshName(string adapter)
        {
            return "name=\"" + (adapter ?? "").Replace("\"", "'") + "\"";
        }

        /// <summary>
        /// Point an adapter at fixed DNS servers.
        ///
        /// Some DPI setups break the system resolver while the rest of the
        /// traffic passes, which is why YouTube can report "no internet" on a
        /// connection that plainly has one. Switching the resolver to public
        /// resolvers is the standard first thing to try, and it is a setting the
        /// user can always put back.
        /// </summary>
        public static ServiceOps.OperationResult SetDns(string adapter, string primary, string secondary)
        {
            if (string.IsNullOrWhiteSpace(adapter)) return new ServiceOps.OperationResult { ok = false, reason = "no_adapter" };
            if (!IsValidDns(primary)) return new ServiceOps.OperationResult { ok = false, reason = "bad_dns" };
            if (!string.IsNullOrWhiteSpace(secondary) && !IsValidDns(secondary)) return new ServiceOps.OperationResult { ok = false, reason = "bad_dns" };

            // Back up the user's own static DNS once, before the first change:
            // ResetDns below must restore what the user had, not blindly DHCP.
            BackupOriginalDns(adapter);

            var name = NetshName(adapter);
            var first = RunNetsh("interface ip set dns " + name + " static " + primary + " primary");
            if (!first.ok) return first;
            if (!string.IsNullOrWhiteSpace(secondary))
            {
                // "set dns ... index=2" REPLACES the server at that index; the
                // second resolver only appears with "add dns". Without this the
                // panel silently leaves the user with a single DNS server.
                var second = RunNetsh("interface ip add dns " + name + " address=" + secondary + " index=2");
                if (!second.ok)
                {
                    // Primary is already applied; leaving it half-applied with a
                    // single resolver is worse than rolling back to the backup.
                    RestoreOriginalDns(adapter);
                    return second;
                }
            }
            // Old cached entries keep the broken resolver alive. The reset lives
            // in ipconfig: netsh has no flushdns verb.
            FlushDnsCache();
            Logger.Info("DNS: " + adapter + " → " + primary
                + (string.IsNullOrWhiteSpace(secondary) ? "" : ", " + secondary), "net");
            return new ServiceOps.OperationResult { ok = true };
        }

        /// <summary>Hand the resolver back to the provider (DHCP).</summary>
        public static ServiceOps.OperationResult ResetDns(string adapter)
        {
            if (string.IsNullOrWhiteSpace(adapter)) return new ServiceOps.OperationResult { ok = false, reason = "no_adapter" };
            var result = RunNetsh("interface ip set dns " + NetshName(adapter) + " dhcp");
            if (!result.ok) return result;
            FlushDnsCache();
            Logger.Info("DNS: " + adapter + " → DHCP (от провайдера)", "net");
            return new ServiceOps.OperationResult { ok = true };
        }

        /// <summary>
        /// Remember the adapter's own static DNS before the panel touches it.
        /// Stored per adapter id in the config; only saved once, and only when
        /// the adapter actually has a static resolver (NameServer set) - a DHCP
        /// adapter has nothing worth restoring.
        /// </summary>
        private static void BackupOriginalDns(string adapter)
        {
            try
            {
                var key = "dnsBackup." + adapter;
                if (!string.IsNullOrEmpty(Config.Get(key, null))) return; // already backed up
                if (!HasStaticDnsInRegistry(adapter)) return;             // DHCP - nothing to keep
                var ns = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\" + adapter)
                    ?.GetValue("NameServer") as string ?? "";
                var parts = ns.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (parts.Length == 0) return;
                Config.Set(key, string.Join("|", parts));
                Logger.Info("DNS: сохранён исходный резолвер " + adapter + " → " + string.Join(", ", parts), "net");
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Put back the resolver saved by BackupOriginalDns, if any. Used when a
        /// partial DNS change must be undone without losing the user's settings.
        /// </summary>
        public static ServiceOps.OperationResult RestoreOriginalDns(string adapter)
        {
            try
            {
                var saved = Config.Get("dnsBackup." + adapter, null);
                if (string.IsNullOrEmpty(saved))
                {
                    // No backup - the adapter was on DHCP before the panel, hand
                    // it back to DHCP.
                    return ResetDns(adapter);
                }
                var parts = saved.Split('|');
                var name = NetshName(adapter);
                var first = RunNetsh("interface ip set dns " + name + " static " + parts[0] + " primary");
                if (!first.ok) return first;
                for (int i = 1; i < parts.Length && i < 3; i++)
                {
                    var more = RunNetsh("interface ip add dns " + name + " address=" + parts[i] + " index=" + (i + 1));
                    if (!more.ok) return more;
                }
                FlushDnsCache();
                Logger.Info("DNS: восстановлен исходный резолвер " + adapter + " → " + string.Join(", ", parts), "net");
                return new ServiceOps.OperationResult { ok = true };
            }
            catch (Exception ex)
            {
                return new ServiceOps.OperationResult { ok = false, reason = "restore_failed", message = ex.Message };
            }
        }

        /// <summary>
        /// Drop the resolver cache. Lives in ipconfig, not netsh: netsh has no
        /// flushdns verb and silently failed on it.
        /// </summary>
        private static void FlushDnsCache()
        {
            try
            {
                var startInfo = new ProcessStartInfo("ipconfig.exe", "/flushdns")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(startInfo))
                {
                    if (p == null) return;
                    p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    p.WaitForExit(15000);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Не удалось сбросить кэш DNS: " + ex.Message, "net");
            }
        }

        private static bool IsValidDns(string value)
        {
            System.Net.IPAddress parsed;
            // TryParse alone accepts short forms like "8.8" and expands them to
            // 8.0.0.8; the user almost certainly meant something else, so require
            // the exact four-octet form.
            var text = (value ?? "").Trim();
            if (text.Split('.').Length != 4) return false;
            return System.Net.IPAddress.TryParse(text, out parsed)
                && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }

        private static ServiceOps.OperationResult RunNetsh(string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo("netsh.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(startInfo))
                {
                    if (p == null) return new ServiceOps.OperationResult { ok = false, reason = "netsh_failed" };
                    // Both streams concurrently: reading them one after another
                    // deadlocks when netsh fills the stderr buffer first. The
                    // WaitForExit result also matters — on timeout ExitCode
                    // throws and the netsh process would be left running.
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    bool exited = p.WaitForExit(15000);
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        return new ServiceOps.OperationResult { ok = false, reason = "netsh_timeout", message = "netsh не ответил за 15 секунд" };
                    }
                    string output = (SafeResult(outTask) ?? "") + (SafeResult(errTask) ?? "");
                    return p.ExitCode == 0
                        ? new ServiceOps.OperationResult { ok = true }
                        : new ServiceOps.OperationResult { ok = false, reason = "netsh_exit_" + p.ExitCode, message = output.Trim() };
                }
            }
            catch (Exception ex)
            {
                return new ServiceOps.OperationResult { ok = false, reason = "netsh_failed", message = ex.Message };
            }
        }

        private static string SafeResult(System.Threading.Tasks.Task<string> task)
        {
            try { return task.Result; }
            catch { return null; }
        }

        /// <summary>Service state via ServiceController (was sc.exe + text parsing).</summary>
        public static ServiceInfo QueryService(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    sc.Refresh();
                    var state = sc.Status;
                    return new ServiceInfo
                    {
                        Exists = true,
                        Running = state == ServiceControllerStatus.Running,
                        State = DescribeServiceState(state),
                        Name = name,
                    };
                }
            }
            catch (InvalidOperationException)
            {
                // Service does not exist.
                return new ServiceInfo { Exists = false, Running = false, State = null, Name = name };
            }
            catch
            {
                return new ServiceInfo { Exists = false, Running = false, State = null, Name = name };
            }
        }

        /// <summary>
        /// State word kept identical to the old parser ("RUNNING", "STOPPED",
        /// "START_PENDING", "STOP_PENDING", "OTHER") so the UI and the saved
        /// results do not change shape.
        /// </summary>
        public static string DescribeServiceState(ServiceControllerStatus state)
        {
            switch (state)
            {
                case ServiceControllerStatus.Running: return "RUNNING";
                case ServiceControllerStatus.StartPending: return "START_PENDING";
                case ServiceControllerStatus.StopPending: return "STOP_PENDING";
                case ServiceControllerStatus.Stopped: return "STOPPED";
                case ServiceControllerStatus.ContinuePending: return "OTHER";
                case ServiceControllerStatus.PausePending: return "OTHER";
                case ServiceControllerStatus.Paused: return "OTHER";
                default: return "OTHER";
            }
        }

        /// <summary>Registry string read (was reg.exe query + regex).</summary>
        public static string ReadRegistryString(string hive, string subKey, string valueName)
        {
            try
            {
                RegistryKey root = hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                using (var key = root.OpenSubKey(subKey))
                {
                    if (key == null) return null;
                    var value = key.GetValue(valueName) as string;
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }
            catch
            {
                return null;
            }
        }

        public static IEnumerable<string> ConflictServiceNames()
        {
            return new[] { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" };
        }
    }
}