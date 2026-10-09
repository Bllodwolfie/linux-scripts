#!/usr/bin/env bash
# DownloadsCleanup: delete/move/skip routing, ignore patterns, advanced rules
# (incl. empty-MoveTo skip), include-only narrowing, shared log on real runs.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd gio jq || exit 0
D="$(mktemp -d "$T_SCRATCH/dl-XXXXXX")"
make_old "$D/old.zip" 5
make_old "$D/song.mp3" 5
make_old "$D/pinned_old.zip" 5
make_old "$D/photo.iso" 5
make_fresh "$D/new.zip"
mkdir -p "$D/sub"
make_old "$D/sub/nested.zip" 5
CFG=$(make_config DownloadsCleanup '{
  "SourceDir": "'"$D"'",
  "CutoffDays": 1,
  "LogDir": "'"$T_SCRATCH"'/logs",
  "LogFile": "CleanupLog.txt",
  "DeleteExts": [".zip"],
  "IgnorePatterns": ["pinned_*"],
  "AdvancedRules": {
    ".iso": {"Action": "Ignore"},
    ".mp3": {"Action": "MoveTo", "Destination": "'"$D"'/sorted"}
  },
  "Categories": {"'"$D"'/sorted": [".mp3"]}
}')

run_case DownloadsCleanup --config-path "$CFG" --dry-run
assert_eq "dl-dryrun-exit" "0" "$T_RC"
assert_eq "dl-dryrun-delete" "1" "$(dryrun_count Delete)"
assert_eq "dl-dryrun-move" "1" "$(dryrun_count Move)"
assert_contains "dl-dryrun-skip-ignored" "$(cat "$T_OUT")" "pinned_old.zip"
assert_contains "dl-dryrun-skip-adv-ignore" "$(cat "$T_OUT")" "photo.iso"
assert_contains "dl-dryrun-move-target" "$(cat "$T_OUT")" "sorted"
assert_file_exists "dl-dryrun-changes-nothing" "$D/old.zip"
if grep '^DRYRUN: ' "$T_OUT" | grep -q "nested.zip"; then t_fail "dl-dryrun-ignores-subfolders" "nested file listed"; else t_pass "dl-dryrun-ignores-subfolders"; fi

# empty-MoveTo destination degrades to Skip, never executes
CFG2=$(make_config DownloadsCleanup2 '{
  "SourceDir": "'"$D"'", "CutoffDays": 1,
  "LogDir": "'"$T_SCRATCH"'/logs", "LogFile": "CleanupLog.txt",
  "DeleteExts": [], "IgnorePatterns": [],
  "AdvancedRules": {".mp3": {"Action": "MoveTo", "Destination": ""}},
  "Categories": {}}')
run_case DownloadsCleanup --config-path "$CFG2" --dry-run
assert_contains "dl-emptymoveto-skips" "$(cat "$T_OUT")" "missing destination"

# include-only narrows the real run to listed targets
printf '%s\n' "$D/old.zip" > "$T_SCRATCH/include.txt"
run_case DownloadsCleanup --config-path "$CFG" --include-only-file "$T_SCRATCH/include.txt"
assert_eq "dl-include-exit" "0" "$T_RC"
assert_file_gone "dl-include-deletes-listed" "$D/old.zip"
assert_file_exists "dl-include-spares-unlisted" "$D/song.mp3"

# full real run: fresh deletable trashed, mp3 moved, ignored kept, log written
make_old "$D/extra2.zip" 5
run_case DownloadsCleanup --config-path "$CFG"
assert_eq "dl-real-exit" "0" "$T_RC"
assert_file_gone "dl-real-trashes-zip" "$D/extra2.zip"
assert_contains "dl-real-logs-trashed" "$(cat "$T_SCRATCH/logs/CleanupLog.txt")" "extra2.zip"
assert_file_exists "dl-real-keeps-pinned" "$D/pinned_old.zip"
assert_file_exists "dl-real-keeps-adv-ignored" "$D/photo.iso"
assert_file_exists "dl-real-moves-mp3" "$D/sorted/song.mp3"
assert_file_exists "dl-real-keeps-fresh" "$D/new.zip"
assert_file_exists "dl-real-writes-log" "$T_SCRATCH/logs/CleanupLog.txt"
assert_contains "dl-real-summary" "$(cat "$T_OUT")" "DownloadsCleanup:"
