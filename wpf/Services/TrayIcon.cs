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
            ThemeManager.ThemeChanged += OnThemeChanged;

            var menu = new ContextMenuStrip();
            StyleMenu(menu);
            menu.Items.Add(MenuItem("Открыть панель", null, (s, e) => Show()));
            menu.Items.Add(Separator());
            // Pinned strategies: one click switches the bypass to that strategy,
            // no window needed. Built fresh on every open so pins stay current.
            var pinned = MenuItem("Стратегии", null, null);
            pinned.DropDownOpening += (s, e) =>
            {
                pinned.DropDownItems.Clear();
                var root = Ydp.Zapret.Status.ResolveRoot(Config.Get("zapretRoot", null));
                var current = Config.Get("processStrategyName", null);
                var pins = (Config.Get(Ydp.Zapret.AutoConnect.KeyPinned, "") ?? "")
                    .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                if (pins.Length == 0)
                {
                    var none = MenuItem("Нет закреплённых — закрепите на странице «Стратегии»", null, null);
                    none.Enabled = false;
                    pinned.DropDownItems.Add(none);
                    return;
                }
                foreach (var pin in pins)
                {
                    var name = pin.Trim();
                    var file = System.IO.Path.Combine(root, name);
                    if (!System.IO.File.Exists(file)) file = System.IO.Path.Combine(root, name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ? name : name + ".bat");
                    var item = MenuItem(name, null, null);
                    item.Checked = current != null && string.Equals(
                        System.IO.Path.GetFileNameWithoutExtension(current),
                        System.IO.Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase);
                    var captured = file;
                    item.Click += (s2, e2) => SwitchToStrategy(root, captured);
                    pinned.DropDownItems.Add(item);
                }
            };
            menu.Items.Add(pinned);
            menu.Items.Add(MenuItem("Подключить рабочий обход", null, async (s, e) => await ConnectWorkingAsync()));
            menu.Items.Add(Separator());
            menu.Items.Add(MenuItem("Выход", null, (s, e) => Environment.Exit(0)));

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

        // ---- tray menu styling (matches the panel theme) ----------------

        private ToolStripMenuItem MenuItem(string text, Image img, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, img, onClick);
            StyleItem(item);
            return item;
        }

        private ToolStripSeparator Separator()
        {
            var sep = new ToolStripSeparator();
            StyleSeparator(sep);
            return sep;
        }

        /// <summary>
        /// Read the current palette's hex colors straight from the merged
        /// dictionary so the menu always matches whatever theme is active.
        /// Falls back to the dark values when a key is missing.
        /// </summary>
        private System.Drawing.Color PaletteColor(string key, string fallbackHex)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app != null && app.Resources.Contains(key))
                {
                    var c = (System.Windows.Media.Color)app.Resources[key];
                    return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
                }
            }
            catch { /* fall through to the fallback */ }
            var hex = fallbackHex.TrimStart('#');
            return System.Drawing.Color.FromArgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }

        private void StyleMenu(ContextMenuStrip menu)
        {
            menu.Renderer = new PanelMenuRenderer(
                () => PaletteColor("BgCardColor", "#1B2336"),
                () => PaletteColor("BgHoverColor", "#252D42"),
                () => PaletteColor("BorderColor", "#334155"));
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.BackColor = PaletteColor("BgCardColor", "#1B2336");
            menu.ForeColor = PaletteColor("TextColor", "#F8FAFC");
            menu.Font = new Font("Segoe UI", 9.5f);
        }

        private void StyleItem(ToolStripMenuItem item)
        {
            item.BackColor = PaletteColor("BgCardColor", "#1B2336");
            item.ForeColor = PaletteColor("TextColor", "#F8FAFC");
            // Hover: the accent-tinted hover color from the palette.
            item.MouseEnter += (s, e) =>
            {
                item.BackColor = PaletteColor("BgHoverColor", "#252D42");
                item.ForeColor = PaletteColor("TextColor", "#F8FAFC");
            };
            item.MouseLeave += (s, e) =>
            {
                item.BackColor = PaletteColor("BgCardColor", "#1B2336");
                item.ForeColor = PaletteColor("TextColor", "#F8FAFC");
            };
        }

        private void StyleSeparator(ToolStripSeparator sep)
        {
            sep.BackColor = PaletteColor("BgCardColor", "#1B2336");
            sep.ForeColor = PaletteColor("BorderColor", "#334155");
        }

        private void OnThemeChanged()
        {
            try
            {
                if (_icon.ContextMenuStrip is ContextMenuStrip menu)
                {
                    StyleMenu(menu);
                    foreach (ToolStripItem it in menu.Items) StyleItems(it);
                }
            }
            catch { /* restyle must never crash the tray */ }
        }

        private void StyleItems(ToolStripItem item)
        {
            if (item is ToolStripMenuItem mi)
            {
                StyleItem(mi);
                foreach (ToolStripItem child in mi.DropDownItems) StyleItems(child);
            }
            else if (item is ToolStripSeparator sep)
            {
                StyleSeparator(sep);
            }
        }

        /// <summary>ToolStripRenderer that paints the menu in the panel's card
        /// color with a subtle border, instead of the default blue Windows look.</summary>
        private sealed class PanelMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly Func<System.Drawing.Color> _bg;
            private readonly Func<System.Drawing.Color> _hover;
            private readonly Func<System.Drawing.Color> _border;

            public PanelMenuRenderer(Func<System.Drawing.Color> bg, Func<System.Drawing.Color> hover, Func<System.Drawing.Color> border)
                : base(new PanelMenuColors(bg, hover, border))
            {
                _bg = bg;
                _hover = hover;
                _border = border;
                RoundedEdges = false;
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var rect = new System.Drawing.Rectangle(System.Drawing.Point.Empty, e.Item.Size);
                // Selected items get the palette hover tint; the rest the card color.
                var fill = e.Item.Selected ? _hover() : _bg();
                using (var b = new System.Drawing.SolidBrush(fill))
                {
                    e.Graphics.FillRectangle(b, rect);
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                var border = _border();
                using (var p = new System.Drawing.Pen(border))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                }
            }
        }

        private sealed class PanelMenuColors : ProfessionalColorTable
        {
            private readonly Func<System.Drawing.Color> _bg;
            private readonly Func<System.Drawing.Color> _hover;
            private readonly Func<System.Drawing.Color> _border;
            public PanelMenuColors(Func<System.Drawing.Color> bg, Func<System.Drawing.Color> hover, Func<System.Drawing.Color> border)
            {
                _bg = bg;
                _hover = hover;
                _border = border;
            }

            public override System.Drawing.Color ToolStripDropDownBackground => _bg();
            public override System.Drawing.Color ImageMarginGradientBegin => _bg();
            public override System.Drawing.Color ImageMarginGradientMiddle => _bg();
            public override System.Drawing.Color ImageMarginGradientEnd => _bg();
            public override System.Drawing.Color MenuBorder => _border();
            public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.Transparent;
            public override System.Drawing.Color MenuItemSelected => _hover();
            public override System.Drawing.Color MenuItemSelectedGradientBegin => _hover();
            public override System.Drawing.Color MenuItemSelectedGradientEnd => _hover();
            public override System.Drawing.Color MenuItemPressedGradientBegin => _bg();
            public override System.Drawing.Color MenuItemPressedGradientEnd => _bg();
            public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(51, 65, 85);
            public override System.Drawing.Color SeparatorLight => System.Drawing.Color.FromArgb(51, 65, 85);
            public override System.Drawing.Color CheckBackground => System.Drawing.Color.Transparent;
            public override System.Drawing.Color CheckSelectedBackground => System.Drawing.Color.Transparent;
            public override System.Drawing.Color CheckPressedBackground => System.Drawing.Color.Transparent;
        }

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
                Logger.Info("Трей: смена стратегии на " + System.IO.Path.GetFileName(strategyFile), "tray");
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool preferService = string.Equals(
                        Config.Get(Ydp.Zapret.AutoConnect.KeyPreferService, "true"), "true", StringComparison.OrdinalIgnoreCase);
                    var r = preferService
                        ? InstallStrategyAsServiceSafe(rootDir, strategyFile)
                        : LaunchStrategyProcessSafe(rootDir, strategyFile);
                    Logger.Info("Трей: " + (r ? "стратегия применена" : "не удалось применить"), "tray");
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("Трей: " + ex.Message, "tray");
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
