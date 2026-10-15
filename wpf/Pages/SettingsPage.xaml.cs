using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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

        /// <summary>
        /// Runs an action on the UI thread, quietly skipping it when the app is
        /// already shutting down: a background continuation must not throw after
        /// the window is gone.
        /// </summary>
        private void OnUi(Action action)
        {
            try
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.Invoke(action);
            }
            catch { /* dispatcher gone between the check and the invoke */ }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Load();
            // Listing adapters touches the network stack, so keep it off the UI
            // thread — and hand the result over instead of letting
            // LoadDnsAdapters poll the network again on the UI thread.
            System.Threading.Tasks.Task.Run(() => Win.ListDnsAdapters())
                .ContinueWith(t =>
                {
                    var adapters = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : new List<Win.DnsAdapter>();
                    OnUi(() => LoadDnsAdapters(adapters));
                });
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

/// <summary>
        /// Modal list picker used by both "remember" and "forget". Returns the
        /// chosen index, or null if the user cancelled. Built in code, so the
        /// list gets an explicit dark style - a bare ListBox keeps the default
        /// white background while inheriting the app's light foreground, which
        /// is white-on-white and unreadable.
        /// </summary>
        private int? ShowStrategyPicker(string title, string question, List<string> labels, string okText)
        {
            var textBrush = TryFindResource("TextBrush") as System.Windows.Media.Brush;
            var accentBrush = TryFindResource("AccentBrush") as System.Windows.Media.Brush;
            var hoverBrush = TryFindResource("BgHoverBrush") as System.Windows.Media.Brush;

            var list = new ListBox
            {
                Height = 280,
                Margin = new Thickness(0, 0, 0, 12),
                BorderThickness = new Thickness(0),
            };
            if (textBrush != null) list.Foreground = textBrush;
            if (hoverBrush != null) list.Background = hoverBrush;
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, textBrush ?? System.Windows.Media.Brushes.White));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
            var selTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selTrigger.Setters.Add(new Setter(Control.BackgroundProperty, accentBrush ?? System.Windows.Media.Brushes.DodgerBlue));
            selTrigger.Setters.Add(new Setter(Control.ForegroundProperty, System.Windows.Media.Brushes.White));
            itemStyle.Triggers.Add(selTrigger);
            list.ItemContainerStyle = itemStyle;
            foreach (var label in labels) list.Items.Add(label);
            if (list.Items.Count > 0) list.SelectedIndex = 0;

            Window dlg = null;
            var okBtn = new Button { Content = okText, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 6, 16, 6), MinWidth = 120 };
            if (TryFindResource("BtnPrimary") is Style primary) okBtn.Style = primary;
            var cancelBtn = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), MinWidth = 100 };
            if (TryFindResource("Btn") is Style normal) cancelBtn.Style = normal;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = question, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 10) });
            panel.Children.Add(list);
            panel.Children.Add(buttons);

            bool confirmed = false;
            okBtn.Click += (s, e2) => { confirmed = true; dlg?.Close(); };
            dlg = new Window
            {
                Title = title,
                Content = panel,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 440,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };
            if (TryFindResource("BgCardBrush") is System.Windows.Media.Brush cardBg) dlg.Background = cardBg;
            if (textBrush != null) dlg.Foreground = textBrush;
            dlg.ShowDialog();
            return confirmed ? list.SelectedIndex : (int?)null;
        }

        /// <summary>
        /// Forget ONE remembered strategy instead of wiping the whole list, so
        /// the good ones survive.
        /// </summary>
        private void BtnForget_Click(object sender, RoutedEventArgs e)
        {
            var known = AutoConnect.KnownWorking();
            if (known.Count == 0) { AutoNote.Text = "Запоминаемых стратегий нет."; return; }

            var labels = known.Select(Path.GetFileNameWithoutExtension).ToList();
            var index = ShowStrategyPicker("Забыть стратегию", "Какую стратегию забыть?", labels, "Забыть");
            if (index == null) return;
            int i = index.Value;
            if (i < 0 || i >= known.Count) return;

            string gone = known[i];
            var remaining = new List<string>(known);
            remaining.RemoveAt(i);
            Config.Set(AutoConnect.KeyKnownWorking, string.Join("|", remaining));
            // If we just forgot the current winner, move the pointer to the
            // next remembered one (or clear it) so the dashboard does not keep
            // pointing at something the user removed.
            if (string.Equals(AutoConnect.LastWinner(), gone, StringComparison.OrdinalIgnoreCase))
                Config.Set(AutoConnect.KeyLastWinner, remaining.Count > 0 ? remaining[0] : string.Empty);
            AutoNote.Text = "Забыта стратегия: " + Path.GetFileNameWithoutExtension(gone)
                + (remaining.Count > 0 ? ". Осталось запомненных: " + remaining.Count : ". Больше ничего не запомнено.");
            Logger.Info("Стратегия забыта вручную: " + gone, "settings");
            Load();
        }

        /// <summary>
        /// Manually pick the strategy to remember, so the dashboard power switch
        /// (which now prefers a remembered strategy over re-searching) brings up
        /// exactly this one. Replaces the old workflow of "run the whole test,
        /// then hope it picks the one I wanted".
        /// </summary>
        private void BtnRememberStrategy_Click(object sender, RoutedEventArgs e)
        {
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(root)) { AutoNote.Text = "Не найдена папка zapret."; return; }

            var strategies = Strategy.ListStrategies(root);
            if (strategies.Count == 0) { AutoNote.Text = "В папке zapret нет стратегий."; return; }

            var labels = strategies.Select(s => s.name).ToList();
            var index = ShowStrategyPicker("Запомнить стратегию", "Какую стратегию запомнить?", labels, "Запомнить");
            if (index == null) return;
            int i = index.Value;
            if (i < 0 || i >= strategies.Count) return;

            var picked = strategies[i];
            AutoConnect.RememberWorking(picked.file);
            Config.Set(AutoConnect.KeyLastWinner, picked.file);
            AutoNote.Text = "Запомнена стратегия: " + picked.name + " — тумблер в «Обход» будет включать её.";
            Logger.Info("Стратегия запомнена вручную: " + picked.file, "settings");
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
                    // Someone installing the panel without the bundled engine
                    // points at their own zapret here. The panel's Telegram
                    // domains must follow the folder the moment it is picked,
                    // not on the next restart.
                    int added = Zapret.FirstRun.EnsureTelegramDomains(dialog.SelectedPath);
                    Logger.Info("Выбрана папка zapret: " + dialog.SelectedPath
                        + (added > 0 ? " (дописано доменов: " + added + ")" : ""), "lists");
                }
            }
            Load();
        }

        private async void BtnAutoRoot_Click(object sender, RoutedEventArgs e)
        {
            // Fast pass first: standard locations (one cache tick, instant).
            var found = Status.ResolveRoot(null);
            if (string.IsNullOrEmpty(found))
            {
                // Nothing standard: sweep every drive for bin\winws.exe. This
                // covers "panel installed as a subfolder of my own zapret"
                // and any non-standard location the known list never knew.
                AutoNote.Text = "Ищу zapret по всем дискам… это может занять минуту.";
                BtnAutoRoot.IsEnabled = false;
                try
                {
                    found = await System.Threading.Tasks.Task.Run(() =>
                        Status.FindRootDeep(msg => Dispatcher.BeginInvoke(new Action(() => AutoNote.Text = msg))));
                }
                finally
                {
                    BtnAutoRoot.IsEnabled = true;
                }
            }

            if (!string.IsNullOrEmpty(found))
            {
                Config.Set("zapretRoot", found);
                int added = Zapret.FirstRun.EnsureTelegramDomains(found);
                AutoNote.Text = "Найдено: " + found
                    + (added > 0 ? " · домены Telegram дописаны: " + added : "");
            }
            else
            {
                AutoNote.Text = "Не нашёл. Укажите папку zapret вручную кнопкой «Выбрать папку…».";
            }
            Load();
        }

        /// <summary>
        /// Fills the adapter list.
        ///
        /// A machine with several adapters (Ethernet + Wi-Fi + VPN) has no single
        /// right answer, so the user picks rather than us guessing and silently
        /// reconfiguring the wrong interface.
        /// </summary>
        /// <summary>
        /// Re-lists the adapters in the background after an apply/reset and
        /// rebinds the fresh list.
        /// </summary>
        private void RefreshDnsAdaptersAsync()
        {
            System.Threading.Tasks.Task.Run(() => Win.ListDnsAdapters())
                .ContinueWith(t =>
                {
                    var adapters = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : new List<Win.DnsAdapter>();
                    OnUi(() => LoadDnsAdapters(adapters));
                });
        }

        /// <summary>
        /// Binds a pre-fetched adapter list. The network poll itself happens in
        /// the background: doing it here again would freeze the UI thread.
        /// </summary>
        private void LoadDnsAdapters(List<Win.DnsAdapter> adapters)
        {
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
                    RefreshDnsAdaptersAsync();
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
                    RefreshDnsAdaptersAsync();
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