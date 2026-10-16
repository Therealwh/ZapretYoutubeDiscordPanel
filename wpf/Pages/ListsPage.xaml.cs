using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ydp.Services;
using Ydp.Zapret;

namespace Ydp.Pages
{
    public partial class ListsPage : UserControl
    {
        private string _root;
        private string _key = "general";

        public ListsPage()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _root = Status.ResolveRoot(Config.Get("zapretRoot", null));
            if (string.IsNullOrEmpty(_root))
            {
                Editor.Text = "Папка zapret не найдена — укажите её в настройках.";
                Editor.IsEnabled = false;
                return;
            }
            Editor.IsEnabled = true;
            // winws refuses to start when a *-user.txt file is missing, so make
            // sure they exist as soon as this page opens.
            Lists.EnsureUserLists(_root);
            Load();
        }

        private string CurrentKey()
        {
            if (TabExclude.IsChecked == true) return "exclude";
            if (TabIpset.IsChecked == true) return "ipsetExclude";
            return "general";
        }

        private void Tab_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _key = CurrentKey();
            Load();
        }

        private void Load()
        {
            _key = CurrentKey();
            Editor.Text = Lists.Read(_root, _key);
            UpdateStats();
        }

        private void UpdateStats()
        {
            var stats = Lists.Stats(_root, _key);
            Stats.Text = $"строк: {stats.lines} · комментариев: {stats.comments} · пустых: {stats.blank} · дублей: {stats.duplicates} · некорректных: {stats.invalid}";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (Lists.Write(_root, _key, Editor.Text))
            {
                Logger.Info("Список сохранён: " + _key, "lists");
                UpdateStats();
            }
            else
            {
                Stats.Text = "Не удалось сохранить (нет прав на запись?).";
            }
        }

        private void BtnDedupe_Click(object sender, RoutedEventArgs e)
        {
            if (Lists.ApplyOperation(_root, _key, "dedupe")) Load();
        }

        private void BtnSort_Click(object sender, RoutedEventArgs e)
        {
            if (Lists.ApplyOperation(_root, _key, "sort")) Load();
        }

        private void BtnCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (Lists.ApplyOperation(_root, _key, "cleanup")) Load();
        }

        // ---- presets: ready-made domain lists -------------------------------
        // Typing every domain by hand is the slowest part of setting up the
        // filter. These fill the "general" list with a known-good set for a
        // service, so a one-click setup works out of the box.

        private static readonly string[] DiscordPreset =
        {
            "discord.com",
            "discord.gg",
            "discordapp.com",
            "discordapp.net",
            "discord.media",
            "cdn.discordapp.com",
            "gateway.discord.gg",
            "media.discordapp.net",
        };

        private static readonly string[] YouTubePreset =
        {
            "youtube.com",
            "youtu.be",
            "googlevideo.com",
            "ytimg.com",
            "ggpht.com",
            "jnn-pa.googleapis.com",
        };

        private void ApplyPreset(string[] domains)
        {
            if (Lists.Write(_root, "general", string.Join(Environment.NewLine, domains)))
            {
                Logger.Info("Применён готовый список доменов (" + domains.Length + ")", "lists");
                TabGeneral.IsChecked = true;
                _key = "general";
                Load();
            }
            else
            {
                Stats.Text = "Не удалось сохранить список (нет прав на запись?).";
            }
        }

        private void BtnPresetDiscord_Click(object sender, RoutedEventArgs e) => ApplyPreset(DiscordPreset);

        private void BtnPresetYoutube_Click(object sender, RoutedEventArgs e) => ApplyPreset(YouTubePreset);

        private void BtnPresetClear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Window.GetWindow(this),
                    "Очистить основной список доменов?",
                    "Очистить список", MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes)
            {
                ApplyPreset(new string[0]);
            }
        }
    }
}