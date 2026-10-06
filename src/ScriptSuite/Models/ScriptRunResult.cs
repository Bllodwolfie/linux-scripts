namespace ScriptSuite.Models;

/// <summary>Structured result of one script run, matching the outcome model of
/// windows-scripts (Success/Warning/Failed/Cancelled). Ported verbatim.</summary>
public sealed class ScriptRunResult
{
    public RunOutcome Outcome { get; set; }
    public string ScriptId { get; set; } = "";
    public List<string> Logs { get; set; } = new();
    public Dictionary<string, int> ItemCounts { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
