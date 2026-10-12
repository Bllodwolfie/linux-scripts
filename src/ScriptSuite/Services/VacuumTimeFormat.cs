using System.Text.RegularExpressions;

namespace ScriptSuite.Services;

/// <summary>VacuumTime unit rules, mirrored EXACTLY from parse_days in
/// scripts/ClearJournal/ClearJournal.sh (same units, same month=30.44d /
/// year=365.25d factors, same bounds) so the Settings UI validates what the
/// script honors — no second rulebook. Bare numbers are days (the script
/// normalizes them with a WARN; journalctl alone would read seconds).</summary>
public static partial class VacuumTimeFormat
{
    [GeneratedRegex(@"^([0-9]+(\.[0-9]+)?)[ \t]*([A-Za-z]*)$")]
    private static partial Regex Shape();

    public static bool IsBareNumber(string? raw)
        => !string.IsNullOrEmpty(raw)
            && Regex.IsMatch(raw, @"^[0-9]+(\.[0-9]+)?$");

    /// <summary>Retention days for a raw value; false = the script falls back
    /// loudly (invalid unit, zero, negative, empty, or over 100 years).</summary>
    public static bool TryGetDays(string? raw, out double days)
    {
        days = 0;
        if (string.IsNullOrEmpty(raw)) return false;
        var m = Shape().Match(raw);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double n))
            return false;
        string unit = m.Groups[3].Value;
        // Capital M is months for journalctl; lowercase m is minutes.
        double factor = unit == "M" ? 30.44 : unit.ToLowerInvariant() switch
        {
            "" => 1.0,
            "us" or "usec" => 1.0 / 86400000000,
            "ms" or "msec" => 1.0 / 86400000,
            "s" or "sec" or "second" or "seconds" => 1.0 / 86400,
            "m" or "min" or "minute" or "minutes" => 1.0 / 1440,
            "h" or "hr" or "hour" or "hours" => 1.0 / 24,
            "d" or "day" or "days" => 1.0,
            "w" or "week" or "weeks" => 7.0,
            "month" or "months" => 30.44,
            "y" or "year" or "years" => 365.25,
            _ => double.NaN,
        };
        if (double.IsNaN(factor)) return false;
        days = n * factor;
        return days > 0 && days <= 36525;
    }
}
