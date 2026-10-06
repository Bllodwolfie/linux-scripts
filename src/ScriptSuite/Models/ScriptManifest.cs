using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptSuite.Models;

/// <summary>
/// Static description of one script: what it is, whether it needs admin, and
/// the shape of its settings fields. Mirrors the JSON manifests shipped under
/// Manifests/ and is the source of truth for how the app presents a script.
/// Ported verbatim from windows-scripts (WPF) — no toolkit dependency.
/// </summary>
public sealed class ScriptManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("requiresAdmin")] public bool RequiresAdmin { get; set; }
    [JsonPropertyName("supportsDryRun")] public bool SupportsDryRun { get; set; }
    [JsonPropertyName("scriptPath")] public string ScriptPath { get; set; } = "";
    [JsonPropertyName("fields")] public List<ScriptField> Fields { get; set; } = new();

    public string AbsoluteScriptPath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ScriptPath));

    [JsonIgnore] public bool IsCustom { get; set; }

    [JsonIgnore] public bool IsExternal { get; set; }
}
