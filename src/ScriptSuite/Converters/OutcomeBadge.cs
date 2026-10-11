namespace ScriptSuite.Converters;

/// <summary>Outcome text → badge class predicates. Badge visuals live in
/// App.axaml styles keyed off style Classes (badge-success/-warning/
/// -error/-info) with DynamicResource setters, so fills follow the theme
/// toggle live. This replaces the old value converters, which resolved one
/// shared brush instance per binding evaluation and froze badge colors
/// until the window was reopened.</summary>
public static class OutcomeBadge
{
    public static bool IsSuccess(string? outcome) =>
        string.Equals(outcome?.Trim(), "Success", StringComparison.OrdinalIgnoreCase);

    public static bool IsWarning(string? outcome) =>
        string.Equals(outcome?.Trim(), "Warning", StringComparison.OrdinalIgnoreCase)
        || (outcome?.Contains("warning", StringComparison.OrdinalIgnoreCase) == true);

    public static bool IsError(string? outcome) =>
        string.Equals(outcome?.Trim(), "Failed", StringComparison.OrdinalIgnoreCase);

    public static bool IsInfo(string? outcome) =>
        string.Equals(outcome?.Trim(), "Cancelled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome?.Trim(), "SkippedBusy", StringComparison.OrdinalIgnoreCase);
}
