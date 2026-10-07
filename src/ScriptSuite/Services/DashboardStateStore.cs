using System.Text.Json;

namespace ScriptSuite.Services;

/// <summary>Dashboard tile state: per-script Run All inclusion + hidden flag,
/// persisted immediately on every toggle to dashboard.json
/// (~/.config/scriptsuite/dashboard.json). Schema matches windows-scripts
/// exactly (PascalCase, absent entry = defaults RunAll:true Hidden:false).
/// Missing/corrupt file loads as empty — never throws. Ported verbatim
/// apart from the XDG path (injected so tests can isolate).</summary>
public sealed class DashboardStateStore
{
    private readonly string _path;
    private Dictionary<string, ScriptDashboardState> _map;

    public DashboardStateStore(string path)
    {
        _path = path;
        _map = Load(path);
    }

    private sealed class FileShape
    {
        public Dictionary<string, ScriptDashboardState>? Scripts { get; set; }
    }

    private sealed class ScriptDashboardState
    {
        public bool RunAll { get; set; } = true;
        public bool Hidden { get; set; }
    }

    private static Dictionary<string, ScriptDashboardState> Load(string path)
    {
        if (!File.Exists(path))
            return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
            return shape?.Scripts ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            new FileShape { Scripts = _map },
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    private ScriptDashboardState Entry(string scriptId)
    {
        if (!_map.TryGetValue(scriptId, out var s))
            _map[scriptId] = s = new ScriptDashboardState();
        return s;
    }

    public bool IsRunAllEnabled(string scriptId) =>
        !_map.TryGetValue(scriptId, out var s) || s.RunAll;

    public bool IsHidden(string scriptId) =>
        _map.TryGetValue(scriptId, out var s) && s.Hidden;

    public void SetRunAll(string scriptId, bool enabled)
    {
        Entry(scriptId).RunAll = enabled;
        Save();
    }

    public void SetHidden(string scriptId, bool hidden)
    {
        Entry(scriptId).Hidden = hidden;
        Save();
    }
}
