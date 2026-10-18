using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace Ydp.Services
{
    /// <summary>
    /// Tray icon. WPF has no built-in tray API on .NET Framework, but
    /// System.Windows.Forms.NotifyIcon is in the BCL РІР‚вЂќ no package, no extra
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
            menu.Items.Add("Р С›РЎвЂљР С”РЎР‚РЎвЂ№РЎвЂљРЎРЉ Р С—Р В°Р Р…Р ВµР В»РЎРЉ", null, (s, e) => Show());
            menu.Items.Add(new ToolStripSeparator());
            // Pinned strategies: one click switches the bypass to that strategy,
            // no window needed. Built fresh on every open so pins stay current.
            var pinned = new ToolStripMenuItem("Р РЋРЎвЂљРЎР‚Р В°РЎвЂљР ВµР С–Р С‘Р С‘");
            pinned.DropDownOpening += (s, e) =>
            {
                pinned.DropDownItems.Clear();
                var root = Ydp.Zapret.Status.ResolveRoot(Config.Get("zapretRoot", null));
                var current = Config.Get("processStrategyName", null);
                var pins = (Config.Get(Ydp.Zapret.AutoConnect.KeyPinned, "") ?? "")
                    .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                if (pins.Length == 0)
                {
                    var none = pinned.DropDownItems.Add("Р СњР ВµРЎвЂљ Р В·Р В°Р С”РЎР‚Р ВµР С—Р В»РЎвЂР Р…Р Р…РЎвЂ№РЎвЂ¦ РІР‚вЂќ Р В·Р В°Р С”РЎР‚Р ВµР С—Р С‘РЎвЂљР Вµ Р Р…Р В° РЎРѓРЎвЂљРЎР‚Р В°Р Р…Р С‘РЎвЂ Р Вµ Р’В«Р РЋРЎвЂљРЎР‚Р В°РЎвЂљР ВµР С–Р С‘Р С‘Р’В»");
                    none.Enabled = false;
                    return;
                }
                foreach (var pin in pins)
                {
                    var name = pin.Trim();
                    var file = System.IO.Path.Combine(root, name);
                    if (!System.IO.File.Exists(file)) file = System.IO.Path.Combine(root, name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ? name : name + ".bat");
                    var item = new ToolStripMenuItem(name)
                    {
                        Checked = current != null && string.Equals(
                            System.IO.Path.GetFileNameWithoutExtension(current),
                            System.IO.Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase),
                    };
                    var captured = file;
                    item.Click += (s2, e2) => SwitchToStrategy(root, captured);
                    pinned.DropDownItems.Add(item);
                }
            };
            menu.Items.Add(pinned);
            menu.Items.Add("Р СџР С•Р Т‘Р С”Р В»РЎР‹РЎвЂЎР С‘РЎвЂљРЎРЉ РЎР‚Р В°Р В±Р С•РЎвЂЎР С‘Р в„– Р С•Р В±РЎвЂ¦Р С•Р Т‘", null, async (s, e) => await ConnectWorkingAsync());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Р вЂ™РЎвЂ№РЎвЂ¦Р С•Р Т‘", null, (s, e) => Environment.Exit(0));

            _icon = new NotifyIcon
            {
                Text = "YoutubeDiscordPanel",
                Icon = LoadIcon(),
                Visible = true,
                ContextMenuStrip = menu,
            };
            _icon.DoubleClick += (s, e) => Show();
            // A click on an update balloon should bring the panel up: that is
            // where the "Р С›Р В±Р Р…Р С•Р Р†Р С‘РЎвЂљРЎРЉ" button lives.
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

                // NotifyIcon is a WinForms control: it may only be touched from
                // the thread that owns it (the UI thread). The health monitor and
                // the engine notifier call this from their own background threads,
                // and the resulting cross-thread exception was swallowed by the
                // catch below РІР‚вЂќ the icon silently stopped updating instead of
                // crashing. Marshal instead.
                var app = System.Windows.Application.Current;
                if (app != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(new Action(() => ApplyState(c, tip)));
                    return;
                }
                ApplyState(c, tip);
            }
            catch { /* a bad icon must never crash the panel */ }
        }

        private void ApplyState(Color c, string tip)
        {
            try
            {
                // Drop the outgoing icon: NotifyIcon does not dispose it, and
                // SetState runs on every health probe (~every 45 s).
                var old = _icon.Icon;
                _icon.Icon = MakeDotIcon(c);
                if (old != null && !old.Equals(_icon.Icon)) old.Dispose();
                _icon.Text = string.IsNullOrEmpty(tip) ? "Zapret Panel" : Trim(tip);
            }
            catch { /* a bad icon must never crash the panel */ }
        }

        private static string Trim(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 127 ? s.Substring(0, 127) : s;   // NotifyIcon limit
        }

        /// <summary>
        /// Paint the state dot.
        ///
        /// GetHicon returns a raw GDI handle and Icon.FromHandle does NOT copy
        /// it РІР‚вЂќ it wraps the same handle. Whoever creates the HICON must call
        /// DestroyIcon, otherwise every state change leaks a GDI object. This
        /// runs on every health probe (~every 45 s), so the leak reached the
        /// ~10k per-process GDI limit in about two days and killed the panel
        /// with OutOfMemoryException. Clone into a private Icon, then destroy
        /// the temporary handle immediately.
        /// </summary>
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
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(hIcon))
                    using (var clone = (Icon)tmp.Clone())
                    {
                        return clone;
                    }
                }
                finally { DestroyIcon(hIcon); }
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

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
            Logger.Info("Р СџР С•Р Т‘Р С”Р В»РЎР‹РЎвЂЎР ВµР Р…Р С‘Р Вµ РЎР‚Р В°Р В±Р С•РЎвЂЎР ВµР С–Р С• Р С•Р В±РЎвЂ¦Р С•Р Т‘Р В° Р С‘Р В· РЎвЂљРЎР‚Р ВµРЎРЏ", "autoconnect");
            var result = await System.Threading.Tasks.Task.Run(() =>
                Zapret.AutoConnect.Connect(root, msg => Logger.Info(msg, "autoconnect"), null));
            string text = result.ok
                ? "Р В Р В°Р В±Р С•РЎвЂЎР С‘Р в„– Р С•Р В±РЎвЂ¦Р С•Р Т‘ Р С—Р С•Р Т‘Р С”Р В»РЎР‹РЎвЂЎРЎвЂР Р…: " + result.strategy
                : "Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ Р С—Р С•Р Т‘Р С”Р В»РЎР‹РЎвЂЎР С‘РЎвЂљРЎРЉ: " + (result.reason ?? "Р С•РЎв‚¬Р С‘Р В±Р С”Р В°");
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

        /// <summary>
        }
        /// Tray quick-switch: apply a pinned strategy without opening the
        /// window. Runs the same code path the Strategies page row uses.
        /// </summary>
        private void SwitchToStrategy(string rootDir, string strategyFile)
        {
            try
            {
                Logger.Info("Р СћРЎР‚Р ВµР в„–: РЎРѓР СР ВµР Р…Р В° РЎРѓРЎвЂљРЎР‚Р В°РЎвЂљР ВµР С–Р С‘Р С‘ Р Р…Р В° " + System.IO.Path.GetFileName(strategyFile), "tray");
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool preferService = string.Equals(
                        Config.Get(Ydp.Zapret.AutoConnect.KeyPreferService, "true"), "true", StringComparison.OrdinalIgnoreCase);
                    var r = preferService
                        ? InstallStrategyAsServiceSafe(rootDir, strategyFile)
                        : LaunchStrategyProcessSafe(rootDir, strategyFile);
                    Logger.Info("Р СћРЎР‚Р ВµР в„–: " + (r ? "РЎРѓРЎвЂљРЎР‚Р В°РЎвЂљР ВµР С–Р С‘РЎРЏ Р С—РЎР‚Р С‘Р СР ВµР Р…Р ВµР Р…Р В°" : "Р Р…Р Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ Р С—РЎР‚Р С‘Р СР ВµР Р…Р С‘РЎвЂљРЎРЉ"), "tray");
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("Р СћРЎР‚Р ВµР в„–: " + ex.Message, "tray");
            }
        }

        private static bool InstallStrategyAsServiceSafe(string root, string file)
        {
            try { return ServiceOps.InstallStrategyAsService(root, file).ok; }
            catch { return false; }
        }

        private static bool LaunchStrategyProcessSafe(string root, string file)
        {
            try { return ServiceOps.LaunchStrategyProcess(root, file).ok; }
            catch { return false; }
        }
    }
}
