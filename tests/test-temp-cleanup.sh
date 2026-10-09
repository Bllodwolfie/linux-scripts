#!/usr/bin/env bash
# TempCleanup: dry-run lists, real run deletes old only, ignore folders win.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

T="$(mktemp -d "$T_SCRATCH/target-XXXXXX")"
make_old "$T/old.tmp" 5
make_fresh "$T/fresh.tmp"
mkdir -p "$T/keep"
make_old "$T/keep/old-inside.tmp" 5
CFG=$(make_config TempCleanup '{"TargetFolder": "'"$T"'", "CutoffDays": 1, "IgnoreFolders": ["'"$T"'/keep"]}')

run_case TempCleanup --config-path "$CFG" --dry-run
assert_eq "temp-dryrun-exit" "0" "$T_RC"
assert_eq "temp-dryrun-one-delete" "1" "$(dryrun_count Delete)"
assert_contains "temp-dryrun-names-old" "$(cat "$T_OUT")" "old.tmp"
assert_file_exists "temp-dryrun-changes-nothing" "$T/old.tmp"

printf '%s\n' "$T/old.tmp" > "$T_SCRATCH/include.txt"
run_case TempCleanup --config-path "$CFG" --include-only-file "$T_SCRATCH/include.txt"
assert_eq "temp-realon-include-exit" "0" "$T_RC"
assert_file_gone "temp-realon-include-deletes-listed" "$T/old.tmp"

make_old "$T/old2.tmp" 5
run_case TempCleanup --config-path "$CFG"
assert_eq "temp-real-exit" "0" "$T_RC"
assert_file_gone "temp-real-deletes-old" "$T/old2.tmp"
assert_file_exists "temp-real-keeps-fresh" "$T/fresh.tmp"
assert_file_exists "temp-real-keeps-ignored-subtree" "$T/keep/old-inside.tmp"
assert_contains "temp-real-summary" "$(cat "$T_OUT")" "Temp cleanup:"
