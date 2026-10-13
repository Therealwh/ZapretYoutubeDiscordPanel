using System.Windows;

namespace Ydp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => StatusPresenter.Refresh(this);
        }
    }
}