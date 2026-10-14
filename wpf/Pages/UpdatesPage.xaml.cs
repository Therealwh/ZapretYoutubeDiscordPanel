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
            string installed = string.IsNullOrEmpty(_root) ? null : Engine.InstalledVersion(_root);
            EngineState.Text = string.IsNullOrEmpty(_root)
                ? "Папка zapret не найдена — укажите её в настройках."
                : "Папка: " + _root + Environment.NewLine
                  + "Установленная версия движка: " + (installed ?? "неизвестна (ставилась вручную)") + Environment.NewLine
                  + "Встроенный архив: " + (size > 0 ? (size / 1024.0 / 1024.0).ToString("0.0") + " МБ" : "не найден");
            BtnInstallBundled.IsEnabled = size > 0 && !string.IsNullOrEmpty(_root);
            BtnUpdateEngine.IsEnabled = false;
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
            BtnUpdateEngine.IsEnabled = false;
            try
            {
                var latest = await Task.Run(() => Engine.LatestRelease());
                if (latest == null)
                {
                    Releases.Text = "Не удалось получить релиз с GitHub. Проверьте интернет.";
                    return;
                }
                if (string.IsNullOrEmpty(latest.assetUrl))
                {
                    Releases.Text = "Релиз " + latest.tag + " найден, но архива .zip в нём нет.";
                    return;
                }

                _pendingRelease = latest;
                string installed = _root == null ? null : Engine.InstalledVersion(_root);
                bool newer = installed != null
                    && Compare(latest.name, installed) > 0;
                bool unknown = installed == null;

                Releases.Text = "Последний релиз zapret: " + latest.tag
                    + " · архив " + latest.assetName
                    + " (" + (latest.assetSize > 0 ? (latest.assetSize / 1024.0 / 1024.0).ToString("0.0") + " МБ" : "размер неизвестен") + ")"
                    + Environment.NewLine
                    + (unknown
                        ? "Установленная версия неизвестна (папка ставилась вручную). Можно обновить."
                        : "Установлено: " + installed)
                    + Environment.NewLine
                    + (unknown
                        ? "Можно обновить: версия движка неизвестна, папка ставилась вручную."
                        : newer ? "Доступно обновление." : "У вас последняя доступная версия.");
                BtnUpdateEngine.IsEnabled = true;
            }
            finally
            {
                BtnReleases.IsEnabled = true;
            }
        }

        private EngineRelease _pendingRelease;

        /// <summary>Compare dotted version numbers; anything unparsable sorts as 0.</summary>
        private static int Compare(string a, string b)
        {
            var pa = (a ?? "").Split('.');
            var pb = (b ?? "").Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
                int vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        }

        private async void BtnUpdateEngine_Click(object sender, RoutedEventArgs e)
        {
            if (_root == null) { Releases.Text = "Сначала укажите папку zapret в настройках."; return; }
            if (_pendingRelease == null || string.IsNullOrEmpty(_pendingRelease.assetUrl)) return;
            if (!Confirm("Обновить zapret до " + _pendingRelease.tag + "?\r\n\r\nФайлы движка в папке "
                + _root + " будут заменены, обход будет остановлен на время установки.\r\n\r\n"
                + "Ваши собственные списки (*-user.txt) и файлы в bin не из архива не затрагиваются.")) return;

            string zip = Path.Combine(Path.GetTempPath(), "zapret-update-" + _pendingRelease.tag + ".zip");
            EngineProgress.Visibility = Visibility.Visible;
            EngineProgress.Value = 0;
            Releases.Text = "Скачиваю " + _pendingRelease.assetName + "…";
            BtnUpdateEngine.IsEnabled = false;

            try
            {
                bool ok = await Task.Run(() => Engine.Download(_pendingRelease.assetUrl, zip,
                    percent => Dispatcher.BeginInvoke(new Action(() => EngineProgress.Value = percent))));
                if (!ok)
                {
                    Releases.Text = "Не удалось скачать архив. Проверьте интернет и попробуйте ещё раз.";
                    return;
                }

                Releases.Text = "Устанавливаю zapret " + _pendingRelease.tag + "…";
                var result = await Task.Run(() => Engine.Install(_root, zip, _pendingRelease.name));
                if (!result.ok)
                {
                    Releases.Text = "Ошибка установки: " + (result.reason ?? "?") + " " + result.message;
                    return;
                }

                Releases.Text = "Готово: zapret обновлён до " + _pendingRelease.tag
                    + ", файлов — " + result.copied.Length + "."
                    + Environment.NewLine
                    + "Подключите рабочий обход заново, чтобы применить новый движок."
                    + (string.IsNullOrEmpty(result.message) ? "" : Environment.NewLine + result.message);
                _pendingRelease = null;
            }
            catch (Exception ex)
            {
                Releases.Text = "Ошибка обновления: " + ex.Message;
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
                EngineProgress.Visibility = Visibility.Collapsed;
                BtnUpdateEngine.IsEnabled = false;
            }
        }

        private async void BtnPanelCheck_Click(object sender, RoutedEventArgs e)
        {
            PanelNote.Text = "Проверяю GitHub…";
            BtnPanelCheck.IsEnabled = false;
            try
            {
                var state = await Task.Run(() => PanelUpdater.Check());
                if (state == null || string.IsNullOrEmpty(state.latest))
                {
                    PanelNote.Text = "Не удалось получить информацию о релизах. Проверьте интернет.";
                    return;
                }
                PanelNote.Text = state.available
                    ? "Доступна версия " + state.latest + " (у вас " + state.current + ")"
                    : "У вас последняя версия: " + state.current;
                BtnPanelInstall.IsEnabled = state.available;
            }
            finally
            {
                BtnPanelCheck.IsEnabled = true;
            }
        }

        private async void BtnPanelInstall_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Скачать и установить новую версию панели?\r\n\r\n"
                + "Панель перезапустится после установки.")) return;
            PanelNote.Text = "Скачиваю обновление…";
            BtnPanelInstall.IsEnabled = false;
            var result = await Task.Run(() => PanelUpdater.Install());
            PanelNote.Text = result.ok
                ? "Обновление установится сразу после закрытия панели."
                : "Ошибка обновления: " + (result.reason ?? "ошибка") + " " + result.message;
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