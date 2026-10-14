using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.ServiceProcess;
using Ydp.Zapret;

namespace Ydp.Services
{
    /// <summary>
    /// Service and process control through the Windows API.
    ///
    /// The Node build shelled out to cmd.exe/sc.exe/Start-Process, and cmd
    /// re-parsed the ImagePath: a folder with a space produced the malformed
    /// path SCM rejects with QueryServiceConfig 1734, the service died with
    /// 1067, and the panel then declared "zapret failed to start" — while the
    /// very same strategy worked from the very same folder as a process, and
    /// zapret's own service.bat worked. Talking to the SCM API directly removes
    /// the shell from the chain, so a path with spaces is no longer special.
    /// </summary>
    public static class ServiceOps
    {
        // ---- SCM interop -------------------------------------------------

        private const int SC_MANAGER_CONNECT = 0x0001;
        private const int SC_MANAGER_ALL_ACCESS = 0xF003F;
        private const int SERVICE_ALL_ACCESS = 0xF01FF;
        private const int SERVICE_QUERY_STATUS = 0x0004;
        private const int SERVICE_START = 0x0010;
        private const int SERVICE_STOP = 0x0020;
        private const int DELETE = 0x00010000;
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        private const uint SERVICE_AUTO_START = 0x00000002;
        private const uint SERVICE_ERROR_NORMAL = 0x00000001;
        private const uint SERVICE_CONTROL_STOP = 0x00000001;
        private const int SC_STATUS_PROCESS_INFO = 1;
        private const uint SERVICE_STOPPED = 1;
        private const uint SERVICE_RUNNING = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machineName, string databaseName, int desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateServiceW(IntPtr manager, string serviceName, string displayName,
            int desiredAccess, uint serviceType, uint startType, uint errorControl, string binaryPath,
            string loadOrderGroup, IntPtr tagId, string dependencies, string serviceStartName, string password);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, int desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartServiceW(IntPtr service, int numServiceArgs, IntPtr args);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr service, uint control, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int infoClass, byte[] buffer, int bufferSize, out int bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        private sealed class ScHandle : IDisposable
        {
            public IntPtr Handle { get; private set; }
            public ScHandle(IntPtr handle) { Handle = handle; }
            public void Dispose()
            {
                if (Handle != IntPtr.Zero) { CloseServiceHandle(Handle); Handle = IntPtr.Zero; }
            }
        }

        private static ScHandle OpenManager()
        {
            IntPtr handle = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            return handle == IntPtr.Zero ? null : new ScHandle(handle);
        }

        private static uint QueryStateRaw(IntPtr service)
        {
            var buffer = new byte[Marshal.SizeOf(typeof(SERVICE_STATUS))];
            int needed;
            if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, buffer, buffer.Length, out needed)) return 0;
            var status = (SERVICE_STATUS)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0), typeof(SERVICE_STATUS));
            return status.dwCurrentState;
        }

        /// <summary>Poll the service state. A missing service counts as STOPPED.</summary>
        /// <summary>
        /// Poll until the service reaches <paramref name="want"/>.
        ///
        /// State is read through ServiceController rather than our hand-rolled
        /// QueryServiceStatusEx call: the raw interop returned all-zero statuses,
        /// which made a perfectly healthy service look like it had failed to
        /// start. ServiceController is also what Diagnostics already uses, so
        /// both views agree.
        /// </summary>
        public static bool WaitServiceState(string name, string want, int timeoutMs = 15000)
        {
            ServiceControllerStatus target;
            switch (want)
            {
                case "RUNNING": target = ServiceControllerStatus.Running; break;
                case "START_PENDING": target = ServiceControllerStatus.StartPending; break;
                case "STOP_PENDING": target = ServiceControllerStatus.StopPending; break;
                default: target = ServiceControllerStatus.Stopped; break;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                using (var controller = OpenController(name))
                {
                    // A missing service counts as STOPPED.
                    if (controller == null) return target == ServiceControllerStatus.Stopped;
                    controller.Refresh();
                    if (controller.Status == target) return true;
                    // It fell back to STOPPED while we were waiting for it to come
                    // up: it died, so stop polling instead of burning the timeout.
                    if (target == ServiceControllerStatus.Running && controller.Status == ServiceControllerStatus.Stopped)
                    {
                        return false;
                    }
                }
                if (DateTime.UtcNow > deadline) return false;
                Thread.Sleep(300);
            } while (true);
        }

        private static ServiceController OpenController(string name)
        {
            try { return new ServiceController(name); }
            catch { return null; }
        }

        /// <summary>Poll until the service can no longer be opened (deleted for good).</summary>
        private static bool WaitServiceGone(string name, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                if (OpenController(name) == null) return true;
                if (DateTime.UtcNow > deadline) return false;
                Thread.Sleep(300);
            } while (true);
        }

        /// <summary>
        /// Delete an existing instance and WAIT until SCM really dropped it.
        /// DeleteService only marks the entry while a handle is open, so the next
        /// CreateService would fail with ERROR_SERVICE_EXISTS (1073) and the entry
        /// would vanish later on its own — the panel would then report that
        /// nothing was installed.
        /// </summary>
        private static void DropExistingService(IntPtr manager, string name)
        {
            IntPtr existing = OpenServiceW(manager, name, SERVICE_STOP | DELETE | SERVICE_QUERY_STATUS);
            if (existing != IntPtr.Zero)
            {
                StopServiceRaw(existing);
                DeleteService(existing);
                CloseServiceHandle(existing);
            }
            WaitServiceGone(name, 15000);
        }

        // ---- winws process ----------------------------------------------

        /// <summary>Wait until winws.exe is gone (or present).</summary>
        public static bool WaitWinws(bool absent, int timeoutMs = 10000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                bool running = Win.ProcessRunning("winws.exe");
                if (running != absent) return true;
                if (DateTime.UtcNow > deadline) return false;
                Thread.Sleep(300);
            } while (true);
        }

        /// <summary>Kill every winws.exe (was taskkill /IM winws.exe /F).</summary>
        public static bool KillWinws()
        {
            bool any = false;
            foreach (var name in new[] { "winws", "winws1", "winws2" })
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); } catch { continue; }
                foreach (var p in procs)
                {
                    try { p.Kill(); any = true; } catch { /* already gone */ }
                    finally { p.Dispose(); }
                }
            }
            return any;
        }

        // ---- operations --------------------------------------------------

        public sealed class OperationResult
        {
            public bool ok { get; set; }
            public string reason { get; set; }
            public string message { get; set; }
            public string[] args { get; set; } = new string[0];
            public string mode { get; set; }
        }

        /// <summary>
        /// Build the ImagePath SCM expects: the exe in quotes when it contains a
        /// space, followed by the winws arguments (which already keep their own
        /// quotes around path values).
        /// </summary>
        public static string BuildImagePath(string exePath, IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            sb.Append(exePath.IndexOf(' ') >= 0 ? "\"" + exePath + "\"" : exePath);
            foreach (var arg in args)
            {
                sb.Append(' ');
                sb.Append(Strategy.QuoteArg(arg));
            }
            return sb.ToString();
        }

        /// <summary>Install the zapret service for a strategy and start it.</summary>
        public static OperationResult InstallStrategyAsService(string rootDir, string strategyFile)
        {
            ParsedStrategy parsed;
            try { parsed = Strategy.ParseStrategyFile(strategyFile, rootDir); }
            catch (Exception e) { return new OperationResult { ok = false, reason = "parse_failed", message = e.Message }; }
            if (parsed.args.Count == 0) return new OperationResult { ok = false, reason = "parse_failed" };

            string winws = Path.Combine(rootDir, "bin", "winws.exe");
            if (!File.Exists(winws)) return new OperationResult { ok = false, reason = "winws_missing" };
            string strategyName = Path.GetFileNameWithoutExtension(strategyFile);

            // winws exits fatally when a referenced list file is missing, and a
            // fresh extraction ships without the *-user.txt files.
            try { Lists.EnsureUserLists(rootDir); } catch { /* best effort */ }

            // A leftover winws.exe holds the WinDivert handle and the new
            // service's winws then dies with code 1.
            KillWinws();
            WaitWinws(true, 8000);

            string imagePath = BuildImagePath(winws, Strategy.RejoinFlagValues(parsed.args));

            using (var manager = OpenManager())
            {
                if (manager == null) return new OperationResult { ok = false, reason = "scm_unavailable", message = "OpenSCManager failed" };

                // Replace an existing service instead of failing with ERROR_SERVICE_EXISTS.
                using (var existing = new ScHandle(OpenServiceW(manager.Handle, Status.ZapretServiceName, SERVICE_STOP | DELETE | SERVICE_QUERY_STATUS)))
                {
                    if (existing.Handle != IntPtr.Zero)
                    {
                        StopServiceRaw(existing.Handle);
                        DeleteService(existing.Handle);
                        WaitServiceState(Status.ZapretServiceName, "STOPPED", 5000);
                    }
                }

                IntPtr created = CreateServiceW(
                    manager.Handle, Status.ZapretServiceName, "zapret",
                    SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                    SERVICE_ERROR_NORMAL, imagePath, null, IntPtr.Zero, null, null, null);
                if (created == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    return new OperationResult { ok = false, reason = "scm_create_failed", message = "CreateService error " + err };
                }
                using (new ScHandle(created)) { }

                // Remember which strategy the service runs, the same registry
                // value the old install chain wrote.
                WriteServiceStrategy(strategyName);
            }

            var started = StartService();
            if (!started.ok) return started;

            // Trust the observed state, not the exit codes.
            bool up = WaitServiceState(Status.ZapretServiceName, "RUNNING", 40000);
            if (!up)
            {
                return new OperationResult { ok = false, reason = "service_start_failed", message = imagePath };
            }
            return new OperationResult { ok = true, args = parsed.args.ToArray(), mode = "service" };
        }

        private static void StopServiceRaw(IntPtr service)
        {
            var status = new SERVICE_STATUS();
            ControlService(service, SERVICE_CONTROL_STOP, ref status);
        }

        /// <summary>Store the strategy name in the service's registry values.</summary>
        public static void WriteServiceStrategy(string strategyName)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"System\CurrentControlSet\Services\zapret"))
                {
                    if (key != null) key.SetValue(Status.StrategyRegistryValue, strategyName ?? string.Empty);
                }
            }
            catch
            {
                // Cosmetic: the dashboard falls back to "не выбрана".
            }
        }

        public static OperationResult StartService()
        {
            using (var manager = OpenManager())
            {
                if (manager == null) return new OperationResult { ok = false, reason = "scm_unavailable" };
                using (var service = new ScHandle(OpenServiceW(manager.Handle, Status.ZapretServiceName, SERVICE_START | SERVICE_QUERY_STATUS)))
                {
                    if (service.Handle == IntPtr.Zero) return new OperationResult { ok = false, reason = "service_missing" };
                    if (QueryStateRaw(service.Handle) == SERVICE_RUNNING) return new OperationResult { ok = true };
                    if (!StartServiceW(service.Handle, 0, IntPtr.Zero))
                    {
                        return new OperationResult { ok = false, reason = "service_start_failed" };
                    }
                }
            }
            bool up = WaitServiceState(Status.ZapretServiceName, "RUNNING", 40000);
            return up ? new OperationResult { ok = true } : new OperationResult { ok = false, reason = "service_start_failed" };
        }

        public static OperationResult StopService()
        {
            using (var manager = OpenManager())
            {
                if (manager == null) return new OperationResult { ok = false, reason = "scm_unavailable" };
                using (var service = new ScHandle(OpenServiceW(manager.Handle, Status.ZapretServiceName, SERVICE_STOP | SERVICE_QUERY_STATUS)))
                {
                    if (service.Handle == IntPtr.Zero) return new OperationResult { ok = true };
                    StopServiceRaw(service.Handle);
                }
            }
            bool down = !WaitServiceState(Status.ZapretServiceName, "RUNNING", 8000);
            return down ? new OperationResult { ok = true } : new OperationResult { ok = false, reason = "service_stop_failed" };
        }

        public static OperationResult RemoveServices()
        {
            KillWinws();
            StopService();
            using (var manager = OpenManager())
            {
                if (manager == null) return new OperationResult { ok = false, reason = "scm_unavailable" };
                foreach (var name in new[] { Status.ZapretServiceName })
                {
                    using (var service = new ScHandle(OpenServiceW(manager.Handle, name, SERVICE_STOP | DELETE | SERVICE_QUERY_STATUS)))
                    {
                        if (service.Handle != IntPtr.Zero)
                        {
                            StopServiceRaw(service.Handle);
                            DeleteService(service.Handle);
                        }
                    }
                }
            }
            return new OperationResult { ok = true };
        }

        /// <summary>
        /// Launch a strategy as a plain process. No PowerShell and no Start-Process:
        /// the exe path goes through FileName (never re-parsed) and the arguments
        /// are quoted here. When the panel is not elevated the same call goes
        /// through the "runas" verb, which is what raises UAC.
        /// </summary>
        public static OperationResult LaunchStrategyProcess(string rootDir, string strategyFile)
        {
            ParsedStrategy parsed;
            try { parsed = Strategy.ParseStrategyFile(strategyFile, rootDir); }
            catch (Exception e) { return new OperationResult { ok = false, reason = "parse_failed", message = e.Message }; }
            if (parsed.args.Count == 0) return new OperationResult { ok = false, reason = "parse_failed" };

            string bin = Path.Combine(rootDir, "bin");
            string winws = Path.Combine(bin, "winws.exe");
            if (!File.Exists(winws)) return new OperationResult { ok = false, reason = "winws_missing" };
            try { Lists.EnsureUserLists(rootDir); } catch { /* best effort */ }

            var joined = Strategy.RejoinFlagValues(parsed.args);
            var argString = string.Join(" ", joined.Select(Strategy.QuoteArg));

            bool elevated = Win.IsAdmin();
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = winws,
                    Arguments = argString,
                    WorkingDirectory = bin,
                    UseShellExecute = !elevated,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                if (!elevated) startInfo.Verb = "runas";
                Process process = Process.Start(startInfo);
                if (process == null) return new OperationResult { ok = false, reason = "launch_failed" };
                process.Dispose();
            }
            catch (Exception e)
            {
                return new OperationResult
                {
                    ok = false,
                    reason = elevated ? "launch_failed" : "elevate_cancelled",
                    message = e.Message,
                };
            }

            bool up = WaitWinws(false, 10000);
            return up ? new OperationResult { ok = true, mode = "process" }
                      : new OperationResult { ok = false, reason = "process_start_failed" };
        }

        /// <summary>Stop the WinDivert driver services so the folder can be replaced.</summary>
        public static void StopDrivers()
        {
            foreach (var name in new[] { "WinDivert", "WinDivert14", "WinDivert64" })
            {
                using (var manager = OpenManager())
                {
                    if (manager == null) return;
                    using (var service = new ScHandle(OpenServiceW(manager.Handle, name, SERVICE_STOP | SERVICE_QUERY_STATUS)))
                    {
                        if (service.Handle == IntPtr.Zero) continue;
                        StopServiceRaw(service.Handle);
                    }
                }
                WaitServiceState(name, "STOPPED", 8000);
            }
        }

        /// <summary>Enable TCP timestamps (netsh), best effort.</summary>
        public static void EnableTcpTimestamps()
        {
            try
            {
                var startInfo = new ProcessStartInfo("netsh.exe", "interface tcp set global timestamps=enabled")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(startInfo)) { if (p != null) p.WaitForExit(8000); }
            }
            catch { /* best effort */ }
        }
    }
}