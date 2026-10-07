using System.Text.Json;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>Stage 6 proofs for SoftwareInventory and SystemHealthReport (both
/// read-only reports: no dry-run exists, so the suite asserts the
/// supportsDryRun=false contract instead, plus real runs, config fallbacks,
/// output validation, and history). Default-path runs (missing config) write
/// to the real user locations the app itself would use — created files are
/// removed afterwards iff they did not pre-exist. Returns report lines ending
/// in SELF-TEST PASS or SELF-TEST FAIL.</summary>
public static class Stage6SelfTest
{
    public static List<string> Run()
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + label);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)!;
        string cfgDir = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage6_cfg_{Guid.NewGuid():N}");
        // History assertions run against a scratch DB, never the user's real
        // history.db.
        string historyPath = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage6_hist_{Guid.NewGuid():N}.db");
        // Default-path side effects: missing/corrupt configs make the scripts
        // write the REAL user locations. Snapshot pre-existence up front; the
        // outer finally removes anything the suite created (belt and braces
        // behind the per-check cleanups, which must also stay).
        string invDefault = Path.Combine(home, "Documents", "Script_Logs", "Software_Inventory.txt");
        string hrDefault = Path.Combine(home, "Documents", "System_Health_Report.html");
        bool invPre = File.Exists(invDefault), hrPre = File.Exists(hrDefault);
        try
        {
            Directory.CreateDirectory(cfgDir);
            var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
            var executor = new BashScriptExecutor(catalog);

            Check(catalog.All.Count == 8, $"catalog holds all 8 scripts (got {catalog.All.Count})");
            Check(catalog.Find("SoftwareInventory")?.SupportsDryRun == false
                    && catalog.Find("SystemHealthReport")?.SupportsDryRun == false,
                "both reports declare supportsDryRun=false (no dry-run contract to test)");

            lines.AddRange(Inventory(executor, home, cfgDir, historyPath));
            lines.AddRange(Health(executor, home, cfgDir, historyPath));
            lines.Add("SELF-TEST " + (lines.Any(l => l.StartsWith("FAIL")) ? "FAIL" : "PASS"));
        }
        catch (Exception ex)
        {
            lines.Add("FAIL exception: " + ex.Message);
            lines.Add("SELF-TEST FAIL");
        }
        finally
        {
            try
            {
                if (!invPre) TryDelete(invDefault);
                if (!hrPre) TryDelete(hrDefault);
                if (Directory.Exists(cfgDir)) Directory.Delete(cfgDir, recursive: true);
                foreach (var p in new[] { historyPath, historyPath + "-wal", historyPath + "-shm" })
                    TryDelete(p);
            }
            catch { }
        }
        return lines;
    }

    private static List<string> Inventory(BashScriptExecutor executor, string home, string cfgDir, string historyPath)
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + "[si] " + label);

        string cfg = WriteCfg(cfgDir, "si.json", new
        {
            OutputFile = Path.Combine(cfgDir, "Software_Inventory.txt"),
        });
        var real = executor.Execute("SoftwareInventory", cfg, dryRun: false);
        string text = File.Exists(Path.Combine(cfgDir, "Software_Inventory.txt"))
            ? File.ReadAllText(Path.Combine(cfgDir, "Software_Inventory.txt")) : "";
        Check(real.Outcome == RunOutcome.Success, $"real run Success (got {real.Outcome})");
        Check(text.Contains("== dpkg (") && text.Contains("== snap (") && text.Contains("bash ")
                && real.Logs.Any(l => l.Contains("packages:")),
            "report has dpkg+snap sections, lists bash, summary counts packages");

        string defaultOut = Path.Combine(home, "Documents", "Script_Logs", "Software_Inventory.txt");
        bool preexisted = File.Exists(defaultOut);
        var missReal = executor.Execute("SoftwareInventory", Path.Combine(cfgDir, "nope.json"), dryRun: false);
        bool created = File.Exists(defaultOut) && new FileInfo(defaultOut).Length > 0;
        Check(missReal.Outcome == RunOutcome.Success && created,
            $"missing config uses real default path (got {missReal.Outcome}, file written: {created})");
        if (!preexisted) TryDelete(defaultOut);

        File.WriteAllText(Path.Combine(cfgDir, "corrupt.json"), "{oops");
        var corrupt = executor.Execute("SoftwareInventory", Path.Combine(cfgDir, "corrupt.json"), dryRun: false);
        Check(corrupt.Outcome == RunOutcome.Warning, $"corrupt config warns (got {corrupt.Outcome})");
        if (!preexisted) TryDelete(defaultOut);

        string blockCfg = WriteCfg(cfgDir, "si-block.json", new { OutputFile = "/tmp" });
        var blocked = executor.Execute("SoftwareInventory", blockCfg, dryRun: false);
        Check(blocked.Outcome == RunOutcome.Failed, $"folder-as-output refused (got {blocked.Outcome})");

        var history = new RunHistoryStore(historyPath);
        history.Insert("SoftwareInventory", DateTime.Now.AddSeconds(-5), DateTime.Now,
            real.Outcome, RunHistoryStore.BuildSummary(real.Logs));
        Check(history.GetLatestByScript().TryGetValue("SoftwareInventory", out var row) && row.Outcome == "Success",
            "history latest SoftwareInventory row is Success");
        return lines;
    }

    private static List<string> Health(BashScriptExecutor executor, string home, string cfgDir, string historyPath)
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + "[hr] " + label);

        string outDir = Path.Combine(cfgDir, "health");
        string cfg = WriteCfg(cfgDir, "hr.json", new
        {
            OutputDir = outDir,
            OutputFile = "health.html",
            MaxTopProcesses = 5,
            MaxErrorEvents = 5,
            ErrorWindowHours = 24,
            RiskGreen = 60,
            RiskYellow = 80,
        });
        var real = executor.Execute("SystemHealthReport", cfg, dryRun: false);
        string htmlPath = Path.Combine(outDir, "health.html");
        string html = File.Exists(htmlPath) ? File.ReadAllText(htmlPath) : "";
        string[] sections = { "sysinfo", "cpu", "gpu", "memory", "storage", "network", "processes", "errors", "temps", "updates" };
        Check(real.Outcome == RunOutcome.Success, $"real run Success (got {real.Outcome})");
        Check(html.Contains("</html>") && sections.All(s => html.Contains($"id=\"{s}\""))
                && html.Contains(Environment.MachineName) && html.Length > 5000,
            $"HTML complete: 10 sections, hostname, {html.Length} bytes");
        Check(real.Logs.Any(l => l.Contains("System health report written to")),
            "console summary line present for history");

        string defaultOut = Path.Combine(home, "Documents", "System_Health_Report.html");
        bool preexisted = File.Exists(defaultOut);
        var missReal = executor.Execute("SystemHealthReport", Path.Combine(cfgDir, "nope.json"), dryRun: false);
        Check(missReal.Outcome == RunOutcome.Success && File.Exists(defaultOut),
            $"missing config uses real default path (got {missReal.Outcome})");
        if (!preexisted) TryDelete(defaultOut);

        File.WriteAllText(Path.Combine(cfgDir, "corrupt-hr.json"), "{oops");
        var corrupt = executor.Execute("SystemHealthReport", Path.Combine(cfgDir, "corrupt-hr.json"), dryRun: false);
        string corruptOut = corrupt.Logs.FirstOrDefault(l => l.Contains("System health report written to")) ?? "";
        Check(corrupt.Outcome == RunOutcome.Warning && corruptOut.Contains(defaultOut),
            $"corrupt config warns + still writes default path (got {corrupt.Outcome})");
        if (!preexisted) TryDelete(defaultOut);

        string dirCfg = WriteCfg(cfgDir, "hr-dir.json", new
        {
            OutputDir = outDir,
            OutputFile = "subdir",
            MaxTopProcesses = 5,
            MaxErrorEvents = 5,
            ErrorWindowHours = 24,
            RiskGreen = 60,
            RiskYellow = 80,
        });
        Directory.CreateDirectory(Path.Combine(outDir, "subdir"));
        var blocked = executor.Execute("SystemHealthReport", dirCfg, dryRun: false);
        Check(blocked.Outcome == RunOutcome.Failed, $"output-is-folder refused (got {blocked.Outcome})");

        var history = new RunHistoryStore(historyPath);
        history.Insert("SystemHealthReport", DateTime.Now.AddSeconds(-5), DateTime.Now,
            real.Outcome, RunHistoryStore.BuildSummary(real.Logs));
        Check(history.GetLatestByScript().TryGetValue("SystemHealthReport", out var row) && row.Outcome == "Success",
            "history latest SystemHealthReport row is Success");
        return lines;
    }

    private static string WriteCfg(string dir, string name, object config)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
