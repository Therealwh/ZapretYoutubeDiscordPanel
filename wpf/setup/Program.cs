using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Ydp.Setup
{
    /// <summary>
    /// Installer / uninstaller for YoutubeDiscordPanel.
    ///
    /// Install:   YoutubeDiscordPanel-Setup.exe            (or /silent)
    /// Uninstall: YoutubeDiscordPanel-Setup.exe /uninstall (or /silent /uninstall)
    ///
    /// Installs into %LocalAppData%\Programs\YoutubeDiscordPanel (per-user, no
    /// admin required), adds Start menu + desktop shortcuts, registers the
    /// uninstall entry and turns on the login item so the panel comes back after
    /// a reboot with the remembered strategy already applied.
    /// </summary>
    internal static class Program
    {
        private const string ProductName = "YoutubeDiscordPanel";
        private const string InstallDirName = "YoutubeDiscordPanel";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "YoutubeDiscordPanel";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YoutubeDiscordPanel";

        private static string InstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", InstallDirName);

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool silent = Array.IndexOf(args, "/silent") >= 0 || Array.IndexOf(args, "/S") >= 0;
            bool uninstall = Array.IndexOf(args, "/uninstall") >= 0;

            try
            {
                return uninstall ? Uninstall(silent) : Install(silent);
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show("Установка не удалась:\r\n" + ex.Message,
                        ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 1;
            }
        }

        /// <summary>
        /// Locate the panel exe next to the installer.
        ///
        /// Releases publish it as "YoutubeDiscordPanel-2.0.0.exe" (versioned so
        /// users can tell builds apart), but downloads land it under the plain
        /// name, and old archives use the plain name too. Accept all of them
        /// instead of failing with "no file next to the installer".
        /// </summary>
        private static string FindPanelExe(string dir)
        {
            string plain = Path.Combine(dir, ProductName + ".exe");
            if (File.Exists(plain)) return plain;

            string newest = null;
            DateTime newestTime = DateTime.MinValue;
            foreach (var file in Directory.GetFiles(dir, ProductName + "*.exe"))
            {
                string name = Path.GetFileName(file);
                // Never mistake the installer or an old setup for the panel.
                if (name.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (string.Equals(name, ProductName + "-latest.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var stamp = File.GetLastWriteTimeUtc(file);
                if (stamp > newestTime) { newestTime = stamp; newest = file; }
            }
            return newest;
        }

        private static int Install(bool silent)
        {
            string sourceDir = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);
            string sourceExe = FindPanelExe(sourceDir);

            if (sourceExe == null)
            {
                MessageBox.Show("Рядом с установщиком нет файла " + ProductName + ".exe",
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (!silent)
            {
                var answer = MessageBox.Show(
                    "Установить " + ProductName + "?\r\n\r\nПапка: " + InstallDir + "\r\n"
                    + "Ничего дополнительно скачивать не нужно: программа работает на .NET Framework, "
                    + "который уже есть в Windows.",
                    ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return 0;
            }

            StopRunning();

            Directory.CreateDirectory(InstallDir);
            File.Copy(sourceExe, Path.Combine(InstallDir, ProductName + ".exe"), true);

            // The engine archive travels with the panel so a first install works
            // with no network at all.
            string bundled = Path.Combine(sourceDir, "zapret-bundled.zip");
            if (File.Exists(bundled))
            {
                File.Copy(bundled, Path.Combine(InstallDir, "zapret-bundled.zip"), true);
            }
            // The offline zapret package is large; copying it is optional and only
            // done when it actually sits next to the installer.
            string bigZip = Path.Combine(sourceDir, "zapret-discord-youtube.zip");
            if (File.Exists(bigZip))
            {
                File.Copy(bigZip, Path.Combine(InstallDir, "zapret-discord-youtube.zip"), true);
            }

            CreateShortcuts();
            WriteUninstallEntry();
            SetAutoStart(true, true);

            if (!silent)
            {
                MessageBox.Show("Готово.\r\n\r\nПанель установлена и добавлена в автозагрузку: "
                    + "при входе в Windows она сама применит запомненную рабочую стратегию.",
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            try { Process.Start(Path.Combine(InstallDir, ProductName + ".exe")); } catch { /* not critical */ }
            return 0;
        }

        private static int Uninstall(bool silent)
        {
            if (!silent)
            {
                var answer = MessageBox.Show("Удалить " + ProductName + "?", ProductName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return 0;
            }

            StopRunning();
            SetAutoStart(false, false);
            RemoveShortcuts();
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { /* may not exist */ }

            // The zapret folder itself is left alone on purpose: it is the user's
            // bypass, not part of the panel, and deleting it would take their
            // working setup with it.
            try { Directory.Delete(InstallDir, true); } catch { /* locked files may remain */ }
            if (!silent)
            {
                MessageBox.Show("Панель удалена. Папка zapret оставлена как есть — обход не тронут.",
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }

        private static void StopRunning()
        {
            foreach (var name in new[] { ProductName, ProductName + "-Setup" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try { process.Kill(); process.WaitForExit(3000); } catch { /* not running */ }
                }
            }
        }

        private static void CreateShortcuts()
        {
            try
            {
                string target = Path.Combine(InstallDir, ProductName + ".exe");
                string workDir = InstallDir;
                string description = "Панель обхода zapret";
                var shell = (dynamic)Type.GetTypeFromProgID("WScript.Shell");

                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

                var startLink = (dynamic)shell.CreateShortcut(Path.Combine(startMenu, ProductName + ".lnk"));
                startLink.TargetPath = target;
                startLink.WorkingDirectory = workDir;
                startLink.Description = description;
                startLink.Save();

                var desktopLink = (dynamic)shell.CreateShortcut(Path.Combine(desktop, ProductName + ".lnk"));
                desktopLink.TargetPath = target;
                desktopLink.WorkingDirectory = workDir;
                desktopLink.Description = description;
                desktopLink.Save();
            }
            catch
            {
                // Shortcuts are a nicety; the install itself already succeeded.
            }
        }

        private static void RemoveShortcuts()
        {
            try
            {
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName + ".lnk"));
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk"));
            }
            catch { /* already gone */ }
        }

        private static void WriteUninstallEntry()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    if (key == null) return;
                    key.SetValue("DisplayName", ProductName);
                    key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
                    key.SetValue("Publisher", "Therealwh");
                    key.SetValue("DisplayIcon", Path.Combine(InstallDir, ProductName + ".exe"));
                    key.SetValue("InstallLocation", InstallDir);
                    key.SetValue("NoModify", 1);
                    key.SetValue("NoRepair", 1);
                    key.SetValue("EstimatedSize", (int)(new FileInfo(Path.Combine(InstallDir, ProductName + ".exe")).Length / 1024));
                    key.SetValue("UninstallString", "\"" + Assembly.GetExecutingAssembly().Location + "\" /uninstall");
                }
            }
            catch { /* optional */ }
        }

        private static void SetAutoStart(bool enabled, bool withAutoApply)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (!enabled)
                    {
                        key.DeleteValue(RunValue, false);
                        return;
                    }
                    string value = "\"" + Path.Combine(InstallDir, ProductName + ".exe") + "\"";
                    if (withAutoApply) value += " --autoconnect";
                    key.SetValue(RunValue, value);
                }
            }
            catch { /* optional */ }
        }
    }
}