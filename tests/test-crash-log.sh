#!/usr/bin/env bash
# Crash recorder: an unhandled exception dies non-zero AND leaves a full
# local trace; clean exits (0 or usage errors) write no crash file.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd dotnet || exit 0
DLL="$(repo_root)/src/ScriptSuite/bin/Release/net8.0/ScriptSuite.dll"
[[ -f "$DLL" ]] || { t_skip "crash-log" "no Release build"; exit 0; }

CRASHDIR="$XDG_DATA_HOME/scriptsuite"
crash_count() { find "$CRASHDIR" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l; }

# Positive: the probe throw dies non-zero with a full trace on disk.
BEFORE=$(crash_count)
T_OUT="$T_SCRATCH/out-crash-probe.log"
SCRIPTSUITE_CRASH_TEST=1 dotnet "$DLL" --self-test 10 > "$T_OUT" 2>&1
T_RC=$?
if [[ "$T_RC" -ne 0 ]]; then t_pass "crash-probe-dies-nonzero"; else t_fail "crash-probe-dies-nonzero" "rc=$T_RC"; fi
AFTER=$(crash_count)
if [[ "$AFTER" -gt "$BEFORE" ]]; then t_pass "crash-probe-writes-file"; else t_fail "crash-probe-writes-file" "no new crash file"; fi
NEWEST=$(find "$CRASHDIR" -maxdepth 1 -name 'crash-*.log' -newermt '-2 minutes' 2>/dev/null | head -n 1)
if [[ -n "$NEWEST" ]] \
    && grep -q 'SCRIPTSUITE_CRASH_TEST probe' "$NEWEST" \
    && grep -q '   at ' "$NEWEST" \
    && grep -q 'nothing sent' "$NEWEST"; then
    t_pass "crash-probe-full-trace"
else
    t_fail "crash-probe-full-trace" "missing message/stack/local-only note in ${NEWEST:-<none>}"
fi
rm -f "$NEWEST"

# Negative: a clean run writes no crash file.
BEFORE2=$(crash_count)
dotnet "$DLL" --self-test 10 > "$T_OUT.clean" 2>&1
if [[ "$?" -eq 0 ]]; then t_pass "crash-clean-exit-zero"; else t_fail "crash-clean-exit-zero" "self-test 10 failed"; fi
if [[ "$(crash_count)" -eq "$BEFORE2" ]]; then t_pass "crash-clean-no-file"; else t_fail "crash-clean-no-file" "clean run wrote a crash file"; fi
