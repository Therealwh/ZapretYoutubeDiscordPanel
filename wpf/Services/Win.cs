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

                    var addresses = new List<string>();
                    bool dhcp = true;
                    try
                    {
                        foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                            addresses.Add(ip.Address.ToString());
                            // DnsAddresses live on the adapter, not on the unicast address.
                            if (nic.GetIPProperties().DnsAddresses.Any(d => d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                            {
                                dhcp = false;
                            }
                        }
                    }
                    catch { /* adapter without an address set */ }

                    result.Add(new DnsAdapter
                    {
                        Name = nic.Name,
                        Title = string.IsNullOrEmpty(description) ? nic.Name : nic.Name + " (" + description + ")",
                        CurrentDns = addresses.Count > 0 ? string.Join(", ", addresses) : "DHCP",
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

            var first = RunNetsh("interface ip set dns name=\"" + adapter + "\" static " + primary + " primary");
            if (!first.ok) return first;
            if (!string.IsNullOrWhiteSpace(secondary))
            {
                var second = RunNetsh("interface ip set dns name=\"" + adapter + "\" static " + secondary + " index=2");
                if (!second.ok) return second;
            }
            // Old cached entries keep the broken resolver alive.
            RunNetsh("interface ip flushdns");
            Logger.Info("DNS: " + adapter + " → " + primary
                + (string.IsNullOrWhiteSpace(secondary) ? "" : ", " + secondary), "net");
            return new ServiceOps.OperationResult { ok = true };
        }

        /// <summary>Hand the resolver back to the provider (DHCP).</summary>
        public static ServiceOps.OperationResult ResetDns(string adapter)
        {
            if (string.IsNullOrWhiteSpace(adapter)) return new ServiceOps.OperationResult { ok = false, reason = "no_adapter" };
            var result = RunNetsh("interface ip set dns name=\"" + adapter + "\" dhcp");
            if (!result.ok) return result;
            RunNetsh("interface ip flushdns");
            Logger.Info("DNS: " + adapter + " → DHCP (от провайдера)", "net");
            return new ServiceOps.OperationResult { ok = true };
        }

        private static bool IsValidDns(string value)
        {
            System.Net.IPAddress parsed;
            return System.Net.IPAddress.TryParse((value ?? "").Trim(), out parsed)
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
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
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