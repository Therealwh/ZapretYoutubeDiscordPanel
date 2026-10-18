using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private void ApplyPreset(string category, string[] domains)
        {
            // Fill this category's section and leave every other line alone. The
            // panel also keeps the Telegram-proxy domains in this same file, so a
            // preset must never replace the list РІР‚вЂќ only add to its own section.
            var existing = Lists.Read(_root, "general") ?? string.Empty;
            var updated = CategorizedDomains.Ensure(existing, category, domains);
            if (Lists.Write(_root, "general", updated))
            {
                // Defensive: guarantee the proxy domains are present.
                Zapret.FirstRun.EnsureTelegramDomains(_root);
                Logger.Info("Готовый список: категория «" + category + "» обновлена", "lists");
                TabGeneral.IsChecked = true;
                _key = "general";
                Load();
            }
            else
            {
                Stats.Text = "Не удалось сохранить список (нет прав на запись?).";
            }
        }

        /// <summary>Lay the file out into the four sections, sorting every domain into the right one.</summary>
        private void BtnOrganize_Click(object sender, RoutedEventArgs e)
        {
            var existing = Lists.Read(_root, "general") ?? string.Empty;
            var organized = CategorizedDomains.Organize(
                existing, DiscordPreset, YouTubePreset, Zapret.FirstRun.TelegramDomains());
            if (Lists.Write(_root, "general", organized))
            {
                Logger.Info("Список разложен по категориям", "lists");
                TabGeneral.IsChecked = true;
                _key = "general";
                Load();
            }
            else
            {
                Stats.Text = "Не удалось разложить по категориям (нет прав на запись?).";
            }
        }

        private void BtnPresetDiscord_Click(object sender, RoutedEventArgs e)
            => ApplyPreset(CategorizedDomains.SectionDiscord, DiscordPreset);

        private void BtnPresetYoutube_Click(object sender, RoutedEventArgs e)
            => ApplyPreset(CategorizedDomains.SectionYouTube, YouTubePreset);

        private void BtnPresetTelegram_Click(object sender, RoutedEventArgs e)
        {
            int added = Zapret.FirstRun.EnsureTelegramDomains(_root);
            Logger.Info("Категория «Telegram»: " + (added > 0 ? "добавлено доменов " + added : "домены уже на месте"), "lists");
            TabGeneral.IsChecked = true;
            _key = "general";
            Load();
        }

        private void BtnPresetClear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Window.GetWindow(this),
                    "Очистить основной список доменов?\n\nДомены Telegram-прокси при этом останутся — панель вернёт их автоматически.",
                    "Очистить список", MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes)
            {
                if (Lists.Write(_root, "general", string.Empty))
                {
                    Zapret.FirstRun.EnsureTelegramDomains(_root);
                    TabGeneral.IsChecked = true;
                    _key = "general";
                    Load();
                }
                else
                {
                    Stats.Text = "Не удалось очистить список (нет прав на запись?).";
                }
            }

        }
        // ---- quick add / remove own domains ----------------------------

        private void QuickDomain_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) BtnQuickAdd_Click(sender, e);
        }

        private void BtnQuickAdd_Click(object sender, RoutedEventArgs e)
        {
            var raw = QuickDomain.Text;
            if (string.IsNullOrWhiteSpace(raw)) { QuickNote.Text = "Введите адрес сайта."; return; }
            string error;
            int added = UserDomains.Add(_root, raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries), out error);
            if (added > 0)
            {
                QuickNote.Text = "Добавлено: " + added + ". Нажмите «Применить», чтобы сайт начал обходиться сразу.";
                QuickDomain.Text = "";
                Load();
            }
            else
            {
                QuickNote.Text = error ?? "Уже в списке.";
            }
        }

        private void BtnQuickRemove_Click(object sender, RoutedEventArgs e)
        {
            var raw = QuickDomain.Text;
            if (string.IsNullOrWhiteSpace(raw)) { QuickNote.Text = "Введите адрес сайта, который надо убрать."; return; }
            int removed = UserDomains.Remove(_root, raw);
            QuickNote.Text = removed > 0 ? "Убрано. Нажмите «Применить»." : "Не найдено в списке.";
            if (removed > 0) { QuickDomain.Text = ""; Load(); }
        }

        private void BtnQuickRestart_Click(object sender, RoutedEventArgs e)
        {
            BtnQuickRestart.IsEnabled = false;
            QuickNote.Text = "Перезапускаю обход…";
            System.Threading.Tasks.Task.Run(() =>
            {
                var root = Status.ResolveRoot(Config.Get("zapretRoot", null));
                var current = Config.Get("lastStrategyFile", null);
                if (!string.IsNullOrEmpty(current) && File.Exists(current))
                    ServiceOps.LaunchStrategyProcess(root, current);
                else
                    ServiceOps.ResetBypass();
            }).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    BtnQuickRestart.IsEnabled = true;
                    QuickNote.Text = t.IsFaulted ? "Ошибка: " + t.Exception.GetBaseException().Message : "Готово.";
                }));
            });
        }
    }
}
