using System.Windows;

namespace Ydp
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var window = new MainWindow();
            MainWindow = window;
            window.SourceInitialized += (s, args) => WindowChrome.ApplyDarkTitleBar(window);
            window.Show();
        }
    }
}