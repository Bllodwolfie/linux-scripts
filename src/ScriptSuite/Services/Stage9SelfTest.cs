using ScriptSuite.ViewModels;

namespace ScriptSuite.Services;

/// <summary>Stage 9 proofs: settings form defaults + auto-save (text-splice
/// preserves unedited keys), Undo, MinAgeDays blocking validation, path
/// warn-but-save, list add/duplicate/remove, pattern lists, corrupt-config
/// fallback, Advanced Rules (add/overlap/blocking MoveTo/no-persist/remove),
/// path expansion parity, FlushPending, and the wizard marker + step list.
/// Same scratch-XDG discipline as Stage 8; ends with SELF-TEST PASS/FAIL.</summary>
public static class Stage9SelfTest
{
    public static List<string> Run()
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + label);

        string? origConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? origData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? origRun = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? origHome = Environment.GetEnvironmentVariable("HOME");
        string scratch = Path.Combine(Path.GetTempPath(), "scriptsuite_stage9_" + Guid.NewGuid().ToString("N")[..8]);
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
            var config = new ScriptConfigService();

            // --- form load + defaults ---
            var dlManifest = catalog.Find("DownloadsCleanup")!;
            var form = new SettingsFormViewModel(dlManifest, config);
            Check(form.Fields.Count == 4, "9 form fields");
            Check(form.Fields[0].TextValue == "$HOME/Downloads" && form.Fields[1].IntText == "7",
                "9 manifest defaults");
            Check(!form.CanUndo, "9 no undo initially");

            // --- text edit + splice preservation + undo ---
            form.Fields[0].TextValue = "$HOME/Downloads2";
            form.Fields[0].CommitPending();
            Check(config.Load("DownloadsCleanup").TryGetPropertyValue("SourceDir", out var sv)
                && sv!.GetValue<string>() == "$HOME/Downloads2", "9 text saved");
            string rawDl = File.ReadAllText(AppPaths.ConfigPathFor("DownloadsCleanup"));
            Check(rawDl.Contains("\"Categories\"") && rawDl.Contains("\"AdvancedRules\""),
                "9 splice preserves other keys");
            Check(form.CanUndo, "9 can undo after edit");
            form.Undo();
            Check(form.Fields[0].TextValue == "$HOME/Downloads"
                && config.Load("DownloadsCleanup")["SourceDir"]!.GetValue<string>() == "$HOME/Downloads",
                "9 undo restores");

            // --- MinAgeDays blocking validation ---
            var trashManifest = catalog.Find("EmptyTrash")!;
            var trashForm = new SettingsFormViewModel(trashManifest, config);
            var minAge = trashForm.Fields[0];
            minAge.IntText = "-3";
            Check(minAge.ValidationMessage == "Must be >= 0", "9 minage message");
            minAge.CommitPending();
            Check(config.Load("EmptyTrash")["MinAgeDays"]!.GetValue<int>() == 0, "9 minage not saved");
            minAge.StepDown();
            Check(minAge.IntText == "0" && config.Load("EmptyTrash")["MinAgeDays"]!.GetValue<int>() == 0,
                "9 stepper blocked at zero");
            minAge.StepUp();
            Check(minAge.IntText == "1" && config.Load("EmptyTrash")["MinAgeDays"]!.GetValue<int>() == 1,
                "9 stepper saves");
            trashForm.Undo();
            Check(minAge.IntText == "0", "9 int undo");

            // --- path warn-but-save ---
            form.Fields[0].TextValue = "relative/path";
            form.Fields[0].CommitPending();
            Check(config.Load("DownloadsCleanup")["SourceDir"]!.GetValue<string>() == "relative/path"
                && form.Fields[0].HasPathWarning, "9 path warn-but-save");
            form.Fields[0].SetPath(Path.Combine(scratch, "home"));
            Check(!form.Fields[0].HasPathWarning, "9 existing path no warning");
            form.Undo(); form.Undo();

            // --- lists ---
            var delExts = form.Fields.First(f => f.Field.Name == "DeleteExts");
            delExts.NewItemText = ".mytest";
            delExts.AddNewItem();
            Check(delExts.Items.Contains(".mytest")
                && config.Load("DownloadsCleanup")["DeleteExts"]!.AsArray().Any(n => n!.GetValue<string>() == ".mytest"),
                "9 list add");
            delExts.NewItemText = ".mytest";
            delExts.AddNewItem();
            Check(delExts.Items.Count(x => x == ".mytest") == 1, "9 list duplicate guarded");
            delExts.RemoveItem(".mytest");
            Check(!delExts.Items.Contains(".mytest"), "9 list remove");
            var ignore = form.Fields.First(f => f.Field.Name == "IgnorePatterns");
            ignore.NewItemText = "pinned_*";
            ignore.AddNewItem();
            Check(config.Load("DownloadsCleanup")["IgnorePatterns"]!.AsArray().Any(n => n!.GetValue<string>() == "pinned_*"),
                "9 pattern add");
            ignore.RemoveItem("pinned_*");

            // --- corrupt fallback (Windows parity: brace-led rot never self-heals) ---
            File.WriteAllText(AppPaths.ConfigPathFor("EmptyTrash"), "{corrupt!!!");
            var corruptForm = new SettingsFormViewModel(trashManifest, config);
            Check(corruptForm.Fields[0].IntText == "0", "9 corrupt defaults");
            corruptForm.Fields[0].StepUp();
            Check(corruptForm.Fields[0].IntText == "1", "9 corrupt holds in memory");
            Check(File.ReadAllText(AppPaths.ConfigPathFor("EmptyTrash")).Contains("\"MinAgeDays\": 1"),
                "9 corrupt edit lands in file text");
            Check(new SettingsFormViewModel(trashManifest, config).Fields[0].IntText == "0",
                "9 corrupt fresh form still falls back");
            File.WriteAllText(AppPaths.ConfigPathFor("EmptyTrash"), "{\"MinAgeDays\": 0}");

            // --- Advanced Rules ---
            var adv = new DownloadsAdvancedRulesViewModel(config);
            Check(adv.Rules.Count == 0, "9 adv empty default");
            adv.NewExtension = ".iso";
            adv.NewAction = "Ignore";
            adv.AddNew();
            Check(adv.Rules.Count == 1
                && config.Load("DownloadsCleanup")["AdvancedRules"]!.AsObject().ContainsKey(".iso"),
                "9 adv add ignore");
            adv.NewExtension = ".psd";
            adv.NewAction = "MoveTo";
            adv.NewDestination = "";
            Check(!adv.CanAdd && adv.NewValidationMessage == "Destination required for MoveTo",
                "9 adv moveto blocked message");
            adv.AddNew();
            Check(!config.Load("DownloadsCleanup")["AdvancedRules"]!.AsObject().ContainsKey(".psd"),
                "9 adv moveto never persisted blank");
            adv.NewDestination = Path.Combine(scratch, "psd-out");
            adv.AddNew();
            Check(config.Load("DownloadsCleanup")["AdvancedRules"]![".psd"]!["Destination"]!.GetValue<string>()
                == Path.Combine(scratch, "psd-out"), "9 adv moveto saved");
            var row = adv.Rules.First(r => r.Extension == ".psd");
            row.Destination = "";
            Check(row.HasValidationError, "9 adv row validation");
            adv.NotifyRowChanged(row);
            Check(config.Load("DownloadsCleanup")["AdvancedRules"]![".psd"]!["Destination"]!.GetValue<string>()
                == Path.Combine(scratch, "psd-out"), "9 adv invalid row not persisted");
            row.Destination = Path.Combine(scratch, "psd-out2");
            adv.NotifyRowChanged(row);
            Check(config.Load("DownloadsCleanup")["AdvancedRules"]![".psd"]!["Destination"]!.GetValue<string>()
                == Path.Combine(scratch, "psd-out2"), "9 adv row saved");
            adv.NewExtension = ".zip";
            Check(adv.NewOverlapHint.Contains("Extensions to delete"), "9 adv overlap hint");
            adv.Remove(row);
            adv.Remove(adv.Rules.First(r => r.Extension == ".iso"));
            Check(adv.Rules.Count == 0
                && !config.Load("DownloadsCleanup")["AdvancedRules"]!.AsObject().Any(), "9 adv remove");

            // --- path expansion parity with common.sh ---
            string home = Path.Combine(scratch, "home");
            Check(PathEnv.ExpandUserPath("~/x") == Path.Combine(home, "x"), "9 expand tilde");
            Check(PathEnv.ExpandUserPath("$HOME/x") == Path.Combine(home, "x"), "9 expand $HOME");
            Check(PathEnv.ExpandUserPath("${HOME}/x") == Path.Combine(home, "x"), "9 expand ${HOME}");
            Check(PathEnv.ExpandUserPath("/abs/x") == "/abs/x", "9 expand passthrough");

            // --- FlushPending (window-close path) ---
            form.Fields[1].IntText = "14";
            form.FlushPending();
            Check(config.Load("DownloadsCleanup")["CutoffDays"]!.GetValue<int>() == 14, "9 flush saves");

            // --- wizard marker ---
            string wizPath = AppPaths.WizardStatePath;
            if (File.Exists(wizPath)) File.Delete(wizPath);
            Check(!WizardStateStore.IsCompleted(), "9 wizard fresh incomplete");
            WizardStateStore.MarkCompleted();
            Check(WizardStateStore.IsCompleted()
                && File.ReadAllText(wizPath).Contains("\"wizardCompleted\": true"), "9 wizard completed");
            File.WriteAllText(wizPath, "{broken");
            Check(!WizardStateStore.IsCompleted(), "9 wizard corrupt incomplete");
            File.Delete(wizPath);

            // --- wizard steps: every script with fields, in catalog order ---
            Check(string.Join(",", catalog.All.Where(m => m.Fields.Count > 0).Select(m => m.Id))
                == "TempCleanup,DownloadsCleanup,ScreenshotsCleanup,EmptyFolderCleanup,EmptyTrash,ClearJournal,SoftwareInventory,SystemHealthReport",
                "9 wizard steps");

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
