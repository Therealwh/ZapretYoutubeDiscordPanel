using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Ydp.Services;
using System.Windows.Controls;
using Ydp.Pages;
using Ydp.Zapret;

namespace Ydp
{
    public partial class MainWindow : Window
    {
        // ---- global hotkey: toggle the bypass without opening the panel -------
        // The panel lives in the tray, so a system-wide combo is the fastest way
        // to flip the bypass on/off. Default Ctrl+Alt+B; the combo is a single
        // WPF KeyGesture parsed from a settings string so it is changeable
        // without touching the code. WM_HOTKEY is delivered to a hook on this
        // window's HwndSource.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0xB921;
        public const string KeyHotkeyEnabled = "hotkeyEnabled";
        private HwndSource _hotkeySource;
        private bool _hotkeyRegistered;

        public static bool HotkeyEnabled
        {
            get { return !string.Equals(Config.Get(KeyHotkeyEnabled, "on"), "off", StringComparison.OrdinalIgnoreCase); }
            set { Config.Set(KeyHotkeyEnabled, value ? "on" : "off"); }
        }

        private void SetupHotkey()
        {
            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.EnsureHandle();
            _hotkeySource = HwndSource.FromHwnd(hwnd);
            _hotkeySource.AddHook(HotkeyHook);
            ApplyHotkeySetting(HotkeyEnabled);
        }

        /// <summary>Register or release the global combo at runtime (Settings toggle).</summary>
        public void ApplyHotkeySetting(bool enabled)
        {
            if (!enabled)
            {
                if (_hotkeyRegistered)
                {
                    var h = new WindowInteropHelper(this);
                    UnregisterHotKey(h.Handle, HotkeyId);
                    _hotkeyRegistered = false;
                    Logger.Info("Горячая клавиша отключена", "hotkey");
                }
                return;
            }
            if (_hotkeyRegistered) return;
            string combo = Config.Get("hotkeyToggle", "Ctrl+Alt+B");
            KeyGesture gesture;
            if (!TryParseGesture(combo, out gesture)) gesture = new KeyGesture(Key.B, ModifierKeys.Control | ModifierKeys.Alt);
            uint mods = 0;
            var m = gesture.Modifiers;
            if ((m & ModifierKeys.Control) != 0) mods |= 0x0002;
            if ((m & ModifierKeys.Alt) != 0) mods |= 0x0001;
            if ((m & ModifierKeys.Shift) != 0) mods |= 0x0004;
            if ((m & ModifierKeys.Windows) != 0) mods |= 0x0008;
            Key key = gesture.Key == Key.System ? System.Windows.Input.Key.ImeProcessed : gesture.Key;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            var helper = new WindowInteropHelper(this);
            _hotkeyRegistered = mods != 0 && RegisterHotKey(helper.Handle, HotkeyId, mods, vk);
            Logger.Info(_hotkeyRegistered
                ? "Горячая клавиша включена: " + combo
                : "Горячую клавишу " + combo + " занять не удалось (занята другой программой)", "hotkey");
        }
        private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                ToggleBypassFromHotkey();
            }
            return IntPtr.Zero;
        }

        private void UnregisterHotkey()
        {
            if (!_hotkeyRegistered) return;
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        private void ToggleBypassFromHotkey()
        {
            var root = Zapret.Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(root)) return;
            try
            {
                var st = Zapret.Status.GetStatus(root);
                if (st.activeMode != "off")
                {
                    ServiceOps.StopServiceAndWinws();
                    Logger.Info("Горячая клавиша: обход выключен", "hotkey");
                    App.Tray?.Notify("YoutubeDiscordPanel", "Обход выключен.");
                }
                else
                {
                    App.Tray?.Notify("YoutubeDiscordPanel", "Включаю обход…");
                    System.Threading.Tasks.Task.Run(() =>
                        Zapret.AutoConnect.Connect(root, m => Logger.Info("Горячая клавиша: " + m, "hotkey"), null, null))
                        .ContinueWith(t => Logger.Info(t.IsFaulted
                            ? "Горячая клавиша: ошибка включения"
                            : "Горячая клавиша: включение завершено", "hotkey"));
                }
            }
            catch (Exception ex) { Logger.Warn("Горячая клавиша: " + ex.Message, "hotkey"); }
        }

        private static bool TryParseGesture(string text, out KeyGesture gesture)
        {
            gesture = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                gesture = (KeyGesture)new System.Windows.Input.KeyGestureConverter()
                    .ConvertFromString(text.Trim());
                return gesture != null;
            }
            catch { gesture = null; return false; }
        }

        // Pages are created on first navigation: building all three XAML trees up
        // front slowed startup and made a crash in any one of them fatal.
        private DashboardPage _dashboard;
        private StrategiesPage _strategies;
        private FiltersPage _filters;
        private ListsPage _lists;
        private UpdatesPage _updates;
        private DiagnosticsPage _diagnostics;
        private LogsPage _logs;
        private SettingsPage _settings;
        private SupportPage _support;
        private ContactsPage _contacts;
        private TransparencyPage _transparency;
        private ExpertPage _expert;

        public MainWindow()
        {
            InitializeComponent();
            VersionBadge.Text = "v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            // `--page=strategies` opens straight into a section (used by autostart
            // flows and when the user wants to jump back to a specific screen).
            string start = null;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) start = arg.Substring(7);
            }
            Navigate(start ?? "dashboard");
            UpdateFooter();
            StartUpdateCheck();
            // A panel that sits in the tray for days would otherwise only ever
            // see updates that were out when it started. Re-check every 30 min so
            // a new release raises a tray notification on its own.
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _updateTimer.Tick += (s, e) => StartUpdateCheck();
            _updateTimer.Start();
            EnsureTelegramDomains();

            // Minimizing goes to the tray instead of the taskbar: the panel is a
            // background tool that stays resident while the bypass runs, and the
            // user asked for it not to sit in the taskbar. The tray icon brings
            // it back (TrayIcon.Show resets WindowState back to Normal).
            StateChanged += (s, e) =>
            {
                // Only hide if there is really a tray icon to come back from:
                // hiding without one would make the panel unreachable.
                if (WindowState != WindowState.Minimized) return;
                if (App.Tray == null) return;
                Hide();
                App.Tray.Notify("YoutubeDiscordPanel", "Панель свёрнута в трей — значок рядом с часами.");
            };

            SourceInitialized += (s, e) => SetupHotkey();
            Closing += (s, e) => UnregisterHotkey();

            Loaded += (s, e) =>
        {
            // The installer puts zapret next to the panel, so the folder is
            // found rather than configured: persist it and add the Telegram
            // domains before anything else reads the settings.
            Zapret.FirstRun.RememberResolvedRoot();
            UpdateFooter();
        };
        }

        /// <summary>Open a section by its tag. Also used by the --page= switch.</summary>
        public void Navigate(string tag)
        {
            try
            {
                // Keep the sidebar in sync: --page=diagnostics (and Nav_Click from
                // the quick-actions card) used to swap the page while the radio
                // stayed on the previous section.
                var nav = FindName("Nav" + char.ToUpperInvariant(tag[0]) + tag.Substring(1)) as System.Windows.Controls.RadioButton;
                if (nav != null && !nav.IsChecked.Value)
                {
                    nav.IsChecked = true;
                    return; // Nav_Checked re-enters Navigate
                }
                switch (tag)
                {
                    case "strategies":
                        if (_strategies == null) _strategies = new StrategiesPage();
                        PageHost.Content = _strategies;
                        break;
                        case "expert":
                            if (_expert == null) _expert = new ExpertPage();
                            PageHost.Content = _expert;
                            break;
                        case "filters":
                        if (_filters == null) _filters = new FiltersPage();
                        PageHost.Content = _filters;
                        break;
                    case "lists":
                        if (_lists == null) _lists = new ListsPage();
                        PageHost.Content = _lists;
                        break;
                    case "updates":
                        if (_updates == null) _updates = new UpdatesPage();
                        PageHost.Content = _updates;
                        break;
                    case "diagnostics":
                        if (_diagnostics == null) _diagnostics = new DiagnosticsPage();
                        PageHost.Content = _diagnostics;
                        break;
                    case "logs":
                        if (_logs == null) _logs = new LogsPage();
                        PageHost.Content = _logs;
                        break;
case "settings":
                        if (_settings == null) _settings = new SettingsPage();
                        PageHost.Content = _settings;
                        break;
case "support":
                            if (_support == null) _support = new SupportPage();
                            PageHost.Content = _support;
                            break;
                        case "contacts":
                            if (_contacts == null) _contacts = new ContactsPage();
                            PageHost.Content = _contacts;
                            break;
                        case "transparency":
                            if (_transparency == null) _transparency = new TransparencyPage();
                            PageHost.Content = _transparency;
                            break;
                    default:
                        if (_dashboard == null) _dashboard = new DashboardPage();
                        PageHost.Content = _dashboard;
                        break;
                }
            }
            catch (Exception ex)
            {
                App.Log("navigate(" + tag + ") failed: " + ex);
                PageHost.Content = new TextBlock { Text = "Страница не открылась: " + ex.Message, Foreground = (System.Windows.Media.Brush)FindResource("TextBrush") };
            }
        }

        /// <summary>Open a section from anywhere (quick actions, tray, deep links).</summary>
        public void ShowSection(string tag)
        {
            switch (tag)
            {
                case "strategies": NavStrategies.IsChecked = true; break;
                case "settings": NavSettings.IsChecked = true; break;
                case "filters": NavFilters.IsChecked = true; break;
                case "lists": NavLists.IsChecked = true; break;
                case "updates": NavUpdates.IsChecked = true; break;
                case "diagnostics": NavDiagnostics.IsChecked = true; break;
                case "logs": NavLogs.IsChecked = true; break;
            case "support": NavSupport.IsChecked = true; break;
                case "contacts": NavContacts.IsChecked = true; break;
                default: NavDashboard.IsChecked = true; break;
            }
            Show();
            Activate();
        }

        private void UpdateFooter()
        {
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            FooterNote.Text = string.IsNullOrEmpty(root) ? "Папка zapret не найдена" : root;
        }

        // ---- custom title bar ------------------------------------------------
        // We draw the caption ourselves, so these three replace the system ones.
        // Close() goes through the normal Closing handler, which hides to the
        // tray instead of killing the panel.

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            // The first radio button is already IsChecked in XAML, so this fires
            // from inside InitializeComponent while PageHost does not exist yet.
            if (PageHost == null) return;
            var item = sender as RadioButton;
            if (item == null || !item.IsChecked.Value) return;
            Navigate(item.Tag as string ?? "dashboard");
            UpdateFooter();
        }

        // ---- update offer -----------------------------------------------------

        /// <summary>
        /// The panel's Telegram domains belong in the user hostlist at all times,
        /// not only right after an engine install: an update or a fresh
        /// *-user.txt can drop them, and Telegram quietly stops working. The
        /// call is idempotent and writes only when something is missing.
        /// </summary>
        private void EnsureTelegramDomains()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var root = Status.ResolveRoot(Config.Get("zapretRoot", null));
                if (string.IsNullOrEmpty(root)) return;
                int added = Zapret.FirstRun.EnsureTelegramDomains(root);
                if (added > 0) Logger.Info("Дописано доменов Telegram в список: " + added, "lists");
            });
        }

        /// <summary>
        /// Check GitHub for a newer release in the background and, if there is
        /// one, offer the update twice: a tray balloon (visible even when the
        /// panel is folded away) and a banner at the top of the window.
        /// </summary>
        private string _lastNotifiedVersion;
        private readonly DispatcherTimer _updateTimer;

        private void StartUpdateCheck()
        {
            System.Threading.Tasks.Task.Run(() => PanelUpdater.Check())
                .ContinueWith(t =>
                {
                    if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion) return;
                    var info = PanelUpdater.State;
                    if (!info.available) return;
                    // Notify once per released version. A panel that has been
                    // running for days re-checks every 30 minutes and must not
                    // repeat the same "new version" balloon.
                    if (info.latest == _lastNotifiedVersion) return;
                    _lastNotifiedVersion = info.latest;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateTitle.Text = "Доступна новая версия панели — v" + info.latest;
                        UpdateDetails.Text = "У вас v" + info.current + ". Обновление заменит файл панели и перезапустит её автоматически.";
                        UpdateBanner.Visibility = Visibility.Visible;
                        App.Tray?.Notify("Доступно обновление панели v" + info.latest,
                            "Сейчас у вас v" + info.current + ". Откройте панель и нажмите «Обновить».");
                        Logger.Info("Найдено обновление панели: v" + info.current + " → v" + info.latest, "updater");
                    }));
                });
        }

        private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            BtnUpdateNow.IsEnabled = false;
            UpdateTitle.Text = "Скачиваю обновление…";
            UpdateDetails.Text = "Панель перезапустится автоматически после установки.";
            var result = await System.Threading.Tasks.Task.Run(() => PanelUpdater.Install());
            if (result.ok)
            {
                App.Tray?.Notify("Обновление установлено", "Панель сейчас перезапустится на v" + result.message + ".");
                Logger.Info("Обновление применено, перезапуск", "updater");
                System.Threading.Thread.Sleep(800);
                Application.Current.Shutdown();
                return;
            }
            UpdateTitle.Text = "Не удалось обновить";
            UpdateDetails.Text = result.reason == "no_update"
                ? "Обновление не найдено — попробуйте позже или скачайте с GitHub вручную."
                : "Ошибка: " + (result.message ?? result.reason ?? "?") + ". Скачайте вручную: " + PanelUpdater.State.url;
            BtnUpdateNow.IsEnabled = true;
        }

        private void BtnWhatsNew_Click(object sender, RoutedEventArgs e)
        {
            var url = PanelUpdater.State.url;
            if (!string.IsNullOrEmpty(url))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }

        private void BtnBannerClose_Click(object sender, RoutedEventArgs e)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
        }
    }
}
