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
