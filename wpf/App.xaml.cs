using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Ydp.Services;
using Ydp.Zapret;

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
                Log("app version " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
                Logger.Info("Панель запущена (админ=" + Win.IsAdmin() + ")", "panel");

                var window = new MainWindow();
                MainWindow = window;
                window.SourceInitialized += (s, args) => WindowChrome.ApplyDarkTitleBar(window);

                Tray = new TrayIcon(window);
                window.Closed += (s, args) => Tray.Dispose();
                // Closing the window hides to the tray instead of killing the
                // bypass, which is how the panel is meant to live after setup.
                window.Closing += (s, args) =>
                {
                    if (!window.IsLoaded) return;
                    var closing = args.Cancel;
                    args.Cancel = true;
                    window.Hide();
                };

                window.Show();
                Log("window shown");

                // `--autoconnect` comes from the login item: apply the
                // remembered working strategy in the background, no questions.
                if (AutoStart.StartedWithAutoApply())
                {
                    string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
                    if (!string.IsNullOrEmpty(root))
                    {
                        var boot = System.Threading.Tasks.Task.Run(() =>
                            AutoConnect.ApplyRememberedOnStartup(root, msg => Logger.Info(msg, "autoconnect")));
                        boot.ContinueWith(t =>
                        {
                            Logger.Info("Автоприменение завершено: " + (t.IsFaulted ? "ошибка" : "ок"), "autoconnect");
                            Tray.Notify("YoutubeDiscordPanel", t.IsFaulted ? "Автоприменение не удалось" : "Запомненная стратегия применена");
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log("STARTUP FAILED: " + ex);
                MessageBox.Show(ex.ToString(), "YoutubeDiscordPanel", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private static TrayIcon Tray;
    }
}