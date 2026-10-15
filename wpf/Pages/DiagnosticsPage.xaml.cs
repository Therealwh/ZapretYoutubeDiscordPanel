using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using Ydp.Services;
using Ydp.Tg;
using Ydp.Zapret;

namespace Ydp.Pages
{
    public sealed class DiagnosticItem
    {
        public string name { get; set; }
        public string detail { get; set; }
        public string verdict { get; set; }

        /// <summary>"ok" / "warn" / "err" — drives the dot and verdict colour in
        /// the template. Derived from the verdict text in Add().</summary>
        public string state { get; set; }
        public bool ok { get { return state == "ok"; } }
    }

    public partial class DiagnosticsPage : UserControl
    {
        private string _root;

        public DiagnosticsPage()
        {
            InitializeComponent();
            Items.ItemsSource = new ObservableCollection<DiagnosticItem>();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            Run();
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            Run();
        }

        private void Run()
        {
            var items = (ObservableCollection<DiagnosticItem>)Items.ItemsSource;
            items.Clear();
            Add(items, "Папка zapret", string.IsNullOrEmpty(_root) ? "не найдена" : _root,
                string.IsNullOrEmpty(_root) ? "Ошибка" : "Ок");

            if (!string.IsNullOrEmpty(_root))
            {
                var validation = Status.ValidateRoot(_root);
                Add(items, "Структура папки",
                    validation.ok ? "bin, lists и winws.exe на месте" : "проблема: " + validation.reason,
                    validation.ok ? "Ок" : "Ошибка");
            }

            bool admin = Win.IsAdmin();
            Add(items, "Права администратора",
                admin ? "панель запущена с правами администратора" : "без прав — установка службы потребует UAC",
                admin ? "Ок" : "Внимание");

            var zapret = Win.QueryService("zapret");
            Add(items, "Служба zapret",
                zapret.Exists ? (zapret.State ?? "—") : "не установлена",
                zapret.Exists ? (zapret.Running ? "Ок" : "Остановлена") : "Нет");

            var wd = Win.QueryService("WinDivert64");
            var wd1 = Win.QueryService("WinDivert");
            bool driverUp = wd.Running || wd1.Running;
            Add(items, "Драйвер WinDivert",
                (wd.Exists || wd1.Exists) ? (driverUp ? "загружен" : "остановлен") : "не найден",
                driverUp ? "Ок" : "Нет");

            bool winws = Win.ProcessRunning("winws.exe");
            string path = Win.ProcessImagePath("winws.exe");
            Add(items, "Процесс winws",
                winws ? (path ?? "запущен") : "не запущен",
                winws ? "Ок" : "Нет");

            if (winws && _root != null && path != null && !path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            {
                Add(items, "Папка winws", "процесс запущен из другой папки: " + path, "Внимание");
            }

            foreach (var nic in SafeNics())
            {
                string gateway = string.Empty;
                try
                {
                    gateway = string.Join(", ", nic.GetIPProperties().GatewayAddresses
                        .Select(g => g.Address.ToString()).ToArray());
                }
                catch { /* some adapters (Bluetooth, virtual) throw here */ }
                Add(items, "Сеть: " + nic.Name,
                    nic.Description + (string.IsNullOrEmpty(gateway) ? "" : " · шлюз " + gateway),
                    "Ок");
            }
        }

        private static IEnumerable<NetworkInterface> SafeNics()
        {
            try { return NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return new NetworkInterface[0]; }
        }

        private static void Add(ObservableCollection<DiagnosticItem> items, string name, string detail, string verdict)
        {
            // Verdict text is the single source of truth; the visual state is
            // derived so the dot and the word can never disagree.
            string state = verdict == "Ок" ? "ok"
                : (verdict == "Внимание" || verdict == "Остановлена") ? "warn"
                : "err";
            items.Add(new DiagnosticItem { name = name, detail = detail, verdict = verdict, state = state });
        }

        private static string RunCapture(string exe, string args)
        {
            try
            {
                var startInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(startInfo))
                {
                    if (p == null) return "не удалось запустить";
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return output.Trim();
                }
            }
            catch (Exception ex)
            {
                return "ошибка: " + ex.Message;
            }
        }

        private void CmdSc_Click(object sender, RoutedEventArgs e) => Console.Text = RunCapture("sc.exe", "query zapret");
        private void CmdWd_Click(object sender, RoutedEventArgs e) => Console.Text = RunCapture("sc.exe", "query WinDivert64");
        private void CmdNet_Click(object sender, RoutedEventArgs e) => Console.Text = RunCapture("ipconfig.exe", "/all");
        private void CmdProxy_Click(object sender, RoutedEventArgs e)
        {
            Console.Text = "Прокси Telegram: " + (TgProxy.Stats.running
                ? "работает, подключений " + TgProxy.Stats.connections
                : "не запущен");
        }
    }
}