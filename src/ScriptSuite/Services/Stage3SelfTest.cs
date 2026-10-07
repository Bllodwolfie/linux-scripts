using System.Diagnostics;
using System.Text.Json;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>Stage 3 pipeline proof for EmptyTrash, against the REAL home Trash
/// (not a fake): test items are trashed via `gio trash`, one DeletionDate is
/// backdated 10 days, another is removed entirely, and the `gio trash --empty`
/// fast path is proven with the user's pre-existing items moved aside and
/// restored. Every phase asserts the user's own trash is untouched.
/// Returns report lines ending in SELF-TEST PASS or SELF-TEST FAIL.</summary>
public static class Stage3SelfTest
{
    private const string Prefix = "scriptsuite-stage3-";

    public static List<string> Run()
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + label);

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        string trashDir = Path.Combine(dataHome, "Trash");
        string filesDir = Path.Combine(trashDir, "files");
        string infoDir = Path.Combine(trashDir, "info");
        string tag = Prefix + Guid.NewGuid().ToString("N")[..8] + "-";
        // History assertions run against a scratch DB, never the user's real
        // history.db.
        string historyPath = Path.Combine(AppPaths.RuntimeRoot,
            $"scriptsuite_stage3_hist_{Guid.NewGuid():N}.db");

        var initialFiles = Snapshot(filesDir);
        var initialInfo = Snapshot(infoDir);

        // Restore action in case a phase throws mid-way (user data first).
        string? backupRoot = null;
        var testPairs = new List<string>(); // info basenames created by this test
        try
        {
            if (!GioAvailable())
            {
                lines.Add("FAIL gio is not installed; cannot trash test items.");
                lines.Add("SELF-TEST FAIL");
                return lines;
            }

            var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
            var executor = new BashScriptExecutor(catalog);
            string config7 = WriteConfig(7);
            string config0 = WriteConfig(0);
            try
            {
                // --- Phase A: filtered runs (user trash stays in place) ---
                string oldSrc = WriteTempFile(tag + "old.tmp");
                string freshSrc = WriteTempFile(tag + "fresh.tmp");
                string nodateSrc = WriteTempFile(tag + "nodate.tmp");
                GioTrash(oldSrc); GioTrash(freshSrc); GioTrash(nodateSrc);
                string oldTarget = oldSrc, freshTarget = freshSrc, nodateTarget = nodateSrc;
                testPairs.AddRange(new[] { FindInfo(filesDir, infoDir, oldTarget), FindInfo(filesDir, infoDir, freshTarget), FindInfo(filesDir, infoDir, nodateTarget) });

                Backdate(infoDir, testPairs[0], daysAgo: 10);
                RemoveDeletionDate(infoDir, testPairs[2]);

                var (items, dry) = executor.GetDryRunItems("EmptyTrash", config7);
                Check(dry.Outcome == RunOutcome.Success, $"dry-run outcome is Success (got {dry.Outcome})");
                var byTarget = items.ToDictionary(i => i.Target);
                bool actionsOk = byTarget.TryGetValue(oldTarget, out var o) && o.Action == "Delete" && o.Detail.Contains("days old")
                    && byTarget.TryGetValue(freshTarget, out var f) && f.Action == "Skip" && f.Detail.Contains("younger than 7 days")
                    && byTarget.TryGetValue(nodateTarget, out var n) && n.Action == "Skip" && n.Detail.Contains("age unknown");
                Check(actionsOk, "dry-run actions: old→Delete aged, fresh→Skip young, nodate→Skip unknown");
                Check(PairPresent(filesDir, infoDir, testPairs[0]) && PairPresent(filesDir, infoDir, testPairs[1]) && PairPresent(filesDir, infoDir, testPairs[2]),
                    "dry-run changed nothing in the Trash");

                var filtered = executor.Execute("EmptyTrash", config7, dryRun: false);
                Check(filtered.Outcome == RunOutcome.Success
                        && !PairPresent(filesDir, infoDir, testPairs[0])
                        && PairPresent(filesDir, infoDir, testPairs[1])
                        && PairPresent(filesDir, infoDir, testPairs[2]),
                    "filtered run deleted only the 10-day-old pair");
                Check(Snapshot(filesDir).IsSupersetOf(initialFiles) && Snapshot(infoDir).IsSupersetOf(initialInfo),
                    "filtered run preserved every pre-existing trash item");

                var only = executor.Execute("EmptyTrash", config7, dryRun: false,
                    includeOnly: new[] { freshTarget, nodateTarget });
                Check(only.Outcome == RunOutcome.Success
                        && !PairPresent(filesDir, infoDir, testPairs[1])
                        && !PairPresent(filesDir, infoDir, testPairs[2]),
                    "include-only run deleted the 2 confirmed pairs");
                Check(SetsEqual(Snapshot(filesDir), initialFiles) && SetsEqual(Snapshot(infoDir), initialInfo),
                    "user trash identical after include-only run");
                testPairs.Clear();

                // --- Phase B: unfiltered MinAgeDays=0 full clear (user items moved
                // --- aside first). Proven via direct pair deletion — `gio trash
                // --- --empty` is a silent no-op on this stack (exits 0, deletes
                // --- nothing), so the script deliberately never calls it.
                // NOTE: backup stays on the SAME filesystem as the Trash (both
                // under ~/.local/share). $XDG_RUNTIME_DIR is tmpfs — Directory.Move
                // across filesystems throws "Invalid cross-device link".
                backupRoot = Path.Combine(AppPaths.DataRoot, "stage3-backup-" + tag.TrimEnd('-'));
                MoveAside(filesDir, infoDir, backupRoot);
                try
                {
                    string aSrc = WriteTempFile(tag + "empty-a.tmp");
                    string bSrc = WriteTempFile(tag + "empty-b.tmp");
                    GioTrash(aSrc); GioTrash(bSrc);
                    var emptied = executor.Execute("EmptyTrash", config0, dryRun: false);
                    Check(emptied.Outcome == RunOutcome.Success
                            && emptied.ItemCounts.GetValueOrDefault("Deleted") == 2
                            && Snapshot(filesDir).Count == 0 && Snapshot(infoDir).Count == 0,
                        $"--empty run cleared 2 test items (got outcome={emptied.Outcome} deleted={emptied.ItemCounts.GetValueOrDefault("Deleted")} filesLeft={Snapshot(filesDir).Count} infoLeft={Snapshot(infoDir).Count} logs=[{string.Join(" | ", emptied.Logs)}])");
                }
                finally
                {
                    Restore(backupRoot, filesDir, infoDir);
                    backupRoot = null;
                }
                Check(SetsEqual(Snapshot(filesDir), initialFiles) && SetsEqual(Snapshot(infoDir), initialInfo),
                    "user trash byte-identical (names) after move-aside + restore");

                // --- Phase C: history ---
                var history = new RunHistoryStore(historyPath);
                history.Insert("EmptyTrash", DateTime.Now.AddSeconds(-5), DateTime.Now,
                    RunOutcome.Success, RunHistoryStore.BuildSummary(
                        executor.GetDryRunItems("EmptyTrash", config7).Result.Logs));
                var latest = history.GetLatestByScript();
                Check(latest.TryGetValue("EmptyTrash", out var row) && row.Outcome == "Success",
                    $"history latest EmptyTrash row is Success (summary: {row?.Summary})");

                Check(!ResiduePresent(filesDir, infoDir, tag) && SetsEqual(Snapshot(filesDir), initialFiles),
                    "final sweep: no test residue, trash matches initial snapshot");
            }
            finally
            {
                TryDelete(config7); TryDelete(config0);
            }

            lines.Add(lines.Any(l => l.StartsWith("FAIL")) ? "SELF-TEST FAIL" : "SELF-TEST PASS");
        }
        catch (Exception ex)
        {
            lines.Add("FAIL exception: " + ex.Message);
            lines.Add("SELF-TEST FAIL");
        }
        finally
        {
            // Never leak test pairs or strand the user's trash aside.
            try
            {
                foreach (var b in testPairs)
                {
                    TryDelete(Path.Combine(filesDir, b));
                    TryDelete(Path.Combine(infoDir, b + ".trashinfo"));
                }
                SweepPrefix(filesDir, infoDir, Prefix);
                if (backupRoot is not null)
                    Restore(backupRoot, filesDir, infoDir);
                TryDeleteDir(Path.Combine(AppPaths.DataRoot, "stage3-src"));
                foreach (var p in new[] { historyPath, historyPath + "-wal", historyPath + "-shm" })
                    TryDelete(p);
            }
            catch { }
        }
        return lines;
    }

    // ------------------------------------------------------------ helpers

    private static HashSet<string> Snapshot(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFileSystemEntries(dir).Select(e => Path.GetFileName(e)!).ToHashSet()
            : new HashSet<string>();

    private static bool SetsEqual(HashSet<string> a, HashSet<string> b) => a.SetEquals(b);

    private static bool GioAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/which", ArgumentList = { "gio" },
                RedirectStandardOutput = true, UseShellExecute = false,
            })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string WriteTempFile(string name)
    {
        // NOTE: must live on the same filesystem as the home Trash. gio
        // refuses to trash from system-internal mounts such as /tmp (tmpfs):
        // "Trashing on system internal mounts is not supported".
        string dir = Path.Combine(AppPaths.DataRoot, "stage3-src");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "stage3 test payload");
        return path;
    }

    private static void GioTrash(string path)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "gio", ArgumentList = { "trash", path },
            RedirectStandardOutput = true, UseShellExecute = false,
        }) ?? throw new InvalidOperationException("gio failed to start");
        if (!p.WaitForExit(15000) || p.ExitCode != 0)
            throw new InvalidOperationException($"gio trash failed for {path}");
        if (File.Exists(path))
            throw new InvalidOperationException($"gio trash left {path} in place");
    }

    private static string DecodeTrashPath(string encoded) =>
        Uri.UnescapeDataString(encoded.Replace('+', ' '));

    /// <summary>Info basename (without .trashinfo) whose Path= decodes to target.</summary>
    private static string FindInfo(string filesDir, string infoDir, string target)
    {
        foreach (var info in Directory.GetFiles(infoDir, "*.trashinfo"))
        {
            foreach (var line in File.ReadLines(info))
            {
                if (line.StartsWith("Path=", StringComparison.Ordinal)
                    && DecodeTrashPath(line["Path=".Length..]) == target)
                    return Path.GetFileNameWithoutExtension(info);
            }
        }
        throw new InvalidOperationException($"trashinfo not found for {target}");
    }

    private static void RewriteInfo(string infoDir, string baseName, Func<string, string?> edit)
    {
        string path = Path.Combine(infoDir, baseName + ".trashinfo");
        var kept = new List<string>();
        bool changed = false;
        foreach (var line in File.ReadAllLines(path))
        {
            var next = edit(line);
            if (next is null) { changed = true; continue; }
            if (next != line) changed = true;
            kept.Add(next);
        }
        if (changed) File.WriteAllLines(path, kept);
    }

    private static void Backdate(string infoDir, string baseName, int daysAgo)
    {
        string stamp = DateTime.Now.AddDays(-daysAgo).ToString("yyyy-MM-ddTHH:mm:ss");
        RewriteInfo(infoDir, baseName,
            line => line.StartsWith("DeletionDate=", StringComparison.Ordinal) ? "DeletionDate=" + stamp : line);
    }

    private static void RemoveDeletionDate(string infoDir, string baseName) =>
        RewriteInfo(infoDir, baseName,
            line => line.StartsWith("DeletionDate=", StringComparison.Ordinal) ? null : line);

    private static bool PairPresent(string filesDir, string infoDir, string baseName) =>
        Directory.GetFileSystemEntries(filesDir, baseName).Length > 0
        && File.Exists(Path.Combine(infoDir, baseName + ".trashinfo"));

    private static bool ResiduePresent(string filesDir, string infoDir, string tag) =>
        Snapshot(filesDir).Any(n => n.Contains(tag)) || Snapshot(infoDir).Any(n => n.Contains(tag));

    private static void SweepPrefix(string filesDir, string infoDir, string prefix)
    {
        if (Directory.Exists(filesDir))
            foreach (var e in Directory.GetFileSystemEntries(filesDir))
                if (Path.GetFileName(e).Contains(prefix))
                    TryDeleteDir(e);
        if (Directory.Exists(infoDir))
            foreach (var f in Directory.GetFiles(infoDir))
                if (Path.GetFileName(f).Contains(prefix))
                    TryDelete(f);
    }

    private static void MoveAside(string filesDir, string infoDir, string backupRoot)
    {
        Directory.CreateDirectory(backupRoot);
        if (Directory.Exists(filesDir)) Directory.Move(filesDir, Path.Combine(backupRoot, "files"));
        if (Directory.Exists(infoDir)) Directory.Move(infoDir, Path.Combine(backupRoot, "info"));
        Directory.CreateDirectory(filesDir);
        Directory.CreateDirectory(infoDir);
    }

    private static void Restore(string backupRoot, string filesDir, string infoDir)
    {
        string bf = Path.Combine(backupRoot, "files");
        string bi = Path.Combine(backupRoot, "info");
        if (Directory.Exists(bf))
        {
            if (Directory.Exists(filesDir)) Directory.Delete(filesDir, recursive: true);
            Directory.Move(bf, filesDir);
        }
        if (Directory.Exists(bi))
        {
            if (Directory.Exists(infoDir)) Directory.Delete(infoDir, recursive: true);
            Directory.Move(bi, infoDir);
        }
        if (Directory.Exists(backupRoot) && !Directory.GetFileSystemEntries(backupRoot).Any())
            Directory.Delete(backupRoot);
    }

    private static string WriteConfig(int minAgeDays)
    {
        string path = Path.Combine(AppPaths.RuntimeRoot,
            $"scriptsuite_stage3_test_{minAgeDays}_{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { MinAgeDays = minAgeDays }));
        return path;
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
