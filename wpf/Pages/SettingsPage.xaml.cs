using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Ydp.Services;
using Ydp.Zapret;

namespace Ydp.Pages
{
    public partial class SettingsPage : UserControl
    {
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Load();
        }

        private void Load()
        {
            _loading = true;
            try
            {
                ChkAutostart.IsChecked = AutoStart.IsEnabled();
                ChkAutoApply.IsChecked = AutoStart.AutoApplyEnabled();
                ChkServiceMode.IsChecked = !string.Equals(Config.Get(AutoConnect.KeyPreferService, "true"), "false", StringComparison.OrdinalIgnoreCase);
                WorkerBox.Text = Config.Get("tgWorkerDomain", string.Empty);
                Tg.TgProxy.WorkerDomain = WorkerBox.Text;
                CfProxyBox.Text = Config.Get("tgCfProxyDomain", string.Empty);
                Tg.TgProxy.CustomCfDomain = CfProxyBox.Text;

                var known = AutoConnect.KnownWorking();
                Remembered.Text = string.IsNullOrEmpty(AutoConnect.LastWinner()) ? "— ещё не запомнена" : Path.GetFileName(AutoConnect.LastWinner());
                RememberedList.Text = known.Count == 0
                    ? "История пуста — стратегия запомнится после первого успешного подключения."
                    : "Запомнено (" + known.Count + "): " + string.Join(", ", known.Select(Path.GetFileName));

                string configured = Config.Get("zapretRoot", null);
                string root = Status.ResolveRoot(configured);
                RootValue.Text = string.IsNullOrEmpty(root) ? "не найдена" : root;
                var validation = Status.ValidateRoot(root ?? string.Empty);
                RootIssues.Text = validation.ok && validation.issues.Length > 0
                    ? "Замечания: " + string.Join(", ", validation.issues)
                    : (validation.ok ? "Папка в порядке." : "Проблема: " + validation.reason);

                AdminNote.Text = Win.IsAdmin()
                    ? "Панель запущена с правами администратора — установка службы и запуск winws доступны."
                    : "Панель запущена без прав администратора. Установка службы и запуск winws потребуют подтверждения в UAC.";
            }
            finally
            {
                _loading = false;
            }
        }

        private void ChkAutostart_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool enabled = ChkAutostart.IsChecked == true;
            if (AutoStart.SetEnabled(enabled, ChkAutoApply.IsChecked == true))
            {
                AutoNote.Text = enabled
                    ? "Панель будет запускаться при входе в Windows."
                    : "Автозапуск выключен.";
            }
            else
            {
                AutoNote.Text = "Не удалось изменить автозапуск.";
            }
        }

        private void ChkAutoApply_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AutoStart.SetEnabled(ChkAutostart.IsChecked == true, ChkAutoApply.IsChecked == true);
            AutoNote.Text = ChkAutoApply.IsChecked == true
                ? "Запомненная рабочая стратегия будет применяться при каждом входе в Windows."
                : "Автоприменение выключено — обход поднимется только через службу Windows.";
            Load();
        }

        private void ChkServiceMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Config.Set(AutoConnect.KeyPreferService, ChkServiceMode.IsChecked == true ? "true" : "false");
        }

        /// <summary>Strip scheme/path and validate a bare domain.</summary>
        private static string NormalizeDomain(string raw)
        {
            string domain = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) domain = domain.Substring(8);
            if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) domain = domain.Substring(7);
            int slash = domain.IndexOf('/');
            if (slash > 0) domain = domain.Substring(0, slash);
            return domain;
        }

        private void BtnSaveCfProxy_Click(object sender, RoutedEventArgs e)
        {
            string domain = NormalizeDomain(CfProxyBox.Text);
            if (domain.Length > 0 && !domain.Contains("."))
            {
                CfProxyNote.Text = "Это не похоже на домен.";
                return;
            }
            CfProxyBox.Text = domain;
            Config.Set("tgCfProxyDomain", domain);
            Tg.TgProxy.CustomCfDomain = domain;
            CfProxyNote.Text = domain.Length == 0
                ? "Домен очищен."
                : "Сохранено. Маршруты: kws1..kws6." + domain + ". Домены должны резолвиться и быть прописаны в списках zapret.";
        }

        /// <summary>Write kws1..kws6.&lt;domain&gt; into the zapret user hostlist.</summary>
        private void BtnAddToLists_Click(object sender, RoutedEventArgs e)
        {
            string domain = NormalizeDomain(CfProxyBox.Text);
            if (domain.Length == 0)
            {
                CfProxyNote.Text = "Сначала укажите домен.";
                return;
            }
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(root))
            {
                CfProxyNote.Text = "Не найдена папка zapret.";
                return;
            }
            string path = System.IO.Path.Combine(root, "lists", "list-general-user.txt");
            try
            {
                var domains = new List<string>();
                for (int dc = 1; dc <= 6; dc++) domains.Add("kws" + dc + "." + domain);
                var existing = System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path) : new string[0];
                var merged = new List<string>(existing);
                foreach (var entry in domains)
                {
                    if (!merged.Any(l => string.Equals(l.Trim(), entry, StringComparison.OrdinalIgnoreCase)))
                    {
                        merged.Add(entry);
                    }
                }
                System.IO.File.WriteAllLines(path, merged);
                CfProxyNote.Text = "Добавлено в " + path + ". Перезапустите обход, чтобы список применился.";
                Logger.Info("Домены " + domain + " добавлены в список zapret", "lists");
            }
            catch (Exception ex)
            {
                CfProxyNote.Text = "Не удалось записать список: " + ex.Message;
            }
        }

        private async void BtnApplyRemembered_Click(object sender, RoutedEventArgs e)
        {
            var remembered = AutoConnect.KnownWorking();
            if (remembered.Count == 0)
            {
                AutoNote.Text = "Пока нечего применять — сначала подберите рабочую стратегию.";
                return;
            }
            AutoNote.Text = "Применяю…";
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(root)) { AutoNote.Text = "Не найдена папка zapret."; return; }
            bool preferService = !string.Equals(Config.Get(AutoConnect.KeyPreferService, "true"), "false", StringComparison.OrdinalIgnoreCase);
            var result = await System.Threading.Tasks.Task.Run(() =>
                preferService
                    ? ServiceOps.InstallStrategyAsService(root, remembered[0])
                    : ServiceOps.LaunchStrategyProcess(root, remembered[0]));
            AutoNote.Text = result.ok
                ? "Подключено: " + Path.GetFileName(remembered[0]) + (preferService ? " (служба)" : " (процесс)")
                : "Не удалось: " + (result.reason ?? "ошибка");
        }

        private void BtnForget_Click(object sender, RoutedEventArgs e)
        {
            Config.Set(AutoConnect.KeyKnownWorking, string.Empty);
            Config.Set(AutoConnect.KeyLastWinner, string.Empty);
            AutoNote.Text = "Запомненные стратегии очищены.";
            Load();
        }

        private void BtnChooseRoot_Click(object sender, RoutedEventArgs e)
        {
            // net48 has no OpenFolderDialog; FolderBrowserDialog is the classic
            // one and works from WPF as long as System.Windows.Forms is referenced.
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку zapret";
                var first = Status.CandidateRoots().FirstOrDefault();
                if (!string.IsNullOrEmpty(first) && Directory.Exists(first)) dialog.SelectedPath = first;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    Config.Set("zapretRoot", dialog.SelectedPath);
                }
            }
            Load();
        }

        private void BtnAutoRoot_Click(object sender, RoutedEventArgs e)
        {
            var found = Status.ResolveRoot(null);
            if (!string.IsNullOrEmpty(found)) Config.Set("zapretRoot", found);
            AutoNote.Text = string.IsNullOrEmpty(found) ? "Автоматически не нашёл." : "Найдено: " + found;
            Load();
        }

        private void BtnSaveWorker_Click(object sender, RoutedEventArgs e)
        {
            string domain = (WorkerBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            // Accept a bare domain or a full URL; strip scheme and path.
            if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) domain = domain.Substring(8);
            if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) domain = domain.Substring(7);
            int slash = domain.IndexOf('/');
            if (slash > 0) domain = domain.Substring(0, slash);
            if (domain.Length > 0 && !domain.Contains("."))
            {
                WorkerNote.Text = "Это не похоже на домен.";
                return;
            }
            WorkerBox.Text = domain;
            Config.Set("tgWorkerDomain", domain);
            Tg.TgProxy.WorkerDomain = domain;
            WorkerNote.Text = domain.Length == 0
                ? "Домен очищен — прокси будет использовать прямые маршруты и общий список."
                : "Сохранено: " + domain;
        }

        private async void BtnProbeRoutes_Click(object sender, RoutedEventArgs e)
        {
            WorkerNote.Text = "Проверяю маршруты…";
            BtnProbe.IsEnabled = false;
            try
            {
                Tg.TgProxy.WorkerDomain = (WorkerBox.Text ?? string.Empty).Trim();
                var routes = await System.Threading.Tasks.Task.Run(() => Tg.TgProxy.ProbeRoutesAsync(2,
                    (msg, level) => Logger.Add(level == "info" ? "info" : level, msg, "tg-proxy")));
                WorkerNote.Text = string.Join(Environment.NewLine, routes);
            }
            catch (Exception ex)
            {
                WorkerNote.Text = "Ошибка проверки: " + ex.Message;
            }
            finally
            {
                BtnProbe.IsEnabled = true;
            }
        }
    }
}