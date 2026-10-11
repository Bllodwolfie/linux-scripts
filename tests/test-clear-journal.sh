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

# Dropped-privilege writer (G-4 follow-up): export bytes must flow through
# setpriv as the invoking uid:gid — root never opens the user path. The
# print-only setpriv stub records argv (never execs); the stub journalctl
# answers list-boots/disk-usage/export/vacuum without touching the journal.
STUBBIN="$T_SCRATCH/stubbin"; mkdir -p "$STUBBIN"
cat > "$STUBBIN/journalctl" <<'EOF'
#!/usr/bin/env bash
# Stub: slow per-boot export (swap window), instant full export, recorded vacuum.
if [[ "$*" == *--disk-usage* ]]; then echo "Journals take up 1M."; exit 0; fi
if [[ "$*" == *--list-boots* ]]; then
    for i in $(seq 1 12); do printf ' %d deadbeef%02d00000000000000000000000 Mon 2020-01-%02d 00:00:00 CET\342\200\224Mon 2020-01-%02d 00:00:01 CET\n' "$i" "$i" "$i" "$i"; done
    exit 0
fi
if [[ "$*" == *--vacuum-time* ]]; then printf '%s\n' "$*" >> "${JSTUB_VACLOG:-/dev/null}"; echo "Vacuuming done."; exit 0; fi
if [[ "${JSTUB_SLOW:-0}" -eq 1 ]]; then for i in $(seq 1 5); do echo "EXPORT_LINE_$i"; sleep 0.1; done
else echo "STUB_EXPORT_LINE_1"; echo "STUB_EXPORT_LINE_2"; fi
exit "${JSTUB_EXPORT_RC:-0}"
EOF
chmod +x "$STUBBIN/journalctl"
cat > "$STUBBIN/setpriv" <<'EOF'
#!/usr/bin/env bash
# Print-only stub: records argv, optionally execs the real setpriv.
printf '%s\n' "$*" >> "${JSTUB_SETPRIV_LOG:-/dev/null}"
if [[ "${JSTUB_SETPRIV_EXEC:-0}" -eq 1 ]]; then exec /usr/bin/setpriv "$@"; fi
exit 0
EOF
chmod +x "$STUBBIN/setpriv"

# T1: the export write goes through setpriv with the caller's uid:gid.
export JSTUB_SETPRIV_LOG="$T_SCRATCH/setpriv.log" JSTUB_SETPRIV_EXEC=0 JSTUB_EXPORT_RC=0 JSTUB_SLOW=0
export JSTUB_VACLOG="$T_SCRATCH/vacuum.log"
CFG_STUB=$(make_config ClearJournalStub '{"Scope":"user","VacuumTime":"7d","VacuumSize":"","BackupDir":"'"$T_SCRATCH"'/stub-bk"}')
PATH="$STUBBIN:$PATH" run_case ClearJournal --config-path "$CFG_STUB"
assert_eq "journal-stub-exit" "0" "$T_RC"
SETPRIV_GOT="$(cat "$T_SCRATCH/setpriv.log" 2>/dev/null)"
assert_contains "journal-setpriv-reuid" "$SETPRIV_GOT" "--reuid=$(id -u)"
assert_contains "journal-setpriv-regid" "$SETPRIV_GOT" "--regid=$(id -g)"
assert_contains "journal-setpriv-path" "$SETPRIV_GOT" "journal-user-"
assert_contains "journal-stub-vacuum-ran" "$(cat "$T_SCRATCH/vacuum.log")" "--vacuum-time"

# T3: export failure still aborts before any vacuum (pipefail preserved).
export JSTUB_EXPORT_RC=1 JSTUB_VACLOG="$T_SCRATCH/vacuum2.log"
: > "$T_SCRATCH/vacuum2.log"
PATH="$STUBBIN:$PATH" run_case ClearJournal --config-path "$CFG_STUB"
assert_eq "journal-abort-exit" "1" "$T_RC"
assert_contains "journal-abort-reason" "$(cat "$T_OUT")" "Pre-vacuum export failed"
assert_eq "journal-abort-no-vacuum" "0" "$(grep -c -- --vacuum-time "$T_SCRATCH/vacuum2.log" || true)"
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

# T2: BackupDir swap mid-export (slow stub journalctl + include-only, 12 boots).
# The whole export is ONE writer stream: swapping BackupDir for a symlink to
# a victim dir mid-run must not redirect a single byte. Uses the REAL setpriv.
if [[ ! -x /usr/bin/setpriv ]]; then
    t_skip "journal-swap-victim-empty" "no setpriv"
    t_skip "journal-swap-orig-complete" "no setpriv"
else
    export JSTUB_SETPRIV_EXEC=1 JSTUB_SLOW=1 JSTUB_EXPORT_RC=0
    export JSTUB_VACLOG="$T_SCRATCH/vacuum3.log"
    SWAPD="$T_SCRATCH/swap"; mkdir -p "$SWAPD/bk"
    : > "$T_SCRATCH/include.txt"
    for i in $(seq 1 12); do printf 'boot deadbeef%02d00000000000000000000000 (2020-01-01)\n' "$i" >> "$T_SCRATCH/include.txt"; done
    CFG_SWAP=$(make_config ClearJournalSwap '{"Scope":"user","VacuumTime":"7d","VacuumSize":"","BackupDir":"'"$SWAPD"'/bk"}')
    SWAP_OUT="$T_SCRATCH/swap-out.log"
    PATH="$STUBBIN:$PATH" bash "$(repo_root)/scripts/ClearJournal/ClearJournal.sh" \
        --config-path "$CFG_SWAP" --include-only-file "$T_SCRATCH/include.txt" > "$SWAP_OUT" 2>&1 &
    SWAP_PID=$!
    SWAPPED=0
    for _ in $(seq 1 100); do
        f=$(find "$SWAPD/bk" -name 'journal-*.export' 2>/dev/null | head -n 1)
        if [[ -n "$f" && "$(wc -l < "$f")" -ge 10 ]]; then
            mv -- "$SWAPD/bk" "$SWAPD/bk.orig" && mkdir -- "$SWAPD/victim" && ln -s victim "$SWAPD/bk"
            SWAPPED=1
            break
        fi
        sleep 0.1
    done
    wait "$SWAP_PID"
    SWAP_RC=$?
    assert_eq "journal-swap-exit" "0" "$SWAP_RC"
    assert_eq "journal-swap-did-swap" "1" "$SWAPPED"
    VICTIM_LINES=$(find "$SWAPD/victim" -name '*.export' -exec cat {} + 2>/dev/null | wc -l)
    assert_eq "journal-swap-victim-empty" "0" "$VICTIM_LINES"
    ORIG_LINES=$(find "$SWAPD/bk.orig" -name '*.export' -exec cat {} + 2>/dev/null | grep -c EXPORT_LINE_ || true)
    assert_eq "journal-swap-orig-complete" "60" "$ORIG_LINES"
fi
