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
if [[ "${JSTUB_SLOW:-0}" -eq 1 ]]; then for i in $(seq 1 60); do echo "EXPORT_LINE_$i"; sleep 0.1; done
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
# The whole export is ONE writer stream, and the selection is IGNORED (S1):
# swapping BackupDir for a symlink to a victim dir mid-run must not redirect
# a single byte. Uses the REAL setpriv.
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
    assert_contains "journal-swap-selection-ignored" "$(cat "$SWAP_OUT")" "not honored"
    VICTIM_LINES=$(find "$SWAPD/victim" -name '*.export' -exec cat {} + 2>/dev/null | wc -l)
    assert_eq "journal-swap-victim-empty" "0" "$VICTIM_LINES"
    ORIG_LINES=$(find "$SWAPD/bk.orig" -name '*.export' -exec cat {} + 2>/dev/null | grep -c EXPORT_LINE_ || true)
    assert_eq "journal-swap-orig-complete" "60" "$ORIG_LINES"
fi

# Privileged-tier caller-home expansion: with HOME=/root (all an elevated
# process sees) + PKEXEC_UID of the invoking user, $HOME/~ in BackupDir and
# its default must resolve under the CALLER's home — never /root.
MYUID=$(id -u)
CALLERHOME=$(getent passwd "$MYUID" | cut -d: -f6)

# Tier preview: default BackupDir (no key), dry-run, zero writes. The Clear
# items name the resolved export dir, proving expansion (VacuumTime 0d makes
# every boot Clear deterministically).
CFG_NODEF=$(make_config ClearJournalNoDef '{"Scope":"user","VacuumTime":"0d","VacuumSize":""}')
HOME=/root PKEXEC_UID=$MYUID run_case ClearJournal --config-path "$CFG_NODEF" --dry-run
assert_eq "tier-def-preview-exit" "0" "$T_RC"
assert_contains "tier-def-resolves-caller" "$(cat "$T_OUT")" "$CALLERHOME/Documents/Script_Logs/JournalBackups"
assert_no_ere "tier-def-no-root" "$T_OUT" '/root/Documents'

# Tier real run: literal $HOME BackupDir + real setpriv writer. Proves the
# end-to-end path lands in the caller's home with real bytes in it.
export JSTUB_SETPRIV_EXEC=1 JSTUB_SLOW=0 JSTUB_EXPORT_RC=0
export JSTUB_SETPRIV_LOG="$T_SCRATCH/setpriv-tier.log" JSTUB_VACLOG="$T_SCRATCH/vacuum-tier.log"
TIERLEAF="tier-bk-$$"
CFG_TIER=$(make_config ClearJournalTier '{"Scope":"user","VacuumTime":"7d","VacuumSize":"","BackupDir":"$HOME/'"$TIERLEAF"'"}')
HOME=/root PKEXEC_UID=$MYUID PATH="$STUBBIN:$PATH" run_case ClearJournal --config-path "$CFG_TIER"
assert_eq "tier-real-exit" "0" "$T_RC"
TIEREXPORT=$(find "$CALLERHOME/$TIERLEAF" -name 'journal-user-*.export' 2>/dev/null | head -n 1)
assert_file_exists "tier-real-export-in-caller-home" "$TIEREXPORT"
assert_contains "tier-real-export-bytes" "$(cat "$TIEREXPORT" 2>/dev/null)" "STUB_EXPORT_LINE"
assert_contains "journal-setpriv-tier-path" "$(cat "$T_SCRATCH/setpriv-tier.log" 2>/dev/null)" "$CALLERHOME/$TIERLEAF"
rm -rf -- "$CALLERHOME/$TIERLEAF"

# Tier fail-closed: unresolvable PKEXEC_UID aborts before anything runs.
CFG_BADUID=$(make_config ClearJournalBadUid '{"Scope":"user","VacuumTime":"7d","VacuumSize":""}')
HOME=/root PKEXEC_UID=60000 run_case ClearJournal --config-path "$CFG_BADUID" --dry-run
assert_eq "tier-baduid-exit" "1" "$T_RC"
assert_contains "tier-baduid-reason" "$(cat "$T_OUT")" "Cannot resolve home"

# VacuumTime normalization (21:04 follow-up): bare numbers are days for the
# preview/export math but SECONDS for journalctl --vacuum-time — the vacuum
# must receive the normalized "<N> days" form. Stub journalctl answers
# list-boots (12 fake 2020 boots) and records vacuum argv; stub setpriv is
# print-only (no files). Retention labels are asserted on the dry-run
# SUMMARY line (robust whatever the Clear/Skip mix is).
export JSTUB_SETPRIV_EXEC=0 JSTUB_SLOW=0 JSTUB_EXPORT_RC=0
VTLOG="$T_SCRATCH/vt-vacuum.log"

vt_dry() { # cfg -> T_OUT; dry-run with stub boots
    PATH="$STUBBIN:$PATH" run_case ClearJournal --config-path "$1" --dry-run
}
vt_real() { # cfg -> T_OUT; stubbed real run (vacuum recorded, nothing touched)
    : > "$VTLOG"; export JSTUB_VACLOG="$VTLOG"
    PATH="$STUBBIN:$PATH" run_case ClearJournal --config-path "$1"
}
vt_cfg() { # name vacuumtime -> path
    make_config "VT$1" '{"Scope":"user","VacuumTime":"'"$2"'","VacuumSize":"","BackupDir":"'"$T_SCRATCH"'/vt-bk"}'
}

# (a) bare "365": summary says days + WARNs every run.
C365=$(vt_cfg Bare365 "365")
vt_dry "$C365"
assert_contains "vt-bare-preview-days" "$(cat "$T_OUT")" "older than 365 days"
assert_contains "vt-bare-warn" "$(cat "$T_OUT")" "no unit"
# (b) vacuum receives the normalized form, not the bare number.
vt_real "$C365"
assert_contains "vt-bare-argv" "$(cat "$VTLOG")" "--vacuum-time=365 days"
# (c) long names keep working (2weeks already agreed; 1month must use
# journalctl's 30.44-day month, not the script's old 30 — see table).
C2W=$(vt_cfg W2 "2weeks")
vt_dry "$C2W"
assert_contains "vt-2weeks-summary" "$(cat "$T_OUT")" "older than 2weeks"
assert_no_ere "vt-2weeks-no-fallback" "$T_OUT" "using default"
C1M=$(vt_cfg M1 "1month")
vt_real "$C1M"
assert_contains "vt-1month-passthrough" "$(cat "$VTLOG")" "--vacuum-time=1month"
# (d) zero/negative/garbage/huge/empty rejected loudly with 7-day fallback.
for spec in "Z0:0" "Z0D:0d" "NEG:-5" "ABC:abc" "PARSEC:7 parsecs" "HUGE:101y"; do
    tag="${spec%%:*}"; val="${spec#*:}"
    C=$(vt_cfg "$tag" "$val")
    vt_dry "$C"
    assert_contains "vt-$tag-warn" "$(cat "$T_OUT")" "default 7 days"
    assert_contains "vt-$tag-math" "$(cat "$T_OUT")" "older than 7 days"
done
CEMPTY=$(make_config VTEMPTY '{"Scope":"user","VacuumTime":"","VacuumSize":"","BackupDir":"'"$T_SCRATCH"'/vt-bk"}')
vt_dry "$CEMPTY"
assert_contains "vt-empty-warn" "$(cat "$T_OUT")" "default 7 days"
# VacuumSize: bare = bytes (stated in the rule line), garbage = WARN + no cap.
CSZ=$(make_config VTSZ '{"Scope":"user","VacuumTime":"7d","VacuumSize":"500","BackupDir":"'"$T_SCRATCH"'/vt-bk"}')
vt_dry "$CSZ"
assert_contains "vt-size-bytes" "$(cat "$T_OUT")" "size cap 500 bytes"
CSZM=$(make_config VTSZM '{"Scope":"user","VacuumTime":"7d","VacuumSize":"500M","BackupDir":"'"$T_SCRATCH"'/vt-bk"}')
vt_dry "$CSZM"
assert_contains "vt-size-mega" "$(cat "$T_OUT")" "size cap 500M"
CSZB=$(make_config VTSZB '{"Scope":"user","VacuumTime":"7d","VacuumSize":"bogus","BackupDir":"'"$T_SCRATCH"'/vt-bk"}')
vt_dry "$CSZB"
assert_contains "vt-size-warn" "$(cat "$T_OUT")" "VacuumSize"
assert_no_ere "vt-size-nocap" "$T_OUT" "size cap"
# Rule line present on both preview and real runs.
assert_contains "vt-rule-dry" "$(cat "$T_OUT")" "Rule: keep entries newer than"
vt_real "$CSZM"
assert_contains "vt-rule-real" "$(cat "$T_OUT")" "Rule: keep entries newer than 7d"

# (f) 25-value consistency: preview/export cutoff == vacuum semantics.
# Cutoff date parsed from Clear details when present (that string IS the
# export --until); otherwise the summary retention label is mapped back to
# seconds. Vacuum arg mapped via journalctl's DOCUMENTED units — notably a
# BARE number is seconds for journalctl (the 21:04 bug), so pre-fix bare
# values mismatch here by design. Tolerance 30s.
vt_secs() { # vacuum-time arg -> seconds (journal truth: bare=s; month=30.44d; year=365.25d)
    LC_ALL=C awk -v a="$1" 'BEGIN{
        n=a; sub(/[ \t]*[A-Za-z]*$/, "", n); u=a; sub(/^[^A-Za-z]*/, "", u); gsub(/[ \t]/, "", u);
        if (u=="") f=1; else if (u~/^(s|sec|seconds?)$/) f=1;
        else if (u~/^(m|min|minutes?)$/) f=60; else if (u~/^(h|hr|hours?)$/) f=3600;
        else if (u~/^(d|days?)$/) f=86400; else if (u~/^(w|weeks?)$/) f=604800;
        else if (u~/^(months?|M)$/) f=2630016; else if (u~/^(y|years?)$/) f=31557600; else f=-1;
        printf "%.0f", n*f }'
}
vt_label_secs() { # retention label from summary ("365 days","7d","2weeks") -> seconds; -1 unknown
    vt_secs "$1"
}
VT_NOW=$(date +%s)
VT_PASS=0; VT_FAIL=0
while IFS='|' read -r val _; do
    [[ -z "$val" ]] && continue
    C=$(make_config "VTT$VT_PASS-$VT_FAIL" '{"Scope":"user","VacuumTime":"'"$val"'","VacuumSize":"","BackupDir":"'"$T_SCRATCH"'/vt-bk"}')
    vt_dry "$C"
    CUT=$(grep -o -m1 'entries before [0-9-]* [0-9:]*' "$T_OUT" | sed 's/entries before //')
    if [[ -n "$CUT" ]]; then
        OBS_EP=$(date -d "$CUT" +%s 2>/dev/null || echo 0)
        OBS_AGE=$(( VT_NOW - OBS_EP ))
    else
        LAB=$(grep -o -m1 'older than .* would be' "$T_OUT" | sed -e 's/older than //' -e 's/ would be//')
        OBS_AGE=$(vt_label_secs "$LAB")
    fi
    vt_real "$C"
    VARG=$(sed -n 's/.*--vacuum-time=//p' "$VTLOG" | head -n 1)
    VSEC=$(vt_secs "$VARG")
    if [[ "$OBS_AGE" -gt 0 && "$VSEC" -gt 0 ]] && LC_ALL=C awk -v a="$OBS_AGE" -v v="$VSEC" 'BEGIN{exit !((a-v<30 && v-a<30))}'; then
        VT_PASS=$(( VT_PASS + 1 ))
    else
        VT_FAIL=$(( VT_FAIL + 1 )); printf 'FAIL vt-table [%s] observed=%s vac=%s\n' "$val" "${CUT:-label:$LAB}" "$VARG"
    fi
done <<'VALUES'
7d|
365|
2weeks|
1month|
1year|
30m|
90s|
1.5h|
24h|
1w|
10 days|
2 days|
48h|
6 months|
3 years|
12h|
45s|
5|
1|
100y|
101y|
0|
0d|
-5|
abc|
VALUES
assert_eq "vt-table-fails" "0" "$VT_FAIL"
printf 'PASS vt-table-passed (%d values)\n' "$VT_PASS"
