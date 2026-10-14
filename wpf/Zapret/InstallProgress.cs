using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;

namespace Ydp.Zapret
{
    /// <summary>
    /// Modal progress window for the first-run download.
    ///
    /// Without it the panel looked frozen: the download ran on a background
    /// thread while the UI sat there doing nothing for half a minute, so the
    /// obvious reading was "the program hung".
    /// </summary>
    internal sealed class InstallProgress : Window
    {
        private readonly TextBlock _status;
        private readonly System.Windows.Controls.ProgressBar _bar;

        public InstallProgress()
        {
            Title = "Установка zapret";
            Width = 460;
            Height = 150;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(255, 0x1B, 0x1F, 0x27));
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(255, 0xE6, 0xED, 0xF2));

            var panel = new StackPanel { Margin = new Thickness(20) };
            Content = panel;

            var head = new TextBlock
            {
                Text = "Ставим zapret",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
            };
            panel.Children.Add(head);

            _status = new TextBlock
            {
                Text = "Подготовка…",
                Margin = new Thickness(0, 10, 0, 12),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(255, 0x9A, 0xA4, 0xB2)),
            };
            panel.Children.Add(_status);

            _bar = new System.Windows.Controls.ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
            };
            panel.Children.Add(_bar);
        }

        public void SetStatus(string text)
        {
            Dispatcher.BeginInvoke(new Action(() => _status.Text = text));
        }

        public void SetProgress(int percent)
        {
            Dispatcher.BeginInvoke(new Action(() => _bar.Value = Math.Max(0, Math.Min(100, percent))));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                int enabled = 1;
                // Dark title bar, same reason as the main window.
                NativeMethods.DwmSetWindowAttribute(handle, 20, ref enabled, 4);
                NativeMethods.DwmSetWindowAttribute(handle, 19, ref enabled, 4);
            }
            catch { /* cosmetic */ }
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}