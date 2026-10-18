using System;
using System.Windows;

namespace Ydp.Services
{
    /// <summary>
    /// Light/dark theme switching.
    ///
    /// Only the palette dictionary changes — Theme/Palette.Dark.xaml and
    /// Theme/Palette.Light.xaml define the same keys, and every style reads them
    /// through DynamicResource. Swapping MergedDictionaries[0] therefore
    /// restyles the running app: no restart, and nothing to reload.
    ///
    /// The choice is stored in config.json under "theme" ("dark" / "light").
    /// </summary>
    public static class ThemeManager
    {
        public const string Dark = "dark";
        public const string Light = "light";
        private const string ConfigKey = "theme";

        // Pack URIs must name the ASSEMBLY (YoutubeDiscordPanel), not the root
        // namespace (Ydp) — getting that wrong loads nothing and throws.
        private const string DarkPaletteUri =
            "pack://application:,,,/YoutubeDiscordPanel;component/Theme/Palette.Dark.xaml";
        private const string LightPaletteUri =
            "pack://application:,,,/YoutubeDiscordPanel;component/Theme/Palette.Light.xaml";

        private static ResourceDictionary _palette;

        /// <summary>Raised after the palette actually changed. The tray menu
        /// uses it to restyle itself to the new theme.</summary>
        public static event Action ThemeChanged;

        /// <summary>Current theme, as stored. Always Dark or Light.</summary>
        public static string Current
        {
            get
            {
                var v = Config.Get(ConfigKey, Dark);
                return string.Equals(v, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
            }
        }

        /// <summary>
        /// Apply a theme. Safe to call before the window exists and safe to call
        /// repeatedly with the same value.
        /// </summary>
        public static void Apply(string theme)
        {
            if (string.IsNullOrEmpty(theme)) theme = Dark;
            theme = string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

            var app = Application.Current;
            if (app == null) return;
            var dicts = app.Resources.MergedDictionaries;
            if (dicts.Count == 0) return;

            // Index 0 is the palette by construction (see App.xaml). Read it
            // from the live list every time instead of trusting a cached
            // reference: if anything else replaced index 0, the stale cache
            // would make Apply() think the theme is already active and the
            // switch would silently do nothing.
            var current = dicts[0];

            string currentPath = SourceOf(current);
            string wantedPath = theme == Light ? LightPaletteUri : DarkPaletteUri;
            if (string.Equals(currentPath, wantedPath, StringComparison.OrdinalIgnoreCase)) return;

            var replacement = new ResourceDictionary { Source = new Uri(wantedPath, UriKind.Absolute) };
            // Replace instead of Remove+Insert: removing first would leave the app
            // without a palette for an instant and DynamicResource lookups during
            // that window would fall back to system defaults (white flashes).
            dicts[0] = replacement;
            _palette = replacement;
            try { ThemeChanged?.Invoke(); } catch { /* a restyle listener must not break the switch */ }
        }

        /// <summary>Apply and remember. Used by the settings selector.</summary>
        public static void SetTheme(string theme)
        {
            Apply(theme);
            try { Config.Set(ConfigKey, theme); } catch { /* config write must not break the UI */ }
            Logger.Info("Тема переключена: " + theme, "theme");
        }

        /// <summary>Apply the stored theme at startup.</summary>
        public static void ApplyStored()
        {
            Apply(Config.Get(ConfigKey, Dark));
        }

        private static string SourceOf(ResourceDictionary d)
        {
            try { return d.Source != null ? d.Source.OriginalString : null; }
            catch { return null; }
        }
    }
}