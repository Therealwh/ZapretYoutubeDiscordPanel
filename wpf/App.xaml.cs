using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Ydp
{
    public partial class App : Application
    {
        private static string _logPath;

        public static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YoutubeDiscordPanel");
                    Directory.CreateDirectory(dir);
                    _logPath = Path.Combine(dir, "panel.log");
                }
                return _logPath;
            }
        }

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch
            {
                // Logging must never be the reason something fails.
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            Log("startup, args=" + string.Join(" ", e.Args));
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log("UNHANDLED: " + args.ExceptionObject);
            };
            DispatcherUnhandledException += (s, args) =>
            {
                Log("DISPATCHER: " + args.Exception);
                args.Handled = true;
            };

            base.OnStartup(e);
            try
            {
                var window = new MainWindow();
                MainWindow = window;
                window.SourceInitialized += (s, args) => WindowChrome.ApplyDarkTitleBar(window);
                window.Show();
                Log("window shown");
            }
            catch (Exception ex)
            {
                Log("STARTUP FAILED: " + ex);
                MessageBox.Show(ex.ToString(), "YoutubeDiscordPanel", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }
}