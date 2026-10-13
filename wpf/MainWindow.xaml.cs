using System;
using System.Windows;
using System.Windows.Controls;
using Ydp.Pages;
using Ydp.Zapret;

namespace Ydp
{
    public partial class MainWindow : Window
    {
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

        public MainWindow()
        {
            InitializeComponent();
            // `--page=strategies` opens straight into a section (used by autostart
            // flows and when the user wants to jump back to a specific screen).
            string start = null;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) start = arg.Substring(7);
            }
            Navigate(start ?? "dashboard");
            UpdateFooter();
        }

        private void Navigate(string tag)
        {
            try
            {
                switch (tag)
                {
                    case "strategies":
                        if (_strategies == null) _strategies = new StrategiesPage();
                        PageHost.Content = _strategies;
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

        private void UpdateFooter()
        {
            string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            FooterNote.Text = string.IsNullOrEmpty(root) ? "Папка zapret не найдена" : root;
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
    }
}