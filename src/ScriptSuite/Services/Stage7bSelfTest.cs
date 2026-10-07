using ScriptSuite.Models;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Services;

/// <summary>Stage 7b proofs for the dashboard layer, all headless: card model
/// (8 scripts, order, admin flag), schedule-dialog validation parity with
/// windows-scripts (interval 1–365, HH:mm, admin risk gate), save/remove
/// through stubbed register functions, a real TempCleanup run through the
/// card's Run path (executor + RunHistory + refresh), a real systemd
/// register/unregister round-trip through the dialog's DEFAULT funcs, and
/// manual/scheduled history separation. Returns report lines ending in
/// SELF-TEST PASS or SELF-TEST FAIL.</summary>
public static class Stage7bSelfTest
{
    public static List<string> Run()
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + label);

        string binary = Path.GetFullPath(Path.Combine(AppPaths.ManifestsDir, "..", "ScriptSuite"));
        if (!File.Exists(binary))
        {
            lines.Add("FAIL runner binary not found: " + binary);
            lines.Add("SELF-TEST FAIL");
            return lines;
        }
        string binaryDir = Path.GetDirectoryName(binary)!;

        string tag = Guid.NewGuid().ToString("N")[..8];
        string schedPath = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage7b_sched_{tag}.json");
        string consentPath = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage7b_consent_{tag}.json");
        string historyPath = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage7b_hist_{tag}.db");
        string scratch = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "stage7b-run");
        // The executor resolves scripts against AppContext.BaseDirectory, so
        // the harness links the real scripts dir into its own output dir.
        string linkDir = Path.Combine(AppContext.BaseDirectory, "scripts");
        bool linkCreated = false;
        try
        {
            var catalog = new ManifestCatalog(Path.Combine(binaryDir, "Manifests"));
            var schedules = new ScheduleStore(schedPath);
            var consents = new RiskConsentStore(consentPath);
            var history = new RunHistoryStore(historyPath);

            // --- 1. card model ---
            var vm = new MainViewModel(catalog,
                new BashScriptExecutor(catalog),
                new PolkitElevationService(catalog, consents),
                history, schedules, consents, _ => "");
            string order = string.Join(",", vm.Cards.Select(c => c.Manifest.Id));
            Check(vm.Cards.Count == 8, $"dashboard builds 8 cards ({vm.Cards.Count})");
            Check(order == "TempCleanup,DownloadsCleanup,ScreenshotsCleanup,EmptyFolderCleanup,EmptyTrash,ClearJournal,SoftwareInventory,SystemHealthReport",
                "cards follow BuiltInOrder (got " + order + ")");
            Check(vm.Cards.Count(c => c.RequiresAdmin) == 1 && vm.Cards.First(c => c.RequiresAdmin).Manifest.Id == "ClearJournal",
                "only ClearJournal carries the ADMIN badge");
            Check(vm.Cards.All(c => !c.HasSchedule && c.ScheduleSummary == "" && c.LastRunText == "Never run"),
                "fresh cards show no schedule and no last run");
            Check(vm.RecentRuns.Count == 1 && vm.RecentScheduled.Count == 1,
                "empty histories show placeholders");

            var temp = catalog.Find("TempCleanup")!;
            var journal = catalog.Find("ClearJournal")!;

            // --- 2. dialog validation (stubbed register: must NOT be called) ---
            int regCalls = 0;
            Func<ScheduleEntry, (bool, string?)> stubReg = e => { regCalls++; return (true, null); };
            foreach (var bad in new[] { "0", "abc", "366", "" })
            {
                bool? closed = null;
                var d = new ScheduleDialogViewModel(temp, schedules, consents, stubReg);
                d.RequestClose += r => closed = r;
                d.IntervalText = bad;
                d.Save();
                if (!string.IsNullOrEmpty(d.ErrorText) && closed is null) continue;
                Check(false, $"interval '{bad}' rejected with error, dialog stays open");
                goto validationDone;
            }
            Check(regCalls == 0, "invalid intervals rejected without touching systemd (4 cases)");
        validationDone:;
            {
                bool? closed = null;
                var d = new ScheduleDialogViewModel(temp, schedules, consents, stubReg);
                d.RequestClose += r => closed = r;
                d.TimeText = "25:00";
                d.Save();
                Check(!string.IsNullOrEmpty(d.ErrorText) && closed is null && regCalls == 0,
                    "bad time rejected with error, dialog stays open");
            }
            {
                bool? closed = null;
                var d = new ScheduleDialogViewModel(journal, schedules, consents, stubReg);
                d.RequestClose += r => closed = r;
                d.Save();
                Check(!string.IsNullOrEmpty(d.ErrorText) && closed is null && regCalls == 0,
                    "admin schedule without risk consent refused");
                Check(d.AdminTimerWarning.Contains("cannot elevate"),
                    "admin dialog warns timers cannot elevate");
                Check(new ScheduleDialogViewModel(temp, schedules, consents, stubReg).AdminTimerWarning == "",
                    "non-admin dialog carries no elevation warning");
            }

            // --- 3. save/remove round-trip through stubs ---
            {
                bool? closed = null;
                var d = new ScheduleDialogViewModel(temp, schedules, consents, stubReg);
                d.RequestClose += r => closed = r;
                d.IntervalText = "1"; d.UnitIndex = 0; d.TimeText = "09:00";
                d.Save();
                Check(closed == true && regCalls == 1 && schedules.Get("TempCleanup")?.TimeOfDay == "09:00",
                    "valid save registers then persists the store entry");
                Check(new ScheduleDialogViewModel(temp, schedules, consents, stubReg).ShowRemove,
                    "existing schedule offers Remove");
                int unregCalls = 0;
                bool? closed2 = null;
                var d2 = new ScheduleDialogViewModel(temp, schedules, consents, stubReg,
                    _ => { unregCalls++; return (true, null); });
                d2.RequestClose += r => closed2 = r;
                d2.Remove();
                Check(closed2 == true && unregCalls == 1 && !schedules.Has("TempCleanup"),
                    "remove unregisters then drops the store entry");
                Check(!new ScheduleDialogViewModel(temp, schedules, consents, stubReg).ShowRemove,
                    "no schedule means no Remove button");
            }

            // --- 4. prefill from an existing entry ---
            schedules.Set(new ScheduleEntry { ScriptId = "TempCleanup", Unit = "Hours", Interval = 3, TimeOfDay = "07:30" });
            {
                var d = new ScheduleDialogViewModel(temp, schedules, consents, stubReg);
                Check(d.IntervalText == "3" && d.UnitIndex == 1 && d.TimeText == "07:30",
                    "dialog pre-fills interval/unit/time from the saved entry");
            }
            schedules.Remove("TempCleanup");

            // --- 5. dashboard Run path: real TempCleanup, scratch config/target ---
            if (!Directory.Exists(linkDir))
            {
                // Walk up from the built binary to the repo root (the folder
                // holding scripts/lib/common.sh) — robust to build layouts.
                string? dir = binaryDir, repoScripts = null;
                for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
                    if (File.Exists(Path.Combine(dir, "scripts", "lib", "common.sh")))
                    { repoScripts = Path.Combine(dir, "scripts"); break; }
                if (repoScripts is null)
                {
                    lines.Add("FAIL repo scripts dir not found above " + binaryDir);
                    lines.Add("SELF-TEST FAIL");
                    return lines;
                }
                Directory.CreateSymbolicLink(linkDir, repoScripts);
                linkCreated = true;
            }
            Directory.CreateDirectory(scratch);
            string oldFile = Path.Combine(scratch, "old.tmp");
            string newFile = Path.Combine(scratch, "new.tmp");
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(newFile, "new");
            File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-10));
            string scratchConfig = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage7b_cfg_{tag}.json");
            File.WriteAllText(scratchConfig, System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object> { ["TargetFolder"] = scratch, ["CutoffDays"] = 7, ["IgnoreFolders"] = new string[0] }));
            try
            {
                var vm2 = new MainViewModel(catalog,
                    new BashScriptExecutor(catalog),
                    new PolkitElevationService(catalog, consents),
                    history, schedules, consents, _ => scratchConfig);
                var card = vm2.Cards.First(c => c.Manifest.Id == "TempCleanup");
                card.RunAsync().GetAwaiter().GetResult();
                var rows = history.GetRecent(5);
                Check(!File.Exists(oldFile) && File.Exists(newFile),
                    "card Run executed the script (old gone, new kept)");
                Check(rows.Count == 1 && rows[0].ScriptId == "TempCleanup" && rows[0].Outcome == "Success"
                        && (rows[0].Summary ?? "").Contains("Temp cleanup"),
                    $"card Run recorded Success + summary in RunHistory (got '{rows.FirstOrDefault()?.Outcome}/{rows.FirstOrDefault()?.Summary}')");
                Check(card.RunStatus.Contains("Success"), "card status line reports Success");
                vm2.RefreshHistories();
                Check(card.LastRunText.Contains("Success") && vm2.RecentRuns.Any(l => l.Contains("TempCleanup") && l.Contains("Success")),
                    "refresh surfaces the run on the card and in Recent runs");
            }
            finally { TryDelete(scratchConfig); }

            // --- 6. real systemd round-trip through the dialog's DEFAULT funcs ---
            if (SystemdScheduleService.TaskExists("TempCleanup"))
            {
                lines.Add("SKIP dialog systemd round-trip (user already has a TempCleanup timer)");
            }
            else
            {
                try
                {
                    bool? closed = null;
                    var d = new ScheduleDialogViewModel(temp, schedules, consents);
                    d.RequestClose += r => closed = r;
                    d.IntervalText = "1"; d.UnitIndex = 0; d.TimeText = "09:00";
                    d.Save();
                    var tcard = new ScriptCardViewModel(temp,
                        new BashScriptExecutor(catalog),
                        new PolkitElevationService(catalog, consents),
                        history, schedules, consents, "", _ => Task.CompletedTask);
                    Check(closed == true && SystemdScheduleService.TaskExists("TempCleanup"),
                        "dialog Save enables a real user timer");
                    Check(tcard.ScheduleSummary == "◷ Daily at 09:00",
                        $"card shows the honest summary (got '{tcard.ScheduleSummary}')");
                    bool? closed2 = null;
                    var d2 = new ScheduleDialogViewModel(temp, schedules, consents);
                    d2.RequestClose += r => closed2 = r;
                    d2.Remove();
                    Check(closed2 == true && !SystemdScheduleService.TaskExists("TempCleanup") && !schedules.Has("TempCleanup"),
                        "dialog Remove disables the timer and clears the store");
                }
                finally
                {
                    SystemdScheduleService.Unregister("TempCleanup");
                    schedules.Remove("TempCleanup");
                }
            }

            // --- 7. history separation ---
            history.InsertScheduled("TempCleanup", DateTime.Now, DateTime.Now, DateTime.Now,
                "Success", "probe", "timer");
            var vm3 = new MainViewModel(catalog,
                new BashScriptExecutor(catalog),
                new PolkitElevationService(catalog, consents),
                history, schedules, consents, _ => "");
            Check(vm3.RecentScheduled.Any(l => l.Contains("probe")),
                "scheduled rows surface under Scheduled runs");
            Check(vm3.RecentRuns.All(l => !l.Contains("probe")),
                "scheduled rows never leak into Recent runs");

            lines.Add(lines.Any(l => l.StartsWith("FAIL")) ? "SELF-TEST FAIL" : "SELF-TEST PASS");
        }
        finally
        {
            TryDelete(schedPath);
            TryDelete(consentPath);
            TryDelete(historyPath);
            TryDelete(historyPath + "-wal");
            TryDelete(historyPath + "-shm");
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
            try { if (linkCreated && Directory.Exists(linkDir)) Directory.Delete(linkDir); } catch { }
        }
        return lines;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
