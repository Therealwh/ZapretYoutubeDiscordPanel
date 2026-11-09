using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Ydp
{
    public sealed class AdPopupWindow : Window
    {
        public AdPopupWindow()
        {
            Title = "Партнёрское предложение";
            Width = 560; Height = 300; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            var root = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(99,102,241)),
                Background = new LinearGradientBrush(Color.FromRgb(35,17,86), Color.FromRgb(7,26,54), 35), ClipToBounds = true };
            var grid = new Grid();
            var close = new Button { Content = "✕", Width = 34, Height = 30, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0,8,8,0), Background = Brushes.Transparent,
                Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
            close.Click += (s,e) => Close(); grid.Children.Add(close);
            var ad = AdsService.Current.Popup;
            if (ad != null && !string.IsNullOrWhiteSpace(ad.Image))
            {
                try { grid.Children.Add(new Image { Source = AdsService.LoadImage(ad.Image), Stretch = Stretch.UniformToFill }); } catch { }
            }
            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(30) };
            body.Children.Add(new TextBlock { Text = "Купить рекламу", FontSize = 32, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            body.Children.Add(new TextBlock { Text = "Расскажите о своём проекте нашей аудитории", FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(219,234,254)), Margin = new Thickness(0,12,0,20), HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Content = "Узнать условия →", Padding = new Thickness(22,10,22,10), Background = new SolidColorBrush(Color.FromRgb(101,56,212)), Foreground = Brushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
            button.Click += (s,e) => { MainWindow.OpenAdvertisingLanding(ad == null ? null : ad.Url); Close(); };
            body.Children.Add(button); grid.Children.Add(body);
            root.Child = grid; Content = root;
            MouseLeftButtonDown += (s,e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        }
    }
}