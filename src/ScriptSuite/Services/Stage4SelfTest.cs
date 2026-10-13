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

            // --- 7e. no-result UX contract: the child's stderr reason is what
            // --- the parent shows in the Output pane (not just History). ---
            {
                var logs = PolkitElevationService.NoResultLogs(2, new List<string>
                {
                    "Elevated child refused: Elevated result directory '/run/user/1000' is outside the caller's runtime dirs — refusing.",
                });
                Check(logs.Count == 2 && logs[0].Contains("did not write a result file")
                        && logs[1].StartsWith("Elevated child refused:"),
                    "no-result logs carry the child stderr reason");
                var bare = PolkitElevationService.NoResultLogs(1, new List<string>());
                Check(bare.Count == 2 && bare[1].Contains("exit code: 1"),
                    "no-result logs fall back to the exit code with a silent child");
            }

            // --- 7f. production path builder: PreviewArgv uses the same
            // --- ElevatedPaths.Build the parent spawns with (no placeholders),
            // --- so previews and tests cannot drift from pkexec argv. ---
            {
                var argv = gated.PreviewArgv("ClearJournal", userCfg).ToList();
                string res = argv[argv.IndexOf("--result-path") + 1];
                string live = argv[argv.IndexOf("--live-log") + 1];
                string inc = argv[argv.IndexOf("--include-only") + 1];
                bool shapeOk = argv[0] == "pkexec"
                    && Path.GetDirectoryName(res) == AppPaths.RuntimeRoot
                    && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(res), @"^scriptsuite_[0-9a-f]{32}\.json$")
                    && live == res + ".live.log" && inc == res + ".include.json";
                Check(shapeOk, "preview argv uses production GUID paths under RuntimeRoot");
                var p2 = ElevatedPaths.Build();
                Check(p2.ResultPath != res && Path.GetDirectoryName(p2.ResultPath) == AppPaths.RuntimeRoot,
                    "path builder mints a fresh GUID per build");
            }

            // --- 7g. component compare truth table (21:04 class): the dir
            // --- itself matches, true children match, sibling prefixes don't.
            {
                bool tableOk =
                    ElevatedArgValidator.IsSameOrBeneath("/run/user/1000", "/run/user/1000")
                    && ElevatedArgValidator.IsSameOrBeneath("/run/user/1000/", "/run/user/1000")
                    && ElevatedArgValidator.IsSameOrBeneath("/run/user/1000/sub", "/run/user/1000")
                    && ElevatedArgValidator.IsSameOrBeneath("/tmp", "/tmp/")
                    && !ElevatedArgValidator.IsSameOrBeneath("/run/user/1000x", "/run/user/1000")
                    && !ElevatedArgValidator.IsSameOrBeneath("/run/user/10000", "/run/user/1000")
                    && !ElevatedArgValidator.IsSameOrBeneath("/run/user/100", "/run/user/1000")
                    && !ElevatedArgValidator.IsSameOrBeneath("/etc", "/run/user/1000")
                    && !ElevatedArgValidator.IsSameOrBeneath("/", "/run/user/1000");
                Check(tableOk, "IsSameOrBeneath truth table (self/child true, sibling prefixes false)");
            }

            // --- 7h. rendezvous ownership handoff (C2): a root child fchowns
            // --- result + live-log to PKEXEC_UID on the open fd; unprivileged
            // --- round-trips skip; an untrustworthy PKEXEC_UID refuses loudly
            // --- with no result file. Forced-root via the euid seam (fake
            // --- owner records, never touches the kernel).
            int noopDays2 = OldestBootPlusMarginDays(30);
            if (noopDays2 < 0)
            {
                lines.Add("SKIP rendezvous handoff (no user journal boots readable)");
            }
            else
            {
                uint selfUid = NativeLib.EffectiveUid();
                uint selfGid = NativeLib.EffectiveGid();
                string? origPkexec = Environment.GetEnvironmentVariable("PKEXEC_UID");
                var handoffSvc = new PolkitElevationService(catalog, new RiskConsentStore(consentPath));
                string hCfg = WriteCfg(cfgDir, "cj-handoff.json",
                    new { Scope = "user", VacuumTime = $"{noopDays2}d", VacuumSize = "", BackupDir = Path.Combine(cfgDir, "backups-handoff") });
                try
                {
                    // Forced root, own uid: both files handed off, run proceeds.
                    Environment.SetEnvironmentVariable("PKEXEC_UID", selfUid.ToString());
                    var paths = ElevatedPaths.Build();
                    File.WriteAllText(paths.IncludePath, "[]");
                    var rec = new RecordingOwner();
                    int hExit = handoffSvc.RunElevatedChild("ClearJournal", hCfg,
                        paths.ResultPath, paths.LiveLogPath, paths.IncludePath, rec, () => 0);
                    Check(hExit == 0 && rec.Calls.Count == 2
                        && rec.Calls.TrueForAll(c => c.Uid == selfUid && c.Gid == selfGid),
                        $"forced-root handoff owns live+result as {selfUid}:{selfGid} (exit={hExit}, calls={rec.Calls.Count})");
                    // Bogus uid: loud refusal, no result file.
                    Environment.SetEnvironmentVariable("PKEXEC_UID", "60000");
                    var paths2 = ElevatedPaths.Build();
                    File.WriteAllText(paths2.IncludePath, "[]");
                    var rec2 = new RecordingOwner();
                    string errCap;
                    var origErr = Console.Error;
                    try
                    {
                        using var errSw = new StringWriter();
                        Console.SetError(errSw);
                        int rExit = handoffSvc.RunElevatedChild("ClearJournal", hCfg,
                            paths2.ResultPath, paths2.LiveLogPath, paths2.IncludePath, rec2, () => 0);
                        errSw.Flush();
                        errCap = rExit + "|" + errSw.ToString();
                    }
                    finally { Console.SetError(origErr); }
                    try { if (File.Exists(paths2.LiveLogPath)) File.Delete(paths2.LiveLogPath); } catch { }
                    Check(errCap.StartsWith("2|") && rec2.Calls.Count == 0
                        && !File.Exists(paths2.ResultPath) && errCap.Contains("refused"),
                        $"bogus PKEXEC_UID refused without result ({errCap.Trim().Replace('\n', ' ')})");
                    // Unprivileged: skip path, zero handoff calls, normal exit.
                    Environment.SetEnvironmentVariable("PKEXEC_UID", null);
                    var paths3 = ElevatedPaths.Build();
                    File.WriteAllText(paths3.IncludePath, "[]");
                    var rec3 = new RecordingOwner();
                    int uExit = handoffSvc.RunElevatedChild("ClearJournal", hCfg,
                        paths3.ResultPath, paths3.LiveLogPath, paths3.IncludePath, rec3);
                    Check(uExit == 0 && rec3.Calls.Count == 0 && File.Exists(paths3.ResultPath),
                        $"unprivileged round-trip skips handoff (exit={uExit})");
                }
                finally { Environment.SetEnvironmentVariable("PKEXEC_UID", origPkexec); }
            }

            // --- 7i. unreadable-result mapping (C3): chmod-000 simulates the
            // --- root-owned-0600 trigger without privilege; exit 0 maps to
            // --- Warning with the helper-finished line, non-zero to Failed
            // --- with the reason. Never an escaping UnauthorizedAccessException.
            {
                string trig = Path.Combine(cfgDir, "unreadable.json");
                File.WriteAllText(trig, "{}");
                File.SetUnixFileMode(trig, UnixFileMode.None);
                Exception? trigger = null;
                try { File.ReadAllText(trig); }
                catch (Exception ex) { trigger = ex; }
                File.SetUnixFileMode(trig, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Check(trigger is UnauthorizedAccessException
                    && trigger.Message.Contains("Access to the path"),
                    $"chmod-000 read-back throws UnauthorizedAccessException ({trigger?.GetType().Name}: {trigger?.Message})");
                var w = PolkitElevationService.MapUnreadableResult("ClearJournal", 0, trigger!);
                Check(w.Outcome == RunOutcome.Warning && w.ScriptId == "ClearJournal"
                    && w.Logs.Any(l => l.Contains("helper finished, but its result could not be read"))
                    && w.Logs.Any(l => l.Contains("Access to the path")),
                    "exit-0 unreadable result maps to Warning + helper-finished line + reason");
                var f = PolkitElevationService.MapUnreadableResult("ClearJournal", 1, trigger!);
                Check(f.Outcome == RunOutcome.Failed && f.ScriptId == "ClearJournal"
                    && f.Logs.Any(l => l.Contains("Access to the path")),
                    "non-zero-exit unreadable result maps to Failed with the reason");
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

    /// <summary>Test double for the rendezvous handoff: records (uid, gid)
    /// per file instead of calling fchown — proves wiring (both files, call
    /// order, identities) without privilege. Order is deterministic: the
    /// child owns the live log first, the result second.</summary>
    private sealed class RecordingOwner : IRendezvousOwner
    {
        public readonly List<(uint Uid, uint Gid)> Calls = new();
        public void OwnFile(FileStream fs, uint uid, uint gid) => Calls.Add((uid, gid));
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
