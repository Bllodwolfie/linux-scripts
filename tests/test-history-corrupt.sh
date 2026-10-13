#!/usr/bin/env bash
# E6: a corrupt or read-only history.db must not crash --scheduled-run (or
# GUI start). Degrade gracefully: still run the job, note the outage, exit
# by outcome. SUT_BIN overrides the binary under test (default: Release
# build; point it at the installed binary for before/after comparisons).
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd dotnet || exit 0
if [[ -z "${T_SCRATCH:-}" ]]; then
    T_SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/scriptsuite-history-XXXXXX")"
    CLEAN_SCRATCH=1
fi
export XDG_DATA_HOME="$T_SCRATCH/data" XDG_CONFIG_HOME="$T_SCRATCH/config"
export HOME="$T_SCRATCH/home" XDG_RUNTIME_DIR="$T_SCRATCH/run"
mkdir -p "$HOME" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_RUNTIME_DIR"

SUT="${SUT_BIN:-$(repo_root)/src/ScriptSuite/bin/Release/net8.0/ScriptSuite.dll}"
if [[ "$SUT" == *.dll ]]; then
    [[ -f "$SUT" ]] || { t_skip "history-corrupt" "no Release build"; exit 0; }
    SUT_RUN=(dotnet "$SUT")
else
    SUT_RUN=("$SUT")
fi
DATA_SS="$XDG_DATA_HOME/scriptsuite"; mkdir -p "$DATA_SS"

# TempCleanup config pointing at a missing dir: script warns + skips
# (Success) — proves the scheduled job itself still runs.
CFG="$T_SCRATCH/hist-cfg.json"
printf '{"TargetFolder":"%s/nowhere","CutoffDays":1,"IgnoreFolders":[]}' "$T_SCRATCH" > "$CFG"

sched_run() { # -> T_RC/T_OUT (per-script config comes from seed_cfg)
    T_OUT="$T_SCRATCH/out-$(date +%s%N).log"
    "${SUT_RUN[@]}" --scheduled-run TempCleanup > "$T_OUT" 2>&1
    T_RC=$?
}

# --scheduled-run reads the seeded per-script config (no --config-path flag).
seed_cfg() {
    mkdir -p "$XDG_CONFIG_HOME/scriptsuite/configs"
    cp -f -- "$CFG" "$XDG_CONFIG_HOME/scriptsuite/configs/TempCleanup.json"
}

crash_files() { find "$DATA_SS" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l; }

# --- Case 1: garbage history.db ---
printf 'NOT-A-SQLITE-FILE' > "$DATA_SS/history.db"
seed_cfg
BEFORE=$(crash_files)
sched_run
assert_eq "hist-corrupt-exit-zero" "0" "$T_RC"
assert_contains "hist-corrupt-job-ran" "$(cat "$T_OUT")" "skipping"
assert_contains "hist-corrupt-graceful" "$(cat "$T_OUT")" "history unavailable"
assert_eq "hist-corrupt-no-crash" "$BEFORE" "$(crash_files)"

# --- Case 2: valid but read-only history.db (a good run stages it) ---
rm -f -- "$DATA_SS/history.db"
seed_cfg
sched_run
if [[ "$T_RC" -eq 0 && -f "$DATA_SS/history.db" ]]; then
    chmod 444 -- "$DATA_SS/history.db"
    BEFORE=$(crash_files)
    sched_run
    assert_eq "hist-readonly-exit-zero" "0" "$T_RC"
    assert_contains "hist-readonly-graceful" "$(cat "$T_OUT")" "history unavailable"
    assert_eq "hist-readonly-no-crash" "$BEFORE" "$(crash_files)"
    chmod 644 -- "$DATA_SS/history.db"
else
    t_skip "hist-readonly" "setup run failed (rc=$T_RC)"
fi

# --- Case 3: GUI start with garbage history.db (display only) ---
if [[ -z "${DISPLAY:-}" ]]; then
    t_skip "hist-gui-corrupt" "no display"
else
    printf 'NOT-A-SQLITE-FILE' > "$DATA_SS/history.db"
    BEFORE=$(crash_files)
    GUI_LOG="$T_SCRATCH/gui-corrupt.log"
    DISPLAY="$DISPLAY" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-}" XDG_SESSION_TYPE="${XDG_SESSION_TYPE:-}" \
        nohup "${SUT_RUN[@]}" > "$GUI_LOG" 2>&1 &
    GUI_PID=$!
    sleep 20
    if kill -0 "$GUI_PID" 2>/dev/null; then
        t_pass "hist-gui-stays-up"
        kill "$GUI_PID" 2>/dev/null || true
    else
        t_fail "hist-gui-stays-up" "GUI died on corrupt history.db"
    fi
    wait "$GUI_PID" 2>/dev/null || true
    assert_eq "hist-gui-no-crash" "$BEFORE" "$(crash_files)"
fi

if [[ "${CLEAN_SCRATCH:-0}" == "1" ]]; then rm -rf -- "$T_SCRATCH"; fi
