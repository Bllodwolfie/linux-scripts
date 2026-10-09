using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ScriptSuite.Converters;

/// <summary>Outcome string → theme status brush (badge backgrounds).
/// Success→Success, Warning→Warning, Failed→Error, Cancelled/SkippedBusy→Info.
/// Resolves live from application resources so the current palette wins.
/// Caveat: an already-open window keeps the brush instance it bound; reopen
/// the window after a theme switch for fresh colors (rows load per open).
/// Ported from windows-scripts OutcomeToBrushConverter.</summary>
public sealed class OutcomeToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = (value?.ToString() ?? "") switch
        {
            "Success" => "Brush.Status.Success",
            "Warning" => "Brush.Status.Warning",
            "Failed" => "Brush.Status.Error",
            "Cancelled" or "SkippedBusy" => "Brush.Status.Info",
            _ => "Brush.Background.Card.Hover",
        };
        try
        {
            if (Application.Current?.TryGetResource(key, out var obj) == true && obj is IBrush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Outcome string → readable text color for use on the matching
/// status brush (Status.*.Foreground). Same staleness caveat as above.</summary>
public sealed class OutcomeToForegroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = (value?.ToString() ?? "") switch
        {
            "Success" => "Brush.Status.Success.Foreground",
            "Warning" => "Brush.Status.Warning.Foreground",
            "Failed" => "Brush.Status.Error.Foreground",
            "Cancelled" or "SkippedBusy" => "Brush.Status.Info.Foreground",
            _ => "Brush.Foreground.Primary",
        };
        try
        {
            if (Application.Current?.TryGetResource(key, out var obj) == true && obj is IBrush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
