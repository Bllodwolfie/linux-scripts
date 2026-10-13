#!/usr/bin/env bash
# Report dry-run: SystemHealthReport and SoftwareInventory must honor
# --dry-run with a single planned Create item and write NOTHING (the old
# code exited 1 on the unknown --dry-run flag, so the Run window could
# never preview them). Real runs still write their reports.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd jq || exit 0
S="$T_SCRATCH/repdry"; mkdir -p "$S"

# --- SystemHealthReport dry-run plans, never writes ---
HCFG=$(make_config RepDryH '{"OutputDir":"'"$S"'/hout","OutputFile":"h.html","MaxTopProcesses":5,"MaxErrorEvents":5,"ErrorWindowHours":24}')
run_case SystemHealthReport --config-path "$HCFG" --dry-run
HD_OUT="$T_OUT"
assert_eq "rep-health-dry-exit" "0" "$T_RC"
assert_eq "rep-health-dry-one-item" "1" "$(dryrun_count)"
assert_eq "rep-health-dry-create" "1" "$(dryrun_count Create)"
assert_contains "rep-health-dry-target" "$(cat "$T_OUT")" "h.html"
assert_file_gone "rep-health-dry-writes-nothing" "$S/hout/h.html"
assert_file_gone "rep-health-dry-no-dir" "$S/hout"

# --- SoftwareInventory dry-run plans, never writes ---
RCFG=$(make_config RepDryR '{"OutputFile":"'"$S"'/rep.txt"}')
run_case SoftwareInventory --config-path "$RCFG" --dry-run
RD_OUT="$T_OUT"
assert_eq "rep-inv-dry-exit" "0" "$T_RC"
assert_eq "rep-inv-dry-one-item" "1" "$(dryrun_count)"
assert_eq "rep-inv-dry-create" "1" "$(dryrun_count Create)"
assert_contains "rep-inv-dry-target" "$(cat "$T_OUT")" "rep.txt"
assert_file_gone "rep-inv-dry-writes-nothing" "$S/rep.txt"

# --- dry-run items are valid JSON (this test's own dry-run logs only) ---
BAD=0
while IFS= read -r dl; do
    /usr/bin/jq -e . <<< "${dl#DRYRUN: }" >/dev/null 2>&1 || BAD=$(( BAD + 1 ))
done < <(grep -h '^DRYRUN: ' "$HD_OUT" "$RD_OUT")
assert_eq "rep-dry-json-valid" "0" "$BAD"

# --- real runs still generate their reports ---
run_case SoftwareInventory --config-path "$RCFG"
assert_eq "rep-inv-real-exit" "0" "$T_RC"
assert_file_exists "rep-inv-real-written" "$S/rep.txt"
run_case SystemHealthReport --config-path "$HCFG"
assert_eq "rep-health-real-exit" "0" "$T_RC"
assert_file_exists "rep-health-real-written" "$S/hout/h.html"

# --- --include-only-file is accepted (whole report, never narrowed) ---
printf '/x\n' > "$S/only.txt"
T_OUT="$T_SCRATCH/out-inc.log"
bash "$(script_path SoftwareInventory)" --config-path "$RCFG" --include-only-file "$S/only.txt" > "$T_OUT" 2>&1
assert_eq "rep-inv-include-accepted" "0" "$?"
bash "$(script_path SystemHealthReport)" --config-path "$HCFG" --include-only-file "$S/only.txt" > "$T_OUT" 2>&1
assert_eq "rep-health-include-accepted" "0" "$?"
