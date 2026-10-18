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
                Editor.Text = "Р СџР В°Р С—Р С”Р В° zapret Р Р…Р Вµ Р Р…Р В°Р в„–Р Т‘Р ВµР Р…Р В° РІР‚вЂќ РЎС“Р С”Р В°Р В¶Р С‘РЎвЂљР Вµ Р ВµРЎвЂ Р Р† Р Р…Р В°РЎРѓРЎвЂљРЎР‚Р С•Р в„–Р С”Р В°РЎвЂ¦.";
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
            Stats.Text = $"РЎРѓРЎвЂљРЎР‚Р С•Р С”: {stats.lines} Р’В· Р С”Р С•Р СР СР ВµР Р…РЎвЂљР В°РЎР‚Р С‘Р ВµР Р†: {stats.comments} Р’В· Р С—РЎС“РЎРѓРЎвЂљРЎвЂ№РЎвЂ¦: {stats.blank} Р’В· Р Т‘РЎС“Р В±Р В»Р ВµР в„–: {stats.duplicates} Р’В· Р Р…Р ВµР С”Р С•РЎР‚РЎР‚Р ВµР С”РЎвЂљР Р…РЎвЂ№РЎвЂ¦: {stats.invalid}";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (Lists.Write(_root, _key, Editor.Text))
            {
                Logger.Info("Р РЋР С—Р С‘РЎРѓР С•Р С” РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…РЎвЂР Р…: " + _key, "lists");
                UpdateStats();
            }
            else
            {
                Stats.Text = "Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљРЎРЉ (Р Р…Р ВµРЎвЂљ Р С—РЎР‚Р В°Р Р† Р Р…Р В° Р В·Р В°Р С—Р С‘РЎРѓРЎРЉ?).";
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
                Logger.Info("Р вЂњР С•РЎвЂљР С•Р Р†РЎвЂ№Р в„– РЎРѓР С—Р С‘РЎРѓР С•Р С”: Р С”Р В°РЎвЂљР ВµР С–Р С•РЎР‚Р С‘РЎРЏ Р’В«" + category + "Р’В» Р С•Р В±Р Р…Р С•Р Р†Р В»Р ВµР Р…Р В°", "lists");
                TabGeneral.IsChecked = true;
                _key = "general";
                Load();
            }
            else
            {
                Stats.Text = "Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљРЎРЉ РЎРѓР С—Р С‘РЎРѓР С•Р С” (Р Р…Р ВµРЎвЂљ Р С—РЎР‚Р В°Р Р† Р Р…Р В° Р В·Р В°Р С—Р С‘РЎРѓРЎРЉ?).";
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
                Logger.Info("Р РЋР С—Р С‘РЎРѓР С•Р С” РЎР‚Р В°Р В·Р В»Р С•Р В¶Р ВµР Р… Р С—Р С• Р С”Р В°РЎвЂљР ВµР С–Р С•РЎР‚Р С‘РЎРЏР С", "lists");
                TabGeneral.IsChecked = true;
                _key = "general";
                Load();
            }
            else
            {
                Stats.Text = "Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ РЎР‚Р В°Р В·Р В»Р С•Р В¶Р С‘РЎвЂљРЎРЉ Р С—Р С• Р С”Р В°РЎвЂљР ВµР С–Р С•РЎР‚Р С‘РЎРЏР С (Р Р…Р ВµРЎвЂљ Р С—РЎР‚Р В°Р Р† Р Р…Р В° Р В·Р В°Р С—Р С‘РЎРѓРЎРЉ?).";
            }
        }

        private void BtnPresetDiscord_Click(object sender, RoutedEventArgs e)
            => ApplyPreset(CategorizedDomains.SectionDiscord, DiscordPreset);

        private void BtnPresetYoutube_Click(object sender, RoutedEventArgs e)
            => ApplyPreset(CategorizedDomains.SectionYouTube, YouTubePreset);

        private void BtnPresetTelegram_Click(object sender, RoutedEventArgs e)
        {
            int added = Zapret.FirstRun.EnsureTelegramDomains(_root);
            Logger.Info("Р С™Р В°РЎвЂљР ВµР С–Р С•РЎР‚Р С‘РЎРЏ Р’В«TelegramР’В»: " + (added > 0 ? "Р Т‘Р С•Р В±Р В°Р Р†Р В»Р ВµР Р…Р С• Р Т‘Р С•Р СР ВµР Р…Р С•Р Р† " + added : "Р Т‘Р С•Р СР ВµР Р…РЎвЂ№ РЎС“Р В¶Р Вµ Р Р…Р В° Р СР ВµРЎРѓРЎвЂљР Вµ"), "lists");
            TabGeneral.IsChecked = true;
            _key = "general";
            Load();
        }

        private void BtnPresetClear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Window.GetWindow(this),
                    "Р С›РЎвЂЎР С‘РЎРѓРЎвЂљР С‘РЎвЂљРЎРЉ Р С•РЎРѓР Р…Р С•Р Р†Р Р…Р С•Р в„– РЎРѓР С—Р С‘РЎРѓР С•Р С” Р Т‘Р С•Р СР ВµР Р…Р С•Р Р†?\n\nР вЂќР С•Р СР ВµР Р…РЎвЂ№ Telegram-Р С—РЎР‚Р С•Р С”РЎРѓР С‘ Р С—РЎР‚Р С‘ РЎРЊРЎвЂљР С•Р С Р С•РЎРѓРЎвЂљР В°Р Р…РЎС“РЎвЂљРЎРѓРЎРЏ РІР‚вЂќ Р С—Р В°Р Р…Р ВµР В»РЎРЉ Р Р†Р ВµРЎР‚Р Р…РЎвЂРЎвЂљ Р С‘РЎвЂ¦ Р В°Р Р†РЎвЂљР С•Р СР В°РЎвЂљР С‘РЎвЂЎР ВµРЎРѓР С”Р С‘.",
                    "Р С›РЎвЂЎР С‘РЎРѓРЎвЂљР С‘РЎвЂљРЎРЉ РЎРѓР С—Р С‘РЎРѓР С•Р С”", MessageBoxButton.YesNo, MessageBoxImage.Question)
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
                    Stats.Text = "Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ Р С•РЎвЂЎР С‘РЎРѓРЎвЂљР С‘РЎвЂљРЎРЉ РЎРѓР С—Р С‘РЎРѓР С•Р С” (Р Р…Р ВµРЎвЂљ Р С—РЎР‚Р В°Р Р† Р Р…Р В° Р В·Р В°Р С—Р С‘РЎРѓРЎРЉ?).";
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
            if (string.IsNullOrWhiteSpace(raw)) { QuickNote.Text = "Р вЂ™Р Р†Р ВµР Т‘Р С‘РЎвЂљР Вµ Р В°Р Т‘РЎР‚Р ВµРЎРѓ РЎРѓР В°Р в„–РЎвЂљР В°."; return; }
            string error;
            int added = UserDomains.Add(_root, raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries), out error);
            if (added > 0)
            {
                QuickNote.Text = "Р вЂќР С•Р В±Р В°Р Р†Р В»Р ВµР Р…Р С•: " + added + ". Р СњР В°Р В¶Р СР С‘РЎвЂљР Вµ Р’В«Р СџРЎР‚Р С‘Р СР ВµР Р…Р С‘РЎвЂљРЎРЉР’В», РЎвЂЎРЎвЂљР С•Р В±РЎвЂ№ РЎРѓР В°Р в„–РЎвЂљ Р Р…Р В°РЎвЂЎР В°Р В» Р С•Р В±РЎвЂ¦Р С•Р Т‘Р С‘РЎвЂљРЎРЉРЎРѓРЎРЏ РЎРѓРЎР‚Р В°Р В·РЎС“.";
                QuickDomain.Text = "";
                Load();
            }
            else
            {
                QuickNote.Text = error ?? "Р Р€Р В¶Р Вµ Р Р† РЎРѓР С—Р С‘РЎРѓР С”Р Вµ.";
            }
        }

        private void BtnQuickRemove_Click(object sender, RoutedEventArgs e)
        {
            var raw = QuickDomain.Text;
            if (string.IsNullOrWhiteSpace(raw)) { QuickNote.Text = "Р вЂ™Р Р†Р ВµР Т‘Р С‘РЎвЂљР Вµ Р В°Р Т‘РЎР‚Р ВµРЎРѓ РЎРѓР В°Р в„–РЎвЂљР В°, Р С”Р С•РЎвЂљР С•РЎР‚РЎвЂ№Р в„– Р Р…Р В°Р Т‘Р С• РЎС“Р В±РЎР‚Р В°РЎвЂљРЎРЉ."; return; }
            int removed = UserDomains.Remove(_root, raw);
            QuickNote.Text = removed > 0 ? "Р Р€Р В±РЎР‚Р В°Р Р…Р С•. Р СњР В°Р В¶Р СР С‘РЎвЂљР Вµ Р’В«Р СџРЎР‚Р С‘Р СР ВµР Р…Р С‘РЎвЂљРЎРЉР’В»." : "Р СњР Вµ Р Р…Р В°Р в„–Р Т‘Р ВµР Р…Р С• Р Р† РЎРѓР С—Р С‘РЎРѓР С”Р Вµ.";
            if (removed > 0) { QuickDomain.Text = ""; Load(); }
        }

        private void BtnQuickRestart_Click(object sender, RoutedEventArgs e)
        {
            BtnQuickRestart.IsEnabled = false;
            QuickNote.Text = "Р СџР ВµРЎР‚Р ВµР В·Р В°Р С—РЎС“РЎРѓР С”Р В°РЎР‹ Р С•Р В±РЎвЂ¦Р С•Р Т‘РІР‚В¦";
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
                    QuickNote.Text = t.IsFaulted ? "Р С›РЎв‚¬Р С‘Р В±Р С”Р В°: " + t.Exception.GetBaseException().Message : "Р вЂњР С•РЎвЂљР С•Р Р†Р С•.";
                }));
            });
        }
    }
}
