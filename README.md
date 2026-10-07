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

## Stage 3 (this commit) — EmptyTrash + Trash contract

Second script through the pipeline (Windows EmptyRecycleBin counterpart):

* `scripts/EmptyTrash/EmptyTrash.sh` — enumerates `info/*.trashinfo`
  (`Path=` URL-decoded, `DeletionDate=` parsed), sizes `files/<name>`, emits
  Delete/Skip dry-run items with ages, `MinAgeDays` filter (0 = everything,
  negative clamps with warning, dateless items deleted only at 0 —
  same rule as Windows), include-only subset, data-then-metadata pair
  deletion with gone-verification.
* **Correction to the scoping report:** `gio trash --empty` is a silent no-op
  on this stack (GLib 2.80: exits 0, deletes nothing — proven by experiment).
  All deletions therefore remove the `files/` + `info/` pair directly for
  whole-trash AND filtered runs; `gio` remains the contract only for
  trashing/listing/restoring. Two further platform findings: `gio trash`
  refuses sources on system-internal mounts (`/tmp` tmpfs — test items live
  under `~/.local/share`), and bash `read` with a tab delimiter drops empty
  fields (tab is IFS whitespace) — items use unit-separator (`\037`)
  delimiting so dateless entries can't shift into "20733 days old".
* `Stage3SelfTest` — against the REAL home Trash: gio-trashes 3 test items,
  backdates one `DeletionDate` 10 days, strips another's, proves dry-run
  actions (Delete/Skip/Skip), filtered + include-only deletion, a full
  MinAgeDays=0 clear with user items moved aside and restored byte-identical,
  and history insert. 11/11 PASS, trash left exactly as found.
* Verified: `SELF-TEST PASS` headless (twice, idempotent) + GUI boot.

## Stage 5 (this commit) — Downloads, Screenshots, EmptyFolder

Three file scripts through the pipeline (Windows counterparts faithful):

* `scripts/DownloadsCleanup/` — non-recursive sort/clean: `DeleteExts` →
  Trash, `Categories` ext→dest moves (`mkdir -p`, overwrite like
  `Move-Item -Force`), unrecognized → Skip. Precedence:
  IgnorePatterns (case-insensitive filename glob) > IncludeOnly >
  AdvancedRules (Ignore|Delete|MoveTo, always wins; empty-destination MoveTo
  is a validated Skip) > DeleteExts > Categories. Shared `CleanupLog.txt`
  auditing, real-run-only logging, `X deleted, Y skipped` summary.
* `scripts/ScreenshotsCleanup/` — non-recursive age deletes to Trash,
  IgnorePatterns, missing folder logs SKIPPED + exits cleanly, log lines
  carry the self-identifying `ScreenshotsCleanup …` prefix (shared log).
* `scripts/EmptyFolderCleanup/` — multi-pass recursive empty-dir removal via
  non-recursive `rmdir` (race-safe, Windows `$false` parity), permanent
  delete (empty = no data), root never removed, hidden entries count,
  IgnoreFolders (case-sensitive subtree) beats IncludeOnly, dry-run
  simulation previews parents emptied by children, full-path log lines.
* Deletes route through `gio trash` (proven headless, no session needed) —
  with one platform rule the tests caught red-handed: **gio cannot trash
  across filesystems** (`/tmp` tmpfs → Trash fails, surfaced as skip+WARN,
  never silent loss). Fixtures live under `$HOME` for this reason.
* `Stage5SelfTest` — 25 checks: exact dry-run action matrices (incl.
  rule-beats-delete-list and pattern-beats-rule), real FS outcomes, Trash
  landing via gio, log content, include-only narrowing, missing/corrupt
  configs per script, history rows. `SELF-TEST PASS` (all suites 2–5 green
  together), user Trash/Downloads/Pictures left byte-identical.

## Stage 6 (this commit) — SoftwareInventory, SystemHealthReport (all 8 scripts)

The two read-only reports (no dry-run contract — asserted via
`supportsDryRun=false` instead):

* `scripts/SoftwareInventory/` — dpkg-query (installed only, sorted) + snap +
  flatpak appendices; Name/Version/Arch/Source columns (Publisher/InstallDate
  have no Linux equivalent, not emulated); B6 output validation (folder target
  refused); `N packages: D dpkg, S snap, F flatpak` summary.
* `scripts/SystemHealthReport/` — same single-file Catppuccin Mocha/Latte
  shell (cards, sidebar, fade/observer JS, theme toggle, footer) with
  config-driven palettes; collectors all Linux-native (`/proc`, `lscpu`,
  `df`, `ip -j`, `ps`, per-boot `journalctl -p`, `apt` pending count,
  `sensors`, DMI strings); updates show *pending* apt count (no last-check
  COM equivalent — documented); mascots embedded only when the PNGs resolve.
* `Stage6SelfTest` — 15 checks: real runs with content assertions (bash
  listed, 10 section ids, hostname), missing/corrupt configs, output-is-dir
  refusal, history rows. Default-path side effects (missing config writes the
  real user locations) are snapshotted and swept. `SELF-TEST PASS`, all
  suites 2–6 green together, user state byte-identical.

## Stage 7a (this commit) — systemd scheduling engine

Unattended runs via user timers (no dashboard UI yet — that’s 7b):

* `SystemdScheduleService` — one tier only (no Interactive/S4U split):
  writes `~/.config/systemd/user/scriptsuite-<id>.service/.timer`,
  `ExecStart=<binary> --scheduled-run <id>`, `daemon-reload` +
  `enable --now`, verified via `is-enabled`. Dialog-model mapping:
  daily/weekly-at-time → `OnCalendar` (wall-clock exact); hourly and N>1
  day/week intervals → `OnUnitActiveSec` + `Persistent` (interval measured
  from enable — documented compromise vs Task Scheduler, `SummaryFor()`
  words it honestly). Timeouts mirror Windows (5 s query, 20 s register,
  kill-on-timeout). Elevated scripts scheduled as timers fail loudly at
  execution (EUID guard → `Failed` row) — no silent privileged background
  runs; the dashboard will warn at schedule time.
* `Program --scheduled-run` — headless execute + `ScheduledRuns` insert
  (never manual `RunHistory`); single-instance via raw-fd `flock`
  (`LOCK_EX|LOCK_NB`, busy → `SkippedBusy` row, never queued). Raw fds are
  load-bearing: .NET `FileStream` enforces `FileShare` with its own flock
  on Unix, so opening the lock file the managed way throws *before* the
  explicit lock ever runs (found empirically). Exit 0 on
  Success/Warning/SkippedBusy, 1 on Failed/unknown id (failed units stay
  visible in systemd).
* `Stage7aSelfTest` — 14 checks: register/list/unregister against the live
  user systemd, `ScheduleStore` round-trip, direct run (row + FS proof +
  history separation), externally-held lock → `SkippedBusy` with target
  untouched, a **genuine `OnActiveSec` timer firing** into the full
  binary→script→history path, unknown-id exit 1. `SELF-TEST PASS`, all
  suites 2–7a green together, no timers/scratch/residue left behind.

## Layout (mirrors windows-scripts for reviewability)

```
src/ScriptSuite/
  Models/ Services/ ViewModels/ Views/
  Manifests/ DefaultConfigs/ Themes/
scripts/<id>/<id>.sh   # Stage 2+
packaging/             # Stage 7 (.deb, polkit, .desktop)
```
