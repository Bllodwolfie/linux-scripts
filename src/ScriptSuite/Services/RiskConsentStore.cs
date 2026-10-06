using System.Text.Json;

namespace ScriptSuite.Services;

/// <summary>Per-script "I understand the risks" opt-in (per-script, not global).
/// Stored separately from schedules so consent survives unschedule.
/// Ported verbatim from windows-scripts (no toolkit dependency).</summary>
public sealed class RiskConsentStore
{
    private readonly string _path;
    private Dictionary<string, bool> _map;

    public RiskConsentStore(string path)
    {
        _path = path;
        _map = Load(path);
    }

    private static Dictionary<string, bool> Load(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    public bool HasConsent(string scriptId) => _map.TryGetValue(scriptId, out var v) && v;
    public void SetConsent(string scriptId, bool consented)
    {
        _map[scriptId] = consented;
        Save();
    }
}
