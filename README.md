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

Requires .NET 8 SDK. No `.deb`, no scripts, no elevation, no scheduling yet — those are Stages 2+.

## Layout (mirrors windows-scripts for reviewability)

```
src/ScriptSuite/
  Models/ Services/ ViewModels/ Views/
  Manifests/ DefaultConfigs/ Themes/
scripts/<id>/<id>.sh   # Stage 2+
packaging/             # Stage 7 (.deb, polkit, .desktop)
```
