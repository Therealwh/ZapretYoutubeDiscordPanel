using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Worst result on this target: bad = SSL/ERR, warn = UNSUP/TIMEOUT,
        /// good = every probe OK. Mirrors how the bundled zapret tester colours
        /// its tokens.
        /// </summary>
        public string state { get; set; }
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
            // Safety net: if the sweep task wedges despite the process kill,
            // unfreeze the UI after a grace period instead of leaving every
            // button dead on "останавливаю проверку…" forever.
            var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            watchdog.Tick += (s, ev) =>
            {
                watchdog.Stop();
                if (!_sweepRunning) return;
                _sweepRunning = false;
                _pendingConnectFile = null;
                _pendingConnectName = null;
                Logger.Warn("Остановка проверки превысила 20 с — разблокирую интерфейс принудительно", "tester");
                SetBusy(false, "Проверка остановлена принудительно. Попробуйте запустить тест заново.");
                BtnAbort.IsEnabled = true;
            };
            watchdog.Start();
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
            Logger.Info("Применяю выбранную стратегию: " + row.Name + " (" + row.File + ")", "apply");
            SetBusy(true, "Применяю " + row.Name + "…");
            bool preferService = string.Equals(Config.Get(AutoConnect.KeyPreferService, "true"), "true", StringComparison.OrdinalIgnoreCase);
            var result = await System.Threading.Tasks.Task.Run(() =>
                preferService
                    ? ServiceOps.InstallStrategyAsService(_root, row.File)
                    : ServiceOps.LaunchStrategyProcess(_root, row.File));
            Logger.Info("Применение " + row.Name + ": " + (result.ok ? "успех " + (result.mode ?? "") : "ошибка " + (result.reason ?? "?") + " " + (result.message ?? "")), "apply");
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
            // Start a fresh report: the list accumulates during the run.
            _runs.Clear();
            _tested = 0;
            // A "keep connected" flag left over from the previous sweep makes the
            // next abort keep a half-verified bypass running and shows a stale
            // status. Every sweep starts from a clean slate.
            _keepConnected = false;
            _pendingConnectFile = null;
            _pendingConnectName = null;
            ProbeList.ItemsSource = _runs;
            var known = AutoConnect.KnownWorking();
            // Clear the abort flag here, on the UI thread, before the sweep task
            // is scheduled: the stop button becomes live with SetBusy above, and
            // a click landing before the method body ran used to be wiped.
            Tester.ResetAbort();
            // Which targets answer with no bypass at all: a green dot next to one
            // of those proves nothing, and the report has to say so instead of
            // letting it look like the strategy did the work.
            //
            // Measured inside the sweep, after its own reset, and reused from the
            // result. Doing a second measurement here cost as long as the whole
            // baseline (every blocked target burns the full timeout) and left the
            // user staring at an empty report for minutes before the first
            // strategy even started.
            
            

            // The bundled zapret tester does the whole cycle itself: refuses to
            // run while a service is installed, stops winws, starts each .bat,
            // probes in parallel, restores what was running. Delegating to it is
            // the only way the panel shows the same picture the reference tool
            // shows; my own re-implementation measured probes in the wrong
            // bypass state and made every config look identical.
            _sweepRunning = true;
            var sweep = await System.Threading.Tasks.Task.Run(() => Tester.RunOriginalTester(_root,
                // BeginInvoke: the tester prints hundreds of lines; a blocking
                // Invoke per line would stall the sweep on every UI frame.
                line => Dispatcher.BeginInvoke(new Action(() =>
                {
                    var text = line == null ? "" : line.Trim();
                    if (text.Length > 0 && !text.StartsWith("[OK]") && !text.StartsWith("[INFO]"))
                        StatusNote.Text = text.Length > 160 ? text.Substring(0, 160) : text;
                })),
                (run, i, total) => Dispatcher.BeginInvoke(new Action(() => ShowRun(run, i, total)))));

            // Keep _sweepRunning true through the pending-connect and abort
            // handling: a "Подключить сейчас" click landing in this window used
            // to take the else branch and install its service in parallel with
            // the abort cleanup's ResetBypass, which could delete what it just
            // installed.
            var pendingHandled = false;
            if (!string.IsNullOrEmpty(_pendingConnectFile))
            {
                await ConnectPending();
                await System.Threading.Tasks.Task.Yield();
                Reload();
                pendingHandled = true;
            }
            _sweepRunning = false;
            if (pendingHandled) return;

            // The report file is written at the very end, so the rows appear
            // once, together — same as reading the tester's own console output.

            // Nothing of the sweep will touch the bypass again from here on.

            if (sweep.aborted)
            {
                if (_keepConnected)
                {
                    // The user pressed "Подключить сейчас" on a passing strategy;
                    // stopping the bypass now would undo exactly that.
                    SetBusy(false, StatusNote.Text);
                }
                else
                {
                    // The loop exits with the last tested strategy still running.
                    // Leaving that half-verified bypass in place is worse than
                    // stopping it, so stop it and say so plainly.
                    await System.Threading.Tasks.Task.Run(() => ServiceOps.ResetBypass());
                    SetBusy(false, "Проверка остановлена. Обход выключен — нажмите «Подключить рабочий обход», когда будете готовы.");
                }
            }
            else
            {
                // The tester's own counting and its "Best strategy" pick, not a
                // verdict I invented: the tokens above are the evidence, the
                // best-config line is what the reference tool itself concludes.
                SetBusy(false, string.IsNullOrEmpty(sweep.winner)
                    ? $"Тестер завершил {sweep.results.Count} конфигураций, но не назвал лучшую — смотри пробы."
                    : $"Тестер завершил {sweep.results.Count} конфигураций. Лучший по подсчёту оригинала: {sweep.winner}. Обход сейчас выключен.");
            }

            // Like the bundled tester, the report is the evidence itself: every
            // strategy with its raw probe tokens, nothing swapped in at the end.
            ProbeTitle.Text = $"Проверено конфигураций: {_tested}"
                + (string.IsNullOrEmpty(sweep.winner) ? "" : " · лучший по подсчёту тестера: " + sweep.winner)
                + (sweep.aborted ? " · прервано досрочно" : "");
            await System.Threading.Tasks.Task.Yield();
            Reload();
        }

        /// <summary>И ICommand, потому что Command биндится только к нему —
        /// Action в этом свойстве молча не срабатывал, и карточки не раскрывались.</summary>
        public sealed class DelegateCommand : System.Windows.Input.ICommand
        {
            private readonly Action _execute;
            // CanExecuteChanged is never raised: the command is always enabled.
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public DelegateCommand(Action execute) { _execute = execute; }
            public bool CanExecute(object parameter) { return true; }
public void Execute(object parameter)
            {
                _execute?.Invoke();
            }
        }

        /// <summary>
        /// One tested strategy in the running report.
        /// </summary>
        // INotifyPropertyChanged is required, not optional: WPF only subscribes to
        // PropertyChanged through this interface. RunRow raised the event but did
        // not implement it, so the binding engine never listened and the
        // Expanded-driven Visibility (and the button label) stayed frozen at the
        // initial False even though the property itself flipped to true.
        public sealed class RunRow : System.ComponentModel.INotifyPropertyChanged
        {
            public string Name { get; set; }
            public bool Ok { get; set; }

            /// <summary>
            /// Dot colour from the probe evidence: green = Discord and YouTube
            /// answered everywhere, red = neither did, yellow = something in
            /// between. UNSUP (curl without TLS 1.3) is not a failure.
            /// </summary>
            public string DotState { get; set; } = "warn";

            public string Verdict { get; set; }
            public bool Expanded
            {
                get { return _expanded; }
                set { _expanded = value; if (PropertyChanged != null) PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs("Expanded")); }
            }
            private bool _expanded;

            /// <summary>Header is a toggle, so a finished strategy folds away.</summary>
            public System.Windows.Input.ICommand ToggleCmd { get; set; }

            public ObservableCollection<ProbeRow> Targets { get; set; } = new ObservableCollection<ProbeRow>();

            /// <summary>
            /// Stops the sweep and connects this strategy, if it works.
            ///
            /// ICommand, not Action: the XAML binds it through Command, and Command
            /// only accepts ICommand. As an Action the binding silently did nothing,
            /// so "Подключить сейчас" never fired.
            /// </summary>
            public System.Windows.Input.ICommand Connect { get; set; }
            public bool CanConnect { get { return Ok && Connect != null; } }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }

        private readonly ObservableCollection<RunRow> _runs = new ObservableCollection<RunRow>();

        public sealed class SummaryRow
        {
            public string Name { get; set; }
            public string Counts { get; set; }
            public string Verdict { get; set; }
            public string State { get; set; }
            public bool IsWinner { get; set; }
        }

        public ObservableCollection<SummaryRow> SummaryRows { get; } = new ObservableCollection<SummaryRow>();
        private int _tested;

        /// <summary>Set when the user connected a strategy, so abort must not stop it.</summary>
        private bool _keepConnected;

        /// <summary>True while the full strategy sweep is actually running.</summary>
        private bool _sweepRunning;

        /// <summary>
        /// Strategy the user picked mid-sweep, installed by BtnTest_Click once the
        /// sweep has fully stopped. See ConnectNow.
        /// </summary>
        private string _pendingConnectFile;
        private string _pendingConnectName;

        private async System.Threading.Tasks.Task ConnectPending()
        {
            string file = _pendingConnectFile;
            string name = _pendingConnectName;
            _pendingConnectFile = null;
            _pendingConnectName = null;
            if (string.IsNullOrEmpty(file)) return;

            StatusNote.Text = "Подключаю " + name + "…";
            Logger.Info("Применяю стратегию службой: " + file, "apply");
            var result = await System.Threading.Tasks.Task.Run(() => ServiceOps.InstallStrategyAsService(_root, file));
            Logger.Info("Применение " + name + ": " + (result.ok ? "успех" : "ошибка " + (result.reason ?? "?") + " " + (result.message ?? "")), "apply");
            if (!result.ok)
            {
                _keepConnected = false;
                SetBusy(false, "Не удалось подключить " + name + ": " + (result.reason ?? "ошибка"));
                return;
            }
            Config.Set(AutoConnect.KeyLastWinner, file);
            AutoConnect.RememberWorking(file);
            SetBusy(false, "Подключено: " + name + " (служба Windows, автозапуск)");
        }

        /// <summary>
        /// "Подключить сейчас" on a strategy that passed: stop the sweep and make
        /// this one the live bypass. The sweep would otherwise keep testing and
        /// would leave the last candidate running, not the one the user picked.
        /// </summary>
        private async void ConnectNow(string file, string name)
        {
            if (string.IsNullOrEmpty(_root))
            {
                StatusNote.Text = "Сначала укажите папку zapret.";
                return;
            }
            _keepConnected = true;

            if (_sweepRunning)
            {
                // Ask first: stopping mid-sweep cancels the rest of the test.
                // «Да» — stop and connect; «Нет» — keep testing, do nothing.
                var answer = MessageBox.Show(
                    "Идёт проверка стратегий.\r\n\r\n" +
                    "«Да» — остановить проверку и подключить «" + name + "» как обход.\r\n" +
                    "«Нет» — продолжить проверку остальных стратегий.",
                    "Подключить «" + name + "» сейчас?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

                if (answer != MessageBoxResult.Yes)
                {
                    _keepConnected = false;
                    StatusNote.Text = "Проверка продолжается — «" + name + "» не подключена.";
                    return;
                }

                // Only record the request. Installing here would race the sweep on
                // the same service name, and whoever lost would leave the user
                // without the strategy they just clicked on.
                _pendingConnectFile = file;
                _pendingConnectName = name;
                Tester.Abort();
                StatusNote.Text = "Останавливаю проверку и подключаю " + name + "…";
                BtnAbort.IsEnabled = false;
                return;
            }

            _pendingConnectFile = file;
            _pendingConnectName = name;
            // No sweep is running, so this installs immediately. Without the busy
            // lock a second click on Apply/Auto/Connect during the install would
            // run a parallel install against the same service name.
            SetBusy(true, "Подключаю " + name + "…");
            await ConnectPending();
        }

        /// <summary>
        /// Add a strategy to the report instead of replacing it.
        ///
        /// The list used to be overwritten on every step, so only the strategy
        /// being tested right now was visible and everything already finished
        /// disappeared - which read as "the test is stuck on the first one".
        /// Now every strategy stays, with its own verdicts.
        /// </summary>
        private void ShowRun(Tester.StrategyRunResult run, int index, int total)
        {
            if (run == null)
            {
                StatusNote.Text = $"Запускаю {index + 1}/{total}…";
                return;
            }
            _tested++;
            bool ok = !run.partial && !run.failed;

            // Short header summary plus a dot colour, computed from the same raw
            // tokens the table below shows. The tester itself never judges a
            // config; this is only a fold/unfold aid, not a replacement for
            // reading the probes.
            string dotState, verdict;
            CompactSummary(run, out dotState, out verdict);

            StatusNote.Text = run.failed
                ? $"{run.name} ({(index + 1)}/{total}): не запустилась — {run.failReason}"
                : $"{run.name} ({(index + 1)}/{total}): {verdict}";

                        var row = new RunRow
            {
                Name = run.name,
                Ok = ok,
                DotState = dotState,
                // Collapsed by default — during the test and after it. The
                // header (dot, name, verdict, connect button) is always visible;
                // the per-target table opens on click. Live per-line output is
                // still visible in the status bar while the config runs.
                Expanded = false,
                Verdict = verdict,
            };
            row.ToggleCmd = new DelegateCommand(() => row.Expanded = !row.Expanded);
            // Only a fully measured strategy can be connected: the partial run of an
            // aborted sweep is a truncated score, not evidence that it works.
            // Wrapped in DelegateCommand because the XAML binds Connect via Command.
            row.Connect = row.Ok ? new DelegateCommand(() => ConnectNow(run.file, run.name)) : null;
            if (run.targets != null)
            {
                foreach (var target in run.targets) row.Targets.Add(TargetRow(target));
            }
            _runs.Add(row);

            ProbePanel.Visibility = Visibility.Visible;
            ProbeTitle.Text = $"Проверено {_tested}";
            ProbeList.ItemsSource = _runs;
        }

        /// <summary>
        /// Flatten a target for the binding: host, address reached, probes.
        ///
        /// Targets that answer with no bypass at all are marked, because a green
        /// dot next to them is not evidence of anything: on this network Google
        /// and Cloudflare were reachable with the bypass switched off, so a
        /// strategy "passing" them says nothing about whether it works. Only
        /// Discord and YouTube were actually blocked.
        /// </summary>
        private ProbeRow TargetRow(TargetResult target)
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
                probes = Tester.TokenSummary(target),
                state = Tester.TokenState(target),
                ok = target.ok,
            };
        }


        /// <summary>
        /// Header line for a config: "Discord OK · YouTube ERR".
        ///
        /// The full per-target tokens stay in the expandable table; the header
        /// only needs one glanceable line. UNSUP (the bundled curl has no
        /// TLS 1.3) is not counted as a failure, matching the tester's own
        /// analytics, which tracks it separately from ERR.
        /// </summary>
        private static void CompactSummary(Tester.StrategyRunResult run, out string dotState, out string verdict)
        {
            dotState = "warn";
            verdict = "";
            if (run == null || run.targets == null) return;

            int dOk, dErr, yOk, yErr;
            GroupCounts(run, "discord", out dOk, out dErr);
            GroupCounts(run, "youtube", out yOk, out yErr);

            string State(int ok, int err)
            {
                if (ok == 0 && err == 0) return "—";
                if (err > 0 && ok > 0) return "часть";
                if (err > 0) return "ERR";
                return "OK";
            }

            verdict = "Discord " + State(dOk, dErr) + " · YouTube " + State(yOk, yErr);

            bool dAlive = dOk > 0, yAlive = yOk > 0;
            if (dAlive && yAlive) dotState = "good";
            else if (!dAlive && !yAlive) dotState = "bad";
            else dotState = "warn";
        }

        private static void GroupCounts(Tester.StrategyRunResult run, string prefix, out int okCount, out int errCount)
        {
            okCount = 0;
            errCount = 0;
            foreach (var t in run.targets)
            {
                if (t.kind != "url") continue;
                if (!t.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var v in t.results.Values)
                {
                    if (v == "OK") okCount++;
                    else if (v == "UNSUP") continue;
                    else errCount++;
                }
            }
        }

        private StrategyRow SelectedRow()
        {
            // Prefer the live selection, but fall back to the remembered one:
            // clicking a row can hand focus to a button in the same click and
            // clear SelectedItem, which made "Применить выбранную" act as if
            // nothing was selected at all.
            return StrategyList.SelectedItem as StrategyRow ?? _selectedRow;
        }

        /// <summary>
        /// Remember the highlighted strategy. Reload() rebuilds Rows, which drops
        /// the DataGrid selection; keeping the row here means the apply buttons
        /// still know what the user picked.
        /// </summary>
        private StrategyRow _selectedRow;

        private void StrategyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = StrategyList.SelectedItem as StrategyRow;
            if (row == null) return;
            _selectedRow = row;
            if (Hint != null)
                Hint.Text = "Выбрана стратегия: " + row.Name + " — кнопка «Применить выбранную» подключит её.";
        }
    }
}