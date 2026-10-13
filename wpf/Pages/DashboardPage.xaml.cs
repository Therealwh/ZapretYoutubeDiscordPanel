using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
        }

        private void Refresh()
        {
            string configured = Config.Get("zapretRoot", null);
            string root = Status.ResolveRoot(configured);
            var st = Status.GetStatus(root);

            bool on = st.activeMode != "off";
            PowerState.Text = on ? "Включён" : "Выключен";
            SwitchTrack.Background = on
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : (System.Windows.Media.Brush)FindResource("BorderBrush2");
            StatStrategy.Text = string.IsNullOrEmpty(st.serviceStrategy) ? "—" : st.serviceStrategy;
            StatMode.Text = st.activeMode == "service" ? "Служба Windows"
                : st.activeMode == "process" ? "Процесс" : "—";
            StatZapret.Text = Describe(st.service);
            StatWinDivert.Text = st.winDivert == null ? "Нет службы" : Describe(st.winDivert);
            StatWinws.Text = st.winwsProcess ? "Запущен" : "Не запущен";
            StatWinwsPath.Text = st.winwsProcessPath ?? "";
            StatRoot.Text = string.IsNullOrEmpty(root) ? "не найдена" : root;
            TgState.Text = Tg.Stats.Running ? "Включён" : "Выключен";
            TgStats.Text = Tg.Stats.Running
                ? $"подключений: {Tg.Stats.Connections} · вверх: {Human(Tg.Stats.BytesUp)} · вниз: {Human(Tg.Stats.BytesDown)}"
                : "";
            TgLink.Text = string.IsNullOrEmpty(Tg.Stats.Link) ? "" : Tg.Stats.Link;
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
    }
}