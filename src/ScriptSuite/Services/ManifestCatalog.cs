using System.Text.Json;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>Loads every script manifest and provides ordering/grouping.
/// Ported from windows-scripts with two Linux changes: RestorePoint is dropped
/// (8 scripts, no System Restore equivalent) and EmptyRecycleBin is renamed
/// EmptyTrash (XDG Trash). BatchOrder keeps the "trash first" invariant.
/// </summary>
public sealed class ManifestCatalog
{
    private static readonly string[] BuiltInOrder =
    {
        "TempCleanup", "DownloadsCleanup", "ScreenshotsCleanup", "EmptyFolderCleanup", "EmptyTrash",
        "ClearJournal",
        "SoftwareInventory", "SystemHealthReport",
    };

    private readonly List<ScriptManifest> _manifests;

    public ManifestCatalog(string manifestsDir)
    {
        _manifests = LoadAll(manifestsDir);
    }

    private static List<ScriptManifest> LoadAll(string dir)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var loaded = new List<ScriptManifest>();
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<ScriptManifest>(File.ReadAllText(file), options);
                    if (m is not null && !string.IsNullOrWhiteSpace(m.Id))
                        loaded.Add(m);
                }
                catch (JsonException)
                {
                    // Skip malformed manifests rather than failing startup.
                }
            }
        }

        return loaded
            .OrderBy(m => Array.IndexOf(BuiltInOrder, m.Id) >= 0 ? Array.IndexOf(BuiltInOrder, m.Id) : int.MaxValue)
            .ThenBy(m => m.Id)
            .ToList();
    }

    public IReadOnlyList<ScriptManifest> All => _manifests;

    public ScriptManifest? Find(string scriptId) => _manifests.FirstOrDefault(m => m.Id == scriptId);

    /// <summary>Category groups in first-appearance order.</summary>
    public List<(string Category, List<ScriptManifest> Scripts)> GroupByCategory()
    {
        var groups = new List<(string Category, List<ScriptManifest> Scripts)>();
        foreach (var m in _manifests)
        {
            var idx = groups.FindIndex(g => g.Category == m.Category);
            if (idx < 0)
                groups.Add((m.Category, new List<ScriptManifest> { m }));
            else
                groups[idx].Scripts.Add(m);
        }
        return groups;
    }

    /// <summary>Safe batch order for Run All: EmptyTrash always first
    /// when included (its permanent empty must run before any deletion that
    /// routes to the Trash via gio), then the remaining scripts in the
    /// dashboard's listed order.</summary>
    public List<ScriptManifest> BatchOrder(Func<string, bool> isIncluded)
    {
        var rest = _manifests.Where(m => isIncluded(m.Id) && m.Id != "EmptyTrash").ToList();
        var bin = _manifests.FirstOrDefault(m => m.Id == "EmptyTrash");
        if (bin is not null && isIncluded(bin.Id))
            rest.Insert(0, bin);
        return rest;
    }
}
