using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ydp.Services;
using Ydp.Zapret;

namespace Ydp.Pages
{
    public partial class UpdatesPage : UserControl
    {
        private string _root;

        public UpdatesPage()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            PanelState.Text = "Установленная версия панели: " + version.Major + "." + version.Minor + "." + version.Build;

            long size = Engine.BundledZipSize();
            EngineState.Text = string.IsNullOrEmpty(_root)
                ? "Папка zapret не найдена — укажите её в настройках."
                : "Папка: " + _root + Environment.NewLine
                  + "Встроенный архив: " + (size > 0 ? (size / 1024.0 / 1024.0).ToString("0.0") + " МБ" : "не найден");
            BtnInstallBundled.IsEnabled = size > 0 && !string.IsNullOrEmpty(_root);
        }

        private async void BtnInstallBundled_Click(object sender, RoutedEventArgs e)
        {
            if (_root == null) return;
            if (!Confirm("Будут заменены файлы движка в папке " + _root + ". Обход будет остановлен. Продолжить?")) return;
            EngineState.Text = "Устанавливаю…";
            var result = await Task.Run(() => Engine.Install(_root, Engine.BundledZipPath));
            EngineState.Text = result.ok
                ? "Готово: обновлено файлов — " + result.copied.Length
                : "Ошибка: " + (result.reason ?? "?") + " " + result.message;
        }

        private async void BtnChooseZip_Click(object sender, RoutedEventArgs e)
        {
            if (_root == null) { EngineState.Text = "Сначала укажите папку zapret в настройках."; return; }
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите архив zapret",
                Filter = "Архив (*.zip)|*.zip",
            };
            if (dialog.ShowDialog() != true) return;
            if (!Confirm("Файлы в папке " + _root + " будут заменены. Продолжить?")) return;
            EngineState.Text = "Устанавливаю…";
            var result = await Task.Run(() => Engine.Install(_root, dialog.FileName));
            EngineState.Text = result.ok
                ? "Готово: обновлено файлов — " + result.copied.Length
                : "Ошибка: " + (result.reason ?? "?") + " " + result.message;
        }

        private async void BtnReleases_Click(object sender, RoutedEventArgs e)
        {
            Releases.Text = "Запрашиваю GitHub…";
            BtnReleases.IsEnabled = false;
            try
            {
                var releases = await Task.Run(() => Engine.LatestReleases());
                Releases.Text = releases.Count == 0
                    ? "Релизы не получены (нет сети или GitHub недоступен)."
                    : "Доступные релизы: " + string.Join(", ", releases.Select(r => r.tag));
            }
            finally
            {
                BtnReleases.IsEnabled = true;
            }
        }

        private void BtnPanelCheck_Click(object sender, RoutedEventArgs e)
        {
            PanelNote.Text = "Проверка обновлений панели будет доступна, когда сборка опубликована в GitHub Releases. "
                + "Установленная версия: " + Assembly.GetExecutingAssembly().GetName().Version;
        }

        private void BtnOpenReleases_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/Therealwh/ZapretYoutubeDiscordPanel/releases") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                PanelNote.Text = "Не удалось открыть браузер: " + ex.Message;
            }
        }

        private static bool Confirm(string message)
        {
            return MessageBox.Show(message, "YoutubeDiscordPanel", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
    }
}