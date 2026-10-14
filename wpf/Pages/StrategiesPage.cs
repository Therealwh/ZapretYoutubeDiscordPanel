using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ydp.Services;
using Ydp.Zapret;

namespace Ydp.Pages
{
    /// <summary>One target line in the live verdict list: dot, name, address, probes.</summary>
    public sealed class ProbeRow
    {
        public string name { get; set; }
        public string address { get; set; }
        public string probes { get; set; }
        public bool ok { get; set; }
    }

    public sealed class StrategyRow : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string File { get; set; }
        public string Badges { get; set; }
        public string Active { get; set; }
        public string State { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// Strategies page: the real list parsed from the zapret folder, the
    /// per-strategy actions, and the tester/autoconnect entry points.
    /// </summary>
    public partial class StrategiesPage : UserControl
    {
        public ObservableCollection<StrategyRow> Rows { get; } = new ObservableCollection<StrategyRow>();
        private bool _busy;
        private string _root;

        public StrategiesPage()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Reload();
        }

        private async void Reload()
        {
            _root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            Rows.Clear();
            if (string.IsNullOrEmpty(_root))
            {
                Hint.Text = "Папка zapret не найдена. Укажите её в настройках или установите zapret.";
                return;
            }
            Hint.Text = "Папка: " + _root;
            var list = Strategy.ListStrategies(_root);
            var status = Status.GetStatus(_root);
            string active = status.serviceStrategy;
            foreach (var s in list)
            {
                Rows.Add(new StrategyRow
                {
                    Name = s.name,
                    File = s.file,
                    Badges = string.Join(" · ", s.badges),
                    Active = string.Equals(s.name, active, StringComparison.OrdinalIgnoreCase) ? "активная" : string.Empty,
                    State = string.IsNullOrEmpty(active) && status.activeMode == "off" ? string.Empty : (string.Equals(s.name, active, StringComparison.OrdinalIgnoreCase) ? "подключена" : string.Empty),
                });
            }
            await System.Threading.Tasks.Task.Yield();
        }

private void SetBusy(bool busy, string note = null)
        {
            _busy = busy;
            BtnAuto.IsEnabled = !busy;
            BtnApply.IsEnabled = !busy;
            BtnStop.IsEnabled = !busy;
            BtnTest.IsEnabled = !busy;
            // Stop only makes sense while a sweep is running.
            BtnAbort.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnAbort.IsEnabled = busy;
            Progress.IsIndeterminate = busy;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (note != null) StatusNote.Text = note;
        }

        /// <summary>
        /// Stop the sweep at the next strategy boundary. Tester.Abort is safe to
        /// call from any thread; the loop checks the flag between strategies and
        /// between probe batches.
        /// </summary>
        private void BtnAbort_Click(object sender, RoutedEventArgs e)
        {
            Tester.Abort();
            BtnAbort.IsEnabled = false;
            StatusNote.Text = "Останавливаю проверку…";
        }

        private bool RequireRoot()
        {
            if (string.IsNullOrEmpty(_root))
            {
                StatusNote.Text = "Сначала укажите папку zapret.";
                return false;
            }
            return true;
        }

        private async void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRoot()) return;
            SetBusy(true, "Подбираем рабочую стратегию…");
            var result = await System.Threading.Tasks.Task.Run(() => AutoConnect.Connect(_root,
                msg => Dispatcher.Invoke(() => StatusNote.Text = msg),
                (run, i, total) => Dispatcher.Invoke(() => StatusNote.Text = DescribeRun(run, i, total))));

            SetBusy(false, Describe(result));
            await System.Threading.Tasks.Task.Yield();
            Reload();
        }

        private static string DescribeRun(Tester.StrategyRunResult run, int index, int total)
        {
            if (run == null) return "Пробую стратегию " + (index + 1) + "/" + total + "…";
            bool discord = run.groups.discord.okTargets > 0;
            string discordText = discord ? "Discord ok" : "Discord недоступен";
            return run.name + ": " + discordText + ", YouTube " + run.groups.youtube.state;
        }

        private static string Describe(ConnectResult r)
        {
            if (r == null) return "Готово";
            if (r.ok && r.already) return $"Уже работает: {r.strategy}";
            if (r.ok && r.restored) return $"Ничего не подошло — вернул прежний обход: {r.strategy}";
            if (r.ok && r.fallback) return $"Идеальной не нашлось — подключил лучшую: {r.strategy}";
            if (r.ok) return $"Готово! Рабочий обход подключен: {r.strategy} ({r.mode})";
            return "Не удалось подключить: " + (r.reason ?? "ошибка");
        }

        private async void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow();
            if (row == null) { StatusNote.Text = "Выберите стратегию в списке."; return; }
            if (!RequireRoot()) return;
            SetBusy(true, "Применяю " + row.Name + "…");
            bool preferService = string.Equals(Config.Get(AutoConnect.KeyPreferService, "true"), "true", StringComparison.OrdinalIgnoreCase);
            var result = await System.Threading.Tasks.Task.Run(() =>
                preferService
                    ? ServiceOps.InstallStrategyAsService(_root, row.File)
                    : ServiceOps.LaunchStrategyProcess(_root, row.File));
            SetBusy(false, result.ok
                ? $"Подключено: {row.Name} ({(result.mode ?? "process")})"
                : "Не удалось: " + (result.reason ?? "ошибка"));
            if (result.ok)
            {
                Config.Set(AutoConnect.KeyLastWinner, row.File);
                AutoConnect.RememberWorking(row.File);
            }
            await System.Threading.Tasks.Task.Yield();
            Reload();
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRoot()) return;
            SetBusy(true, "Останавливаю обход…");
            var stopped = await System.Threading.Tasks.Task.Run(() => new[]
            {
                ServiceOps.KillWinws(),
                ServiceOps.StopService().ok,
            });
            SetBusy(false, stopped[0] || stopped[1] ? "Обход остановлен" : "Останавливать было нечего");
            await System.Threading.Tasks.Task.Yield();
            Reload();
        }

private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRoot()) return;
            SetBusy(true, "Полный тест стратегий…");
            var known = AutoConnect.KnownWorking();
            var sweep = await System.Threading.Tasks.Task.Run(() => Tester.RunStrategyTests(_root, known, stopOnFirst: false,
                onProgress: (run, i, total) => Dispatcher.Invoke(() => ShowRun(run, i, total))));

            var ok = sweep.results.Count(r => !r.failed);
            if (sweep.aborted)
            {
                // The loop exits with the last tested strategy still running.
                // Leaving that half-verified bypass in place would be worse than
                // stopping it, so we stop it and say so plainly.
                await System.Threading.Tasks.Task.Run(() => ServiceOps.StopServiceAndWinws());
                SetBusy(false, "Проверка остановлена. Обход выключен — нажмите «Подключить рабочий обход», когда будете готовы.");
            }
            else
            {
                SetBusy(false, ok > 0
                    ? $"Проверено стратегий: {sweep.results.Count}, запустилось: {ok}. Лучшая: {sweep.winner ?? "—"}"
                    : "Ни одна стратегия не запустилась");
            }

            // Keep the detail of the best strategy on screen so the user can see
            // WHICH address answered, not just which strategy won.
            var best = sweep.results.Where(r => !r.failed && r.targets != null && r.targets.Count > 0)
                .OrderByDescending(r => r.score.ok).FirstOrDefault();
            if (best != null)
            {
                Dispatcher.Invoke(() => ShowDetail(best));
            }
            await System.Threading.Tasks.Task.Yield();
            Reload();
        }

        /// <summary>Show the live verdicts of the strategy being tested.</summary>
        private void ShowRun(Tester.StrategyRunResult run, int index, int total)
        {
            if (run == null)
            {
                StatusNote.Text = $"Запускаю {index + 1}/{total}…";
                return;
            }
            StatusNote.Text = $"{run.name} ({(index + 1)}/{total}): Discord ok={run.groups.discord.okTargets}, "
                + $"YouTube={run.groups.youtube.state}, проб ok={run.score.ok}";
            if (run.targets != null && run.targets.Count > 0) ShowDetail(run);
        }

        private void ShowDetail(Tester.StrategyRunResult run)
        {
            ProbePanel.Visibility = Visibility.Visible;
            ProbeTitle.Text = "Куда стучимся: " + run.name;
            ProbeList.ItemsSource = run.targets.Select(TargetRow).ToList();
        }

        /// <summary>Flatten a target for the binding: host, address reached, probes.</summary>
        private static ProbeRow TargetRow(TargetResult target)
        {
            string host = target.url ?? "";
            if (target.kind == "ping") host = "ping " + host;
            string address = string.IsNullOrEmpty(target.ip)
                ? host
                : host + "  →  " + target.ip;
            if (target.kind == "ping" && !string.IsNullOrEmpty(target.result) && target.result != "OK")
                address += "  (" + target.result + ")";
            return new ProbeRow
            {
                name = target.name,
                address = address,
                probes = target.probes,
                ok = target.ok,
            };
        }

        private StrategyRow SelectedRow()
        {
            return StrategyList.SelectedItem as StrategyRow;
        }
    }
}