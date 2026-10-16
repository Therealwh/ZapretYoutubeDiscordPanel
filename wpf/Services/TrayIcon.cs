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
            // A click on an update balloon should bring the panel up: that is
            // where the "Обновить" button lives.
            _icon.BalloonTipClicked += (s, e) => Show();
        }

        /// <summary>
        /// Tint the tray icon by live bypass state: green = working, red = on
        /// but not unblocking, grey = off. Lets you read the state at a glance
        /// without opening the window.
        /// </summary>
        public void SetState(string state, string tip)
        {
            try
            {
                Color c;
                if (state == "ok") c = Color.FromArgb(0x2E, 0xD5, 0x73);       // green
                else if (state == "err") c = Color.FromArgb(0xE8, 0x4A, 0x4A);  // red
                else c = Color.FromArgb(0x8A, 0x94, 0xA6);                    // grey
                _icon.Icon = MakeDotIcon(c);
                _icon.Text = string.IsNullOrEmpty(tip) ? "Zapret Panel" : Trim(tip);
            }
            catch { /* a bad icon must never crash the panel */ }
        }

        private static string Trim(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 127 ? s.Substring(0, 127) : s;   // NotifyIcon limit
        }

        private static Icon MakeDotIcon(Color color)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(color))
                        g.FillEllipse(brush, 3, 3, 26, 26);
                    using (var pen = new Pen(Color.FromArgb(0x20, 0x20, 0x28), 2))
                        g.DrawEllipse(pen, 4, 4, 24, 24);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
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

        /// <summary>Lets the window really close instead of hiding to the tray.</summary>
        public Action AllowClose { get; set; }

        private void Exit()
        {
            AllowClose?.Invoke();
            Environment.Exit(0);
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