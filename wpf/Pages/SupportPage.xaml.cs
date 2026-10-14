using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ydp.Services;

namespace Ydp.Pages
{
    /// <summary>
    /// Donation addresses with a copy button each.
    ///
    /// A crypto address is useless if it cannot be taken from the screen, and
    /// these are long enough that a single wrong character matters, so the
    /// button copies the exact string rather than making anyone retype it.
    /// </summary>
    public partial class SupportPage : UserControl
    {
        public SupportPage()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Trc20.Text = "TGWtYdfLEVEVXScaE4Ut7ornk1A3CXCCfe";
            Trx.Text = "TGWtYdfLEVEVXScaE4Ut7ornk1A3CXCCfe";
            TonUsdt.Text = "UQCGYVM9hg2JcqnEQpTPibPxHUuSOB03zhaSE8Yn-C2aHlH-";
            TonGram.Text = "UQCGYVM9hg2JcqnEQpTPibPxHUuSOB03zhaSE8Yn-C2aHlH-";
            Bnb.Text = "0xde5a28A77359cdCf32666Db8Aae5f5Bb13c5f886";
            Note.Text = "Спасибо! 💙 Проверьте адрес перед отправкой: первые и последние символы должны совпадать с показанным.";
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            var box = Find(this, button.Tag as string) as TextBox;
            if (box == null || string.IsNullOrEmpty(box.Text)) return;

            try
            {
                Clipboard.SetText(box.Text);
                Flash(button, "Скопировано");
                Logger.Info("Адрес кошелька скопирован: " + button.Tag, "support");
            }
            catch (Exception ex)
            {
                // The clipboard is shared with the whole desktop; it can be
                // locked by another app. Say so instead of doing nothing.
                Flash(button, "Не вышло");
                Note.Text = "Не удалось скопировать: " + ex.Message + " — скопируйте вручную.";
            }
        }

        /// <summary>Confirm in place instead of a modal that would block the UI.</summary>
        private async void Flash(Button button, string text)
        {
            string original = "Скопировать";
            button.Content = text;
            await System.Threading.Tasks.Task.Delay(1400);
            if (button != null && button.IsLoaded) button.Content = original;
        }

        private static FrameworkElement Find(FrameworkElement root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            if (root.Name == name) return root;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = Find(VisualTreeHelper.GetChild(root, i) as FrameworkElement, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}