using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ydp.Services;
using Ydp.Tg;
using Ydp.Zapret;

namespace Ydp.Pages
{
    public partial class DashboardPage : UserControl
    {
        private readonly DispatcherTimer _timer;
        private bool _busy;

        public DashboardPage()
        {
            InitializeComponent();
            // Refresh once a second while the panel is open: every field comes
            // from a direct API call, so this costs no child processes.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Refresh();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _timer.Start();
            Refresh();
            TgProxyState();
        }

        // ---- Telegram proxy -------------------------------------------------

        private static int TgPort()
        {
            int.TryParse(Config.Get("tgProxyPort", "1443"), out int port);
            return port > 0 ? port : TgProxy.DefaultPort;
        }

        private static byte[] TgSecret()
        {
            string hex = Config.Get("tgProxySecret", null);
            if (!string.IsNullOrWhiteSpace(hex) && hex.Length == 32)
            {
                try
                {
                    var bytes = new byte[16];
                    for (int i = 0; i < 16; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                    return bytes;
                }
                catch
                {
                    // Fall through and generate a new secret.
                }
            }
            var fresh = TgCrypto.NewSecret();
            Config.Set("tgProxySecret", BitConverter.ToString(fresh).Replace("-", string.Empty).ToLowerInvariant());
            return fresh;
        }

        private void TgProxyState()
        {
            TgProxy.LoadSecret(TgSecret());
            TgProxy.RefreshStats();
            
            TgProxy.Stats.port = TgPort();
            
        }

        private async void BtnTgToggle_Click(object sender, RoutedEventArgs e)
        {
            BtnTgToggle.IsEnabled = false;
            try
            {
                if (TgProxy.Running)
                {
                    TgProxy.Stop();
                }
                else
                {
                    await TgProxy.StartAsync(TgProxy.DefaultHost, TgPort(), TgSecret(), true,
                        (msg, level) => Logger.Add(level == "info" ? "info" : level, msg, "tg-proxy"));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Прокси Telegram: " + ex.Message, "tg-proxy");
            }
            finally
            {
                BtnTgToggle.IsEnabled = true;
                TgProxyState();
                Refresh();
            }
        }

        private void BtnTgRegen_Click(object sender, RoutedEventArgs e)
        {
            var secret = TgProxy.RegenerateSecret();
            Config.Set("tgProxySecret", BitConverter.ToString(secret).Replace("-", string.Empty).ToLowerInvariant());
            if (TgProxy.Running)
            {
                TgProxy.Stop();
                var port = TgPort();
                System.Threading.Tasks.Task.Run(() => TgProxy.StartAsync(TgProxy.DefaultHost, port, secret, true,
                    (msg, level) => Logger.Add(level == "info" ? "info" : level, msg, "tg-proxy")));
            }
            TgProxyState();
            Refresh();
        }

        private void BtnTgCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TgProxy.ProxyLink());
                TgLink.Text = TgProxy.ProxyLink();
            }
            catch (Exception ex)
            {
                TgHint.Text = "Не удалось скопировать: " + ex.Message;
            }
        }

        private void BtnTgOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TgProxy.ProxyLink())
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                TgHint.Text = "Не удалось открыть Telegram: " + ex.Message;
            }
        }

private void Refresh()
        {
            string configured = Config.Get("zapretRoot", null);
            string root = Status.ResolveRoot(configured);
            var st = Status.GetStatus(root);

            bool on = st.activeMode != "off";
            PowerState.Text = on
                ? "Включён · " + (st.serviceStrategy ?? "текущая стратегия")
                : "Выключен — обход не активен";
            SwitchTrack.Background = on
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : (System.Windows.Media.Brush)FindResource("BorderBrush2");
            Knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Knob.Margin = on ? new Thickness(4, 0, 4, 0) : new Thickness(4, 0, 0, 0);
            Knob.Fill = on
                ? (System.Windows.Media.Brush)FindResource("TextBrush")
                : (System.Windows.Media.Brush)FindResource("TextDimBrush");

            StatStrategy.Text = string.IsNullOrEmpty(st.serviceStrategy) ? "—" : st.serviceStrategy;
            StatMode.Text = st.activeMode == "service" ? "Служба Windows"
                : st.activeMode == "process" ? "Процесс" : "—";
            StatZapret.Text = Describe(st.service);
            StatWinDivert.Text = st.winDivert == null ? "Нет службы" : Describe(st.winDivert);
            StatWinws.Text = st.winwsProcess ? "Запущен" : "Не запущен";
            StatWinwsPath.Text = st.winwsProcessPath ?? "";
            StatRoot.Text = string.IsNullOrEmpty(root) ? "не найдена" : root;

            var remembered = AutoConnect.KnownWorking();
            StatRemembered.Text = remembered.Count == 0
                ? "не запомнено"
                : System.IO.Path.GetFileName(remembered[0]);

            StatAutostart.Text = AutoStart.IsEnabled()
                ? (AutoStart.AutoApplyEnabled() ? "включён" : "только панель")
                : "выключен";

            
            TgState.Text = TgProxy.Running ? "Включён" : "Выключен";
            // Only one line about the state: the hint already names the endpoint.
            TgStats.Text = TgProxy.Running
                ? "подключений: " + TgProxy.Stats.connections + " · вверх: " + Human(TgProxy.Stats.bytesUp)
                  + " · вниз: " + Human(TgProxy.Stats.bytesDown)
                : string.Empty;
            TgLink.Text = TgProxy.ProxyLink();
        }

        private static string Describe(ServiceInfoView service)
        {
            if (service == null || !service.exists) return "Не установлена";
            return service.running ? "Работает" : "Остановлена";
        }

        private static string Human(long bytes)
        {
            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int i = 0;
            while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
            return value.ToString("0.0") + " " + units[i];
        }

        private async void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            BtnAuto.IsEnabled = false;
            BtnRestart.IsEnabled = false;
            PowerState.Text = "Подбираю рабочую стратегию…";
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            try
            {
                if (string.IsNullOrEmpty(root))
                {
                    PowerState.Text = "Папка zapret не найдена";
                    return;
                }
                var result = await System.Threading.Tasks.Task.Run(() => AutoConnect.Connect(root,
                    msg => Dispatcher.Invoke(() => PowerState.Text = msg), null));
                PowerState.Text = DescribeResult(result);
            }
            finally
            {
                _busy = false;
                BtnAuto.IsEnabled = true;
                BtnRestart.IsEnabled = true;
                Refresh();
            }
        }

        private static string DescribeResult(ConnectResult r)
        {
            if (r == null) return "Готово";
            if (r.ok && r.already) return "Уже работает: " + r.strategy;
            if (r.ok && r.restored) return "Вернул прежний обход: " + r.strategy;
            if (r.ok && r.fallback) return "Подключил лучшую доступную: " + r.strategy;
            if (r.ok) return "Рабочий обход подключён: " + r.strategy;
            return "Не удалось подключить: " + (r.reason ?? "ошибка");
        }

private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(root)) return;
            _busy = true;
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    Services.ServiceOps.KillWinws();
                    Services.ServiceOps.StopService();
                    Services.ServiceOps.StartService();
                });
            }
            finally
            {
                _busy = false;
                Refresh();
            }
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    Services.ServiceOps.KillWinws();
                    Services.ServiceOps.StopService();
                });
            }
            finally
            {
                _busy = false;
                Refresh();
            }
        }

        /// <summary>Jump to a section from the quick-actions card.</summary>
        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            var target = button?.Tag as string;
            if (string.IsNullOrEmpty(target)) return;
            if (Window.GetWindow(this) is MainWindow main) main.ShowSection(target);
        }

        private async void BtnFindWorking_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            QuickNote.Text = "Подбираю рабочую стратегию…";
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            try
            {
                if (string.IsNullOrEmpty(root))
                {
                    QuickNote.Text = "Не найдена папка zapret.";
                    return;
                }
                var result = await System.Threading.Tasks.Task.Run(() => AutoConnect.Connect(root,
                    msg => Dispatcher.Invoke(() => QuickNote.Text = msg), null));
                QuickNote.Text = DescribeResult(result);
            }
            catch (Exception ex)
            {
                QuickNote.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                _busy = false;
                Refresh();
            }
        }

        private async void BtnVerify_Click(object sender, RoutedEventArgs e)
        {
            QuickNote.Text = "Проверяю текущий обход…";
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(root))
            {
                QuickNote.Text = "Не найдена папка zapret.";
                return;
            }
            var verify = await System.Threading.Tasks.Task.Run(() => Tester.VerifyStrategy(root, attempts: 1));
            QuickNote.Text = verify.ok
                ? "Обход работает: Discord и YouTube отвечают."
                : "Обход не отвечает (проверена одна попытка).";
        }
    }
}