using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ydp.Services;
using Ydp.Zapret;

namespace Ydp.Pages
{
    /// <summary>
    /// DPI expert: build a strategy out of real zapret flags, and let the
    /// autotuner search for a working combination on this network.
    ///
    /// Both halves write the same artefact - an ordinary .bat strategy - so a
    /// result from either can be pinned, remembered and applied like any other.
    /// </summary>
    public sealed partial class ExpertPage : UserControl
    {
        private string _root;
        private DpiPlan _plan = new DpiPlan { name = "Мой обход" };
        private readonly Dictionary<string, FrameworkElement> _editors =
            new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        private bool _loading;
        private volatile bool _tuneCancel;
        private bool _busy;

        public ExpertPage()
        {
            InitializeComponent();
            BuildEditors();
            LoadDefaults();
        }

        // ---- editor construction -----------------------------------------

        /// <summary>
        /// The flag rows are built in code: 18 knobs with labels, hints and
        /// per-flag value lists is exactly what an ItemsControl+DataTemplate is
        /// good at, and keeping the catalog in one place means the expert and
        /// the autotuner can never disagree about what a flag is.
        /// </summary>
        private void BuildEditors()
        {
            var host = new StackPanel();
            foreach (var group in DpiCatalog.Groups)
            {
                var flags = DpiCatalog.Flags.Where(f => f.group == group).ToList();
                if (flags.Count == 0) continue;

                host.Children.Add(new TextBlock
                {
                    Text = group,
                    Style = (Style)FindResource("H1"),
                    Margin = new Thickness(0, 12, 0, 8),
                });

                foreach (var flag in flags)
                {
                    host.Children.Add(MakeRow(flag));
                }
            }
            FlagHost.ItemsSource = null;
            FlagHost.Items.Clear();
            FlagHost.Items.Add(host);
        }

        private FrameworkElement MakeRow(DpiFlag flag)
        {
            var label = new TextBlock
            {
                Text = flag.label,
                Foreground = (Brush)FindResource("TextBrush"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 2),
            };
            var hint = new TextBlock
            {
                Text = flag.hint,
                Style = (Style)FindResource("Dim"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            };

            FrameworkElement editor;
            if (flag.isChoice)
            {
                var box = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
                box.Items.Add("");
                foreach (var v in flag.values) box.Items.Add(v);
                // Multi-value flags (--dpi-desync=fake,split2) hold combinations
                // that are not in the list, so the box has to accept free text -
                // otherwise loading defaults would silently show an empty field.
                if (flag.multi) box.IsEditable = true;
                box.SelectionChanged += (s, e) => Capture(flag.id, box.Text);
                // ComboBox has no TextChanged of its own; Control raises it and an
                // editable box forwards the typed text through it.
                box.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent,
                    new TextChangedEventHandler((s, e) => Capture(flag.id, box.Text)));
                _editors[flag.id] = box;
                editor = box;
            }
            else
            {
                var tb = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
                tb.TextChanged += (s, e) => Capture(flag.id, tb.Text);
                _editors[flag.id] = tb;
                editor = tb;
            }

            return new StackPanel { Children = { label, hint, editor } };
        }

        private void Capture(string id, string value)
        {
            if (_loading) return;
            _plan.Set(id, value);
            UpdatePreview();
        }

        private void Show(string id, string value)
        {
            _loading = true;
            try
            {
                var ed = _editors[id];
                var box = ed as ComboBox;
                if (box != null) box.Text = value ?? "";
                else
                {
                    var tb = ed as TextBox;
                    if (tb != null) tb.Text = value ?? "";
                }
            }
            finally { _loading = false; }
        }

        private void LoadDefaults()
        {
            _plan = AutoTuner.PlanFor(1);
            _plan.name = NameBox != null ? NameBox.Text : _plan.name;
            foreach (var flag in DpiCatalog.Flags) Show(flag.id, _plan.Get(flag.id));
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (PreviewText == null) return;
            var args = DpiStrategy.ToArgs(_plan);
            PreviewText.Text = args.Count == 0
                ? "Ни один флаг не выбран — стратегия ничего не изменит."
                : "Применится: " + string.Join("  ", args);
        }

        // ---- page lifecycle ---------------------------------------------

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            LoadStrategies();
        }

        private void LoadStrategies()
        {
            LoadBox.Items.Clear();
            LoadBox.Items.Add("— выберите стратегию —");
            if (!string.IsNullOrEmpty(_root))
            {
                try
                {
                    foreach (var s in Strategy.ListStrategies(_root))
                        LoadBox.Items.Add(s.file);
                }
                catch { }
            }
            if (LoadBox.Items.Count > 0) LoadBox.SelectedIndex = 0;
            LoadBox.SelectionChanged += LoadBox_SelectionChanged;
        }

        private void LoadBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var file = LoadBox.SelectedItem as string;
            if (string.IsNullOrEmpty(file) || file.StartsWith("—", StringComparison.Ordinal)) return;
            if (string.IsNullOrEmpty(_root)) return;
            try
            {
                var parsed = Strategy.ParseStrategyFile(file, _root);
                _plan = DpiStrategy.FromArgs(parsed.args, Path2(file));
                _plan.name = Path2(file);
                foreach (var flag in DpiCatalog.Flags) Show(flag.id, _plan.Get(flag.id));
                ExpertNote.Text = "Загружены флаги из " + Path2(file)
                    + ". Неизвестные панели флаги сохранены как есть при применении.";
                UpdatePreview();
            }
            catch (Exception ex)
            {
                ExpertNote.Text = "Не удалось разобрать стратегию: " + ex.Message;
            }
        }

        private static string Path2(string file)
        {
            int i = (file ?? "").LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 ? file.Substring(i + 1) : file;
        }

        // ---- actions -----------------------------------------------------

        private void BtnDefaults_Click(object sender, RoutedEventArgs e)
        {
            _plan = AutoTuner.PlanFor(1);
            foreach (var flag in DpiCatalog.Flags) Show(flag.id, _plan.Get(flag.id));
            ExpertNote.Text = "Рекомендованный набор: fake+split2, обман md5sig, авто-TTL.";
            UpdatePreview();
        }

        private string SafeName()
        {
            string raw = string.IsNullOrWhiteSpace(NameBox.Text) ? "Мой обход" : NameBox.Text.Trim();
            return Strategy.SafeStrategyName(raw);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e) { Save(false); }

        private void BtnApply_Click(object sender, RoutedEventArgs e) { Save(true); }

        private void Save(bool apply)
        {
            if (string.IsNullOrEmpty(_root))
            {
                ExpertNote.Text = "Сначала укажите папку zapret.";
                return;
            }
            if (DpiStrategy.ToArgs(_plan).Count == 0)
            {
                ExpertNote.Text = "Выберите хотя бы один флаг — иначе стратегия ничего не изменит.";
                return;
            }

            string name = SafeName();
            try
            {
                _plan.name = name;
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(_root, name),
                    DpiStrategy.WriteBat(_plan, _root, name));

                if (apply)
                {
                    bool preferService = !string.Equals(
                        Config.Get(AutoConnect.KeyPreferService, "true"), "false", StringComparison.OrdinalIgnoreCase);
                    var res = preferService
                        ? Services.ServiceOps.InstallStrategyAsService(_root, name)
                        : Services.ServiceOps.LaunchStrategyProcess(_root, name);
                    if (res != null && res.ok)
                    {
                        Services.ServiceOps.WriteServiceStrategy(name);
                        ExpertNote.Text = "Сохранено и применено: " + name
                            + ". Проверьте кнопкой «Проверить обход» на главной.";
                        Logger.Info("DPI-эксперт: применена стратегия " + name, "strategies");
                    }
                    else
                    {
                        ExpertNote.Text = "Сохранено как " + name + ", но применить не вышло: "
                            + (res == null ? "?" : (res.reason ?? res.message ?? "ошибка"));
                    }
                }
                else
                {
                    ExpertNote.Text = "Сохранено как " + name + ". Стратегия появилась в общем списке.";
                }
                LoadStrategies();
            }
            catch (Exception ex)
            {
                ExpertNote.Text = "Не удалось сохранить: " + ex.Message;
            }
        }

        // ---- autotuner ---------------------------------------------------

        private void BtnTune_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(_root))
            {
                TuneVerdictBox.Visibility = Visibility.Visible;
                TuneVerdict.Text = "Сначала укажите папку zapret.";
                return;
            }

            _busy = true;
            _tuneCancel = false;
            BtnTune.IsEnabled = false;
            BtnTuneStop.IsEnabled = true;
            TuneVerdictBox.Visibility = Visibility.Collapsed;
            TuneItems.ItemsSource = null;
            TuneProgress.Text = "Перебираю сочетания… это займёт несколько минут.";

            var root = _root;
            System.Threading.Tasks.Task.Run(() => AutoTuner.Search(
                root,
                (i, total, label) => OnUi(() => TuneProgress.Text =
                    "Проверяю " + (i + 1) + "/" + total + ": " + label),
                // stop at the first candidate that fully unblocks both services
                steps => steps.Any(s => s.ok),
                msg => Logger.Info(msg, "autotune"),
                () => _tuneCancel))
            .ContinueWith(t => OnUi(() =>
            {
                _busy = false;
                BtnTune.IsEnabled = true;
                BtnTuneStop.IsEnabled = false;
                if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
                {
                    TuneVerdictBox.Visibility = Visibility.Visible;
                    TuneVerdict.Text = "Подбор прерван: "
                        + (t.Exception != null ? t.Exception.GetBaseException().Message : "ошибка");
                    return;
                }
                var outcome = t.Result;
                TuneItems.ItemsSource = outcome.steps;
                if (outcome.aborted)
                {
                    TuneProgress.Text = "Остановлено.";
                    TuneVerdictBox.Visibility = Visibility.Visible;
                    TuneVerdict.Text = "Подбор остановлен вами. Прежняя стратегия не тронута.";
                    return;
                }
                if (outcome.winner != null)
                {
                    TuneProgress.Text = "Готово.";
                    TuneVerdictBox.Visibility = Visibility.Visible;
                    TuneVerdict.Text = "Рабочее сочетание: " + outcome.winner.label
                        + ". Сохранено как стратегия " + outcome.winnerFile
                        + " и применено — она уже в общем списке.";
                    LoadStrategies();
                }
                else
                {
                    TuneProgress.Text = "Готово.";
                    TuneVerdictBox.Visibility = Visibility.Visible;
                    TuneVerdict.Text = "Ни одно сочетание не разблокировало Discord и YouTube одновременно. "
                        + "Провайдер, похоже, режет трафик жёстче обычного — попробуйте режим службы "
                        + "или другую сеть."
                        + (string.IsNullOrEmpty(outcome.restoredTo) ? "" : " Прежняя стратегия восстановлена.");
                }
            }));
        }

        private void BtnTuneStop_Click(object sender, RoutedEventArgs e)
        {
            _tuneCancel = true;
            TuneProgress.Text = "Останавливаю…";
        }

        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }
    }
}