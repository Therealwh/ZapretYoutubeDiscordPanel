using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Ydp.Services;

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
    }
}