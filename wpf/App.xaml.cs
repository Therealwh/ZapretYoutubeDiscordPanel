using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Ydp.Services;
using Ydp.Tg;
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
            // One panel at a time. Two instances fight over the Telegram port, and
            // the loser shows "proxy off" while the winner works - which reads as
            // a broken panel. The second instance says so and exits.
            bool firstInstance;
            _singleInstance = new Mutex(true, @"Local\YoutubeDiscordPanelSingleton", out firstInstance);
            if (!firstInstance)
            {
                MessageBox.Show("Панель уже запущена — проверьте значок в трее.",
                    "YoutubeDiscordPanel", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

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

                // Telegram proxy reads its settings from the config directly: the proxy must
                // know its routes even when the settings page was never opened
                // (after a login autostart, or in --proxy-test).
                TgProxy.CustomCfDomain = Config.Get("tgCfProxyDomain", string.Empty);
                TgProxy.WorkerDomain = Config.Get("tgWorkerDomain", string.Empty);

                // `--selftest` runs the ported-logic tests and exits, so CI and
                // a quick manual check can verify the build without a UI.
                foreach (var arg in e.Args)
                {
                    if (!string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)) continue;
                    Environment.ExitCode = Ydp.Tests.SelfTest.Run();
                    Shutdown();
                    return;
                }

                foreach (var arg in e.Args)
                {
                    if (!string.Equals(arg, "--proxy-test", StringComparison.OrdinalIgnoreCase)) continue;
                    // Headless route probe: start the proxy, wait, and let the
                    // per-route log lines tell us which transports this network
                    // actually allows. Used for support and before releasing.
                    RunProxyTest();
                    return;
                }

                // Start the Telegram proxy together with the panel when configured to. The
                // user points Telegram at 127.0.0.1:<port>, so a panel that comes
                // up without its listener leaves the client reconnecting to a dead
                // port - which reads as "the panel is broken".
                if (!string.Equals(Config.Get("tgProxyAuto", "true"), "false", StringComparison.OrdinalIgnoreCase))
                {
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            int port;
                            if (!int.TryParse(Config.Get("tgProxyPort", "1443"), out port) || port <= 0) port = TgProxy.DefaultPort;
                            var stats = await TgProxy.StartAsync(TgProxy.DefaultHost, port, TgProxy.Secret, true,
                                (msg, level) => Logger.Add(level == "info" ? "info" : level, msg, "tg-proxy"));
                            Logger.Info(stats.running
                                ? "Прокси Telegram запущен автоматически на " + TgProxy.DefaultHost + ":" + port
                                : "Прокси Telegram не запущен: " + (stats.lastError ?? "ошибка"), "tg-proxy");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Автостарт прокси Telegram: " + ex.Message, "tg-proxy");
                        }
                    });
                }

                if (!string.IsNullOrEmpty(TgProxy.CustomCfDomain))
                {
                    Logger.Info("Cloudflare-релей: kws1..kws6." + TgProxy.CustomCfDomain, "tg-proxy");
                }

                Logger.Info("Панель запущена (админ=" + Win.IsAdmin() + ")", "panel");

                var window = new MainWindow();
                MainWindow = window;
                window.SourceInitialized += (s, args) => WindowChrome.ApplyDarkTitleBar(window);

                Tray = new TrayIcon(window);
                window.Closed += (s, args) => Tray.Dispose();
                // Closing the window hides to the tray instead of killing the
                // bypass. The first close explains this, so the window does not
                // look like it simply refused to close.
                int closeCount = 0;
                window.Closing += (s, args) =>
                {
                    var e = args as System.ComponentModel.CancelEventArgs;
                    if (e == null || !_allowClose)
                    {
                        if (e != null) e.Cancel = true;
                        window.Hide();
                        closeCount++;
                        if (closeCount == 1)
                        {
                            Tray.Notify("Панель свёрнута в трей",
                                "Обход продолжает работать. Откройте панель двойным щелчком по значку, "
                                + "выход — через «Выход» в меню значка.");
                        }
                    }
                };
                Tray.AllowClose = () => _allowClose = true;

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
        private static bool _allowClose;

        private static Mutex _singleInstance;

        /// <summary>
        /// Headless proxy route probe (--proxy-test). Writes the result next to
        /// the exe as proxytest.txt and logs every line, so a user who cannot
        /// get Telegram working can hand over one file.
        /// </summary>
        private static void RunProxyTest()
        {
            var report = new System.Text.StringBuilder();
            Action<string, string> log = (message, level) =>
            {
                report.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
                Log("proxy-test: " + message);
            };
            try
            {
                log("start", "info");
                var task = TgProxy.StartAsync(TgProxy.DefaultHost, TgProxy.DefaultPort, null, true, log);
                if (!task.Wait(10000)) log("listener did not come up in 10s", "warn");
                log("listening on " + TgProxy.DefaultHost + ":" + TgProxy.DefaultPort, "info");

                // A local client is not needed: probe every route directly so the report says
                // which transport this network actually allows.
                // The probe MUST leave the WPF dispatcher: an async chain started
                // on the UI thread captures its SynchronizationContext, so
                // GetAwaiter().GetResult() here would deadlock against itself.
                System.Threading.Thread.Sleep(3000);
                var routes = System.Threading.Tasks.Task
                    .Run(() => TgProxy.ProbeRoutesAsync(2, log))
                    .GetAwaiter().GetResult();
                foreach (var route in routes)
                {
                    report.AppendLine("  route: " + route);
                    log("route: " + route, route.Contains("OK") ? "info" : "warn");
                }

                // Every DC has its own relay record; check them all so a dead kws3
                // does not only show up when Telegram happens to pick DC3.
                var dcChecks = System.Threading.Tasks.Task
                    .Run(() => TgProxy.ProbeCfRelaysAsync(new[] { 1, 2, 3, 4, 5, 203 }, log))
                    .GetAwaiter().GetResult();
                foreach (var check in dcChecks)
                {
                    report.AppendLine("  dc: " + check);
                }

                report.AppendLine();
                report.AppendLine("PROXYTEST listener=" + (TgProxy.Running ? "up" : "down")
                    + " url=" + TgProxy.ProxyLink());
                Log("PROXYTEST listener=" + (TgProxy.Running ? "up" : "down"));
                TgProxy.Stop();
            }
            catch (Exception ex)
            {
                report.AppendLine("FAILED: " + ex);
                Log("proxy-test FAILED: " + ex.Message);
            }
            finally
            {
                // Always leave the report behind, success or not: it is the file
                // a user is asked to send when Telegram refuses to work.
                try
                {
                    string outPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "proxytest.txt");
                    System.IO.File.WriteAllText(outPath, report.ToString());
                }
                catch { /* nothing else we can do */ }
                Environment.Exit(0);
            }
        }
    }
}