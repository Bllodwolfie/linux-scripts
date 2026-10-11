using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace ScriptSuite.Services;

/// <summary>Runtime Mocha/Latte switch. Both token files are declared in
/// App.axaml, so their blocks are loaded once at startup through compiled
/// XAML (no runtime asset loading anywhere). Apply() keeps exactly the
/// active theme's block in Application.Styles: same key set in both files,
/// so every DynamicResource binding re-resolves with no restart. It then
/// pins the Fluent theme variant to match — otherwise a light palette on
/// a dark-system Fluent theme would be unreadable.
/// NOTE: the swap must be remove/add of the loaded blocks, NOT a
/// StyleInclude.Source rewrite: Avalonia's compiled XAML inlines
/// &lt;StyleInclude&gt; content, so no include node survives at runtime to
/// match (diagnosed headless: the app.Styles tree holds FluentTheme plus
/// bare Styles nodes carrying our 22 Brush.* keys). Matching a
/// non-existent node silently no-ops — exactly the "Latte leaves Mocha
/// surfaces" bug. Remove/add also keeps headless self-tests working,
/// where no platform asset services exist.
/// Persists via ThemeStore (theme.json, Dark default). Headless-safe: with no
/// Application it only records CurrentTheme and persists. An explicit
/// appOverride lets headless self-tests drive a detached instance.
/// Ported from windows-scripts ThemeManager (WPF MergedDictionaries swap).</summary>
public static class ThemeManager
{
    public static string CurrentTheme { get; private set; } = "Dark";

    private const string TokenMarker = "Brush.Background.Primary";

    /// <summary>Token blocks detached by earlier switches, keyed by theme.
    /// Re-added on demand so toggling never needs runtime asset loading.</summary>
    private static readonly Dictionary<string, Styles> _detached =
        new(StringComparer.OrdinalIgnoreCase);

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

    public static void Apply(string theme, bool save = true, Application? appOverride = null)
    {
        theme = theme.Equals("Latte", StringComparison.OrdinalIgnoreCase) ? "Latte" : "Dark";
        CurrentTheme = theme;

        var app = appOverride ?? Application.Current;
        if (app is not null)
        {
            // Keep exactly the active theme's token block: detach the rest
            // into the cache, then re-attach the cached active block.
            foreach (var style in app.Styles.ToList())
            {
                if (style is Styles s && OwnsTokens(s) && TokenThemeOf(s) != theme)
                {
                    app.Styles.Remove(s);
                    string? detached = TokenThemeOf(s);
                    if (detached is not null)
                        _detached[detached] = s;
                }
            }
            bool activePresent = false;
            foreach (var style in app.Styles)
            {
                if (style is Styles s && OwnsTokens(s) && TokenThemeOf(s) == theme)
                { activePresent = true; break; }
            }
            if (!activePresent && _detached.TryGetValue(theme, out var cached))
                app.Styles.Add(cached);
            app.RequestedThemeVariant = theme == "Latte" ? ThemeVariant.Light : ThemeVariant.Dark;
        }

        if (save)
        {
            try { new ThemeStore(AppPaths.ThemePath).SetTheme(theme); }
            catch { }
        }
    }

    /// <summary>True when the Styles node carries our token block
    /// (identified by the marker brush key).</summary>
    internal static bool OwnsTokens(Styles s)
    {
        foreach (var child in s)
        {
            if (child is Style st && st.Resources.ContainsKey(TokenMarker))
                return true;
        }
        return false;
    }

    /// <summary>Which theme a token block carries, read off the marker
    /// brush value. Null when the block is foreign or unrecognized.</summary>
    internal static string? TokenThemeOf(Styles s)
    {
        foreach (var child in s)
        {
            if (child is Style st
                && st.Resources.TryGetValue(TokenMarker, out var o)
                && o is SolidColorBrush b)
            {
                if (b.Color == Color.FromRgb(0x1E, 0x1E, 0x2E)) return "Dark";
                if (b.Color == Color.FromRgb(0xEF, 0xF1, 0xF5)) return "Latte";
                return null;
            }
        }
        return null;
    }
}
