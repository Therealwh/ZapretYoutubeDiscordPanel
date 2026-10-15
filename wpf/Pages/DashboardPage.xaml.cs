using System;
using System.Windows;
using System.Windows.Input;
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
            LoadWebProxy();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            // Without this the timer keeps firing after leaving the page and
            // pins the page instance in memory.
            _timer.Stop();
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

/// <summary>
        /// The switch on this page is the primary control, so it has to do the
        /// obvious thing: off means connect, on means disconnect. It used to be a
        /// picture with no handler at all.
        /// </summary>
        private async void Switch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_busy) return;
            var status = Status.GetStatus(Status.ResolveRoot(Config.Get("zapretRoot", null)));
            bool on = (status.service != null && status.service.running) || status.winwsProcess;
            if (on)
            {
                BtnStop_Click(sender, new RoutedEventArgs());
                return;
            }
            BtnAuto_Click(sender, new RoutedEventArgs());
            await System.Threading.Tasks.Task.Yield();
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

        // ---- веб-прокси ----------------------------------------------------

        private void LoadWebProxy()
        {
            if (WebServerBox != null && !WebServerBox.IsLoaded) return;
            WebServerBox.Text = Config.Get("tgWebServer", "tgchat.run");
            WebSecretBox.Text = Config.Get("tgWebSecret", "dd5add7f440f0c60fc46e8b5e8ac69b737");
        }

        private string WebServer => NormalizeWebServer(WebServerBox.Text);

        private string WebSecret => (WebSecretBox.Text ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>Accept a bare host or a full tg://webproxy link and keep only the host.</summary>
        private static string NormalizeWebServer(string raw)
        {
            string value = (raw ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            if (value.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(value, @"server=([^&\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim().ToLowerInvariant();
                return string.Empty;
            }
            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(8);
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7);
            int slash = value.IndexOf('/');
            if (slash > 0) value = value.Substring(0, slash);
            return value.Trim().ToLowerInvariant();
        }

        private string WebLink()
        {
            string server = WebServer;
            string secret = WebSecret;
            if (server.Length == 0) return string.Empty;
            return "tg://webproxy?server=" + server + "&secret=" + secret;
        }

        private void SaveWebProxy()
        {
            Config.Set("tgWebServer", WebServer);
            Config.Set("tgWebSecret", WebSecret);
        }

        private void BtnWebConnect_Click(object sender, RoutedEventArgs e)
        {
            string server = WebServer;
            if (server.Length == 0)
            {
                WebNote.Text = "Укажите адрес прокси.";
                return;
            }
            SaveWebProxy();
            string link = WebLink();
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
                WebNote.Text = "Открываю Telegram с веб-прокси " + server + "…";
            }
            catch (Exception ex)
            {
                WebNote.Text = "Не удалось открыть: " + ex.Message + " · ссылка: " + link;
            }
        }

        private void BtnCopyServer_Click(object sender, RoutedEventArgs e)
        {
            CopyText(WebServer, "Адрес скопирован");
        }

        private void BtnCopySecret_Click(object sender, RoutedEventArgs e)
        {
            CopyText(WebSecret, "Секрет скопирован");
        }

        private void CopyText(string value, string message)
        {
            try
            {
                Clipboard.SetText(value ?? string.Empty);
                WebNote.Text = message + ": " + value;
            }
            catch (Exception ex)
            {
                WebNote.Text = "Не удалось скопировать: " + ex.Message;
            }
        }

        /// <summary>Everything the dashboard shows, collected off the UI thread.
        /// SCM/WMI service queries cost tens of milliseconds each; doing them in
        /// the tick handler is what used to make the page stutter every second.
        /// </summary>
        private sealed class DashSnapshot
        {
            public string Root;
            public StatusSnapshot Status;
            public System.Collections.Generic.List<string> Remembered;
            public bool AutoOn;
            public bool AutoApply;
        }

        private bool _refreshing;

        private void Refresh()
        {
            // Skip if the previous snapshot has not landed yet: the timer must
            // never queue a second blocking read while one is in flight.
            if (_refreshing) return;
            _refreshing = true;
            string configured = Config.Get("zapretRoot", null);

            System.Threading.Tasks.Task.Run(() =>
            {
                var root = Status.ResolveRoot(configured);
                return new DashSnapshot
                {
                    Root = root,
                    Status = Status.GetStatus(root),
                    Remembered = AutoConnect.KnownWorking(),
                    AutoOn = AutoStart.IsEnabled(),
                    AutoApply = AutoStart.AutoApplyEnabled(),
                };
            }).ContinueWith(t =>
            {
                _refreshing = false;
                if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion) return;
                var snap = t.Result;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ApplySnapshot(snap); }
                    catch (Exception ex)
                    {
                        // One bad frame must never freeze the whole page silently.
                        Logger.Warn("Refresh не выполнен: " + ex.Message, "panel");
                    }
                }));
            });
        }

        private void ApplySnapshot(DashSnapshot snap)
        {
            string root = snap.Root;
            var st = snap.Status;

            bool on = st.activeMode != "off";
            PowerState.Text = on
                ? "Включён · " + (st.serviceStrategy ?? "текущая стратегия")
                : "Выключен — обход не активен";
            PowerState.Foreground = on
                ? (System.Windows.Media.Brush)FindResource("OkBrush")
                : (System.Windows.Media.Brush)FindResource("ErrBrush");
            // The whole card carries the state: green border + glow when the
            // bypass is up, red when it is down. You do not need to read
            // anything to know whether it works.
            BypassCard.BorderBrush = on
                ? (System.Windows.Media.Brush)FindResource("OkBrush")
                : (System.Windows.Media.Brush)FindResource("ErrBrush");
            BypassCard.Effect = (System.Windows.Media.Effects.Effect)FindResource(on ? "GlowOk" : "GlowErr");
            SwitchTrack.Background = on
                ? (System.Windows.Media.Brush)FindResource("OkBrush")
                : (System.Windows.Media.Brush)FindResource("BgHoverBrush");
            // FindResource(null) throws — and this Refresh dies mid-way, leaving
            // every tile frozen on the values from before the switch. "Off"
            // simply has no glow.
            SwitchTrack.Effect = on ? (System.Windows.Media.Effects.Effect)FindResource("GlowOk") : null;
            Knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Knob.Margin = on ? new Thickness(4, 0, 4, 0) : new Thickness(4, 0, 0, 0);
            Knob.Fill = on
                ? (System.Windows.Media.Brush)FindResource("TextBrush")
                : (System.Windows.Media.Brush)FindResource("TextDimBrush");

            StatStrategy.Text = string.IsNullOrEmpty(st.serviceStrategy) ? "—" : st.serviceStrategy;
            SetTile(TileStrategy, string.IsNullOrEmpty(st.serviceStrategy) ? "idle" : "ok");
            StatMode.Text = st.activeMode == "service" ? "Служба Windows"
                : st.activeMode == "process" ? "Процесс" : "—";
            SetTile(TileMode, st.activeMode == "service" ? "ok" : st.activeMode == "process" ? "warn" : "idle");

            StatZapret.Text = Describe(st.service);
            SetTile(TileZapret, ServiceTileState(st.service));

            StatWinDivert.Text = st.winDivert == null ? "Нет службы" : Describe(st.winDivert);
            SetTile(TileWinDivert, st.winDivert == null ? "err" : ServiceTileState(st.winDivert));

            StatWinws.Text = st.winwsProcess ? "Запущен" : "Не запущен";
            SetTile(TileWinws, st.winwsProcess ? "ok" : "err");
            StatWinwsPath.Text = st.winwsProcessPath ?? "";
            StatRoot.Text = string.IsNullOrEmpty(root) ? "не найдена" : root;
            SetTile(TileRoot, string.IsNullOrEmpty(root) ? "err" : "ok");

            var remembered = snap.Remembered;
            StatRemembered.Text = remembered.Count == 0
                ? "не запомнено"
                : System.IO.Path.GetFileName(remembered[0]);
            SetTile(TileRemembered, remembered.Count == 0 ? "idle" : "ok");

            StatAutostart.Text = snap.AutoOn
                ? (snap.AutoApply ? "включён" : "только панель")
                : "выключен";
            SetTile(TileAutostart, snap.AutoOn ? (snap.AutoApply ? "ok" : "warn") : "idle");

            
            TgState.Text = TgProxy.Running ? "Включён" : "Выключен";
            TgState.Foreground = TgProxy.Running
                ? (System.Windows.Media.Brush)FindResource("OkBrush")
                : (System.Windows.Media.Brush)FindResource("TextDimBrush");
            // The toggle must show what the next click will DO. A button that
            // keeps saying "Запустить" while the proxy is running looks broken.
            BtnTgToggle.Content = TgProxy.Running ? "Остановить" : "Запустить";
            BtnTgToggle.Style = (Style)FindResource(TgProxy.Running ? "BtnDanger" : "BtnPrimary");
            // Only one line about the state: the hint already names the endpoint.
            TgStats.Text = TgProxy.Running
                ? "подключений: " + TgProxy.Stats.connections + " · вверх: " + Human(TgProxy.Stats.bytesUp)
                  + " · вниз: " + Human(TgProxy.Stats.bytesDown)
                : string.Empty;
            TgLink.Text = TgProxy.ProxyLink();
        }

        /// <summary>
        /// Apply a status tile style: ok/warn/err carry border + tint + glow,
        /// idle is the plain card. One line per tile keeps the state story
        /// readable in code as well as on screen.
        /// </summary>
        private void SetTile(System.Windows.Controls.Border tile, string state)
        {
            var name = "Tile" + char.ToUpperInvariant(state[0]) + state.Substring(1);
            if (TryFindResource(name) is Style style) tile.Style = style;
        }

        private static string ServiceTileState(ServiceInfoView service)
        {
            if (service == null || !service.exists) return "err";
            return service.running ? "ok" : "warn";
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
            SetBusy(true, "Подбираю рабочую стратегию…");
            BtnStop.IsEnabled = false;
            PowerState.Text = "Подбираю рабочую стратегию…";
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            try
            {
if (string.IsNullOrEmpty(root))
    {
        // The user pressed "connect" - do the obvious thing instead of printing
        // "zapret not found" and leaving them stuck. An earlier design asked the
        // same question in a modal box on startup, where it swallowed every
        // click on the main window and looked exactly like a hang.
        PowerState.Text = "zapret не найден — ставлю его, выберите папку…";
        string installed = await Zapret.FirstRun.InstallInteractiveAsync(Window.GetWindow(this));
        if (string.IsNullOrEmpty(installed))
        {
            PowerState.Text = "zapret не установлен. Поставьте его в «Обновления» или в настройках укажите папку.";
            return;
        }
        root = installed;
        PowerState.Text = "zapret установлен, подключаю обход…";
    }
                var result = await System.Threading.Tasks.Task.Run(() => AutoConnect.Connect(root,
                    msg => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        PowerState.Text = msg;
                        BusyText.Text = msg;
                    })), null));
                PowerState.Text = DescribeResult(result);
            }
            catch (Exception ex)
            {
                // async void: an unhandled exception here crashes the whole app.
                PowerState.Text = "Ошибка подбора: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
                BtnStop.IsEnabled = true;
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

        /// <summary>
        /// The visible "not frozen" signal: connecting legitimately takes tens of
        /// seconds (service swap, waiting winws out, probe retries), and a
        /// silently disabled button reads as a hang.
        /// </summary>
        private void SetBusy(bool busy, string step = null)
        {
            _busy = busy;
            BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (step != null) BusyText.Text = step;
            BtnAuto.IsEnabled = !busy;
            BtnRestart.IsEnabled = !busy;
        }

        private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(root)) return;
            SetBusy(true, "Перезапускаю обход: останавливаю прежний…");
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    Services.ServiceOps.KillWinws();
                    SetBusyText("Перезапуск: ставлю службу и жду запуска winws…");
                    Services.ServiceOps.StopService();
                    Services.ServiceOps.StartService();
                });
            }
            catch (Exception ex)
            {
                PowerState.Text = "Ошибка перезапуска: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
                Refresh();
            }
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, "Останавливаю обход…");
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    Services.ServiceOps.KillWinws();
                    Services.ServiceOps.StopService();
                });
            }
            catch (Exception ex)
            {
                PowerState.Text = "Ошибка остановки: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
                Refresh();
            }
        }

        /// <summary>Thread-safe step text update for the busy panel. BeginInvoke:
        /// the producer (a background job) must never wait for the UI frame.</summary>
        private void SetBusyText(string text)
        {
            Dispatcher.BeginInvoke(new Action(() => BusyText.Text = text));
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
            if (_busy) return;
            QuickNote.Text = "Проверяю текущий обход…";
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(root))
            {
                QuickNote.Text = "Не найдена папка zapret.";
                return;
            }
            // A verify run touches the same service the connect/restart buttons
            // manage, so it has to respect the same busy lock.
            SetBusy(true, "Проверяю текущий обход…");
            try
            {
                var verify = await System.Threading.Tasks.Task.Run(() => Tester.VerifyStrategy(root, attempts: 1));
                QuickNote.Text = verify.ok
                    ? "Обход работает: Discord и YouTube отвечают."
                    : "Обход не отвечает (проверена одна попытка).";
            }
            catch (Exception ex)
            {
                QuickNote.Text = "Ошибка проверки: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
                Refresh();
            }
        }
    }
}