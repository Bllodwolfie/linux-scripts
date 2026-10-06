using System.Text.Json;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>Stage 2 pipeline proof (also wired to the scaffold UI button).
/// Seeds a scratch dir under the data root — never the real /tmp — then
/// exercises dry-run, real run, include-only run, and history insert through
/// the same BashScriptExecutor + RunHistoryStore the dashboard will use.
/// Returns report lines ending in SELF-TEST PASS or SELF-TEST FAIL.</summary>
public static class Stage2SelfTest
{
    public static List<string> Run()
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + label);

        string scratch = Path.Combine(AppPaths.DataRoot, "stage2-test");
        string keepDir = Path.Combine(scratch, "keep");
        string configPath = Path.Combine(AppPaths.RuntimeRoot,
            "scriptsuite_stage2_test_config.json");
        try
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(keepDir);

            string oldFile = Path.Combine(scratch, "old.tmp");
            string newFile = Path.Combine(scratch, "new.tmp");
            string keptFile = Path.Combine(keepDir, "old2.tmp");
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(newFile, "new");
            File.WriteAllText(keptFile, "kept");
            File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-10));
            File.SetLastWriteTime(keptFile, DateTime.Now.AddDays(-10));

            var config = new
            {
                TargetFolder = scratch,
                CutoffDays = 7,
                IgnoreFolders = new[] { keepDir },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));

            var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
            var executor = new BashScriptExecutor(catalog);

            // 1. Dry-run: exactly the old file, nothing else, nothing changed.
            var (items, dry) = executor.GetDryRunItems("TempCleanup", configPath);
            Check(dry.Outcome == RunOutcome.Success, $"dry-run outcome is Success (got {dry.Outcome})");
            Check(items.Count == 1 && items[0].Target == oldFile,
                $"dry-run lists only old.tmp (got {items.Count}: {string.Join(", ", items.Select(i => Path.GetFileName(i.Target)))})");
            Check(File.Exists(oldFile), "dry-run changed nothing on disk");

            // 2. Real run: deletes old.tmp, keeps new.tmp + ignored file.
            var result = executor.Execute("TempCleanup", configPath, dryRun: false);
            Check(result.Outcome == RunOutcome.Success, $"real run outcome is Success (got {result.Outcome})");
            Check(result.ItemCounts.GetValueOrDefault("Deleted") == 1
                  && result.ItemCounts.GetValueOrDefault("Skipped") == 0,
                $"counts are 1 deleted / 0 skipped (got {result.ItemCounts.GetValueOrDefault("Deleted")}/{result.ItemCounts.GetValueOrDefault("Skipped")})");
            Check(!File.Exists(oldFile) && File.Exists(newFile) && File.Exists(keptFile),
                "real run deleted only old.tmp");

            // 3. Include-only: re-seed two old files, confirm just one.
            string old3 = Path.Combine(scratch, "old3.tmp");
            File.WriteAllText(oldFile, "old-again");
            File.WriteAllText(old3, "old3");
            File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-10));
            File.SetLastWriteTime(old3, DateTime.Now.AddDays(-10));
            var only = executor.Execute("TempCleanup", configPath, dryRun: false,
                includeOnly: new[] { old3 });
            Check(!File.Exists(old3) && File.Exists(oldFile),
                "include-only run touched just old3.tmp");
            Check(only.Outcome == RunOutcome.Success, $"include-only outcome is Success (got {only.Outcome})");

            // 4. History round-trip with the shared summary builder.
            var history = new RunHistoryStore(AppPaths.HistoryDbPath);
            var started = DateTime.Now.AddSeconds(-5);
            history.Insert("TempCleanup", started, DateTime.Now, result.Outcome,
                RunHistoryStore.BuildSummary(result.Logs));
            var latest = history.GetLatestByScript();
            Check(latest.TryGetValue("TempCleanup", out var row) && row.Outcome == "Success",
                $"history latest TempCleanup row is Success (summary: {row?.Summary})");

            lines.Add(lines.Any(l => l.StartsWith("FAIL")) ? "SELF-TEST FAIL" : "SELF-TEST PASS");
        }
        catch (Exception ex)
        {
            lines.Add("FAIL exception: " + ex.Message);
            lines.Add("SELF-TEST FAIL");
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { }
            try { if (File.Exists(configPath)) File.Delete(configPath); } catch { }
        }
        return lines;
    }
}
