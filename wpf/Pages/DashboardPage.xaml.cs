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

        private async void BtnTgFree_Click(object sender, RoutedEventArgs e)
        {
            BtnTgFree.IsEnabled = false;
            try
            {
                int port = TgPort();
                // netstat + kill: blocking, keep it off the UI thread.
                string msg = await System.Threading.Tasks.Task.Run(() => TgProxy.FreePort(port));
                TgHint.Text = msg;
                Logger.Info(msg, "tg-proxy");
            }
            catch (Exception ex)
            {
                TgHint.Text = "Ошибка при освобождении порта: " + ex.Message;
            }
            finally
            {
                BtnTgFree.IsEnabled = true;
            }
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
            WebSecretBox.Text = Config.Get("tgWebSecret", NewWebSecret());
        }

        /// <summary>
        /// A per-install secret for the web proxy link. No hardcoded default:
        /// a constant compiled into a distributed binary is not a secret, every
        /// install without a saved value would share it. Generated once per
        /// install on first use and then stored by the regular save path.
        /// </summary>
        private static string NewWebSecret()
        {
            try
            {
                var bytes = new byte[16];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                    rng.GetBytes(bytes);
                var secret = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                Config.Set("tgWebSecret", secret);
                return secret;
            }
            catch
            {
                // Config unavailable: fall back to a random value for this
                // session rather than a shared constant.
                return Guid.NewGuid().ToString("N");
            }
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
            if (_refreshing || _busy) return;
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
            // One clear "current strategy" instead of two side-by-side numbers
            // (running vs remembered) that used to contradict each other and
            // read as a bug. When the bypass is on, show what is really running;
            // when it is off, show what will be used next, labelled as such.
            string running = st.serviceStrategy;
            if (string.IsNullOrEmpty(running))
                running = Config.Get("processStrategyName", null);
            string rememberedFirst = remembered.Count == 0
                ? null
                : System.IO.Path.GetFileNameWithoutExtension(remembered[0]);
            if (on)
            {
                StatStrategy.Text = string.IsNullOrEmpty(running) ? "текущая" : running;
                StatStrategySub.Text = remembered.Count > 1
                    ? "в запасе ещё " + (remembered.Count - 1)
                    : string.Empty;
            }
            else
            {
                StatStrategy.Text = string.IsNullOrEmpty(rememberedFirst) ? "выключена" : rememberedFirst;
                StatStrategySub.Text = string.IsNullOrEmpty(rememberedFirst)
                    ? "обход выключен, ничего не запомнено"
                    : "включится по умолчанию";
            }
            SetTile(TileStrategy, on ? "ok" : "idle");

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
            PowerState.Foreground = (System.Windows.Media.Brush)FindResource("WarnBrush");
            // Pending look: amber track while the picker runs, so the switch
            // reflects the operation instead of sitting grey for minutes.
            SwitchTrack.Background = (System.Windows.Media.Brush)FindResource("WarnBrush");
            SwitchTrack.Effect = (System.Windows.Media.Effects.Effect)FindResource("GlowWarn");
            BypassCard.BorderBrush = (System.Windows.Media.Brush)FindResource("WarnSoftBrush");
            BypassCard.Effect = (System.Windows.Media.Effects.Effect)FindResource("GlowWarn");
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
                    })), null,
                    // Ask before spending minutes on a full search after the
                    // remembered strategies fail: Повторить / Найти / Отменить.
                    () => Dispatcher.Invoke(() => ShowRememberedFailurePrompt())));
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

        /// <summary>
        /// Shown when no remembered strategy passed its quick check. Returns the
        /// user's choice: retry the memory, run the full search, or stay off.
        /// Runs on the UI thread (the caller marshals it there).
        /// </summary>
        private Zapret.AutoConnect.RememberedFailureAction ShowRememberedFailurePrompt()
        {
            var textBrush = TryFindResource("TextBrush") as System.Windows.Media.Brush;

            var title = new TextBlock
            {
                Text = "Запомненная стратегия не сработала",
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Margin = new Thickness(0, 0, 0, 8),
            };
            var body = new TextBlock
            {
                Text = "Быстрая проверка запомненной стратегии не прошла — сеть изменилась или она больше не работает.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
                Margin = new Thickness(0, 0, 0, 16),
            };
            if (textBrush != null)
            {
                title.Foreground = textBrush;
                body.Foreground = textBrush;
            }

            Zapret.AutoConnect.RememberedFailureAction choice = Zapret.AutoConnect.RememberedFailureAction.Search;
            bool decided = false;

            Window dlg = null;
            var retryBtn = new Button { Content = "Повторить", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 6, 16, 6), MinWidth = 110, ToolTip = "Ещё раз проверить запомненные стратегии" };
            if (TryFindResource("Btn") is Style normal) retryBtn.Style = normal;
            var searchBtn = new Button { Content = "Запустить поиск", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 6, 16, 6), MinWidth = 150, ToolTip = "Перебрать все стратегии — это надолго" };
            if (TryFindResource("BtnPrimary") is Style primary) searchBtn.Style = primary;
            var cancelBtn = new Button { Content = "Отменить", Padding = new Thickness(16, 6, 16, 6), MinWidth = 100 };
            if (TryFindResource("Btn") is Style normal2) cancelBtn.Style = normal2;

            retryBtn.Click += (s, e2) => { choice = Zapret.AutoConnect.RememberedFailureAction.Retry; decided = true; dlg?.Close(); };
            searchBtn.Click += (s, e2) => { choice = Zapret.AutoConnect.RememberedFailureAction.Search; decided = true; dlg?.Close(); };
            cancelBtn.Click += (s, e2) => { choice = Zapret.AutoConnect.RememberedFailureAction.Cancel; decided = true; dlg?.Close(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(retryBtn);
            buttons.Children.Add(searchBtn);
            buttons.Children.Add(cancelBtn);

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(title);
            panel.Children.Add(body);
            panel.Children.Add(buttons);

            dlg = new Window
            {
                Title = "Запомненная стратегия не сработала",
                Content = panel,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 460,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };
            if (TryFindResource("BgCardBrush") is System.Windows.Media.Brush cardBg) dlg.Background = cardBg;
            if (textBrush != null) dlg.Foreground = textBrush;
            dlg.ShowDialog();

            // Closing via the title-bar X counts as cancel: never start a
            // multi-minute search the user did not ask for.
            return decided ? choice : Zapret.AutoConnect.RememberedFailureAction.Cancel;
        }

        private static string DescribeResult(ConnectResult r)
        {
            if (r == null) return "Готово";
            if (r.ok && r.already) return "Уже работает: " + r.strategy;
            if (r.ok && r.restored) return "Вернул прежний обход: " + r.strategy;
            if (r.ok && r.fallback) return "Подключил лучшую доступную: " + r.strategy;
            if (r.ok) return "Рабочий обход подключён: " + r.strategy;
            // Cancelling the prompt is a choice, not a failure to report.
            if (r.reason == "user_cancelled") return "Обход не включён — подбор отменён";
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
                // Stop the service BEFORE killing winws. When the bypass runs as
                // the "zapret" service, killing winws first makes the SCM restart
                // the service and winws comes straight back, so the bypass never
                // actually goes off. StopServiceAndWinws does it in the right
                // order and then waits for winws to disappear.
                await System.Threading.Tasks.Task.Run(() =>
                    Services.ServiceOps.StopServiceAndWinws());
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
                // One probe -> per-target verdict, and the tray icon follows.
                var health = await System.Threading.Tasks.Task.Run(() => BypassHealth.Probe(root, 6));
                QuickNote.Text = health == null
                    ? "Проверка уже выполняется — посмотрите результат в логе."
                    : BypassHealth.Describe(health);
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