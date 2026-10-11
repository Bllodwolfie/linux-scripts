using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ScriptSuite;

namespace ScriptSuite.Services;

/// <summary>Stage 10 proofs: the runtime Mocha/Latte switch re-resolves
/// token brushes (Background.Primary/Card, Status.Success badge fill),
/// pins the Fluent RequestedThemeVariant, replaces (never duplicates)
/// the token block, and restores everything on switch-back.
/// Headless: drives a detached App instance (no Application.Current,
/// no window, no platform) through ThemeManager.Apply's appOverride —
/// the same remove+add path the live toggle uses. Regression guard for
/// the "Latte leaves Mocha surfaces" bug: Avalonia's compiled XAML
/// inlines StyleInclude content, so include-matching swaps silently
/// no-op. Ends with SELF-TEST PASS or SELF-TEST FAIL.</summary>
public static class Stage10SelfTest
{
    public static List<string> Run()
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + label);
        string initialTheme = ThemeManager.CurrentTheme;
        try
        {
            var app = new App();
            app.Initialize();
            // Normalize like production startup (ThemeManager.Initialize
            // before the first window): exactly the Dark block active.
            ThemeManager.Apply("Dark", save: false, appOverride: app);
            string Res(string key)
            {
                if (app.TryGetResource(key, null, out var o) && o is SolidColorBrush b)
                    return $"#{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
                return "<unresolved>";
            }
            int TokenBlocks()
            {
                int n = 0;
                foreach (var s in app.Styles)
                    if (s is Styles ss && ThemeManager.OwnsTokens(ss)) n++;
                return n;
            }

            string mochaBg = Res("Brush.Background.Primary");
            string mochaCard = Res("Brush.Background.Card");
            string mochaOk = Res("Brush.Status.Success");
            Check(mochaBg != "<unresolved>" && mochaCard != "<unresolved>" && mochaOk != "<unresolved>",
                "10 Mocha tokens resolve on a fresh instance");
            Check(TokenBlocks() == 1, "10 exactly one token block loaded");

            ThemeManager.Apply("Latte", save: false, appOverride: app);
            Check(Res("Brush.Background.Primary") != mochaBg, "10 Latte re-resolves Background.Primary");
            Check(Res("Brush.Background.Card") != mochaCard, "10 Latte re-resolves Background.Card");
            Check(Res("Brush.Status.Success") != mochaOk, "10 Latte re-resolves badge fill");
            Check(app.RequestedThemeVariant == ThemeVariant.Light, "10 Latte pins Fluent Light variant");
            Check(TokenBlocks() == 1, "10 switch replaces (no duplicate blocks)");

            ThemeManager.Apply("Dark", save: false, appOverride: app);
            Check(Res("Brush.Background.Primary") == mochaBg, "10 switch-back restores Background.Primary");
            Check(Res("Brush.Background.Card") == mochaCard, "10 switch-back restores Background.Card");
            Check(Res("Brush.Status.Success") == mochaOk, "10 switch-back restores badge fill");
            Check(app.RequestedThemeVariant == ThemeVariant.Dark, "10 Dark pins Fluent Dark variant");
            Check(TokenBlocks() == 1, "10 still exactly one token block");

            lines.Add(lines.Any(l => l.StartsWith("FAIL")) ? "SELF-TEST FAIL" : "SELF-TEST PASS");
        }
        catch (Exception ex)
        {
            lines.Add("FAIL exception: " + ex.GetType().Name + ": " + ex.Message);
            lines.Add("SELF-TEST FAIL");
        }
        finally
        {
            ThemeManager.Apply(initialTheme, save: false);
        }
        return lines;
    }
}
