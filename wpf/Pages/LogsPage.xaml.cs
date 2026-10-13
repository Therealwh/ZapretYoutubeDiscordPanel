using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ydp.Services;

namespace Ydp.Pages
{
    public partial class LogsPage : UserControl
    {
        public LogsPage()
        {
            InitializeComponent();
            LevelFilter.ItemsSource = new[] { "all", "info", "warn", "error" };
            LevelFilter.SelectedItem = "all";
            Logger.Appended += OnAppended;
            Unloaded += (s, e) => Logger.Appended -= OnAppended;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void OnAppended(LogEntry entry)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Only auto-follow when the user is looking at the tail.
                if (LogList.Items.Count > 400)
                {
                    var items = LogList.Items.Cast<LogEntry>().Skip(200).ToList();
                    LogList.Items.Clear();
                    foreach (var item in items) LogList.Items.Add(item);
                }
                if (Matches(entry)) LogList.Items.Add(entry);
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }));
        }

        private bool Matches(LogEntry entry)
        {
            string level = LevelFilter.SelectedItem as string;
            string text = SearchBox.Text;
            if (!string.IsNullOrEmpty(level) && level != "all" && !string.Equals(entry.level, level, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(text))
            {
                bool hit = (entry.msg ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                        || (entry.source ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit) return false;
            }
            return true;
        }

        private void Refresh()
        {
            var items = Logger.Filtered(LevelFilter.SelectedItem as string, SearchBox.Text);
            LogList.Items.Clear();
            foreach (var item in items) LogList.Items.Add(item);
            Meta.Text = "строк: " + items.Count + " · файл: " + Logger.Path;
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            Logger.Clear();
            Refresh();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт логов",
                FileName = "ydp-logs-" + DateTime.Now.ToString("yyyyMM-dd-HHmm") + ".txt",
                Filter = "Текстовый файл (*.txt)|*.txt",
            };
            if (dialog.ShowDialog() != true) return;
            bool ok = Logger.Export(dialog.FileName, LevelFilter.SelectedItem as string, SearchBox.Text);
            Meta.Text = ok ? "Экспортировано: " + dialog.FileName : "Не удалось сохранить файл.";
        }

        private void BtnShowFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = Logger.Path;
                if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Meta.Text = "Не удалось открыть папку: " + ex.Message;
            }
        }
    }
}