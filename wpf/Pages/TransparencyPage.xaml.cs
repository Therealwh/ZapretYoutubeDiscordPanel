using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ydp.Services;
using Ydp.Zapret;

namespace Ydp.Pages
{
    /// <summary>
    /// Trust page: what the panel does, what it does not, and four checks that
    /// run on the user's machine. The checks are the point - see Trust.cs.
    /// </summary>
    public sealed partial class TransparencyPage : UserControl
    {
        public TransparencyPage()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Run();
        }

        private void BtnRecheck_Click(object sender, RoutedEventArgs e)
        {
            Run();
        }

        private void Run()
        {
            BtnRecheck.IsEnabled = false;
            LoadAv();
            Items.ItemsSource = null;
            try
            {
                // Opening the certificate stores and the SCM can take a moment,
                // and this runs from a Loaded handler.
                System.Threading.Tasks.Task.Run(() => Trust.RunAll())
                    .ContinueWith(t =>
                    {
                        OnUi(() =>
                        {
                            BtnRecheck.IsEnabled = true;
                            Items.ItemsSource = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                                ? (System.Collections.IEnumerable)t.Result
                                : new List<TrustCheck>
                                {
                                    new TrustCheck { title = "Проверка не удалась", state = "warn",
                                                     detail = t.Exception != null
                                                         ? t.Exception.GetBaseException().Message
                                                         : "неизвестная ошибка" },
                                };
                        });
                    });
            }
            catch
            {
                BtnRecheck.IsEnabled = true;
            }
        }

        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }

        // ---- antivirus helper ------------------------------------------

        private void LoadAv()
        {
            try
            {
                var avs = AvHelper.Detect();
                AvList.Text = "Обнаружено: " + string.Join(", ", avs.Select(a => a.product));
                var root = Status.ResolveRoot(Config.Get("zapretRoot", null));
                var folders = AvHelper.FoldersToWhitelist(root);
                AvNote.Text = "Папки: " + string.Join("; ", folders);
            }
            catch (Exception ex)
            {
                AvList.Text = "Не удалось определить антивирус: " + ex.Message;
            }
        }

        private void BtnAvExclude_Click(object sender, RoutedEventArgs e)
        {
            BtnAvExclude.IsEnabled = false;
            AvNote.Text = "Применяю…";
            var root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            var folders = AvHelper.FoldersToWhitelist(root);
            System.Threading.Tasks.Task.Run(() => AvHelper.AddDefenderExclusions(folders))
                .ContinueWith(t =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BtnAvExclude.IsEnabled = true;
                        AvNote.Text = t.IsFaulted ? "Ошибка: " + t.Exception.GetBaseException().Message : t.Result;
                    }));
                });
        }
    }
}