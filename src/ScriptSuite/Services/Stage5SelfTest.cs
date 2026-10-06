using System.Text.Json;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>Stage 5 proofs for DownloadsCleanup, ScreenshotsCleanup and
/// EmptyFolderCleanup. All fixtures live under $HOME (same filesystem as the
/// Trash — gio cannot trash across filesystems, proven in Stage 5 probing).
/// Trashed test fixtures use unique stage5- names and are swept from the real
/// Trash in the finally block. Returns report lines ending in SELF-TEST PASS
/// or SELF-TEST FAIL.</summary>
public static class Stage5SelfTest
{
    private const string Prefix = "stage5-";

    public static List<string> Run()
    {
        var lines = new List<string>();

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)!;
        string trashFiles = TrashDir(home, "files");
        string trashInfo = TrashDir(home, "info");
        string cfgDir = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage5_cfg_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(cfgDir);
            var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
            var executor = new BashScriptExecutor(catalog);

            lines.AddRange(Downloads(executor, home, cfgDir, trashInfo));
            lines.AddRange(Screenshots(executor, home, cfgDir));
            lines.AddRange(EmptyFolders(executor, home, cfgDir));
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
                SweepTrashPrefix(trashFiles, trashInfo, Prefix);
                foreach (var d in new[] { "stage5-dest", "stage5-sc", "stage5-ef" })
                {
                    string p = Path.Combine(home, d);
                    if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                }
                string dlSrc = Path.Combine(home, "Downloads", "stage5-dl");
                if (Directory.Exists(dlSrc)) Directory.Delete(dlSrc, recursive: true);
                string scParent = Path.Combine(home, "Pictures", "Screenshots", "stage5-sc");
                if (Directory.Exists(scParent)) Directory.Delete(scParent, recursive: true);
                string shotRoot = Path.Combine(home, "Pictures", "Screenshots");
                if (Directory.Exists(shotRoot) && !Directory.GetFileSystemEntries(shotRoot).Any())
                    Directory.Delete(shotRoot);
                if (Directory.Exists(cfgDir)) Directory.Delete(cfgDir, recursive: true);
            }
            catch { }
        }
        return lines;
    }

    // ---------------------------------------------------------- Downloads

    private static List<string> Downloads(BashScriptExecutor executor, string home, string cfgDir, string trashInfo)
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + "[dl] " + label);

        string src = Path.Combine(home, "Downloads", "stage5-dl");
        string destRoot = Path.Combine(home, "stage5-dest");
        if (Directory.Exists(src)) Directory.Delete(src, recursive: true);
        if (Directory.Exists(destRoot)) Directory.Delete(destRoot, recursive: true);
        Directory.CreateDirectory(src);
        DateTime old = DateTime.Now.AddDays(-10);
        Seed(Path.Combine(src, Prefix + "setup-old.exe"), old);
        Seed(Path.Combine(src, Prefix + "notes-old.txt"), old);
        Seed(Path.Combine(src, Prefix + "mystery-old.xyz"), old);
        Seed(Path.Combine(src, Prefix + "keep-old.tmp"), old);
        Seed(Path.Combine(src, Prefix + "arch-old.zip"), old);
        File.WriteAllText(Path.Combine(src, Prefix + "fresh-new.exe"), "new");

        string cfg = WriteCfg(cfgDir, "dl.json", new
        {
            SourceDir = src,
            CutoffDays = 7,
            LogDir = Path.Combine(cfgDir, "logs"),
            LogFile = "CleanupLog.txt",
            DeleteExts = new[] { ".exe", ".zip" },
            IgnorePatterns = new[] { "*.tmp" },
            AdvancedRules = new Dictionary<string, object>
            {
                [".zip"] = new { Action = "MoveTo", Destination = Path.Combine(destRoot, "custom") },
            },
            Categories = new Dictionary<string, object>
            {
                [Path.Combine(destRoot, "text")] = new[] { ".txt" },
            },
        });

        var (items, dry) = executor.GetDryRunItems("DownloadsCleanup", cfg);
        var byFile = items.ToDictionary(i => Path.GetFileName(i.Target));
        Check(dry.Outcome == RunOutcome.Success, $"dry-run Success (got {dry.Outcome})");
        Check(byFile.TryGetValue(Prefix + "setup-old.exe", out var e1) && e1.Action == "Delete"
                && byFile.TryGetValue(Prefix + "notes-old.txt", out var e2) && e2.Action == "Move"
                && byFile.TryGetValue(Prefix + "mystery-old.xyz", out var e3) && e3.Action == "Skip"
                && byFile.TryGetValue(Prefix + "keep-old.tmp", out var e4) && e4.Action == "Skip" && e4.Detail.Contains("ignore pattern")
                && byFile.TryGetValue(Prefix + "arch-old.zip", out var e5) && e5.Action == "Move" && e5.Detail.Contains("advanced rule")
                && !byFile.ContainsKey(Prefix + "fresh-new.exe"),
            $"dry-run actions exact (Delete/Move/Skip/Skip-pattern/Move-rule, young absent; got {items.Count})");

        var real = executor.Execute("DownloadsCleanup", cfg, dryRun: false);
        Check(real.Outcome == RunOutcome.Success, $"real run Success (got {real.Outcome})");
        Check(real.ItemCounts.GetValueOrDefault("Deleted") == 1 && real.ItemCounts.GetValueOrDefault("Skipped") == 0,
            $"counts 1 deleted / 0 skipped (got {real.ItemCounts.GetValueOrDefault("Deleted")}/{real.ItemCounts.GetValueOrDefault("Skipped")})");
        Check(!File.Exists(Path.Combine(src, Prefix + "setup-old.exe"))
                && File.Exists(Path.Combine(destRoot, "text", Prefix + "notes-old.txt"))
                && File.Exists(Path.Combine(destRoot, "custom", Prefix + "arch-old.zip"))
                && File.Exists(Path.Combine(src, Prefix + "mystery-old.xyz"))
                && File.Exists(Path.Combine(src, Prefix + "keep-old.tmp"))
                && File.Exists(Path.Combine(src, Prefix + "fresh-new.exe")),
            "real run: trashed setup, moved notes+arch, kept the rest");
        Check(TrashContains(trashInfo, Prefix + "setup-old.exe"), "trashed file landed in the real Trash via gio");
        string log = File.ReadAllText(Path.Combine(cfgDir, "logs", "CleanupLog.txt"));
        Check(log.Contains("Cleanup started") && log.Contains("MOVED") && log.Contains("DELETED") && log.Contains("Cleanup finished"),
            "shared log has started/MOVED/DELETED/finished lines");

        string a = Path.Combine(src, Prefix + "inc-a.exe"), b = Path.Combine(src, Prefix + "inc-b.exe");
        File.WriteAllText(a, "a"); File.WriteAllText(b, "b");
        File.SetLastWriteTime(a, old); File.SetLastWriteTime(b, old);
        var only = executor.Execute("DownloadsCleanup", cfg, dryRun: false, includeOnly: new[] { a });
        Check(!File.Exists(a) && File.Exists(b), "include-only trashed just the confirmed file");

        var (missItems, missDry) = executor.GetDryRunItems("DownloadsCleanup", Path.Combine(cfgDir, "nope.json"));
        Check(missDry.Outcome == RunOutcome.Success, $"missing config falls back (got {missDry.Outcome}, {missItems.Count} items from real Downloads)");
        File.WriteAllText(Path.Combine(cfgDir, "corrupt.json"), "{oops");
        var (_, corruptDry) = executor.GetDryRunItems("DownloadsCleanup", Path.Combine(cfgDir, "corrupt.json"));
        Check(corruptDry.Outcome == RunOutcome.Warning, $"corrupt config warns (got {corruptDry.Outcome})");

        var history = new RunHistoryStore(AppPaths.HistoryDbPath);
        history.Insert("DownloadsCleanup", DateTime.Now.AddSeconds(-5), DateTime.Now,
            real.Outcome, RunHistoryStore.BuildSummary(real.Logs));
        Check(history.GetLatestByScript().TryGetValue("DownloadsCleanup", out var row) && row.Outcome == "Success",
            "history latest DownloadsCleanup row is Success");
        return lines;
    }

    // -------------------------------------------------------- Screenshots

    private static List<string> Screenshots(BashScriptExecutor executor, string home, string cfgDir)
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + "[sc] " + label);

        // Fixtures live under the real Screenshots root (in-scope, so no
        // scope warning) with unique stage5- names; the parent is removed
        // afterwards only if left empty (see outer finally).
        string src = Path.Combine(home, "Pictures", "Screenshots", "stage5-sc");
        if (Directory.Exists(src)) Directory.Delete(src, recursive: true);
        Directory.CreateDirectory(src);
        DateTime old = DateTime.Now.AddDays(-10);
        Seed(Path.Combine(src, Prefix + "shot-old.png"), old);
        Seed(Path.Combine(src, Prefix + "keep-shot.png"), old);
        File.WriteAllText(Path.Combine(src, Prefix + "shot-new.png"), "new");

        string cfg = WriteCfg(cfgDir, "sc.json", new
        {
            TargetFolder = src,
            CutoffDays = 7,
            LogDir = Path.Combine(cfgDir, "logs-sc"),
            LogFile = "CleanupLog.txt",
            IgnorePatterns = new[] { "*keep-*" },
        });

        var (items, dry) = executor.GetDryRunItems("ScreenshotsCleanup", cfg);
        var byFile = items.ToDictionary(i => Path.GetFileName(i.Target));
        Check(dry.Outcome == RunOutcome.Success, $"dry-run Success (got {dry.Outcome})");
        Check(byFile.TryGetValue(Prefix + "shot-old.png", out var e1) && e1.Action == "Delete"
                && byFile.TryGetValue(Prefix + "keep-shot.png", out var e2) && e2.Action == "Skip"
                && !byFile.ContainsKey(Prefix + "shot-new.png"),
            "dry-run actions exact (Delete/Skip-pattern/young absent)");

        var real = executor.Execute("ScreenshotsCleanup", cfg, dryRun: false);
        Check(real.Outcome == RunOutcome.Success
                && real.ItemCounts.GetValueOrDefault("Removed") == 1
                && !File.Exists(Path.Combine(src, Prefix + "shot-old.png"))
                && File.Exists(Path.Combine(src, Prefix + "keep-shot.png"))
                && File.Exists(Path.Combine(src, Prefix + "shot-new.png")),
            $"real run removed only old shot (counts {real.ItemCounts.GetValueOrDefault("Removed")} removed)");

        string missCfg = WriteCfg(cfgDir, "sc-miss.json", new
        {
            TargetFolder = Path.Combine(home, "Pictures", "Screenshots", "stage5-nope"),
            CutoffDays = 7,
            LogDir = Path.Combine(cfgDir, "logs-sc"),
            LogFile = "CleanupLog.txt",
        });
        var missReal = executor.Execute("ScreenshotsCleanup", missCfg, dryRun: false);
        string missLog = File.ReadAllText(Path.Combine(cfgDir, "logs-sc", "CleanupLog.txt"));
        Check(missReal.Outcome == RunOutcome.Success && missLog.Contains("Screenshots folder not found"),
            "missing folder logs SKIPPED + finishes cleanly");

        var (missItems, missDry) = executor.GetDryRunItems("ScreenshotsCleanup", Path.Combine(cfgDir, "nope.json"));
        Check(missDry.Outcome == RunOutcome.Success, $"missing config falls back (got {missDry.Outcome})");
        File.WriteAllText(Path.Combine(cfgDir, "corrupt-sc.json"), "{oops");
        var (_, corruptDry) = executor.GetDryRunItems("ScreenshotsCleanup", Path.Combine(cfgDir, "corrupt-sc.json"));
        Check(corruptDry.Outcome == RunOutcome.Warning, $"corrupt config warns (got {corruptDry.Outcome})");

        var history = new RunHistoryStore(AppPaths.HistoryDbPath);
        history.Insert("ScreenshotsCleanup", DateTime.Now.AddSeconds(-5), DateTime.Now,
            real.Outcome, RunHistoryStore.BuildSummary(real.Logs));
        Check(history.GetLatestByScript().TryGetValue("ScreenshotsCleanup", out var row) && row.Outcome == "Success",
            "history latest ScreenshotsCleanup row is Success");
        return lines;
    }

    // -------------------------------------------------------- EmptyFolders

    private static List<string> EmptyFolders(BashScriptExecutor executor, string home, string cfgDir)
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + "[ef] " + label);

        string root = Path.Combine(home, "stage5-ef");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(Path.Combine(root, "a", "b", "c"));
        Directory.CreateDirectory(Path.Combine(root, "full"));
        Directory.CreateDirectory(Path.Combine(root, "keep", "empty"));
        File.WriteAllText(Path.Combine(root, "full", "file.txt"), "x");
        File.WriteAllText(Path.Combine(root, "rootfile.txt"), "x");

        string cfg = WriteCfg(cfgDir, "ef.json", new
        {
            TargetFolder = root,
            IgnoreFolders = new[] { Path.Combine(root, "keep") },
            LogDir = Path.Combine(cfgDir, "logs-ef"),
            LogFile = "CleanupLog.txt",
        });

        var (items, dry) = executor.GetDryRunItems("EmptyFolderCleanup", cfg);
        var got = items.Where(i => i.Action == "Delete").Select(i => i.Target).ToHashSet();
        var want = new HashSet<string>
        {
            Path.Combine(root, "a", "b", "c"),
            Path.Combine(root, "a", "b"),
            Path.Combine(root, "a"),
        };
        Check(dry.Outcome == RunOutcome.Success && got.SetEquals(want),
            $"dry-run simulation lists c+b+a, excludes ignored (got {got.Count})");

        var real = executor.Execute("EmptyFolderCleanup", cfg, dryRun: false);
        Check(real.Outcome == RunOutcome.Success
                && !Directory.Exists(Path.Combine(root, "a"))
                && Directory.Exists(Path.Combine(root, "full"))
                && Directory.Exists(Path.Combine(root, "keep", "empty"))
                && File.Exists(Path.Combine(root, "rootfile.txt"))
                && Directory.Exists(root),
            "real run removed the empty chain only (root kept)");

        Directory.CreateDirectory(Path.Combine(root, "x"));
        Directory.CreateDirectory(Path.Combine(root, "y"));
        var only = executor.Execute("EmptyFolderCleanup", cfg, dryRun: false,
            includeOnly: new[] { Path.Combine(root, "x") });
        Check(!Directory.Exists(Path.Combine(root, "x")) && Directory.Exists(Path.Combine(root, "y")),
            "include-only removed just the confirmed folder");

        string missCfg = WriteCfg(cfgDir, "ef-miss.json", new
        {
            TargetFolder = Path.Combine(home, "stage5-nope"),
            IgnoreFolders = new string[0],
            LogDir = Path.Combine(cfgDir, "logs-ef"),
            LogFile = "CleanupLog.txt",
        });
        var missReal = executor.Execute("EmptyFolderCleanup", missCfg, dryRun: false);
        Check(missReal.Outcome == RunOutcome.Success && missReal.Logs.Count == 0,
            "missing target exits silently with no output");

        var (missItems, missDry) = executor.GetDryRunItems("EmptyFolderCleanup", Path.Combine(cfgDir, "nope.json"));
        Check(missDry.Outcome == RunOutcome.Success, $"missing config falls back (got {missDry.Outcome})");
        File.WriteAllText(Path.Combine(cfgDir, "corrupt-ef.json"), "{oops");
        var (_, corruptDry) = executor.GetDryRunItems("EmptyFolderCleanup", Path.Combine(cfgDir, "corrupt-ef.json"));
        Check(corruptDry.Outcome == RunOutcome.Warning, $"corrupt config warns (got {corruptDry.Outcome})");

        var history = new RunHistoryStore(AppPaths.HistoryDbPath);
        history.Insert("EmptyFolderCleanup", DateTime.Now.AddSeconds(-5), DateTime.Now,
            real.Outcome, RunHistoryStore.BuildSummary(real.Logs));
        Check(history.GetLatestByScript().TryGetValue("EmptyFolderCleanup", out var row) && row.Outcome == "Success",
            "history latest EmptyFolderCleanup row is Success");
        return lines;
    }

    // ------------------------------------------------------------ helpers

    private static void Seed(string path, DateTime mtime)
    {
        File.WriteAllText(path, "stage5 fixture");
        File.SetLastWriteTime(path, mtime);
    }

    private static string WriteCfg(string dir, string name, object config)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string TrashDir(string home, string leaf)
    {
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(home, ".local", "share");
        return Path.Combine(dataHome, "Trash", leaf);
    }

    private static bool TrashContains(string infoDir, string fileName)
    {
        if (!Directory.Exists(infoDir)) return false;
        foreach (var info in Directory.GetFiles(infoDir, "*.trashinfo"))
        {
            foreach (var line in File.ReadLines(info))
            {
                if (line.StartsWith("Path=", StringComparison.Ordinal)
                    && Uri.UnescapeDataString(line["Path=".Length..].Replace('+', ' ')).EndsWith(fileName))
                    return true;
            }
        }
        return false;
    }

    private static void SweepTrashPrefix(string filesDir, string infoDir, string prefix)
    {
        if (Directory.Exists(infoDir))
            foreach (var f in Directory.GetFiles(infoDir, "*.trashinfo"))
                if (Path.GetFileName(f).Contains(prefix))
                {
                    string b = Path.GetFileNameWithoutExtension(f);
                    TryDelete(Path.Combine(filesDir, b));
                    TryDelete(f);
                }
        if (Directory.Exists(filesDir))
            foreach (var e in Directory.GetFileSystemEntries(filesDir))
                if (Path.GetFileName(e).Contains(prefix))
                    TryDeleteDir(e);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
