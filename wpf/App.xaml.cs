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
            // A process-mode winws started by THIS instance has no other owner:
            // kill it when the panel exits or crashes, so the next start does not
            // see "off" while an orphan keeps filtering traffic. Service mode is
            // untouched - the service outlives the panel by design.
            ServiceOps.InstallProcessExitCleanup();

            // One panel at a time. Two instances fight over the Telegram port, and
            // the loser shows "proxy off" while the winner works - which reads as
            // a broken panel. A second launch instead signals the running one to
            // show itself and exits; no modal dialog, nothing to click away.
            bool firstInstance;
            _singleInstance = new Mutex(true, @"Local\YoutubeDiscordPanelSingleton", out firstInstance);
            if (!firstInstance)
            {
                SignalRunningInstance();
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

                // Stored light/dark choice, before the first window renders so the
                // app never flashes dark and then repaints.
                ThemeManager.ApplyStored();

                // Telegram proxy reads its settings from the config directly: the proxy must
                // know its routes even when the settings page was never opened
                // (after a login autostart, or in --proxy-test).
                TgProxy.CustomCfDomain = Config.Get("tgCfProxyDomain", string.Empty);
                TgProxy.WorkerDomain = Config.Get("tgWorkerDomain", string.Empty);

                // `--selftest` runs the ported-logic tests and exits, so CI and
                // a quick manual check can verify the build without a UI.
                // `--selftest --root <dir>` additionally runs the path-independence test against
                // that zapret root (used with a spaced path on another drive).
                string selftestRoot = null;
                for (int i = 0; i < e.Args.Length - 1; i++)
                {
                    if (string.Equals(e.Args[i], "--root", StringComparison.OrdinalIgnoreCase))
                    {
                        selftestRoot = e.Args[i + 1];
                        break;
                    }
                }
                foreach (var arg in e.Args)
                {
                    if (!string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)) continue;
                    int failures = Ydp.Tests.SelfTest.Run();
                    if (selftestRoot != null) failures += Ydp.Tests.PathTest.Run(selftestRoot);
                    Environment.ExitCode = failures == 0 ? 0 : 1;
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

                // Bypass watchdog: opt-in from Settings. Runs on its own thread
                // so a dead bypass recovers even while the user is on another page.
                BypassWatchdog.Start();
                // Watch the zapret engine itself for new releases (tray notice).
                EngineNotifier.Start();

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

                foreach (var arg in e.Args)
                {
                    if (!string.Equals(arg, "--autoconnect", StringComparison.OrdinalIgnoreCase)) continue;
                    // Headless "connect working bypass" - the same code path the
                    // dashboard button uses, useful for autostart and for support.
                    RunAutoConnect();
                    return;
                }

                if (!string.IsNullOrEmpty(TgProxy.CustomCfDomain))
                {
                    Logger.Info("Cloudflare-релей: kws1..kws6." + TgProxy.CustomCfDomain, "tg-proxy");
                }

                Logger.Info("Панель запущена (админ=" + Win.IsAdmin() + ")", "panel");

var window = new MainWindow();
                MainWindow = window;
                window.SourceInitialized += (s, args) => WindowChrome.ApplyDarkTitleBar(window);

                // `--page logs` opens straight on a section: handy for support
                // ("open the logs and look at me") instead of clicking there.
                for (int i = 0; i < e.Args.Length - 1; i++)
                {
                    if (!e.Args[i].StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) continue;
                    string tag = e.Args[i].Substring("--page=".Length);
                    if (string.IsNullOrEmpty(tag)) continue;
                    window.Dispatcher.BeginInvoke(new Action(() => window.Navigate(tag)));
                    break;
                }

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
                ListenForShowRequests(window);
                Log("window shown");

// `--autoconnect` comes from the login item: apply the
                // remembered working strategy in the background, no questions.
                // Repair the login entry first: if it points at a path that no
                // longer exists, the bypass silently stays down after a reboot.
                AutoStart.EnsureAutoStartEntry();

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

        /// <summary>The tray icon, once created. Windows hide on minimize only if it exists.</summary>
        public static TrayIcon Tray;
        private static bool _allowClose;

        private static Mutex _singleInstance;
        private const string ShowEventName = @"Local\YoutubeDiscordPanelShow";
        private static EventWaitHandle _showEvent;

        /// <summary>
        /// Headless "подключить рабочий обход" (--autoconnect): verify what runs
        /// now, search the known-good strategies first, install/launch the first
        /// working one and remember it. Same code path as the dashboard button.
        /// </summary>
        private static void RunAutoConnect()
        {
            var report = new System.Text.StringBuilder();
            try
            {
                string root = Status.ResolveRoot(Config.Get("zapretRoot", null));
                if (string.IsNullOrEmpty(root))
                {
                    report.AppendLine("FAILED: не найдена папка zapret");
                    Logger.Error("Автоподбор: не найдена папка zapret", "autoconnect");
                }
                else
                {
                    report.AppendLine("root: " + root);
                    var result = System.Threading.Tasks.Task.Run(() => AutoConnect.Connect(root,
                        msg => Logger.Info(msg, "autoconnect"),
                        (run, i, total) => Logger.Info(
                            "пробую " + (i + 1) + "/" + total + ": "
                            + (run == null ? "..." : run.name
                               + " Discord=" + (run.groups.discord.works ? "ok" : "нет")
                               + ", YouTube=" + run.groups.youtube.state),
                            "autoconnect"))).GetAwaiter().GetResult();

                    report.AppendLine("ok=" + result.ok
                        + " already=" + result.already
                        + " restored=" + result.restored
                        + " fallback=" + result.fallback
                        + " mode=" + result.mode
                        + " strategy=" + result.strategy
                        + " tried=" + result.candidatesTried
                        + (string.IsNullOrEmpty(result.reason) ? "" : " reason=" + result.reason));
                    Logger.Info("Автоподбор: " + report.ToString().Trim(), "autoconnect");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("FAILED: " + ex.Message);
                Logger.Error("Автоподбор упал: " + ex.Message, "autoconnect");
            }
            finally
            {
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autoconnect.txt"),
                        report.ToString());
                }
                catch { /* report is optional */ }
                Environment.Exit(0);
            }
        }

        /// <summary>Ask an already-running panel to come to the foreground.</summary>
        private static void SignalRunningInstance()
        {
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(ShowEventName))
                {
                    signal.Set();
                }
            }
            catch
            {
                // The other instance may be shutting down; nothing to do.
            }
        }

        /// <summary>Listen for "show me" requests from later launches.</summary>
        private static void ListenForShowRequests(Window window)
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            }
            catch
            {
                return;
            }
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                        window.Dispatcher.Invoke(() =>
                        {
                            window.Show();
                            window.WindowState = WindowState.Normal;
                            window.Activate();
                            window.Topmost = true;
                            window.Topmost = false;
                        });
                    }
                    catch
                    {
                        return;
                    }
                }
            })
            { IsBackground = true };
            thread.Start();
        }

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