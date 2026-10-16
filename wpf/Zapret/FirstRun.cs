using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ydp.Services;

namespace Ydp.Zapret
{
    /// <summary>What the first-run prompt did.</summary>
    public enum FirstRunOutcome
    {
        Installed,
        ManualChosen,
        Skipped,
        Failed,
    }

    /// <summary>
    /// First run: the panel was installed but zapret itself is not on the
    /// machine yet. Rather than making the user hunt for a download, we offer
    /// to fetch the newest release from GitHub, let them pick the folder, and
    /// remember it so the panel never asks again.
    /// </summary>
    public static class FirstRun
    {
        /// <summary>
        /// Called on every start: make sure the settings point at the folder we
        /// actually use, and that the Telegram domains are in the hostlist.
        ///
        /// The installer unpacks zapret next to the panel, so the folder is
        /// discovered rather than configured. Without persisting it, the
        /// settings kept showing an empty or stale path, and the user had no way
        /// to see which folder the bypass was taken from.
        /// </summary>
        public static void RememberResolvedRoot()
        {
            string configured = Config.Get("zapretRoot", null);
            string resolved = Status.ResolveRoot(configured);
            if (string.IsNullOrEmpty(resolved)) return;

            if (!string.Equals(resolved, configured, StringComparison.OrdinalIgnoreCase))
            {
                Config.Set("zapretRoot", resolved);
                Logger.Info("Рабочая папка zapret: " + resolved, "engine");
            }

            // The Telegram domains are added once, after the first successful
            // install; reinstalling the engine keeps the user's own list file.
            if (!string.Equals(Config.Get("tgDomainsAdded", null), "true", StringComparison.OrdinalIgnoreCase))
            {
                int added = EnsureTelegramDomains(resolved);
                if (added > 0)
                {
                    Config.Set("tgDomainsAdded", "true");
                    Logger.Info("Домены Telegram добавлены в список zapret: " + added, "lists");
                }
            }
        }

        /// <summary>True when zapret is missing and the user has not declined.</summary>
        public static bool NeedsInstall()
        {
            if (!string.IsNullOrEmpty(Status.ResolveRoot(Config.Get("zapretRoot", null)))) return false;
            return !string.Equals(Config.Get("zapretDeclined", null), "true", StringComparison.OrdinalIgnoreCase);
        }

/// <summary>The folder is always called this, so files never land loose.</summary>
        private const string FolderName = "Zapret";

        /// <summary>
        /// Where we suggest installing. A previously configured or existing
        /// folder wins; otherwise the system drive, which is what a user means by
        /// "поставь как обычно".
        /// </summary>
        public static string SuggestedLocation()
        {
            string configured = Config.Get("zapretRoot", null);
            if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured)) return Path.GetDirectoryName(configured.TrimEnd('\\'));

            string existing = Status.ResolveRoot(null);
            if (!string.IsNullOrEmpty(existing) && Directory.Exists(existing)) return Path.GetDirectoryName(existing.TrimEnd('\\'));

            return Environment.GetFolderPath(Environment.SpecialFolder.System);
        }

        public static async Task<FirstRunOutcome> RunInteractiveAsync(Window owner)
        {
            MessageBoxResult answer = MessageBox.Show(owner,
                "На этом компьютере не найден zapret — без него обход не заработает.\r\n\r\n"
+ "Скачать последнюю версию с GitHub и установить? Это около 1,5 МБ.\r\n\r\n"
                + "Программа сама создаст папку Zapret в выбранном вами месте и запомнит её.",
                "Zapret Panel", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return FirstRunOutcome.Skipped;

            if (answer == MessageBoxResult.No)
            {
                // "Not now" is not "never": do not keep nagging on every start.
                Config.Set("zapretDeclined", "true");
                return FirstRunOutcome.Skipped;
            }

// The picker chooses WHERE, and we always create a "Zapret" folder
            // inside it. Pointing the dialog straight at the install folder made
            // it easy to unpack 60 loose files onto a disk root by accident.
            string parent = PickLocation(owner);
            if (parent == null) return FirstRunOutcome.Skipped;

            string dir = Path.Combine(parent, FolderName);
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Не удалось создать папку:\n" + dir + "\n\n" + ex.Message,
                    "Zapret Panel", MessageBoxButton.OK, MessageBoxImage.Error);
                return FirstRunOutcome.Failed;
            }

            return await InstallAsync(owner, dir);
        }

        /// <summary>
        /// Ask for the location and install, without any yes/no question.
        ///
        /// Used by "Подключить рабочий обход": the user has already said what
        /// they want by pressing the button, so a further question is noise.
        /// </summary>
        public static async Task<string> InstallInteractiveAsync(Window owner)
        {
            string parent = PickLocation(owner);
            if (parent == null) return null;
            string dir = Path.Combine(parent, FolderName);
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Не удалось создать папку:\n" + dir + "\n\n" + ex.Message,
                    "Zapret Panel", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
            var outcome = await InstallAsync(owner, dir);
            return outcome == FirstRunOutcome.Installed ? dir : null;
        }

        /// <summary>Where to install. Always one folder, named Zapret.</summary>
        public static string PickLocation(Window owner)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Выберите диск или папку — внутри создастся папка Zapret";
                dialog.ShowNewFolderButton = false;

                string suggested = SuggestedLocation();
                if (Directory.Exists(suggested)) dialog.SelectedPath = suggested;

                IntPtr handle = IntPtr.Zero;
                if (owner != null) handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
                if (handle == IntPtr.Zero)
                    return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
                var ownerForm = System.Windows.Forms.NativeWindow.FromHandle(handle);
                return dialog.ShowDialog(ownerForm) == System.Windows.Forms.DialogResult.OK
                    ? dialog.SelectedPath : null;
            }
        }

        /// <summary>Download the newest engine into <paramref name="dir"/>.</summary>
        public static async Task<FirstRunOutcome> InstallAsync(Window owner, string dir)
        {
            var progress = new InstallProgress();
            try
            {
                if (owner != null && owner.IsLoaded) progress.Owner = owner;
                progress.Show();
                progress.SetStatus("Ищу последнюю версию zapret…");
                await Task.Delay(50);

                var release = await Task.Run(() => Engine.LatestRelease());
                if (release == null || string.IsNullOrEmpty(release.assetUrl))
                {
                    progress.Close();
                    MessageBox.Show(owner, "Не удалось получить список релизов с GitHub.\nПроверьте интернет.",
                        "Zapret Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return FirstRunOutcome.Failed;
                }

                progress.SetStatus("Скачиваю " + release.assetName + "…");
                progress.SetProgress(0);
                string zip = Path.Combine(Path.GetTempPath(), "zapret-firstrun.zip");
                bool ok = await Task.Run(() => Engine.Download(release.assetUrl, zip,
                    percent => progress.SetProgress((int)(percent * 0.9))));
                if (!ok)
                {
                    progress.Close();
                    MessageBox.Show(owner, "Не удалось скачать архив. Проверьте интернет и попробуйте ещё раз.",
                        "Zapret Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return FirstRunOutcome.Failed;
                }

                progress.SetStatus("Распаковываю в " + dir + "…");
                progress.SetProgress(95);
                var result = await Task.Run(() => Engine.Install(dir, zip, release.name));
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
                progress.Close();
                if (!result.ok)
                {
                    MessageBox.Show(owner, "Не удалось распаковать архив:\n" + (result.reason ?? "?") + " " + result.message,
                        "Zapret Panel", MessageBoxButton.OK, MessageBoxImage.Error);
                    return FirstRunOutcome.Failed;
                }

                // Remember the folder so the panel finds zapret from now on.
                Config.Set("zapretRoot", dir);
                Config.Set("zapretDeclined", "false");

                int added = EnsureTelegramDomains(dir);
                Logger.Info("zapret установлен в " + dir + ", файлов: " + result.copied.Length
                    + ", доменов добавлено: " + added, "engine");
                MessageBox.Show(owner,
                    "zapret установлен в:\n" + dir + "\n\nДоменов для Telegram добавлено: " + added
                    + ".\nМожно подключать рабочий обход.",
                    "Zapret Panel", MessageBoxButton.OK, MessageBoxImage.Information);
                return FirstRunOutcome.Installed;
            }
            catch (Exception ex)
            {
                try { progress.Close(); } catch { }
                Logger.Error("Первичная установка zapret не удалась: " + ex.Message, "engine");
                MessageBox.Show(owner, "Ошибка установки zapret:\n" + ex.Message,
                    "Zapret Panel", MessageBoxButton.OK, MessageBoxImage.Error);
                return FirstRunOutcome.Failed;
            }
        }

        /// <summary>
        /// Put the Telegram bypass domains into the zapret hostlist, once.
        ///
        /// The panel's own Cloudflare relay needs kws1..kws6 pointing at the
        /// user's own domain, and zapret has no Telegram entries of its own, so
        /// without these lines Telegram stays blocked after an install.
        /// Returns how many lines were added.
        /// </summary>
                /// <summary>
        /// The domain list the built-in Telegram proxy needs. Exposed so the
        /// Lists page can file these into the "Telegram" category when it
        /// reorganises the domain list.
        /// </summary>
        public static List<string> TelegramDomains()
        {
            var wanted = new List<string>();
            string domain = Config.Get("tgCfProxyDomain", null);
            if (string.IsNullOrWhiteSpace(domain)) domain = Tg.TgProxy.CustomCfDomain;
            if (string.IsNullOrWhiteSpace(domain)) domain = "wtfprivet.online";
            domain = domain.Trim().TrimStart('.').ToLowerInvariant();
            if (domain.Length == 0) return wanted;
            for (int dc = 1; dc <= 6; dc++) wanted.Add("kws" + dc + "." + domain);
            // Cloudflare fronts Telegram too, otherwise the web/worker routes
            // keep being cut by the same DPI.
            wanted.Add("cloudflare.com");
            wanted.Add("cloudflare.dev");
            wanted.Add("workers.dev");
            return wanted;
        }

        public static int EnsureTelegramDomains(string root)
        {
            if (string.IsNullOrEmpty(root)) return 0;
            var wanted = TelegramDomains();
            if (wanted.Count == 0) return 0;

            string path = Path.Combine(root, "lists", "list-general-user.txt");
            try
            {
                var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                int added = 0;
                foreach (var entry in wanted)
                {
                    if (existing.Split('\n').Any(l => string.Equals(l.Trim(), entry, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    added++;
                }
                // Store the proxy domains under their own "# === Telegram ==="
                // section so the file stays organised and nothing else is
                // disturbed.
                var updated = CategorizedDomains.Ensure(existing, CategorizedDomains.SectionTelegram, wanted);
                if (updated != existing) File.WriteAllText(path, updated);
                return added;
            }
            catch (Exception ex)
            {
                Logger.Warn("Не удалось дописать домены Telegram: " + ex.Message, "lists");
                return 0;
            }
        }
    }
}