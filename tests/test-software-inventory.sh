#!/usr/bin/env bash
# SoftwareInventory: report written, corrupt config falls back with a warning.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

OUT="$T_SCRATCH/reports/inventory.txt"
CFG=$(make_config SoftwareInventory '{"OutputFile": "'"$OUT"'"}')

run_case SoftwareInventory --config-path "$CFG"
assert_eq "inv-exit" "0" "$T_RC"
assert_file_exists "inv-report-written" "$OUT"
assert_contains "inv-report-total" "$(cat "$OUT")" "Total:"

CFG_BAD=$(make_config SoftwareInventoryBadConfig '{corrupt!!!')
run_case SoftwareInventory --config-path "$CFG_BAD"
assert_eq "inv-corrupt-still-runs" "0" "$T_RC"
assert_contains "inv-corrupt-warns" "$(cat "$T_OUT")" "WARN:"

CFG_REL=$(make_config SoftwareInventoryRelConfig '{"OutputFile": "relative/path.txt"}')
run_case SoftwareInventory --config-path "$CFG_REL"
assert_eq "inv-relative-path-refused" "1" "$T_RC"
assert_contains "inv-relative-path-errors" "$(cat "$T_OUT")" "ERROR:"
