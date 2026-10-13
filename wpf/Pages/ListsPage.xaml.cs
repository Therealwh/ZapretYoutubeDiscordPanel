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
    }
}