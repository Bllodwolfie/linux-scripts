#!/usr/bin/env bash
# Elevation argv hardening (G-1..G-7): the elevated child must reject hostile
# argv without running anything — no real pkexec anywhere in this file.
# The built binary is driven directly with SCRIPTSUITE_FORCE_PRIVILEGED_CHECK=1,
# which forces the privileged validation path with the caller = current user.
# Every case asserts: nonzero exit, the reason on stderr, NO result file.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd dotnet || exit 0
DLL="$(repo_root)/src/ScriptSuite/bin/Release/net8.0/ScriptSuite.dll"
[[ -f "$DLL" ]] || { t_skip "elevation-argv" "no Release build"; exit 0; }

export SCRIPTSUITE_FORCE_PRIVILEGED_CHECK=1
# Caller home for the forced check is $HOME (already the test scratch root).
mkdir -p "$HOME/.config/scriptsuite/configs"
RT="$T_SCRATCH/rt"; mkdir -p "$RT"

G() { printf '%s/rt/scriptsuite_%s.json' "$T_SCRATCH" "$(printf '%032x' "$1")"; }

child() { # args... -> sets T_RC, captures stdout+stderr to T_OUT
    T_OUT="$T_SCRATCH/out-$(date +%s%N).log"
    dotnet "$DLL" "$@" > "$T_OUT" 2>&1
    T_RC=$?
}

GOODCFG="$HOME/.config/scriptsuite/configs/ClearJournal.json"
printf '{"Scope":"user","VacuumTime":"36500d","VacuumSize":"","BackupDir":"%s/backups"}' "$T_SCRATCH" > "$GOODCFG"
printf '{"ClearJournal": true}' > "$HOME/.config/scriptsuite/risk-consents.json"

# 1. unknown script id is rejected, nothing runs.
child --elevated-run NoSuchScript --config-path "$GOODCFG" --result-path "$(G 1)"
assert_eq "elev-unknown-exit" "2" "$T_RC"
assert_contains "elev-unknown-reason" "$(cat "$T_OUT")" "Unknown script id"
assert_file_gone "elev-unknown-no-result" "$(G 1)"

# 2. known but non-admin script is rejected (child allow-list, G-6).
TCCFG="$HOME/.config/scriptsuite/configs/TempCleanup.json"
printf '{"TargetFolder":"%s/t","CutoffDays":7,"IgnoreFolders":[]}' "$T_SCRATCH" > "$TCCFG"
child --elevated-run TempCleanup --config-path "$TCCFG" --result-path "$(G 2)"
assert_eq "elev-nonadmin-exit" "2" "$T_RC"
assert_contains "elev-nonadmin-reason" "$(cat "$T_OUT")" "not elevation-eligible"
assert_file_gone "elev-nonadmin-no-result" "$(G 2)"

# 3. symlinked result path is rejected, target untouched (G-2).
echo "PRECIOUS" > "$RT/victim.txt"
ln -sf "$RT/victim.txt" "$RT/link.json"
child --elevated-run ClearJournal --config-path "$GOODCFG" --result-path "$RT/link.json"
assert_eq "elev-symlink-exit" "2" "$T_RC"
assert_contains "elev-symlink-reason" "$(cat "$T_OUT")" "symlink"
assert_eq "elev-symlink-untouched" "PRECIOUS" "$(cat "$RT/victim.txt")"

# 4. result path outside the runtime confinement is rejected (G-2).
child --elevated-run ClearJournal --config-path "$GOODCFG" --result-path /etc/scriptsuite_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.json
assert_eq "elev-etcdir-exit" "2" "$T_RC"
assert_file_gone "elev-etcdir-no-result" "/etc/scriptsuite_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.json"

# 5. pre-existing result file is never opened (hardlink/plant, G-2).
echo "PLANTED" > "$(G 5)"
child --elevated-run ClearJournal --config-path "$GOODCFG" --result-path "$(G 5)"
assert_eq "elev-exists-exit" "2" "$T_RC"
assert_eq "elev-exists-untouched" "PLANTED" "$(cat "$(G 5)")"

# 6. non-JSON --include-only aborts, never widens to unrestricted (G-7).
printf 'not json{{{' > "$RT/bad.include.json"
child --elevated-run ClearJournal --config-path "$GOODCFG" --result-path "$(G 6)" --include-only "$RT/bad.include.json"
assert_eq "elev-include-exit" "2" "$T_RC"
assert_contains "elev-include-reason" "$(cat "$T_OUT")" "include-only"
assert_file_gone "elev-include-no-result" "$(G 6)"

# 7. no consent for the caller -> rejected before anything runs.
printf '{"TempCleanup": true}' > "$HOME/.config/scriptsuite/risk-consents.json"
child --elevated-run ClearJournal --config-path "$GOODCFG" --result-path "$(G 7)"
assert_eq "elev-consent-exit" "2" "$T_RC"
assert_contains "elev-consent-reason" "$(cat "$T_OUT")" "consent"
assert_file_gone "elev-consent-no-result" "$(G 7)"
printf '{"ClearJournal": true}' > "$HOME/.config/scriptsuite/risk-consents.json"

# 8. positive control: consented ClearJournal, user scope, retention older
# than the oldest user boot. The vacuum provably deletes nothing, so the
# export is honestly empty -> Warning, not Success. What this proves is the
# accept path: exit 0, result transport, script executed (ExitCode 0).
OLDEST=$(journalctl --user --list-boots --no-pager 2>/dev/null | awk 'NF>=4 && $1 != "IDX" {print $3, $4}' | head -n 1)
if [[ -z "$OLDEST" ]]; then
    t_skip "elev-ok" "no user journal boots"
else
    OLD_EP=$(date -d "$OLDEST" +%s 2>/dev/null || echo 0)
    RET_DAYS=$(( ( $(date +%s) - OLD_EP ) / 86400 + 30 ))
    [[ "$RET_DAYS" -lt 30 ]] && RET_DAYS=30
    printf '{"Scope":"user","VacuumTime":"%sd","VacuumSize":"","BackupDir":"%s/backups"}' "$RET_DAYS" "$T_SCRATCH" > "$GOODCFG"
    printf '[]' > "$RT/empty.include.json"
    child --elevated-run ClearJournal --config-path "$GOODCFG" --result-path "$(G 8)" --live-log "$(G 8).live.log" --include-only "$RT/empty.include.json"
    assert_eq "elev-ok-exit" "0" "$T_RC"
    assert_file_exists "elev-ok-result" "$(G 8)"
    assert_contains "elev-ok-executed" "$(cat "$(G 8)")" '"ExitCode": 0'
    assert_contains "elev-ok-warning" "$(cat "$(G 8)")" '"Outcome": "Warning"'
    assert_contains "elev-ok-empty-export" "$(cat "$(G 8)")" "export is empty"
fi

# --- G-7 gaps: --validate-elevated is the print-only validator entrypoint
# --- (ACCEPT exit 0 / refused exit 2, no side effects). Forced privileged
# --- tier via the same hook, so confinement applies. ---
printf '{"ClearJournal": true}' > "$HOME/.config/scriptsuite/risk-consents.json"
printf '[]' > "$RT/empty.include.json"

validate() { # args... -> sets T_RC, captures to T_OUT
    T_OUT="$T_SCRATCH/out-$(date +%s%N).log"
    dotnet "$DLL" --validate-elevated "$@" > "$T_OUT" 2>&1
    T_RC=$?
}

# 9. missing --include-only file aborts, never widens (G-7).
validate ClearJournal --config-path "$GOODCFG" --result-path "$(G 9)" --include-only "$RT/does-not-exist.include.json"
assert_eq "elev-missing-inc-exit" "2" "$T_RC"
assert_contains "elev-missing-inc-reason" "$(cat "$T_OUT")" "does not exist"
assert_file_gone "elev-missing-inc-no-result" "$(G 9)"

# 10. null entry in the include array aborts (G-7 strict JSON).
printf '["/home/somewhere/x",null]' > "$RT/null.include.json"
validate ClearJournal --config-path "$GOODCFG" --result-path "$(G 10)" --include-only "$RT/null.include.json"
assert_eq "elev-null-inc-exit" "2" "$T_RC"
assert_contains "elev-null-inc-reason" "$(cat "$T_OUT")" "include-only"
assert_file_gone "elev-null-inc-no-result" "$(G 10)"

# 11. valid JSON outside the runtime dirs is refused in the privileged tier.
if VTDIR=$(mktemp -d /var/tmp/scriptsuite-inc-XXXXXX 2>/dev/null); then
    printf '["/home/somewhere/x"]' > "$VTDIR/outside.include.json"
    validate ClearJournal --config-path "$GOODCFG" --result-path "$(G 11)" --include-only "$VTDIR/outside.include.json"
    assert_eq "elev-outside-inc-exit" "2" "$T_RC"
    assert_contains "elev-outside-inc-reason" "$(cat "$T_OUT")" "runtime dirs"
    assert_file_gone "elev-outside-inc-no-result" "$(G 11)"
    # 12. parent-dir symlink escaping the runtime dir is refused too.
    ln -sf "$VTDIR" "$RT/esc"
    validate ClearJournal --config-path "$GOODCFG" --result-path "$(G 12)" --include-only "$RT/esc/outside.include.json"
    assert_eq "elev-esc-inc-exit" "2" "$T_RC"
    assert_contains "elev-esc-inc-reason" "$(cat "$T_OUT")" "symlink"
    assert_file_gone "elev-esc-inc-no-result" "$(G 12)"
    rm -rf -- "$VTDIR" "$RT/esc"
else
    t_skip "elev-outside-inc" "no writable dir outside runtime confinement"
    t_skip "elev-esc-inc" "no writable dir outside runtime confinement"
fi

# 13. oversized include aborts (size cap, 1 MB).
python3 -c "print('[' + '\"x\",'*300000 + '\"y\"]')" > "$RT/big.include.json" 2>/dev/null || \
    { i=0; printf '[' > "$RT/big.include.json"; while [[ "$i" -lt 300000 ]]; do printf '"x",' >> "$RT/big.include.json"; i=$(( i + 1 )); done; printf '"y"]' >> "$RT/big.include.json"; }
validate ClearJournal --config-path "$GOODCFG" --result-path "$(G 13)" --include-only "$RT/big.include.json"
assert_eq "elev-big-inc-exit" "2" "$T_RC"
assert_contains "elev-big-inc-reason" "$(cat "$T_OUT")" "size limit"
assert_file_gone "elev-big-inc-no-result" "$(G 13)"

# 14. trailing-symlink include is refused.
printf '[]' > "$RT/real.include.json"
ln -sf "$RT/real.include.json" "$RT/sym.include.json"
validate ClearJournal --config-path "$GOODCFG" --result-path "$(G 14)" --include-only "$RT/sym.include.json"
assert_eq "elev-sym-inc-exit" "2" "$T_RC"
assert_contains "elev-sym-inc-reason" "$(cat "$T_OUT")" "symlink"
assert_file_gone "elev-sym-inc-no-result" "$(G 14)"

# 15. valid runtime-dir include is still ACCEPTed (parent flow unbroken).
validate ClearJournal --config-path "$GOODCFG" --result-path "$(G 15)" --include-only "$RT/empty.include.json"
assert_eq "elev-good-inc-exit" "0" "$T_RC"
assert_contains "elev-good-inc-accept" "$(cat "$T_OUT")" "ACCEPT"

# 16. end-to-end: --elevated-run with a missing include file refuses (exit 2,
# no result file) instead of running unrestricted.
child --elevated-run ClearJournal --config-path "$GOODCFG" --result-path "$(G 16)" --include-only "$RT/does-not-exist.include.json"
assert_eq "elev-run-missing-exit" "2" "$T_RC"
assert_contains "elev-run-missing-reason" "$(cat "$T_OUT")" "does not exist"
assert_file_gone "elev-run-missing-no-result" "$(G 16)"
