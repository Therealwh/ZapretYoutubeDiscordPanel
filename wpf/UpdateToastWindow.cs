using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Ydp
{
    /// <summary>
    /// Compact update notification anchored above the Windows taskbar. Unlike
    /// a tray balloon it has real actions, so "Позже" can snooze the reminder.
    /// </summary>
    public sealed class UpdateToastWindow : Window
    {
        public event EventHandler UpdateRequested;
        public event EventHandler LaterRequested;

        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(137, 82, 255));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(183, 190, 214));

        public UpdateToastWindow(string version)
        {
            Title = "YoutubeDiscordPanel — обновление";
            Width = 390;
            Height = 174;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = true;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(18, 22, 43)),
                BorderBrush = Accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18),
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(95, 45, 220),
                    BlurRadius = 24,
                    ShadowDepth = 0,
                    Opacity = 0.38
                }
            };

            var root = new StackPanel();
            var heading = new DockPanel { LastChildFill = true };
            var mark = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(53, 35, 104)),
                Child = new TextBlock
                {
                    Text = "↥",
                    FontSize = 22,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
            DockPanel.SetDock(mark, Dock.Left);
            heading.Children.Add(mark);
            var title = new TextBlock
            {
                Text = "Доступно обновление",
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 0, 0, 0)
            };
            heading.Children.Add(title);
            root.Children.Add(heading);

            root.Children.Add(new TextBlock
            {
                Text = "Вышла версия " + version + ". Установите обновление, чтобы получить новые возможности.",
                Foreground = Muted,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(45, 7, 0, 14)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var update = MakeButton("Обновить сейчас", Accent, Brushes.White, true);
            update.Click += (s, e) => UpdateRequested?.Invoke(this, EventArgs.Empty);
            var later = MakeButton("Позже", new SolidColorBrush(Color.FromRgb(35, 40, 65)), Brushes.White, false);
            later.Margin = new Thickness(8, 0, 0, 0);
            later.Click += (s, e) => LaterRequested?.Invoke(this, EventArgs.Empty);
            buttons.Children.Add(update);
            buttons.Children.Add(later);
            root.Children.Add(buttons);
            card.Child = root;
            Content = card;

            Loaded += (s, e) =>
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - Width - 14;
                Top = area.Bottom - Height - 12;
            };
        }

        private static Button MakeButton(string text, Brush background, Brush foreground, bool primary)
        {
            return new Button
            {
                Content = text,
                Background = background,
                Foreground = foreground,
                BorderBrush = primary ? Accent : new SolidColorBrush(Color.FromRgb(57, 63, 91)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 8, 14, 8),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                MinWidth = primary ? 142 : 78
            };
        }
    }
}
