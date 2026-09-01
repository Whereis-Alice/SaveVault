using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Playnite.SDK;

namespace SaveVault.Services
{
    /// <summary>
    /// Publishes the resource keys the plugin's controls bind to, without ever overwriting a
    /// theme.
    ///
    /// Every visual value is a DynamicResource so a theme - or Theme Forge on top of a theme -
    /// can restyle the panel. Theme dictionaries are merged long before add-ons load, so a
    /// plugin dictionary merged normally would win and silently defeat the theme. Each key is
    /// therefore only added when nobody defined it yet, and the fallback is derived from the
    /// standard Playnite keys the running theme already provides.
    /// </summary>
    public static class ThemeBridge
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Key, the theme keys to inherit from in order, and a last resort literal.</summary>
        private static readonly Tuple<string, string[], string>[] BrushKeys =
        {
            Tuple.Create("SaveVaultTextBrush", new[] { "TextBrush" }, "#FFEFEFEF"),
            Tuple.Create("SaveVaultSubTextBrush", new[] { "TextBrushDarker", "TextBrushDark", "TextBrush" }, "#FF9AA0A6"),
            Tuple.Create("SaveVaultAccentBrush", new[] { "GlyphBrush", "HighlightGlyphBrush" }, "#FF1A9FFF"),
            Tuple.Create("SaveVaultSectionBackgroundBrush", new string[0], "#00000000"),
            Tuple.Create("SaveVaultCardBackgroundBrush", new[] { "GridItemBackgroundBrush", "ControlBackgroundBrush" }, "#14FFFFFF"),
            Tuple.Create("SaveVaultCardHoverBackgroundBrush", new[] { "DetailsViewItemIsMouseOverBackgroundBrush", "NormalBrush" }, "#26FFFFFF"),
            Tuple.Create("SaveVaultCardBorderBrush", new string[0], "#1AFFFFFF"),
            Tuple.Create("SaveVaultChipBackgroundBrush", new[] { "GroupCountBackgroundBrush" }, "#33000000"),
            Tuple.Create("SaveVaultProtectedBrush", new string[0], "#FF63C98A"),
            Tuple.Create("SaveVaultPendingBrush", new string[0], "#FFFFC14D"),
            Tuple.Create("SaveVaultUnknownBrush", new[] { "TextBrushDarker" }, "#FF9AA0A6")
        };

        private static readonly Tuple<string, double>[] DoubleKeys =
        {
            Tuple.Create("SaveVaultHeaderFontSize", 15.0),
            Tuple.Create("SaveVaultTextFontSize", 12.0),
            Tuple.Create("SaveVaultSmallFontSize", 11.0),
            Tuple.Create("SaveVaultSectionSpacing", 10.0)
        };

        private static bool done;

        public static void EnsureDefaults()
        {
            if (done || Application.Current == null)
            {
                return;
            }

            done = true;
            var resources = Application.Current.Resources;

            try
            {
                foreach (var entry in BrushKeys)
                {
                    if (Contains(resources, entry.Item1))
                    {
                        continue;
                    }

                    var inherited = FirstBrush(resources, entry.Item2);
                    resources[entry.Item1] = inherited ?? Parse(entry.Item3);
                }

                foreach (var entry in DoubleKeys)
                {
                    if (!Contains(resources, entry.Item1))
                    {
                        resources[entry.Item1] = entry.Item2;
                    }
                }

                if (!Contains(resources, "SaveVaultCornerRadius"))
                {
                    resources["SaveVaultCornerRadius"] = InheritCornerRadius(resources, 4);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Save Vault: could not publish default resources");
            }
        }

        /// <summary>Every key a theme may override, for documentation and the settings page.</summary>
        public static IEnumerable<string> PublishedKeys()
        {
            foreach (var entry in BrushKeys)
            {
                yield return entry.Item1;
            }

            foreach (var entry in DoubleKeys)
            {
                yield return entry.Item1;
            }

            yield return "SaveVaultCornerRadius";
        }

        private static bool Contains(ResourceDictionary resources, string key)
        {
            try
            {
                return resources.Contains(key) || resources[key] != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Brush FirstBrush(ResourceDictionary resources, string[] keys)
        {
            foreach (var key in keys)
            {
                try
                {
                    var brush = resources[key] as Brush;
                    if (brush != null)
                    {
                        return brush;
                    }
                }
                catch (Exception)
                {
                    // A theme can throw while resolving a broken resource; try the next one.
                }
            }

            return null;
        }

        private static CornerRadius InheritCornerRadius(ResourceDictionary resources, double fallback)
        {
            try
            {
                var value = resources["ControlCornerRadius"];
                if (value is CornerRadius)
                {
                    return (CornerRadius)value;
                }

                if (value is double)
                {
                    return new CornerRadius((double)value);
                }
            }
            catch (Exception)
            {
                // Theme does not define it; fall through.
            }

            return new CornerRadius(fallback);
        }

        private static Brush Parse(string hex)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }
            catch (Exception)
            {
                return Brushes.Transparent;
            }
        }
    }
}
