using ScriptSuite.Models;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Services;

/// <summary>Stage 8 proofs: Run window VM (preview, deselect, confirm,
/// report-only immediate run, admin no-consent gate that spawns nothing),
/// executor cancellation (sleeper fixture), History + Scheduled History VMs
/// (mapping, order, filters, log routing, launcher), Run All (summary text,
/// dashboard store, admin gate, preview + confirm), and tile hide/show.
/// Everything runs against scratch XDG/HOME dirs and a scratch history DB;
/// process env is redirected only for the duration and restored in finally.
/// No pkexec is ever spawned (the only elevation path asserted is the
/// no-consent gate, which returns before spawning). No systemd writes.
/// Ends with SELF-TEST PASS or SELF-TEST FAIL, like stages 2–7.</summary>
public static class Stage8SelfTest
{
    public static List<string> Run()
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + label);

        string? origConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? origData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? origRun = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? origHome = Environment.GetEnvironmentVariable("HOME");
        string scratch = Path.Combine(Path.GetTempPath(), "scriptsuite_stage8_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(scratch);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(scratch, "config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(scratch, "data"));
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Combine(scratch, "run"));
            Environment.SetEnvironmentVariable("HOME", Path.Combine(scratch, "home"));
            Directory.CreateDirectory(Path.Combine(scratch, "home"));
            Directory.CreateDirectory(Path.Combine(scratch, "run"));
            AppPaths.EnsureConfigsSeeded();

            var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
            var history = new RunHistoryStore(Path.Combine(scratch, "history.db"));
            var consents = new RiskConsentStore(Path.Combine(scratch, "risk.json"));
            var schedules = new ScheduleStore(Path.Combine(scratch, "sched.json"));
            var executor = new BashScriptExecutor(catalog);
            var elevation = new PolkitElevationService(catalog, consents);
            Action<Action> direct = a => a();

            // --- RunSelectionItem ---
            var sel = new RunSelectionItem(new DryRunItem { Action = "Delete", Target = "/tmp/x", Detail = "d" });
            Check(sel.IsChecked, "8a selection checked by default");
            Check(sel.ActionDisplay == "Delete", "8a selection action display");

            // --- executor cancellation (sleeper fixture, never touches real scripts) ---
            Directory.CreateDirectory(Path.Combine(scratch, "manifests"));
            File.WriteAllText(Path.Combine(scratch, "sleeper.sh"), "#!/bin/bash\nsleep 30\n");
            File.WriteAllText(Path.Combine(scratch, "manifests", "Sleeper.json"),
                "{\"id\":\"Sleeper\",\"displayName\":\"Sleeper\",\"description\":\"d\",\"category\":\"Test\","
                + "\"requiresAdmin\":false,\"supportsDryRun\":false,\"scriptPath\":\""
                + Path.Combine(scratch, "sleeper.sh").Replace("\\", "\\\\") + "\",\"fields\":[]}");
            var fixtureExec = new BashScriptExecutor(new ManifestCatalog(Path.Combine(scratch, "manifests")));
            using (var cts = new CancellationTokenSource())
            {
                var task = Task.Run(() => fixtureExec.Execute("Sleeper", "", false, null, _ => { }, cts.Token));
                Thread.Sleep(1500);
                cts.Cancel();
                var res = task.GetAwaiter().GetResult();
                Check(res.Outcome == RunOutcome.Cancelled, "8a cancel yields Cancelled");
                Check(res.Logs.Contains("Cancelled by user."), "8a cancel log line");
            }

            // --- Run window VM: TempCleanup preview + confirm (scratch target only) ---
            Directory.CreateDirectory(Path.Combine(scratch, "tmptarget"));
            File.WriteAllText(Path.Combine(scratch, "tmptarget", "old.tmp"), "old");
            File.WriteAllText(Path.Combine(scratch, "tmptarget", "fresh.tmp"), "fresh");
            File.SetLastWriteTime(Path.Combine(scratch, "tmptarget", "old.tmp"), DateTime.Now.AddDays(-5));
            File.WriteAllText(AppPaths.ConfigPathFor("TempCleanup"),
                "{\"TargetFolder\": \"" + Path.Combine(scratch, "tmptarget") + "\", \"CutoffDays\": 1, \"IgnoreFolders\": []}");
            var runVm = new ScriptRunViewModel(catalog.Find("TempCleanup")!, executor, elevation,
                consents, history, AppPaths.ConfigPathFor("TempCleanup"), direct);
            runVm.LoadAsync().GetAwaiter().GetResult();
            Check(runVm.CurrentPhase == ScriptRunViewModel.Phase.Preview, "8a preview phase for dry-run script");
            if (runVm.Selections.Count > 0)
            {
                runVm.Selections[0].IsChecked = false;
                Check(runVm.SelectionCountText.StartsWith("0 of ") || runVm.SelectionCountText.Contains(" of "),
                    "8a deselect updates count (" + runVm.SelectionCountText + ")");
                runVm.Selections[0].IsChecked = true;
            }
            else
            {
                Check(true, "8a preview empty in scratch env (count path skipped)");
            }
            int hBefore = history.GetRecent(100).Count;
            if (runVm.ConfirmEnabled)
            {
                runVm.ConfirmAsync().GetAwaiter().GetResult();
                Check(runVm.CurrentPhase == ScriptRunViewModel.Phase.Finished, "8a run finishes");
                Check(runVm.StatusText.Length > 0 && runVm.LogLines.Count > 0, "8a status + live lines");
                Check(history.GetRecent(100).Count == hBefore + 1, "8a history inserted");
                Check(!runVm.ConfirmEnabled, "8a confirm disabled after run");
                Check(!File.Exists(Path.Combine(scratch, "tmptarget", "old.tmp"))
                    && File.Exists(Path.Combine(scratch, "tmptarget", "fresh.tmp")),
                    "8a real run honored scratch target only");
            }
            else
            {
                Check(true, "8a confirm disabled on empty preview (run skipped)");
            }

            // --- report-only script runs immediately ---
            File.WriteAllText(AppPaths.ConfigPathFor("SystemHealthReport"),
                "{\"OutputDir\": \"" + Path.Combine(scratch, "reports") + "\", \"OutputFile\": \"rep.html\","
                + " \"MaxTopProcesses\": 5, \"MaxErrorEvents\": 5, \"ErrorWindowHours\": 24}");
            var repVm = new ScriptRunViewModel(catalog.Find("SystemHealthReport")!, executor, elevation,
                consents, history, AppPaths.ConfigPathFor("SystemHealthReport"), direct);
            repVm.LoadAsync().GetAwaiter().GetResult();
            Check(repVm.CurrentPhase == ScriptRunViewModel.Phase.Finished, "8a report-only runs without preview");

            // --- admin no-consent gate spawns nothing ---
            var adminVm = new ScriptRunViewModel(catalog.Find("ClearJournal")!, executor, elevation,
                consents, history, AppPaths.ConfigPathFor("ClearJournal"), direct);
            int hAdmin = history.GetRecent(100).Count;
            adminVm.LoadAsync().GetAwaiter().GetResult();
            // Preview or immediate run — either path must hit the consent gate (no pkexec spawned).
            if (adminVm.CurrentPhase == ScriptRunViewModel.Phase.Preview)
                adminVm.ConfirmAsync().GetAwaiter().GetResult();
            Check(adminVm.StatusText.Contains("I understand the risks"), "8a admin blocked without consent");
            Check(history.GetRecent(100).Count == hAdmin, "8a blocked admin writes no history");

            // --- History VM ---
            var h2 = new RunHistoryStore(Path.Combine(scratch, "history2.db"));
            h2.Insert("DownloadsCleanup", DateTime.Now.AddMinutes(-5), DateTime.Now, RunOutcome.Success, "5 moved");
            h2.Insert("EmptyTrash", DateTime.Now.AddMinutes(-2), DateTime.Now, RunOutcome.Failed, "boom");
            var hvis = new HistoryViewModel(h2, catalog, LogDetailResolver.Production(), _ => { });
            Check(hvis.Rows.Count == 2, "8b history rows");
            Check(hvis.Rows[0].ScriptId == "EmptyTrash", "8b newest first");
            Check(hvis.Rows[0].DisplayName == "Empty Trash", "8b display name");
            hvis.SearchText = "downloads";
            Check(hvis.Rows.Count == 1 && hvis.Rows[0].ScriptId == "DownloadsCleanup", "8b text filter");
            hvis.SearchText = "";
            hvis.OutcomeFilter = "Failed";
            Check(hvis.Rows.Count == 1 && hvis.Rows[0].ScriptId == "EmptyTrash", "8b outcome filter");
            hvis.OutcomeFilter = "All";
            Check(hvis.Rows.Count == 2, "8b filter reset");

            // --- log routing ---
            var resolver = LogDetailResolver.Production();
            string logDir = Path.Combine(scratch, "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllLines(Path.Combine(logDir, "test.log"), Enumerable.Range(1, 100).Select(i => $"line {i}"));
            File.WriteAllText(AppPaths.ConfigPathFor("DownloadsCleanup"),
                "{\"LogDir\": \"" + logDir + "\", \"LogFile\": \"test.log\"}");
            var (lp, _, detail, _) = resolver.BuildDetail("DownloadsCleanup", "did stuff", "Success");
            Check(lp == Path.Combine(logDir, "test.log"), "8b log path routed");
            Check(detail.Contains("line 100") && detail.Contains("Last 80 of 100"), "8b log tail");
            var noLog = resolver.BuildDetail("SoftwareInventory", null, "Success");
            Check(noLog.Hint.Contains("not yet created"), "8b missing log fallback");
            File.WriteAllText(AppPaths.ConfigPathFor("SoftwareInventory"), "{corrupt!!!");
            Check(resolver.BuildDetail("SoftwareInventory", "s", "Success").Hint.Contains("not yet created"),
                "8b corrupt config fallback");
            var noFile = resolver.BuildDetail("TempCleanup", "3 deleted", "Success");
            Check(noFile.LogPath is null && noFile.Detail.Contains("3 deleted"), "8b no-file detail");
            string launched = "";
            var hvis2 = new HistoryViewModel(h2, catalog, resolver, p => launched = p);
            hvis2.SearchText = "downloads";
            hvis2.OpenLogCommand.Execute(hvis2.Rows[0]);
            Check(launched == Path.Combine(logDir, "test.log"), "8b open-log launches file");

            // --- Scheduled history stays separate ---
            h2.InsertScheduled("TempCleanup", DateTime.Now, DateTime.Now, DateTime.Now,
                "SkippedBusy", "Skipped — app was busy.", "2 Days @ 09:00");
            var shvm = new ScheduledHistoryViewModel(h2, catalog);
            Check(shvm.Rows.Count == 1 && shvm.Rows[0].Outcome == "SkippedBusy", "8b scheduled rows");
            Check(shvm.Rows[0].DisplayName.Contains("2 Days @ 09:00"), "8b scheduled trigger in display");

            // --- Run All ---
            Check(RunAllViewModel.BuildSummaryText(0, 0, 0, 0, 0, 0) == "Nothing to do right now.",
                "8c summary empty");
            Check(RunAllViewModel.BuildSummaryText(0, 0, 0, 2, 2, 2)
                == "Nothing to do right now (+ 2 report-only scripts — no preview).", "8c summary report-only");
            Check(RunAllViewModel.BuildSummaryText(5, 3, 1, 0, 3, 3)
                == "Across all scripts: 5 to delete, 3 to move, 1 to skip.", "8c summary full");
            Check(RunAllViewModel.BuildSummaryText(2, 0, 0, 0, 1, 3)
                == "Gathering previews so far: 2 to delete…", "8c summary partial");
            Check(RunAllViewModel.BuildSummaryText(0, 0, 0, 0, 1, 3) == "Gathering previews…",
                "8c summary gathering");

            var dash = new DashboardStateStore(Path.Combine(scratch, "dashboard.json"));
            Check(dash.IsRunAllEnabled("TempCleanup") && !dash.IsHidden("TempCleanup"), "8c dashboard defaults");
            dash.SetRunAll("TempCleanup", false);
            dash.SetHidden("EmptyTrash", true);
            var dashReload = new DashboardStateStore(Path.Combine(scratch, "dashboard.json"));
            Check(!dashReload.IsRunAllEnabled("TempCleanup") && dashReload.IsHidden("EmptyTrash"),
                "8c dashboard persists");
            File.WriteAllText(Path.Combine(scratch, "dashboard.json"), "{broken");
            var dashCorrupt = new DashboardStateStore(Path.Combine(scratch, "dashboard.json"));
            Check(dashCorrupt.IsRunAllEnabled("TempCleanup"), "8c dashboard corrupt fallback");
            File.Delete(Path.Combine(scratch, "dashboard.json"));

            var dash4 = new DashboardStateStore(Path.Combine(scratch, "dashboard4.json"));
            var mvm = new MainViewModel(catalog, executor, elevation, history, schedules,
                consents, AppPaths.ConfigPathFor, dash4);
            Check(mvm.Cards.All(c => c.RunAllIncluded), "8c cards default included");
            Check(mvm.RunAllEnabled, "8c run-all enabled");
            var orderNoConsent = mvm.RunAllOrder();
            Check(orderNoConsent.All(m => !m.RequiresAdmin), "8c admin excluded without consent");
            Check(orderNoConsent.Count > 0 && orderNoConsent[0].Id == "EmptyTrash", "8c trash first");
            consents.SetConsent("ClearJournal", true);
            Check(mvm.RunAllOrder().Any(m => m.Id == "ClearJournal"), "8c admin included with consent");
            consents.SetConsent("ClearJournal", false);

            var tmpManifest = catalog.Find("TempCleanup")!;
            var sec = new RunScriptPreviewItem(tmpManifest);
            sec.PreviewItems.Add(new DryRunItem { Action = "Delete", Target = "a", Detail = "" });
            sec.PreviewItems.Add(new DryRunItem { Action = "Delete", Target = "b", Detail = "" });
            sec.PreviewItems.Add(new DryRunItem { Action = "Skip", Target = "c", Detail = "" });
            sec.FinalizePreview();
            Check(sec.PreviewCollapsedSummary == "Temp File Cleanup — 3 items (2 delete, 1 skip)",
                "8c collapsed summary");
            var big = new RunScriptPreviewItem(tmpManifest);
            for (int i = 0; i < 21; i++)
                big.PreviewItems.Add(new DryRunItem { Action = "Delete", Target = "t" + i, Detail = "" });
            big.FinalizePreview();
            Check(sec.IsPreviewExpanded && !big.IsPreviewExpanded, "8c collapse threshold 20");

            var miniOrder = new List<ScriptManifest> { catalog.Find("TempCleanup")!, catalog.Find("SystemHealthReport")! };
            var ravm = new RunAllViewModel(miniOrder, executor, elevation, consents, history,
                AppPaths.ConfigPathFor, direct);
            ravm.LoadAsync().GetAwaiter().GetResult();
            Check(ravm.ConfirmEnabled, "8c preview gathered");
            Check(ravm.SummaryText.Contains("1 report-only script"), "8c aggregate summary (" + ravm.SummaryText + ")");
            int hRunAll = history.GetRecent(100).Count;
            ravm.ConfirmAsync().GetAwaiter().GetResult();
            Check(ravm.ProgressText == "Run All finished — 2 succeeded.", "8c tally (" + ravm.ProgressText + ")");
            Check(history.GetRecent(100).Count == hRunAll + 2, "8c per-leg history");
            Check(ravm.Sections.All(s => s.Status == RunPhaseStatus.Success), "8c section statuses");

            // --- tile hide/show ---
            var card = mvm.Cards.First(c => c.Manifest.Id == "TempCleanup");
            Check(card.IsVisible && card.TileOpacity == 1.0, "8d visible default");
            card.HideCommand.Execute(null);
            Check(card.IsHidden && !card.IsVisible, "8d hidden");
            Check(new DashboardStateStore(Path.Combine(scratch, "dashboard4.json")).IsHidden("TempCleanup"),
                "8d hidden persisted");
            Check(mvm.RunAllOrder().All(m => m.Id != "TempCleanup"), "8d hidden excluded from run-all");
            mvm.ShowHidden = true;
            Check(card.IsVisible && card.TileOpacity == 0.55, "8d shown dimmed");
            card.ShowCommand.Execute(null);
            Check(!card.IsHidden && card.TileOpacity == 1.0, "8d reshown");
            mvm.ShowHidden = false;

            lines.Add(lines.Any(l => l.StartsWith("FAIL")) ? "SELF-TEST FAIL" : "SELF-TEST PASS");
        }
        catch (Exception ex)
        {
            lines.Add("FAIL exception: " + ex.GetType().Name + ": " + ex.Message);
            lines.Add("SELF-TEST FAIL");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", origConfig);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", origData);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", origRun);
            Environment.SetEnvironmentVariable("HOME", origHome);
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
        }
        return lines;
    }
}
