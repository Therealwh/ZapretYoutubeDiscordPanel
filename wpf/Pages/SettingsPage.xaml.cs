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

        /// <summary>
        /// An empty box looked like a broken panel. Say plainly whether the value
        /// is set, and give an example of the expected format.
        /// </summary>
        private void UpdateRelayHints()
        {
            if (WorkerBox == null) return;
            bool hasWorker = !string.IsNullOrWhiteSpace(WorkerBox.Text);
            WorkerHint.Visibility = hasWorker ? Visibility.Collapsed : Visibility.Visible;
            if (hasWorker)
            {
                WorkerNote.Text = "Сохранён: " + WorkerBox.Text.Trim();
            }
            else
            {
                WorkerNote.Text = "Воркер не задан. Telegram подключается напрямую.";
            }

            bool hasRelay = !string.IsNullOrWhiteSpace(CfProxyBox.Text);
            CfProxyHint.Visibility = hasRelay ? Visibility.Collapsed : Visibility.Visible;
            if (hasRelay) CfProxyNote.Text = "Сохранён: " + CfProxyBox.Text.Trim();
        }

        private void WorkerBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRelayHints();

        private void CfProxyBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRelayHints();

private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Load();
            // Listing adapters touches the network stack, so keep it off the UI thread.
            System.Threading.Tasks.Task.Run(() => Win.ListDnsAdapters())
                .ContinueWith(t => Dispatcher.Invoke(() => LoadDnsAdapters()));
        }

        private void Load()
        {
            _loading = true;
            try
            {
                ChkAutostart.IsChecked = AutoStart.IsEnabled();
                ChkAutoApply.IsChecked = AutoStart.AutoApplyEnabled();
                ChkServiceMode.IsChecked = !string.Equals(Config.Get(AutoConnect.KeyPreferService, "true"), "false", StringComparison.OrdinalIgnoreCase);
// Ship pre-filled: a fresh install works immediately, and anyone with their
    // own infrastructure overwrites it here.
    WorkerBox.Text = Config.Get("tgWorkerDomain", Tg.TgProxy.DefaultWorkerDomain);
  Tg.TgProxy.WorkerDomain = WorkerBox.Text;
  CfProxyBox.Text = Config.Get("tgCfProxyDomain", Tg.TgProxy.DefaultCfDomain);
  Tg.TgProxy.CustomCfDomain = CfProxyBox.Text;
  UpdateRelayHints();

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

        /// <summary>
        /// Fills the adapter list.
        ///
        /// A machine with several adapters (Ethernet + Wi-Fi + VPN) has no single
        /// right answer, so the user picks rather than us guessing and silently
        /// reconfiguring the wrong interface.
        /// </summary>
        private void LoadDnsAdapters()
        {
            var adapters = Win.ListDnsAdapters();
            DnsAdapterBox.ItemsSource = adapters;
            if (adapters.Count > 0) DnsAdapterBox.SelectedIndex = 0;
            if (adapters.Count == 0)
            {
                DnsNote.Text = "Активных адаптеров не найдено.";
                return;
            }
            var current = DnsAdapterBox.SelectedItem as Win.DnsAdapter;
            if (current != null)
            {
                DnsNote.Text = current.Dhcp
                    ? "Сейчас DNS от провайдера (DHCP): " + current.CurrentDns + ". Применение переключит на фиксированный DNS до перезагрузки сети."
                    : "Сейчас задан вручную: " + current.CurrentDns;
            }
        }

        private void DnsAdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var current = DnsAdapterBox.SelectedItem as Win.DnsAdapter;
            if (current == null) return;
            DnsNote.Text = current.Dhcp
                ? "Сейчас DNS от провайдера (DHCP): " + current.CurrentDns + ". Применение переключит на фиксированный DNS до перезагрузки сети."
                : "Сейчас задан вручную: " + current.CurrentDns;
        }

        private string SelectedAdapterName()
        {
            var current = DnsAdapterBox.SelectedItem as Win.DnsAdapter;
            return current == null ? null : current.Name;
        }

        private void SetDnsNote(string text, bool error)
        {
            DnsNote.Text = text;
            DnsNote.Foreground = error
                ? (System.Windows.Media.Brush)FindResource("WarnBrush")
                : (System.Windows.Media.Brush)FindResource("TextDimBrush");
        }

        private async void BtnApplyDns_Click(object sender, RoutedEventArgs e)
        {
            string adapter = SelectedAdapterName();
            if (string.IsNullOrEmpty(adapter))
            {
                SetDnsNote("Сначала выбери сетевой адаптер.", true);
                return;
            }
            string primary = (DnsPrimaryBox.Text ?? string.Empty).Trim();
            string secondary = (DnsSecondaryBox.Text ?? string.Empty).Trim();

            var button = sender as Button;
            if (button != null) button.IsEnabled = false;
            SetDnsNote("Применяю DNS…", false);
            try
            {
                var result = await System.Threading.Tasks.Task.Run(() => Win.SetDns(adapter, primary, secondary));
                if (result.ok)
                {
                    SetDnsNote("Готово: DNS " + primary + (string.IsNullOrEmpty(secondary) ? "" : " / " + secondary)
                        + ". Если браузер уже открыт — перезапусти его, иначе он продолжит резолвить по старому резолверу.", false);
                    LoadDnsAdapters();
                    return;
                }
                if (result.reason == "bad_dns")
                {
                    SetDnsNote("Нужен корректный IPv4-адрес, например 8.8.8.8 или 77.88.8.8.", true);
                    return;
                }
                SetDnsNote("Не удалось применить DNS. Возможно, нет прав администратора."
                    + (string.IsNullOrEmpty(result.message) ? "" : " " + result.message), true);
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

        private async void BtnResetDns_Click(object sender, RoutedEventArgs e)
        {
            string adapter = SelectedAdapterName();
            if (string.IsNullOrEmpty(adapter))
            {
                SetDnsNote("Сначала выбери сетевой адаптер.", true);
                return;
            }
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;
            SetDnsNote("Возвращаю DNS провайдера…", false);
            try
            {
                var result = await System.Threading.Tasks.Task.Run(() => Win.ResetDns(adapter));
                if (result.ok)
                {
                    SetDnsNote("Готово: DNS снова от провайдера (DHCP).", false);
                    LoadDnsAdapters();
                    return;
                }
                SetDnsNote("Не удалось вернуть DNS провайдера. Возможно, нет прав администратора."
                    + (string.IsNullOrEmpty(result.message) ? "" : " " + result.message), true);
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
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