# linux-scripts

Linux (KDE Neon) counterpart of [windows-scripts](https://github.com/Bllodwolfie/windows-scripts) — confirmed scope items #1 (per-script Linux mapping) and #2 (Avalonia architecture) from the scoping report.

* **GUI:** Avalonia (C# / .NET), `src/ScriptSuite` (`net8.0` for Stage 1; retarget `net10.0` when the SDK lands — never `net*-desktop`)
* **Scripts:** idiomatic Bash rewrites in `scripts/` (Stage 2+)
* **Scope:** 8 scripts — `RestorePoint` dropped; `EmptyRecycleBin` → `EmptyTrash`, `ClearEventLogs` → `ClearJournal`

## Stage 1 (this commit) — scaffold only

Proves the pipeline boots before any script execution:

* Avalonia MVVM app boots on KDE Neon (Wayland)
* XDG paths (`AppPaths`: `~/.config/scriptsuite`, `~/.local/share/scriptsuite`, `$XDG_RUNTIME_DIR`)
* `ManifestCatalog` loads shipped `Manifests/*.json` (1 manifest: TempCleanup)
* `EnsureConfigsSeeded` copies `DefaultConfigs/` → `~/.config/scriptsuite/configs/`
* `RunHistoryStore` initializes `history.db` (RunHistory + ScheduledRuns, WAL)
* `ThemeStore` reads `theme.json` (Dark default); stub Mocha/Latte tokens in `Themes/`
* Ported verbatim: `Models/*`, `ScriptConfigService`, `RunHistoryStore`, `ThemeStore`

## Build

```bash
dotnet build src/ScriptSuite -c Release -r linux-x64
dotnet run --project src/ScriptSuite
```

Requires .NET 8 SDK. No `.deb`, no elevation, no scheduling yet — those are Stages 4+.

## Stage 2 (this commit) — TempCleanup end-to-end

First script through the full pipeline (mirrors how TempCleanup was the proof
case on Windows):

* `scripts/lib/common.sh` — shared helpers: `INFO:/WARN:/ERROR:/DRYRUN:` log
  protocol, jq config readers (`cfg_str/cfg_int/cfg_list`), canonical paths,
  dangerous-root guard. Requires `jq` (`sudo apt install jq`).
* `scripts/TempCleanup/TempCleanup.sh` — idiomatic Bash: `find -mmin` age
  filter, files-only, `IgnoreFolders` wins over age and `--include-only`,
  **permanent `rm`** (never Trash — `/tmp` is tmpfs), corrupt config warns +
  falls back, missing target warns + `0 deleted, 0 skipped`, dangerous roots
  (`/`, `/home`, `$HOME`, `/etc`, …) refused with exit 1.
* `BashScriptExecutor` — spawns `/bin/bash`, parses `DRYRUN:` JSON items,
  Failed/Warning/Success precedence identical to Windows `ScriptExecutor`.
* Scaffold UI gained a self-test button running dry-run → real run →
  include-only → history insert against a scratch dir (never `/tmp`).
* Verified: `SELF-TEST PASS` (9/9 checks) headless + GUI boot on Plasma.

## Layout (mirrors windows-scripts for reviewability)

```
src/ScriptSuite/
  Models/ Services/ ViewModels/ Views/
  Manifests/ DefaultConfigs/ Themes/
scripts/<id>/<id>.sh   # Stage 2+
packaging/             # Stage 7 (.deb, polkit, .desktop)
```
