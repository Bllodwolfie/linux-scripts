using System.Text.Json;

namespace ScriptSuite.Services;

/// <summary>Persists per-script schedules (scriptId -> ScheduleEntry) with
/// atomic tmp+move writes. Ported from windows-scripts (path-injected like
/// RiskConsentStore so tests can isolate).</summary>
public sealed class ScheduleStore
{
    private readonly string _path;
    private Dictionary<string, Models.ScheduleEntry> _map;

    public ScheduleStore(string path)
    {
        _path = path;
        _map = Load(path);
    }

    private static Dictionary<string, Models.ScheduleEntry> Load(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Models.ScheduleEntry>>(File.ReadAllText(path))
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    public IReadOnlyDictionary<string, Models.ScheduleEntry> All => _map;
    public bool Has(string scriptId) => _map.ContainsKey(scriptId);
    public Models.ScheduleEntry? Get(string scriptId) => _map.TryGetValue(scriptId, out var e) ? e : null;

    public void Set(Models.ScheduleEntry entry)
    {
        _map[entry.ScriptId] = entry;
        Save();
    }

    public void Remove(string scriptId)
    {
        if (_map.Remove(scriptId)) Save();
    }
}
