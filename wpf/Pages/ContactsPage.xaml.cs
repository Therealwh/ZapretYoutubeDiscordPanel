using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Ydp.Services;

namespace Ydp.Pages
{
    /// <summary>
    /// Contacts: the author's Telegram channel and a couple of shortcuts. Static
    /// links only — nothing here touches the bypass.
    /// </summary>
    public partial class ContactsPage : UserControl
    {
        public const string ChannelUrl = "https://t.me/wtfpoxy";
        public const string ChannelHandle = "@wtfpoxy";
        // Telegram deep link: opens the channel inside the Telegram app.
        public const string ChannelAppUrl = "tg://resolve?domain=wtfpoxy";

        public ContactsPage()
        {
            InitializeComponent();
        }

        private void BtnOpenChannel_Click(object sender, RoutedEventArgs e)
        {
            // Prefer the Telegram app via the tg://resolve deep link; if Telegram
            // is not installed the shell open fails and we fall back to the web.
            try
            {
                Process.Start(new ProcessStartInfo(ChannelAppUrl) { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                Logger.Info("Канал: не удалось открыть через tg://resolve (" + ex.Message + ") — открываю сайт", "contacts");
            }
            try
            {
                Process.Start(new ProcessStartInfo(ChannelUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warn("Не удалось открыть канал: " + ex.Message, "contacts");
                MessageBox.Show("Откройте вручную: " + ChannelUrl, "Канал", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnCopyChannel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ChannelUrl);
                Logger.Info("Ссылка на канал скопирована", "contacts");
            }
            catch (Exception ex)
            {
                Logger.Warn("Не удалось скопировать ссылку: " + ex.Message, "contacts");
            }
        }
    }
}
