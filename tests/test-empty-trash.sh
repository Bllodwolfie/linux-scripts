#!/usr/bin/env bash
# EmptyTrash: trashinfo-driven listing, age filter, permanent delete of pairs.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd gio || exit 0
# Private Trash: other test files trash into the shared scratch Trash during
# the same run.sh invocation — enumerate only our own pair.
export XDG_DATA_HOME="$T_SCRATCH/trashtest-data"
mkdir -p "$XDG_DATA_HOME"
TRASH="$XDG_DATA_HOME/Trash"
mkdir -p "$TRASH/files" "$TRASH/info"

seed_trashed() { # name days-old
    echo "data" > "$TRASH/files/$1"
    touch -d "$2 days ago" "$TRASH/files/$1"
    printf '[Trash Info]\nPath=/home/somewhere/%s\nDeletionDate=%s\n' "$1" \
        "$(date -d "$2 days ago" +%Y-%m-%dT%H:%M:%S)" > "$TRASH/info/$1.trashinfo"
}

seed_trashed old.txt 5
seed_trashed fresh.txt 0
CFG=$(make_config EmptyTrash '{"MinAgeDays": 1}')

run_case EmptyTrash --config-path "$CFG" --dry-run
assert_eq "trash-dryrun-exit" "0" "$T_RC"
assert_eq "trash-dryrun-one-delete" "1" "$(dryrun_count Delete)"
assert_contains "trash-dryrun-names-old" "$(cat "$T_OUT")" "old.txt"
assert_file_exists "trash-dryrun-changes-nothing" "$TRASH/files/old.txt"

run_case EmptyTrash --config-path "$CFG"
assert_eq "trash-real-exit" "0" "$T_RC"
assert_file_gone "trash-real-deletes-file" "$TRASH/files/old.txt"
assert_file_gone "trash-real-deletes-info" "$TRASH/info/old.txt.trashinfo"
assert_file_exists "trash-real-keeps-fresh-file" "$TRASH/files/fresh.txt"
assert_file_exists "trash-real-keeps-fresh-info" "$TRASH/info/fresh.txt.trashinfo"
assert_contains "trash-real-summary" "$(cat "$T_OUT")" "Trash:"

CFG0=$(make_config EmptyTrash0 '{"MinAgeDays": 0}')
run_case EmptyTrash --config-path "$CFG0" --dry-run
assert_eq "trash-zero-means-all" "1" "$(dryrun_count Delete)"

# S1: a checked Skip row (younger than MinAgeDays) must survive a confirmed
# subset run — the real branch re-derives the age verdict instead of trusting
# the selection (was: permanently deleted despite "would be skipped").
seed_trashed old2.txt 5
seed_trashed young.txt 0
CFG_INC=$(make_config EmptyTrashInc '{"MinAgeDays": 1}')
run_case EmptyTrash --config-path "$CFG_INC" --dry-run
grep '^DRYRUN: ' "$T_OUT" | sed 's/^DRYRUN: //' | jq -r .target > "$T_SCRATCH/trash.targets"
run_case EmptyTrash --config-path "$CFG_INC" --include-only-file "$T_SCRATCH/trash.targets"
assert_eq "trash-inc-exit" "0" "$T_RC"
assert_file_gone "trash-inc-deletes-old" "$TRASH/files/old2.txt"
assert_file_gone "trash-inc-deletes-old-info" "$TRASH/info/old2.txt.trashinfo"
assert_file_exists "trash-inc-keeps-young-skip" "$TRASH/files/young.txt"
assert_file_exists "trash-inc-keeps-young-info" "$TRASH/info/young.txt.trashinfo"
assert_contains "trash-inc-skip-warn" "$(cat "$T_OUT")" "younger than"
