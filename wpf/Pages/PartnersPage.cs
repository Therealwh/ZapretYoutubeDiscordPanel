using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace Ydp.Pages
{
    /// <summary>Five compact partner placements. The landing page is opened externally.</summary>
    public sealed class PartnersPage : UserControl
    {
        public PartnersPage()
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = "Партнёры", FontSize = 27, FontWeight = FontWeights.Bold,
                Foreground = Brush("#F4F6FF"), Margin = new Thickness(0, 0, 0, 5) });
            root.Children.Add(new TextBlock { Text = "Наши партнёры и рекламодатели", FontSize = 13,
                Foreground = Brush("#9AA8C7"), Margin = new Thickness(0, 0, 0, 18) });
            var grid = new UniformGrid { Columns = 3, Rows = 2 };
            for (int i = 0; i < 5; i++) grid.Children.Add(CreatePlaceholder(i + 1));
            root.Children.Add(grid);
            root.Children.Add(new TextBlock { Text = "Хотите разместить свою рекламу? Нажмите на любой баннер — откроется страница для рекламодателей.",
                TextWrapping = TextWrapping.Wrap, Foreground = Brush("#9AA8C7"), FontSize = 12, Margin = new Thickness(2, 16, 2, 0) });
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };
        }

        private static UIElement CreatePlaceholder(int number)
        {
            var card = new Border { Height = 140, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1), BorderBrush = Brush("#5967C8"), Cursor = Cursors.Hand,
                Background = new LinearGradientBrush(Color.FromRgb(35, 17, 86), Color.FromRgb(7, 26, 54), 35) };
            var grid = new Grid { ClipToBounds = true };
            grid.Children.Add(new TextBlock { Text = "✦", FontSize = 64, Foreground = Brush("#334F8BFF"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -16, 8, 0) });
            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 8, 10, 8) };
            body.Children.Add(new TextBlock { Text = "Купить рекламу", FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(new TextBlock { Text = "Ваш баннер может быть здесь", FontSize = 11,
                Foreground = Brush("#DCE7FF"), Margin = new Thickness(0, 5, 0, 8) });
            var cta = new Border { Background = Brush("#6538D4"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left };
            cta.Child = new TextBlock { Text = "Узнать подробнее →", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
            body.Children.Add(cta);
            grid.Children.Add(body);
            card.Child = grid;
            card.MouseLeftButtonUp += (s, e) => MainWindow.OpenAdvertisingLanding();
            return card;
        }

        private static SolidColorBrush Brush(string value)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
    }
}