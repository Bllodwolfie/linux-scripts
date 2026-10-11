#!/usr/bin/env bash
# ClearJournal: DRY-RUN ONLY — never vacuum the real journal in tests.
# Asserts Clear/Skip items, exit 0, and zero side effects (no export files,
# no backup dir created).
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd journalctl || exit 0
BACKUP="$T_SCRATCH/journal-backups"
CFG=$(make_config ClearJournal '{
  "Scope": "system", "VacuumTime": "36500d", "VacuumSize": "",
  "BackupDir": "'"$BACKUP"'"}')

run_case ClearJournal --config-path "$CFG" --dry-run
assert_eq "journal-dryrun-exit" "0" "$T_RC"
assert_contains "journal-dryrun-summary" "$(cat "$T_OUT")" "Journal dry-run:"
assert_file_gone "journal-dryrun-no-backupdir" "$BACKUP"
assert_eq "journal-dryrun-clear-items" "0" "$(dryrun_count Clear)"
if [[ "$(dryrun_count Skip)" -ge 1 ]]; then t_pass "journal-dryrun-skips-recent"; else t_fail "journal-dryrun-skips-recent" "no Skip items"; fi

# BackupDir hardening (G-4/G-5, H-7): hostile backup dirs are refused BEFORE
# any vacuum (exit 1, reason named); the export name is unpredictable and
# exclusively created.
CFG_ETC=$(make_config ClearJournalEtc '{"Scope":"system","VacuumTime":"7d","VacuumSize":"","BackupDir":"/etc/cron.d"}')
run_case ClearJournal --config-path "$CFG_ETC"
assert_eq "journal-backupdir-etc-exit" "1" "$T_RC"
assert_contains "journal-backupdir-etc-reason" "$(cat "$T_OUT")" "protected system path"

CFG_TRAV=$(make_config ClearJournalTrav '{"Scope":"system","VacuumTime":"7d","VacuumSize":"","BackupDir":"'"$T_SCRATCH"'/a/../../../etc"}')
run_case ClearJournal --config-path "$CFG_TRAV"
assert_eq "journal-backupdir-trav-exit" "1" "$T_RC"
assert_contains "journal-backupdir-trav-reason" "$(cat "$T_OUT")" "protected system path"

mkdir -p "$T_SCRATCH/real-backup"
ln -s "$T_SCRATCH/real-backup" "$T_SCRATCH/link-backup"
CFG_LINK=$(make_config ClearJournalLink '{"Scope":"system","VacuumTime":"7d","VacuumSize":"","BackupDir":"'"$T_SCRATCH"'/link-backup"}')
run_case ClearJournal --config-path "$CFG_LINK"
assert_eq "journal-backupdir-link-exit" "1" "$T_RC"
assert_contains "journal-backupdir-link-reason" "$(cat "$T_OUT")" "symlinked"

mkdir -p "$T_SCRATCH/elsewhere"
ln -s "$T_SCRATCH/elsewhere" "$T_SCRATCH/anc"
CFG_ANC=$(make_config ClearJournalAnc '{"Scope":"system","VacuumTime":"7d","VacuumSize":"","BackupDir":"'"$T_SCRATCH"'/anc/sub"}')
run_case ClearJournal --config-path "$CFG_ANC"
assert_eq "journal-backupdir-anc-exit" "1" "$T_RC"
assert_contains "journal-backupdir-anc-reason" "$(cat "$T_OUT")" "symlinked"

# Positive: user-scope real run with retention older than the oldest boot
# (vacuum provably deletes nothing). Pre-planted predictable export names
# must be left untouched; exactly one unpredictable export must appear.
OLDEST=$(journalctl --user --list-boots --no-pager 2>/dev/null | awk 'NF>=4 && $1 != "IDX" {print $3, $4}' | head -n 1)
if [[ -z "$OLDEST" ]]; then
    t_skip "journal-export-unpredictable" "no user journal boots"
else
    OLD_EP=$(date -d "$OLDEST" +%s 2>/dev/null || echo 0)
    RET_DAYS=$(( ($(date +%s) - OLD_EP) / 86400 + 30 ))
    [[ "$RET_DAYS" -lt 30 ]] && RET_DAYS=30
    BDIR="$T_SCRATCH/backups-live"; mkdir -p "$BDIR"
    for off in -2 -1 0 1 2 3; do
        ST=$(date -d "$off seconds" '+%Y%m%d_%H%M%S')
        printf 'MARKER' > "$BDIR/journal-user-$ST.export"
    done
    CFG_LIVE=$(make_config ClearJournalLive '{"Scope":"user","VacuumTime":"'"$RET_DAYS"'d","VacuumSize":"","BackupDir":"'"$BDIR"'"}')
    run_case ClearJournal --config-path "$CFG_LIVE"
    assert_eq "journal-live-exit" "0" "$T_RC"
    if grep -rq 'MARKER' "$BDIR" 2>/dev/null && [[ "$(grep -rl 'MARKER' "$BDIR" | wc -l)" -eq 6 ]]; then
        t_pass "journal-plants-untouched"
    else
        t_fail "journal-plants-untouched" "predictable-name plant disturbed"
    fi
    NEWN=$(find "$BDIR" -name 'journal-user-*.export' ! -exec grep -q 'MARKER' {} \; -print | wc -l)
    assert_eq "journal-one-new-export" "1" "$NEWN"
    if find "$BDIR" -name 'journal-user-*.export' ! -exec grep -q 'MARKER' {} \; -print \
        | grep -Eq 'journal-user-[0-9]{8}_[0-9]{6}-[0-9]+-[0-9]+\.export$'; then
        t_pass "journal-export-unpredictable"
    else
        t_fail "journal-export-unpredictable" "no PID-RANDOM suffixed export"
    fi
fi
