using System.Text.Json;

namespace ScriptSuite.Services;

/// <summary>Log-routed history detail (windows-scripts HistoryWindow.BuildDetail
/// counterpart). Reads the individual script's own report/log files — never
/// the history DB — using the script's live config with shipped fallbacks:
///   DownloadsCleanup|EmptyFolderCleanup|ScreenshotsCleanup: shared LogDir/LogFile
///   SoftwareInventory: OutputFile
///   SystemHealthReport: OutputDir/OutputFile (HTML preview)
///   ClearJournal: BackupDir directory listing (journal-*.export snapshots)
///   TempCleanup|EmptyTrash: no persistent log — summary + outcome detail.
/// Linux deviations: XDG log homes ($HOME/Documents/Script_Logs), $HOME/~
/// expansion instead of %VAR%, generic file listing instead of .evtx.
/// Home dir + config-path lookup are injected so tests run on scratch dirs.</summary>
public sealed class LogDetailResolver
{
    private readonly Func<string, string> _configPathFor;
    private readonly string _homeDir;

    public LogDetailResolver(Func<string, string> configPathFor, string homeDir)
    {
        _configPathFor = configPathFor;
        _homeDir = homeDir;
    }

    public static LogDetailResolver Production() =>
        new(AppPaths.ConfigPathFor,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public (string? LogPath, string? LogDir, string Detail, string Hint) BuildDetail(
        string scriptId, string? summary, string outcome)
    {
        var cfg = ReadConfig(scriptId);
        string? Get(string key, string? fallback) =>
            cfg.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? Expand(v) : fallback is null ? null : Expand(fallback);

        return scriptId switch
        {
            "DownloadsCleanup" or "EmptyFolderCleanup" or "ScreenshotsCleanup" =>
                FileTailDetail(
                    Get("LogDir", "$HOME/Documents/Script_Logs"),
                    Get("LogFile", "CleanupLog.txt"),
                    scriptId, sharedNote: true),
            "SoftwareInventory" =>
                FileTailDetail(
                    dirOf(Get("OutputFile", "$HOME/Documents/Script_Logs/Software_Inventory.txt")),
                    nameOf(Get("OutputFile", "$HOME/Documents/Script_Logs/Software_Inventory.txt")),
                    scriptId, sharedNote: false),
            "SystemHealthReport" =>
                HtmlReportDetail(
                    Get("OutputDir", "$HOME/Documents"),
                    Get("OutputFile", "System_Health_Report.html"),
                    scriptId),
            "ClearJournal" =>
                BackupDirDetail(
                    Get("BackupDir", "$HOME/Documents/Script_Logs/JournalBackups"),
                    scriptId, summary, outcome),
            _ => (null, null,
                $"No persistent log file — this script writes only to console.\n"
                + $"Summary for this run: {summary ?? outcome}\nOutcome: {outcome}\n\n"
                + "If you need more detail, re-run the script and watch the live output.",
                "No persistent log file — detail is the summary + outcome"),
        };
    }

    private static (string? LogPath, string? LogDir, string Detail, string Hint) FileTailDetail(
        string? dir, string? name, string scriptId, bool sharedNote)
    {
        string path = dir is null ? (name ?? "") : Path.Combine(dir, name ?? "");
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var lines = File.ReadAllLines(path);
            int take = Math.Min(80, lines.Length);
            string tail = string.Join("\n", lines.Skip(lines.Length - take));
            string note = sharedNote ? "Shared log — entries from several scripts may be interleaved.\n\n" : "";
            return (path, dir,
                $"{note}Last {take} of {lines.Length} lines from {Path.GetFileName(path)}:\n\n{tail}",
                $"Log file: {path}");
        }
        return (null, dir,
            $"No log file yet at {path}. Run '{scriptId}' to generate it.",
            $"Log file (not yet created): {path}");
    }

    private static (string? LogPath, string? LogDir, string Detail, string Hint) HtmlReportDetail(
        string? dir, string? name, string scriptId)
    {
        string path = dir is null ? (name ?? "") : Path.Combine(dir, name ?? "");
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var lines = File.ReadAllLines(path);
            int take = Math.Min(40, lines.Length);
            string head = string.Join("\n", lines.Take(take));
            string more = lines.Length > take ? $"\n… (truncated, {lines.Length} lines total)" : "";
            return (path, dir,
                $"HTML report at {path} — open the file in a browser (showing first {take} lines):\n\n{head}{more}",
                $"Report file: {path}");
        }
        return (null, dir,
            $"No report yet at {path}. Run '{scriptId}' to generate it.",
            $"Report file (not yet created): {path}");
    }

    private static (string? LogPath, string? LogDir, string Detail, string Hint) BackupDirDetail(
        string? dir, string scriptId, string? summary, string outcome)
    {
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            var files = new DirectoryInfo(dir).GetFiles("journal-*.export")
                .OrderByDescending(f => f.LastWriteTime).ToList();
            var top = files.Take(10)
                .Select(f => $"{f.Name} {f.Length / 1024} KB {f.LastWriteTime:yyyy-MM-dd HH:mm}");
            string more = files.Count > 10 ? $"\n… and {files.Count - 10} more" : "";
            return (null, dir,
                $"Export snapshots in {dir}: {files.Count} file(s).\n"
                + string.Join("\n", top) + more
                + $"\n\nSummary for this run: {summary ?? outcome}",
                $"Backup folder: {dir}");
        }
        return (null, dir,
            $"Backup folder not yet created: {dir}. Run '{scriptId}' to generate it.",
            $"Backup folder (not yet created): {dir}");
    }

    private Dictionary<string, string> ReadConfig(string scriptId)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = _configPathFor(scriptId);
            if (!File.Exists(path)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString() ?? "";
        }
        catch (JsonException) { }
        catch (IOException) { }
        return map;
    }

    /// <summary>Expands ~, $HOME and ${HOME} prefixes. Scripts use the same
    /// convention (common.sh expand_user_path); anything else passes through.</summary>
    public string Expand(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
            return Path.Combine(_homeDir, path.Length > 1 ? path[2..] : "");
        if (path.StartsWith("$HOME/", StringComparison.Ordinal))
            return Path.Combine(_homeDir, path["$HOME/".Length..]);
        if (path.StartsWith("${HOME}/", StringComparison.Ordinal))
            return Path.Combine(_homeDir, path["${HOME}/".Length..]);
        if (path == "$HOME" || path == "${HOME}")
            return _homeDir;
        return path;
    }

    private static string? dirOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return Path.GetDirectoryName(path); } catch { return null; }
    }

    private static string? nameOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return Path.GetFileName(path); } catch { return null; }
    }
}
