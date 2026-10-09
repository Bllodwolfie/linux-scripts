using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace ScriptSuite.Services;

/// <summary>Runtime Mocha/Latte switch. Apply() swaps the token StyleInclude
/// in place (same key set in both files, so every DynamicResource binding
/// re-resolves with no restart) and pins the Fluent theme variant to match —
/// otherwise a light palette on a dark-system Fluent theme would be unreadable.
/// Persists via ThemeStore (theme.json, Dark default). Headless-safe: with no
/// Application it only records CurrentTheme and persists. Ported from
/// windows-scripts ThemeManager (WPF MergedDictionaries swap).</summary>
public static class ThemeManager
{
    public static string CurrentTheme { get; private set; } = "Dark";

    /// <summary>Loads the persisted theme without rewriting it. Call before
    /// the first window renders to avoid a flash.</summary>
    public static void Initialize()
    {
        try
        {
            Apply(new ThemeStore(AppPaths.ThemePath).Theme, save: false);
        }
        catch
        {
            Apply("Dark", save: false);
        }
    }

    public static void Apply(string theme, bool save = true)
    {
        theme = theme.Equals("Latte", StringComparison.OrdinalIgnoreCase) ? "Latte" : "Dark";
        CurrentTheme = theme;

        var app = Application.Current;
        if (app is not null)
        {
            string leaf = theme == "Latte" ? "Tokens.Latte.axaml" : "Tokens.axaml";
            foreach (var style in app.Styles)
            {
                if (style is StyleInclude include
                    && include.Source?.OriginalString.Contains("Themes/Tokens") == true)
                {
                    include.Source = new Uri($"avares://ScriptSuite/Themes/{leaf}");
                    break;
                }
            }
            app.RequestedThemeVariant = theme == "Latte" ? ThemeVariant.Light : ThemeVariant.Dark;
        }

        if (save)
        {
            try { new ThemeStore(AppPaths.ThemePath).SetTheme(theme); }
            catch { }
        }
    }
}
