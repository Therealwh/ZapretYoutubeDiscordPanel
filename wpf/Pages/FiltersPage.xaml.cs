using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Ydp.Services;
using Ydp.Zapret;

namespace Ydp.Pages
{
    public partial class FiltersPage : UserControl
    {
        private string _root;

        public FiltersPage()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            ShowState();
        }

        private string GameFilterFile => _root == null ? null : Path.Combine(_root, "utils", "game_filter.enabled");

        private void ShowState()
        {
            if (_root == null)
            {
                GameNote.Text = "Папка zapret не найдена.";
                IpNote.Text = string.Empty;
                return;
            }
            try
            {
                GameNote.Text = File.Exists(GameFilterFile)
                    ? "Текущий режим: " + FirstValue(File.ReadAllLines(GameFilterFile))
                    : "Фильтр выключен (файл game_filter.enabled отсутствует)";
            }
            catch (Exception ex)
            {
                GameNote.Text = "Не удалось прочитать состояние: " + ex.Message;
            }
            IpNote.Text = "Режим: " + (string.Equals(Config.Get("ipsetMode", "off"), "on", StringComparison.OrdinalIgnoreCase) ? "включён" : "выключен");
        }

        private static string FirstValue(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                string value = (line ?? string.Empty).Trim();
                if (value.Length > 0 && !value.StartsWith("#", StringComparison.Ordinal)) return value;
            }
            return "по умолчанию";
        }

        private void WriteGameFilter(string content)
        {
            if (_root == null) return;
            try
            {
                Directory.CreateDirectory(Path.Combine(_root, "utils"));
                File.WriteAllText(GameFilterFile, content);
                Logger.Info("Режим игрового фильтра изменён: " + FirstValue(content.Split('\n')), "filters");
                GameNote.Text = "Сохранено. Перезапустите обход, чтобы применилось.";
            }
            catch (Exception ex)
            {
                GameNote.Text = "Не удалось сохранить: " + ex.Message;
            }
            ShowState();
        }

        private void GameOff_Click(object sender, RoutedEventArgs e) => WriteGameFilter("mode = disabled\n");
        private void GameTcp_Click(object sender, RoutedEventArgs e) => WriteGameFilter("mode = tcp\n");
        private void GameUdp_Click(object sender, RoutedEventArgs e) => WriteGameFilter("mode = udp\n");
        private void GameAll_Click(object sender, RoutedEventArgs e) => WriteGameFilter("mode = all\n");

        private void IpOff_Click(object sender, RoutedEventArgs e)
        {
            Config.Set("ipsetMode", "off");
            ShowState();
        }

        private void IpOn_Click(object sender, RoutedEventArgs e)
        {
            Config.Set("ipsetMode", "on");
            ShowState();
        }

        private async void IpRefresh_Click(object sender, RoutedEventArgs e)
        {
            IpNote.Text = "Загружаю…";
            try
            {
                var client = new WebClient();
                string content = await client.DownloadStringTaskAsync(
                    "https://raw.githubusercontent.com/bol-van/zapret/master/lists/ipset-all.txt");
                string target = Path.Combine(_root ?? string.Empty, "lists", "ipset-all.txt");
                File.WriteAllText(target, content);
                IpNote.Text = "Обновлено: " + content.Length + " байт";
                Logger.Info("ipset-all.txt обновлён", "filters");
            }
            catch (Exception ex)
            {
                IpNote.Text = "Не удалось обновить: " + ex.Message;
            }
        }

        private void TcpStamps_Click(object sender, RoutedEventArgs e)
        {
            ServiceOps.EnableTcpTimestamps();
            NetNote.Text = "TCP timestamps включены.";
        }

        private void DnsFlush_Click(object sender, RoutedEventArgs e)
        {
            Run("ipconfig.exe", "/flushdns");
            NetNote.Text = "DNS-кэш сброшен.";
        }

        private static int Run(string exe, string args)
        {
            try
            {
                var startInfo = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(startInfo))
                {
                    if (p == null) return -1;
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode;
                }
            }
            catch
            {
                return -1;
            }
        }

        private void BtnCheck_Click(object sender, RoutedEventArgs e)
        {
            var lines = new System.Collections.Generic.List<string>();
            var resolved = Dns.GetHostAddresses("www.youtube.com");
            lines.Add("DNS www.youtube.com: " + (resolved.Length > 0
                ? string.Join(", ", resolved.Select(a => a.ToString()).ToArray())
                : "не резолвится"));
            var ping = new Ping();
            try
            {
                var reply = ping.Send("1.1.1.1", 2000);
                lines.Add("Пинг 1.1.1.1: " + (reply.Status == IPStatus.Success ? reply.RoundtripTime + " мс" : reply.Status.ToString()));
            }
            catch (Exception ex)
            {
                lines.Add("Пинг 1.1.1.1: ошибка (" + ex.Message + ")");
            }
            lines.Add("Панель: " + (Win.IsAdmin() ? "с правами администратора" : "без прав администратора"));
            NetNote.Text = string.Join(Environment.NewLine, lines);
        }
    }
}