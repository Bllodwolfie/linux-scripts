using System.Diagnostics;
using System.Text.Json;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>Stage 4 headless proofs for ClearJournal + PolkitElevationService.
/// Everything here runs without privilege prompts (Scope=user runs, config
/// fallbacks, consent gate, direct child invocation). The two tests that need
/// a real polkit dialog (Cancel = Cancelled, Allow = privileged run) are
/// interactive by design and live OUTSIDE this suite — see the Stage 4 pause
/// step. Returns report lines ending in SELF-TEST PASS or SELF-TEST FAIL.</summary>
public static class Stage4SelfTest
{
    public static List<string> Run()
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + label);

        string consentPath = Path.Combine(AppPaths.RuntimeRoot,
            $"scriptsuite_stage4_consents_{Guid.NewGuid():N}.json");
        string cfgDir = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage4_cfg_{Guid.NewGuid():N}");
        // History assertions run against a scratch DB, never the user's real
        // history.db.
        string historyPath = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage4_hist_{Guid.NewGuid():N}.db");
        try
        {
            Directory.CreateDirectory(cfgDir);
            var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
            var executor = new BashScriptExecutor(catalog);

            // --- 1. dry-run (Scope=user): succeeds, enumerates boots ---
            string userCfg = WriteCfg(cfgDir, "cj-user.json",
                new { Scope = "user", VacuumTime = "7d", VacuumSize = "", BackupDir = Path.Combine(cfgDir, "backups") });
            var (userItems, userDry) = executor.GetDryRunItems("ClearJournal", userCfg);
            Check(userDry.Outcome == RunOutcome.Success, $"user dry-run outcome is Success (got {userDry.Outcome})");
            Check(userItems.Count >= 1, $"user dry-run enumerates boots (got {userItems.Count})");

            // --- 2. dry/real parity (Scope=user, 7d): the vacuum must never
            // --- remove MORE than previewed (post Clear <= pre Clear); the
            // --- export file must exist (backup-first rule). Removing LESS is
            // --- legitimate: vacuum is file-granular and root-owned journal
            // --- files surface as Permission-denied warnings, not errors.
            var preDelete = userItems.Where(i => i.Action == "Clear").Select(i => i.Target).ToList();
            var real = executor.Execute("ClearJournal", userCfg, dryRun: false);
            var (postItems, _) = executor.GetDryRunItems("ClearJournal", userCfg);
            int postDelete = postItems.Count(i => i.Action == "Clear");
            string[] exports = Directory.Exists(Path.Combine(cfgDir, "backups"))
                ? Directory.GetFiles(Path.Combine(cfgDir, "backups"), "*.export") : Array.Empty<string>();
            var postTargets = postItems.Where(i => i.Action == "Clear").Select(i => i.Target).ToHashSet();
            Check(real.Outcome is RunOutcome.Success or RunOutcome.Warning
                    && postDelete <= preDelete.Count
                    && postTargets.IsSubsetOf(preDelete)
                    && exports.Length == 1,
                $"parity: pre={preDelete.Count} Clear, real={real.Outcome}, post={postDelete} Clear (subset), exports={exports.Length}"
                + (preDelete.Count == 0 ? " (trivial: nothing older than 7d)" : ""));

            // --- 3. missing config -> silent defaults (system dry-run reads OK) ---
            var (missItems, missDry) = executor.GetDryRunItems("ClearJournal",
                Path.Combine(cfgDir, "does-not-exist.json"));
            Check(missDry.Outcome == RunOutcome.Success && missItems.Count >= 1,
                $"missing config falls back to defaults (got {missDry.Outcome}, {missItems.Count} items)");

            // --- 4. corrupt config -> WARN + defaults ---
            string corruptCfg = Path.Combine(cfgDir, "corrupt.json");
            File.WriteAllText(corruptCfg, "{oops");
            var (_, corruptDry) = executor.GetDryRunItems("ClearJournal", corruptCfg);
            Check(corruptDry.Outcome == RunOutcome.Warning
                    && corruptDry.Logs.Any(l => l.Contains("corrupt or unreadable")),
                $"corrupt config warns + falls back (got {corruptDry.Outcome})");

            // --- 5. consent gate: no consent -> Cancelled fast, nothing spawned ---
            var gatedStore = new RiskConsentStore(consentPath);
            var gated = new PolkitElevationService(catalog, gatedStore);
            var sw = Stopwatch.StartNew();
            var denied = gated.RunElevated("ClearJournal", userCfg);
            sw.Stop();
            Check(denied.Outcome == RunOutcome.Cancelled
                    && denied.Logs.Any(l => l.Contains("risk consent required"))
                    && sw.Elapsed < TimeSpan.FromSeconds(5),
                $"no consent -> Cancelled without spawning (got {denied.Outcome} in {sw.Elapsed.TotalSeconds:F1}s)");

            // --- 6. consented spawn: INTERACTIVE-ONLY, skipped headless. This
            // --- tool session lives INSIDE the graphical logind session, so
            // --- any pkexec reaches the real KDE auth agent and pops a genuine
            // --- desktop prompt (proven by experiment — a spawn attempt hung
            // --- 30s awaiting a click). There is no way to exercise the
            // --- transport without prompting the user, so Cancel/Allow are
            // --- covered by the interactive pause step instead. ---
            lines.Add("SKIP consented pkexec spawn (interactive-only: needs a real desktop prompt)");

            // --- 7. child round-trip direct (no pkexec): binary --elevated-run
            // --- ClearJournal user scope proves the serialization contract
            // --- deterministically. The child allow-list accepts only
            // --- RequiresAdmin scripts; retention = oldest user boot + 30d
            // --- so the vacuum provably deletes nothing (side-effect-free by
            // --- construction, like the old TempCleanup variant). Unavailable
            // --- user journal -> SKIP, not FAIL (headless/CI without logind).
            string binary = Path.GetFullPath(Path.Combine(AppPaths.ManifestsDir, "..", "ScriptSuite"));
            int noopDays = OldestBootPlusMarginDays(30);
            if (noopDays < 0)
            {
                lines.Add("SKIP direct child round-trip (no user journal boots readable)");
            }
            else
            {
                string cjCfg = WriteCfg(cfgDir, "cj-child.json",
                    new { Scope = "user", VacuumTime = $"{noopDays}d", VacuumSize = "", BackupDir = Path.Combine(cfgDir, "backups-child") });
                string resultPath = Path.Combine(cfgDir, "child-result.json");
                string liveLog = resultPath + ".live.log";
                string includePath = Path.Combine(cfgDir, "child-include.json");
                File.WriteAllText(includePath, "[]");
                int childExit = RunChild(binary, "ClearJournal", cjCfg, resultPath, liveLog, includePath, out string childErr);
                bool childOk = childExit == 0 && File.Exists(resultPath) && File.Exists(liveLog)
                    && new FileInfo(liveLog).Length > 0;
                string childOutcome = "";
                if (File.Exists(resultPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(resultPath));
                    childOutcome = doc.RootElement.GetProperty("Outcome").GetString() ?? "";
                    // Retention exceeds the oldest boot, so the vacuum deletes
                    // nothing and the empty export honestly yields Warning —
                    // what this proves is the accept path (transport + run).
                    childOk &= childOutcome == "Warning"
                        && doc.RootElement.GetProperty("Logs").GetArrayLength() > 0
                        && doc.RootElement.GetProperty("ExitCode").GetInt32() == 0;
                }
                Check(childOk, $"direct child round-trip: exit={childExit}, outcome={childOutcome}, live log written{childErr}");
            }

            // --- 7b. child allow-list: TempCleanup (requiresAdmin=false) dies
            // --- with exit 2 and writes no result file, pkexec or not. ---
            {
                string tcScratch = Path.Combine(cfgDir, "stage4-tc");
                Directory.CreateDirectory(tcScratch);
                string tcCfg = WriteCfg(cfgDir, "tc.json",
                    new { TargetFolder = tcScratch, CutoffDays = 7, IgnoreFolders = new string[0] });
                string rejResult = Path.Combine(cfgDir, "child-reject-tc.json");
                int rejExit = RunChild(binary, "TempCleanup", tcCfg, rejResult, null, null, out string rejErr);
                Check(rejExit == 2 && !File.Exists(rejResult) && rejErr.Contains("not elevation-eligible"),
                    $"non-admin child rejected: exit={rejExit}{rejErr}");
            }

            // --- 7c. unknown script id dies with exit 2, no result file. ---
            {
                string unkResult = Path.Combine(cfgDir, "child-reject-unk.json");
                int unkExit = RunChild(binary, "NoSuchScript", Path.Combine(cfgDir, "does-not-exist.json"),
                    unkResult, null, null, out string unkErr);
                Check(unkExit == 2 && !File.Exists(unkResult) && unkErr.Contains("Unknown script id"),
                    $"unknown-id child rejected: exit={unkExit}{unkErr}");
            }

            // --- 7d. garbage include-only aborts (never widens), exit 2. ---
            {
                string badInclude = Path.Combine(cfgDir, "child-bad.include.json");
                File.WriteAllText(badInclude, "not json{{{");
                string badResult = Path.Combine(cfgDir, "child-reject-inc.json");
                string cjCfg2 = WriteCfg(cfgDir, "cj-child2.json",
                    new { Scope = "user", VacuumTime = "7d", VacuumSize = "", BackupDir = Path.Combine(cfgDir, "backups-child2") });
                int badExit = RunChild(binary, "ClearJournal", cjCfg2, badResult, null, badInclude, out string badErr);
                Check(badExit == 2 && !File.Exists(badResult) && badErr.Contains("never widening"),
                    $"garbage include-only rejected: exit={badExit}{badErr}");
            }

            // --- 8. system-scope real run as non-root -> refused with Failed ---
            string sysCfg = WriteCfg(cfgDir, "cj-sys.json",
                new { Scope = "system", VacuumTime = "7d", VacuumSize = "", BackupDir = Path.Combine(cfgDir, "backups-sys") });
            var sysReal = executor.Execute("ClearJournal", sysCfg, dryRun: false);
            Check(sysReal.Outcome == RunOutcome.Failed
                    && sysReal.Logs.Any(l => l.Contains("requires root")),
                $"system real as non-root refused (got {sysReal.Outcome})");

            // --- 9. history round-trip ---
            var history = new RunHistoryStore(historyPath);
            history.Insert("ClearJournal", DateTime.Now.AddSeconds(-5), DateTime.Now,
                real.Outcome, RunHistoryStore.BuildSummary(real.Logs));
            var latest = history.GetLatestByScript();
            Check(latest.TryGetValue("ClearJournal", out var row) && row.Outcome == real.Outcome.ToString(),
                $"history latest ClearJournal row is {real.Outcome} (summary: {row?.Summary})");

            lines.Add(lines.Any(l => l.StartsWith("FAIL")) ? "SELF-TEST FAIL" : "SELF-TEST PASS");
        }
        catch (Exception ex)
        {
            lines.Add("FAIL exception: " + ex.Message);
            lines.Add("SELF-TEST FAIL");
        }
        finally
        {
            try { if (Directory.Exists(cfgDir)) Directory.Delete(cfgDir, recursive: true); } catch { }
            try { if (File.Exists(consentPath)) File.Delete(consentPath); } catch { }
            foreach (var p in new[] { historyPath, historyPath + "-wal", historyPath + "-shm" })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
        return lines;
    }

    private static string WriteCfg(string dir, string name, object config)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        return path;
    }

    /// <summary>Runs the built binary as an --elevated-run child (no pkexec).
    /// Returns the exit code; stderr tail is appended to errTail for Check().
    /// Never throws (a spawn failure yields exit 127).</summary>
    private static int RunChild(string binary, string scriptId, string cfg,
        string result, string? liveLog, string? include, out string errTail)
    {
        errTail = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--elevated-run");
            psi.ArgumentList.Add(scriptId);
            psi.ArgumentList.Add("--config-path"); psi.ArgumentList.Add(cfg);
            psi.ArgumentList.Add("--result-path"); psi.ArgumentList.Add(result);
            if (liveLog is not null) { psi.ArgumentList.Add("--live-log"); psi.ArgumentList.Add(liveLog); }
            if (include is not null) { psi.ArgumentList.Add("--include-only"); psi.ArgumentList.Add(include); }
            using var p = Process.Start(psi);
            if (p is null) { errTail = " (spawn returned null)"; return 127; }
            p.WaitForExit(60000);
            string err = p.StandardError.ReadToEnd();
            if (err.Length > 160) err = err[^160..];
            errTail = " (stderr: " + err.Trim().Replace('\n', ' ') + ")";
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            errTail = " (spawn: " + ex.Message + ")";
            return 127;
        }
    }

    /// <summary>Oldest user-journal boot age in days plus margin, for a
    /// provably no-op vacuum retention. Returns -1 when the user journal is
    /// unreadable (headless/CI without logind).</summary>
    private static int OldestBootPlusMarginDays(int margin)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "journalctl",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add("--list-boots");
            psi.ArgumentList.Add("--no-pager");
            using var p = Process.Start(psi);
            if (p is null) return -1;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            DateTime oldest = DateTime.MaxValue;
            foreach (var line in output.Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line,
                    @"\b(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})");
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, out var dt) && dt < oldest)
                    oldest = dt;
            }
            if (oldest == DateTime.MaxValue) return -1;
            return (int)(DateTime.Now - oldest).TotalDays + margin;
        }
        catch { return -1; }
    }
}
