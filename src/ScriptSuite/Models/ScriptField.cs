using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptSuite.Models;

/// <summary>
/// One configurable setting of a script, declared in its manifest.
/// Ported verbatim from windows-scripts (WPF).
/// </summary>
public sealed class ScriptField
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("default")] public JsonElement Default { get; set; }
    [JsonPropertyName("helpText")] public string? HelpText { get; set; }
    [JsonPropertyName("helpDetail")] public string? HelpDetail { get; set; }

    [JsonPropertyName("pathKind")] public string? PathKind { get; set; }

    public bool IsFilePath => string.Equals(PathKind, "file", StringComparison.OrdinalIgnoreCase);

    public bool HasDefault => Default.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
}
