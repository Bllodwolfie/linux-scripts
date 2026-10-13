#!/usr/bin/env bash
# A-3: find/stat error handling. Swallowed directory-walk errors must surface
# as WARN (Warning outcome), never as silent Success; unstattable files must
# be reported, not silently skipped; an unreadable dir must never read as
# empty (EmptyFolder would rmdir it away). Scratch dirs only; permissions
# restored at the end so the runner can clean up.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd gio jq || exit 0
source "$(repo_root)/scripts/lib/common.sh"

R="$T_SCRATCH/walk"; mkdir -p "$R"

# --- Unit contract: direct helper checks (deterministic, no races) ---
if file_detail /nonexistent-walk-probe >/dev/null 2>&1; then
    t_fail "walk-unit-filedetail-rc" "file_detail on missing path returned 0"
else
    t_pass "walk-unit-filedetail-rc"
fi
is_old_file /nonexistent-walk-probe 7 "$(date +%s)"
rc=$?
assert_eq "walk-unit-isold-rc2" "2" "$rc"

# --- TempCleanup: unreadable subdir -> partial-walk WARN, normal work done ---
T1="$R/temp"; mkdir -p "$T1/noperm"
make_old "$T1/old.tmp" 5
make_old "$T1/noperm/hidden.tmp" 5
chmod 000 "$T1/noperm"
TCFG=$(make_config WalkTemp '{"TargetFolder":"'"$T1"'","CutoffDays":1,"IgnoreFolders":[]}')
run_case TempCleanup --config-path "$TCFG" --dry-run
assert_contains "walk-temp-dry-warn" "$(cat "$T_OUT")" "Partial walk"
assert_contains "walk-temp-dry-delete" "$(cat "$T_OUT")" "old.tmp"

run_case TempCleanup --config-path "$TCFG"
assert_eq "walk-temp-exit" "0" "$T_RC"
assert_contains "walk-temp-partial-warn" "$(cat "$T_OUT")" "Partial walk"
assert_file_gone "walk-temp-deletes-visible" "$T1/old.tmp"

# --- ScreenshotsCleanup: unreadable target -> partial-walk WARN ---
S1="$R/shots"; mkdir -p "$S1"
make_old "$S1/old.png" 5
chmod 000 "$S1"
SCFG=$(make_config WalkShots '{"TargetFolder":"'"$S1"'","CutoffDays":1,"IgnorePatterns":[],"LogDir":"'"$R"'/logs","LogFile":"shots.log"}')
run_case ScreenshotsCleanup --config-path "$SCFG"
assert_eq "walk-shots-exit" "0" "$T_RC"
assert_contains "walk-shots-partial-warn" "$(cat "$T_OUT")" "Partial walk"
chmod 755 "$S1"

# --- DownloadsCleanup: unreadable target -> partial-walk WARN ---
D1="$R/dl"; mkdir -p "$D1"
make_old "$D1/old.zip" 5
chmod 000 "$D1"
DCFG=$(make_config WalkDl '{"SourceDir":"'"$D1"'","CutoffDays":1,"LogDir":"'"$R"'/logs","LogFile":"dl.log","DeleteExts":[".zip"],"IgnorePatterns":[],"AdvancedRules":{},"Categories":{}}')
run_case DownloadsCleanup --config-path "$DCFG"
assert_eq "walk-dl-exit" "0" "$T_RC"
assert_contains "walk-dl-partial-warn" "$(cat "$T_OUT")" "Partial walk"
chmod 755 "$D1"

# --- EmptyFolderCleanup: unreadable EMPTY dir is kept (never "empty") + WARN ---
F1="$R/folders"; mkdir -p "$F1/keep" "$F1/locked"
chmod 000 "$F1/locked"
FCFG=$(make_config WalkFolders '{"TargetFolder":"'"$F1"'"}')
run_case EmptyFolderCleanup --config-path "$FCFG"
assert_eq "walk-folders-exit" "0" "$T_RC"
assert_contains "walk-folders-partial-warn" "$(cat "$T_OUT")" "Partial walk"
assert_file_exists "walk-folders-keeps-unreadable" "$F1/locked"
assert_file_gone "walk-folders-deletes-verified-empty" "$F1/keep"
chmod 755 "$F1/locked"

chmod -R u+rwx "$R" 2>/dev/null || true
