using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>
/// Runs the Bash maintenance scripts. Every run (dry-run or real) spawns
/// /bin/bash on the script's absolute path — no exec-bit or PATH assumption —
/// and consumes the log protocol from scripts/lib/common.sh:
///   INFO:/WARN:/ERROR: log lines, DRYRUN: {json item} preview rows.
/// includeOnly narrows a real run to the listed Target values (preview
/// deselection), passed via a temp file under $XDG_RUNTIME_DIR.
/// Outcome precedence mirrors windows-scripts ScriptExecutor:
///   Failed  - nonzero exit or any ERROR: line
///   Warning - completed but left something behind (any WARN: line)
///   Success - clean completion.
/// (Linux counterpart of ScriptExecutor.cs; the PowerShell-SDK in-process path
/// and the runas elevated path have no equivalent here. Elevation arrives in
/// Stage 4 via pkexec + a JSON result-file round-trip.)
/// </summary>
public sealed class BashScriptExecutor
{
    private readonly ManifestCatalog _catalog;

    public BashScriptExecutor(ManifestCatalog catalog) => _catalog = catalog;

    /// <summary>Runs a script's dry-run and returns the structured preview
    /// items plus the run's log output. Never changes anything.</summary>
    public (List<DryRunItem> Items, ScriptRunResult Result) GetDryRunItems(string scriptId, string configPath)
    {
        return Invoke(scriptId, configPath, dryRun: true, includeOnly: null, onLogLine: null);
    }

    /// <summary>Runs a script (dry-run or real). includeOnly restricts the run
    /// to the listed Target values; onLogLine receives each output line live.
    /// Runs on the calling thread; UI callers must dispatch to a worker.</summary>
    public ScriptRunResult Execute(string scriptId, string configPath, bool dryRun,
        IReadOnlyList<string>? includeOnly = null, Action<string>? onLogLine = null)
    {
        return Invoke(scriptId, configPath, dryRun, includeOnly, onLogLine).Result;
    }

    private (List<DryRunItem> Items, ScriptRunResult Result) Invoke(
        string scriptId, string configPath, bool dryRun,
        IReadOnlyList<string>? includeOnly, Action<string>? onLogLine)
    {
        var manifest = _catalog.Find(scriptId)
            ?? throw new InvalidOperationException($"Manifest not found: {scriptId}");
        string scriptPath = manifest.AbsoluteScriptPath;
        if (!File.Exists(scriptPath))
            throw new InvalidOperationException($"Script file not found: {scriptPath}");

        // Confirmed Target list for the run (preview deselection). Newline-
        // separated file; the scripts canonicalize each entry before matching.
        string? includePath = null;
        if (includeOnly is { Count: > 0 })
        {
            includePath = Path.Combine(AppPaths.RuntimeRoot,
                "scriptsuite_" + Guid.NewGuid().ToString("N") + ".include");
            Directory.CreateDirectory(Path.GetDirectoryName(includePath)!);
            File.WriteAllLines(includePath, includeOnly);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = false, // scripts speak only on stdout; inherit stderr
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        if (!string.IsNullOrEmpty(configPath))
        {
            psi.ArgumentList.Add("--config-path");
            psi.ArgumentList.Add(configPath);
        }
        if (dryRun)
            psi.ArgumentList.Add("--dry-run");
        if (includePath is not null)
        {
            psi.ArgumentList.Add("--include-only-file");
            psi.ArgumentList.Add(includePath);
        }

        var logs = new List<string>();
        var items = new List<DryRunItem>();
        bool sawError = false, sawWarn = false;
        void AddLog(string line)
        {
            logs.Add(line);
            onLogLine?.Invoke(line);
        }

        int exitCode;
        try
        {
            using var child = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            string? line;
            while ((line = child.StandardOutput.ReadLine()) is not null)
            {
                if (line.StartsWith("DRYRUN:", StringComparison.Ordinal))
                {
                    var item = ParseDryRunLine(line["DRYRUN:".Length..].Trim());
                    if (item is not null)
                    {
                        items.Add(item);
                        AddLog("OUTPUT: Action=" + item.Action + "; Target=" + item.Target + "; Detail=" + item.Detail);
                    }
                    else
                    {
                        sawError = true;
                        AddLog("ERROR: malformed DRYRUN line: " + line);
                    }
                }
                else if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                {
                    sawError = true;
                    AddLog(line);
                }
                else if (line.StartsWith("WARN:", StringComparison.Ordinal))
                {
                    sawWarn = true;
                    AddLog(line);
                }
                else if (line.StartsWith("INFO:", StringComparison.Ordinal))
                {
                    AddLog(line);
                }
                else
                {
                    AddLog("OUTPUT: " + line);
                }
            }
            child.WaitForExit();
            exitCode = child.ExitCode;
        }
        finally
        {
            if (includePath is not null)
                TryDelete(includePath);
        }

        if (exitCode != 0)
        {
            sawError = true;
            AddLog($"ERROR: script exited with code {exitCode}.");
        }

        RunOutcome outcome = sawError ? RunOutcome.Failed
            : sawWarn ? RunOutcome.Warning
            : RunOutcome.Success;

        var result = new ScriptRunResult
        {
            Outcome = outcome,
            ScriptId = scriptId,
            Logs = logs,
        };
        ParseTempCleanupCounts(logs, result.ItemCounts);
        return (items, result);
    }

    private static DryRunItem? ParseDryRunLine(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new DryRunItem
            {
                Action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
                Target = root.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "",
                Detail = root.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "",
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ParseTempCleanupCounts(List<string> logs, Dictionary<string, int> counts)
    {
        foreach (var l in logs)
        {
            var m = Regex.Match(l, @"Temp cleanup:\s*(\d+)\s+deleted,\s*(\d+)\s+skipped");
            if (m.Success)
            {
                counts["Deleted"] = int.Parse(m.Groups[1].Value);
                counts["Skipped"] = int.Parse(m.Groups[2].Value);
                return;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
