using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace Ydp.Services
{
    /// <summary>
    /// Tray icon. WPF has no built-in tray API on .NET Framework, but
    /// System.Windows.Forms.NotifyIcon is in the BCL — no package, no extra
    /// process, nothing to install.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Window _window;
        private bool _disposed;

        public TrayIcon(Window window)
        {
            _window = window;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть панель", null, (s, e) => Show());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Подключить рабочий обход", null, async (s, e) => await ConnectWorkingAsync());
            menu.Items.Add("Выход", null, (s, e) => Environment.Exit(0));

            _icon = new NotifyIcon
            {
                Text = "YoutubeDiscordPanel",
                Icon = LoadIcon(),
                Visible = true,
                ContextMenuStrip = menu,
            };
            _icon.DoubleClick += (s, e) => Show();
        }

        private static Icon LoadIcon()
        {
            try
            {
                string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (System.IO.File.Exists(path))
                {
                    var icon = Icon.ExtractAssociatedIcon(path);
                    if (icon != null) return icon;
                }
            }
            catch
            {
                // Fall through to a generated icon.
            }
            try
            {
                using (var bmp = new Bitmap(32, 32))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.FromArgb(0x6D, 0x6B, 0xFF));
                        using (var brush = new SolidBrush(Color.White))
                        {
                            g.FillEllipse(brush, 8, 8, 16, 16);
                        }
                    }
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        public void Show()
        {
            _window.Show();
            _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        private async System.Threading.Tasks.Task ConnectWorkingAsync()
        {
            string root = Zapret.Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(root)) return;
            Logger.Info("Подключение рабочего обхода из трея", "autoconnect");
            var result = await System.Threading.Tasks.Task.Run(() =>
                Zapret.AutoConnect.Connect(root, msg => Logger.Info(msg, "autoconnect"), null));
            string text = result.ok
                ? "Рабочий обход подключён: " + result.strategy
                : "Не удалось подключить: " + (result.reason ?? "ошибка");
            Logger.Info(text, "autoconnect");
            Show();
        }

        public void Notify(string title, string message)
        {
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = message;
                _icon.ShowBalloonTip(3000);
            }
            catch
            {
                // Notifications are optional.
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}