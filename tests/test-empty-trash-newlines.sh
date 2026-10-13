#!/usr/bin/env bash
# A-1/A-2: newline filenames must not split rows (false counts, orphaned
# pairs); deletion failures must be labeled by cause, never generic
# "locked". Private Trash under scratch, seeded directly (trashinfo format).
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd jq || exit 0
export XDG_DATA_HOME="$T_SCRATCH/trashnl-data"
TRASH="$XDG_DATA_HOME/Trash"
mkdir -p "$TRASH/files" "$TRASH/info"

seed_pair() { # files-name pathenc deletiondate
    printf 'data' > "$TRASH/files/$1"
    printf '[Trash Info]\nPath=%s\nDeletionDate=%s\n' "$2" "$3" > "$TRASH/info/$1.trashinfo"
}
OLD_DT=$(date -d '10 days ago' +%Y-%m-%dT%H:%M:%S)
seed_pair 'plain.txt' '/home/somewhere/plain.txt' "$OLD_DT"
seed_pair 'nl-target.txt' 'with%0Anewline.txt' "$OLD_DT"
seed_pair $'we\nird.txt' 'we%0Aird.txt' "$OLD_DT"

CFG=$(make_config TrashNL '{"MinAgeDays": 1}')

# --- A-1: dry-run rows stay whole ---
run_case EmptyTrash --config-path "$CFG" --dry-run
assert_eq "trashnl-dryrun-exit" "0" "$T_RC"
assert_eq "trashnl-dryrun-rows" "3" "$(dryrun_count)"
assert_eq "trashnl-dryrun-deletes" "3" "$(dryrun_count Delete)"
assert_contains "trashnl-dryrun-target-intact" "$(cat "$T_OUT")" 'with\nnewline'
assert_contains "trashnl-dryrun-base-intact" "$(cat "$T_OUT")" 'we\nird'

# --- A-1: real run deletes exactly the 3 pairs, counts honestly ---
run_case EmptyTrash --config-path "$CFG"
assert_eq "trashnl-real-exit" "0" "$T_RC"
assert_contains "trashnl-real-count" "$(cat "$T_OUT")" "Trash: 3 item(s) permanently deleted."
assert_file_gone "trashnl-real-plain-gone" "$TRASH/files/plain.txt"
assert_file_gone "trashnl-real-target-gone" "$TRASH/files/nl-target.txt"
assert_file_gone "trashnl-real-base-gone" "$TRASH/files/"$'we\nird.txt'
assert_file_gone "trashnl-real-no-orphan-info" "$TRASH/info/"$'we\nird.txt.trashinfo'
if [[ -z "$(ls -A "$TRASH/files")" && -z "$(ls -A "$TRASH/info")" ]]; then
    t_pass "trashnl-real-no-residue"
else
    t_fail "trashnl-real-no-residue" "leftovers: $(ls "$TRASH/files" "$TRASH/info" | tr '\n' ' ')"
fi

# --- A-2: undeletable pair is labeled by cause, never "locked" ---
seed_pair 'prot.txt' '/home/somewhere/prot.txt' "$OLD_DT"
chmod 555 "$TRASH/files" "$TRASH/info"
run_case EmptyTrash --config-path "$CFG"
assert_eq "trashnl-prot-exit" "0" "$T_RC"
assert_contains "trashnl-prot-cause" "$(cat "$T_OUT")" "Permission denied"
assert_no_ere "trashnl-prot-no-locked" "$T_OUT" "[Ll]ocked"
assert_file_exists "trashnl-prot-kept" "$TRASH/files/prot.txt"
chmod 755 "$TRASH/files" "$TRASH/info"
rm -rf -- "$TRASH/files" "$TRASH/info"
