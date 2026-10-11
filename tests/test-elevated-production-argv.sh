#!/usr/bin/env bash
# Production pkexec argv coverage (21:04 post-mortem): the parent builds
# result/live/include DIRECTLY in RuntimeRoot (bare dir) via ElevatedPaths,
# and --preview-elevated prints that same builder's output — previews and
# validator input can never drift from what pkexec receives. The old
# validator refused the bare runtime dir itself (trailing-separator
# StartsWith), so every ACCEPT below FAILs on pre-fix code; negatives refuse
# on both (regression locks). No pkexec anywhere (direct child entry only).
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd dotnet || exit 0
DLL="$(repo_root)/src/ScriptSuite/bin/Release/net8.0/ScriptSuite.dll"
[[ -f "$DLL" ]] || { t_skip "prodargv" "no Release build"; exit 0; }

# Headless: never let a usage-error fall through to the GUI.
BINRUN=(env -u DISPLAY -u WAYLAND_DISPLAY timeout 60 dotnet "$DLL")
export SCRIPTSUITE_FORCE_PRIVILEGED_CHECK=1
mkdir -p "$HOME/.config/scriptsuite/configs"
RT="$T_SCRATCH/rt"; mkdir -p "$RT"

GOODCFG="$HOME/.config/scriptsuite/configs/ClearJournal.json"
printf '{"Scope":"user","VacuumTime":"7d","VacuumSize":"","BackupDir":"%s/backups"}' "$T_SCRATCH" > "$GOODCFG"
printf '{"ClearJournal": true}' > "$HOME/.config/scriptsuite/risk-consents.json"

G() { printf 'scriptsuite_%032x.json' "$1"; }

preview_paths() { # outvars _PV_R/L/I; uses current XDG_RUNTIME_DIR
    local out; out=$("${BINRUN[@]}" --preview-elevated ClearJournal --config-path "$GOODCFG") || return 1
    _PV_R=$(printf '%s' "$out" | grep -A1 '^--result-path$' | tail -n 1)
    _PV_L=$(printf '%s' "$out" | grep -A1 '^--live-log$' | tail -n 1)
    _PV_I=$(printf '%s' "$out" | grep -A1 '^--include-only$' | tail -n 1)
    [[ -n "$_PV_R" && -n "$_PV_L" && -n "$_PV_I" ]]
}

preview_paths_unset_tmp() { # preview with XDG_RUNTIME_DIR unset (tmp fallback)
    local out
    out=$(unset XDG_RUNTIME_DIR; "${BINRUN[@]}" --preview-elevated ClearJournal --config-path "$GOODCFG") || return 1
    _PV_R=$(printf '%s' "$out" | grep -A1 '^--result-path$' | tail -n 1)
    _PV_L=$(printf '%s' "$out" | grep -A1 '^--live-log$' | tail -n 1)
    _PV_I=$(printf '%s' "$out" | grep -A1 '^--include-only$' | tail -n 1)
    [[ -n "$_PV_R" && -n "$_PV_L" && -n "$_PV_I" ]]
}

validate() { # extra note; remaining args = argv tail after --validate-elevated
    T_OUT="$T_SCRATCH/out-$(date +%s%N).log"
    "${BINRUN[@]}" --validate-elevated "$@" > "$T_OUT" 2>&1
    T_RC=$?
}

# --- A. exact production shape: bare RuntimeRoot (/run/user/1000) ---
if [[ -d /run/user/$(id -u) ]]; then
    if XDG_RUNTIME_DIR=/run/user/$(id -u) preview_paths; then
        assert_eq "prod-shape-dir" "/run/user/$(id -u)" "$(dirname "$_PV_R")"
        if [[ "$(basename "$_PV_R")" =~ ^scriptsuite_[0-9a-f]{32}\.json$ ]]; then t_pass "prod-shape-guid"; else t_fail "prod-shape-guid" "got $(basename "$_PV_R")"; fi
        assert_eq "prod-shape-live" "$_PV_R.live.log" "$_PV_L"
        assert_eq "prod-shape-include" "$_PV_R.include.json" "$_PV_I"
        validate ClearJournal --config-path "$GOODCFG" --result-path "$_PV_R" --live-log "$_PV_L" --include-only "$_PV_I"
        assert_eq "prod-bare-runtime-accept" "0" "$T_RC"
        assert_contains "prod-bare-runtime-word" "$(cat "$T_OUT")" "ACCEPT"
    else
        t_fail "prod-preview" "preview entrypoint failed"
    fi
    # The literal filenames from the 21:04 pkexec journal line (files absent).
    J=8374be147f6c4fb79f2bf0a7a4d8cf79
    validate ClearJournal --config-path "$GOODCFG" \
        --result-path "/run/user/$(id -u)/scriptsuite_$J.json" \
        --live-log "/run/user/$(id -u)/scriptsuite_$J.json.live.log" \
        --include-only "/run/user/$(id -u)/scriptsuite_$J.json.include.json"
    assert_eq "prod-journal-names-accept" "0" "$T_RC"
else
    t_skip "prodargv-a" "no /run/user/$(id -u)"
fi

# --- B. bare /tmp fallback shape (XDG_RUNTIME_DIR unset) ---
if preview_paths_unset_tmp; then
    assert_eq "prod-tmp-dir" "/tmp" "$(dirname "$_PV_R")"
    validate ClearJournal --config-path "$GOODCFG" --result-path "$_PV_R" --live-log "$_PV_L" --include-only "$_PV_I"
    assert_eq "prod-bare-tmp-accept" "0" "$T_RC"
else
    t_fail "prod-preview-tmp" "preview entrypoint failed"
fi

# --- C. negatives: every one refuses (exit 2), pre- and post-fix ---
neg() { # name needle; argv tail in NEG_TAIL array
    validate ClearJournal --config-path "$GOODCFG" "${NEG_TAIL[@]}"
    if [[ "$T_RC" -eq 2 ]]; then t_pass "$1-exit"; else t_fail "$1-exit" "rc=$T_RC"; fi
    assert_contains "$1-reason" "$(cat "$T_OUT" 2>/dev/null)" "$2"
}
U=$(id -u)
# Sibling-prefix dirs (/run/user/1000x) need privilege to create; if they
# exist, the validator must refuse by confinement, else by absence.
XDIR_REASON="does not exist"
mkdir -p "/run/user/${U}x" "/run/user/${U}000" 2>/dev/null && XDIR_REASON="outside the caller's runtime dirs"
NEG_TAIL=(--result-path "/run/user/${U}x/$(G 21)" --live-log "/run/user/${U}x/$(G 21).live.log"); neg "neg-1000x" "$XDIR_REASON"
NEG_TAIL=(--result-path "/run/user/${U}000/$(G 22)" --live-log "/run/user/${U}000/$(G 22).live.log"); neg "neg-10000" "$XDIR_REASON"
NEG_TAIL=(--result-path "/run/user/$U/../../etc/$(G 23)" --live-log "/run/user/$U/../../etc/$(G 23).live.log"); neg "neg-traversal" "does not exist"
mkdir -p "$RT/realdir"; ln -sfn "$RT/realdir" "$RT/dirlink"
NEG_TAIL=(--result-path "$RT/dirlink/$(G 24)" --live-log "$RT/dirlink/$(G 24).live.log"); neg "neg-symdir" "symlink"
NEG_TAIL=(--result-path "/etc/$(G 25)" --live-log "/etc/$(G 25).live.log"); neg "neg-etc-owned" "outside the caller's runtime dirs"
NEG_TAIL=(--result-path "/run/user/$U/evil.json" --live-log "/run/user/$U/evil.json.live.log"); neg "neg-badname" "must look like"
mkdir -p "/run/user/$U/$(G 26)"
NEG_TAIL=(--result-path "/run/user/$U/$(G 26)" --live-log "/run/user/$U/$(G 26).live.log"); neg "neg-isdir" "already exists"
rmdir "/run/user/$U/$(G 26)"

# --- D. end-to-end gap test: forced tier + bare production-shaped runtime
# --- dir; the script must START and write a result file (exit 2 + no file
# --- pre-fix — the exact 21:04 signature).
export XDG_RUNTIME_DIR="$T_SCRATCH/prun"; mkdir -p "$XDG_RUNTIME_DIR"
OLDEST=$(journalctl --user --list-boots --no-pager 2>/dev/null | awk 'NF>=4 && $1 != "IDX" {print $3, $4}' | head -n 1)
if [[ -z "$OLDEST" ]]; then
    t_skip "prodargv-e2e" "no user journal boots"
else
    OLD_EP=$(date -d "$OLDEST" +%s 2>/dev/null || echo 0)
    RET_DAYS=$(( ($(date +%s) - OLD_EP) / 86400 + 30 ))
    [[ "$RET_DAYS" -lt 30 ]] && RET_DAYS=30
    printf '{"Scope":"user","VacuumTime":"%sd","VacuumSize":"","BackupDir":"%s/ebk"}' "$RET_DAYS" "$T_SCRATCH" > "$GOODCFG"
    if preview_paths; then
        assert_eq "e2e-shape-bare-prun" "$XDG_RUNTIME_DIR" "$(dirname "$_PV_R")"
        printf '[]' > "$_PV_I"
        T_OUT="$T_SCRATCH/out-$(date +%s%N).log"
        "${BINRUN[@]}" --elevated-run ClearJournal --config-path "$GOODCFG" \
            --result-path "$_PV_R" --live-log "$_PV_L" --include-only "$_PV_I" > "$T_OUT" 2>&1
        assert_eq "e2e-exit" "0" "$?"
        assert_file_exists "e2e-result-written" "$_PV_R"
        assert_contains "e2e-executed" "$(cat "$_PV_R" 2>/dev/null)" '"ExitCode": 0'
        assert_file_exists "e2e-livelog-written" "$_PV_L"
    else
        t_fail "prod-preview-e2e" "preview entrypoint failed"
    fi
fi

# --- E. forced-refusal stderr contract (what the parent shows in Output) ---
T_OUT="$T_SCRATCH/out-$(date +%s%N).log"
"${BINRUN[@]}" --elevated-run NoSuchScript --config-path "$GOODCFG" --result-path "$RT/$(G 31)" > "$T_OUT" 2>&1
assert_eq "refusal-exit" "2" "$?"
assert_contains "refusal-stderr-contract" "$(cat "$T_OUT")" "Elevated child refused:"
