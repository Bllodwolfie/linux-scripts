#!/usr/bin/env bash
# ScreenshotsCleanup: old screenshots trashed, fresh kept, log written.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd gio || exit 0
S="$(mktemp -d "$T_SCRATCH/shots-XXXXXX")"
make_old "$S/shot1.png" 5
make_fresh "$S/shot2.png"
CFG=$(make_config ScreenshotsCleanup '{
  "TargetFolder": "'"$S"'", "CutoffDays": 1,
  "LogDir": "'"$T_SCRATCH"'/logs", "LogFile": "CleanupLog.txt",
  "IgnorePatterns": []}')

run_case ScreenshotsCleanup --config-path "$CFG" --dry-run
assert_eq "shots-dryrun-exit" "0" "$T_RC"
assert_eq "shots-dryrun-one-delete" "1" "$(dryrun_count Delete)"
assert_file_exists "shots-dryrun-changes-nothing" "$S/shot1.png"

run_case ScreenshotsCleanup --config-path "$CFG"
assert_eq "shots-real-exit" "0" "$T_RC"
assert_file_gone "shots-real-trashes-old" "$S/shot1.png"
assert_file_exists "shots-real-keeps-fresh" "$S/shot2.png"
assert_contains "shots-real-summary" "$(cat "$T_OUT")" "ScreenshotsCleanup:"
