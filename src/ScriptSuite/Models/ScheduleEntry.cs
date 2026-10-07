using System.Text.Json.Serialization;

namespace ScriptSuite.Models;

/// <summary>Per-script schedule. Simple recurring interval, no cron.
/// Stored in ~/.config/scriptsuite/schedules.json via ScheduleStore.
/// Ported verbatim from windows-scripts.</summary>
public sealed class ScheduleEntry
{
    [JsonPropertyName("scriptId")] public string ScriptId { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    // Unit: Days | Hours | Weeks
    [JsonPropertyName("unit")] public string Unit { get; set; } = "Days";
    [JsonPropertyName("interval")] public int Interval { get; set; } = 1;
    // HH:mm 24h
    [JsonPropertyName("timeOfDay")] public string TimeOfDay { get; set; } = "09:00";
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    [JsonPropertyName("nextRun")] public string? NextRun { get; set; }
}
