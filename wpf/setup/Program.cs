using System;
using System.Collections.Generic;
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
        internal static string ProductNamePublic => ProductName;
        private const string InstallDirName = "YoutubeDiscordPanel";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "YoutubeDiscordPanel";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YoutubeDiscordPanel";
        /// <summary>Where the user chose to install, remembered for uninstall/reinstall.</summary>
        private const string SettingsKey = @"Software\YoutubeDiscordPanel";

        /// <summary>
        /// Install folder. Empty until Install() runs: before that, the value
        /// chosen in the dialog wins, then a /dir switch, then the folder
        /// remembered from the previous install, and only then the default
        /// per-user location.
        /// </summary>
        private static string chosenDir;

        private static string DefaultDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", InstallDirName);

        private static string InstallDir
        {
            get
            {
                if (!string.IsNullOrEmpty(chosenDir)) return chosenDir;
                string remembered = ReadSetting("InstallDir");
                return string.IsNullOrEmpty(remembered) ? DefaultDir : remembered;
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
Application.EnableVisualStyles();
            bool silent = Array.IndexOf(args, "/silent") >= 0 || Array.IndexOf(args, "/S") >= 0;
            bool uninstall = Array.IndexOf(args, "/uninstall") >= 0;

            // /dir "D:\Programs\Panel" installs into a chosen folder without
            // any dialog, for scripted installs and unattended setups.
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "/dir", StringComparison.OrdinalIgnoreCase)) chosenDir = args[i + 1];
            }

try
            {
                return uninstall ? Uninstall(silent) : Install(silent);
            }
            catch (Exception ex)
            {
                // Silent mode used to swallow every failure: nothing was shown
                // and nothing was written, so a broken install looked like a
                // no-op. Always record it.
                Log("Установка прервана: " + ex);
                if (!silent)
                {
                    MessageBox.Show("Установка не удалась:\r\n" + ex.Message,
                        ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 1;
            }
            finally
            {
                CleanupPayloads();
            }
        }

        /// <summary>
        /// Locate the panel exe: first inside the installer itself, then next to
        /// it. The embedded copy is what makes a single download work.
        /// </summary>
        private static string FindPanelExe(string dir)
        {
            string extracted = ExtractPayload("Setup.Payload.panel.exe", "panel.exe");
            if (extracted != null) return extracted;

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

        /// <summary>Write an embedded resource to a temp file and return its path.</summary>
        private static string ExtractPayload(string resourceName, string fileName)
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;
                    string target = Path.Combine(Path.GetTempPath(),
                        "ydp-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + fileName);
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                        stream.CopyTo(output);
                    extractedFiles.Add(target);
                    return target;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Temp files written from embedded resources, removed on exit.</summary>
        private static readonly List<string> extractedFiles = new List<string>();

        private static void CleanupPayloads()
        {
            foreach (var file in extractedFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            extractedFiles.Clear();
        }

private static int Install(bool silent)
        {
            Log("Install: старт, silent=" + silent);
            bool createShortcuts = true, createDesktopShortcut = true, addAutoStart = true, installEngine = true;
            string sourceDir = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);
            string sourceExe = FindPanelExe(sourceDir);
            Log("Install: панель = " + (sourceExe ?? "НЕ НАЙДЕНА"));

            if (sourceExe == null)
            {
                MessageBox.Show("Рядом с установщиком нет файла " + ProductName + ".exe",
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

if (!silent)
            {
                using (var form = new InstallForm(InstallDir))
                {
                    if (form.ShowDialog() != DialogResult.OK) return 0;
                    // Always land in a folder named YoutubeDiscordPanel. Taking
                    // the picked folder verbatim allowed files to be dropped
                    // straight into "C:\Program Files", and that is exactly how
                    // a mismatched autostart path got written.
                    chosenDir = form.SelectedDirectory.TrimEnd('\\');
                    if (!string.Equals(Path.GetFileName(chosenDir), ProductName, StringComparison.OrdinalIgnoreCase))
                    {
                        chosenDir = Path.Combine(chosenDir, ProductName);
                    }
                    createShortcuts = form.CreateShortcuts;
                    createDesktopShortcut = form.CreateDesktopShortcut;
                    addAutoStart = form.AddAutoStart;
                    installEngine = form.InstallEngine;
                }
            }
            WriteSetting("InstallDir", InstallDir);

StopRunning();

            Directory.CreateDirectory(InstallDir);
            Log("Install: копирую панель в " + InstallDir);
            File.Copy(sourceExe, Path.Combine(InstallDir, ProductName + ".exe"), true);
            Log("Install: панель скопирована");

            // The engine archive travels with the panel so a first install works
            // with no network at all. Prefer the copy embedded in this
            // installer; fall back to one sitting next to it.
            string bundled = ExtractPayload("Setup.Payload.zapret.zip", "zapret-bundled.zip");
            if (bundled == null) bundled = Path.Combine(sourceDir, "zapret-bundled.zip");
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
            Log("Install: движок = " + (bundled == null ? "нет" : bundled));

            if (installEngine && bundled != null && File.Exists(bundled))
            {
                string engineDir = Path.Combine(InstallDir, "Zapret");
                Log("Install: распаковываю zapret в " + engineDir);
                if (ExtractEngine(bundled, engineDir))
                {
                    Log("Install: движок zapret распакован");
                }
            }

            if (createShortcuts || createDesktopShortcut) CreateShortcuts(createDesktopShortcut);
            Log("Install: ярлыки обработаны");
            WriteUninstallEntry();
            SetAutoStart(addAutoStart, addAutoStart);
            Log("Install: готово, папка " + InstallDir);

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
            // Never kill ourselves. The loop below used to match the installer's
            // own name when the file was called YoutubeDiscordPanel-Setup.exe,
            // so the installer terminated itself mid-install: exit code -1, no
            // files copied, and not even a chance to report the failure.
            int self = Process.GetCurrentProcess().Id;
            foreach (var name in new[] { ProductName, ProductName + "-Setup" })
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.Id == self) continue;
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                    catch { /* already gone */ }
                    finally { process.Dispose(); }
                }
            }
        }

private static void CreateShortcuts(bool desktop)
        {
            try
            {
                string target = Path.Combine(InstallDir, ProductName + ".exe");
                string workDir = InstallDir;
                string description = "Панель обхода zapret";
                // Activator.CreateInstance, not a cast of Type to dynamic:
                // GetTypeFromProgID returns a Type, and casting it yields a
                // TypeInfo whose members are not the COM object's, so
                // CreateShortcut threw RuntimeBinderException.
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) throw new InvalidOperationException("WScript.Shell недоступен");
                var shell = (dynamic)Activator.CreateInstance(shellType);

                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

                var startLink = (dynamic)shell.CreateShortcut(Path.Combine(startMenu, ProductName + ".lnk"));
                startLink.TargetPath = target;
                startLink.WorkingDirectory = workDir;
                startLink.Description = description;
                startLink.Save();

                if (!desktop) return;

                var desktopLink = (dynamic)shell.CreateShortcut(Path.Combine(desktopDir, ProductName + ".lnk"));
                desktopLink.TargetPath = target;
                desktopLink.WorkingDirectory = workDir;
                desktopLink.Description = description;
                desktopLink.Save();
            }
            catch (Exception ex)
            {
                // This used to be an empty catch, so shortcuts silently never
                // appeared and nobody could tell why. Log it instead.
                Log("Не удалось создать ярлыки: " + ex);
            }
        }

        /// <summary>
        /// Unpack the bundled zapret archive into <paramref name="target"/>.
        ///
        /// The release zip has a version folder on top ("zapret-...-1.10.3/"),
        /// so that prefix is stripped: the panel expects bin\winws.exe and lists\
        /// directly under the folder it is given.
        /// </summary>
        private static bool ExtractEngine(string zipPath, string target)
        {
            try
            {
                Directory.CreateDirectory(target);
                string root = Path.GetFullPath(target);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                {
                    root += Path.DirectorySeparatorChar;
                }

                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    // The common prefix of the entries, if the first segment is
                    // the same folder for all of them.
                    string prefix = DetectPrefix(archive);

                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                        string relative = prefix.Length > 0
                            ? entry.FullName.Substring(prefix.Length)
                            : entry.FullName;
                        relative = relative.Replace('/', Path.DirectorySeparatorChar)
                            .TrimStart(Path.DirectorySeparatorChar);
                        if (relative.Length == 0) continue;

                        string destination = Path.GetFullPath(Path.Combine(root, relative));
                        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException("запись выходит за пределы папки: " + entry.FullName);
                        }
                        string dir = Path.GetDirectoryName(destination);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        entry.ExtractToFile(destination, true);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("Не удалось распаковать движок zapret: " + ex.Message);
                return false;
            }
        }

        private static string DetectPrefix(ZipArchive archive)
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                int slash = entry.FullName.IndexOf('/');
                if (slash <= 0) return string.Empty;
                string candidate = entry.FullName.Substring(0, slash + 1);
                // Only a real folder, not a file that happens to contain a slash.
                string asPath = candidate.TrimEnd('/');
                bool isFolder = false;
                foreach (var other in archive.Entries)
                {
                    if (other.FullName.TrimEnd('/') == asPath) { isFolder = true; break; }
                }
                return isFolder ? candidate : string.Empty;
            }
            return string.Empty;
        }

        /// <summary>Append to a small log next to the installer, for support.</summary>
        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "ydp-setup.log");
                File.AppendAllText(path,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch { /* logging must never break the install */ }
        }

        private static string ReadSetting(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    return key == null ? null : key.GetValue(name) as string;
                }
            }
            catch { return null; }
        }

        private static void WriteSetting(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    key.SetValue(name, value ?? "");
            }
            catch { /* a missing preference must not fail the install */ }
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