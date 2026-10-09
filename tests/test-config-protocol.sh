#!/usr/bin/env bash
# Config protocol: missing config is silent defaults; corrupt config warns
# and keeps defaults; wrong-typed values fall back per key.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

T="$(mktemp -d "$T_SCRATCH/cfgproto-XXXXXX")"
make_old "$T/old.tmp" 5

run_case TempCleanup --config-path "$T_SCRATCH/nope-missing.json" --dry-run
assert_eq "cfg-missing-exit" "0" "$T_RC"

BAD="$T_SCRATCH/bad.json"
printf '{corrupt!!!' > "$BAD"
run_case TempCleanup --config-path "$BAD" --dry-run
assert_eq "cfg-corrupt-exit" "0" "$T_RC"
assert_contains "cfg-corrupt-warns" "$(cat "$T_OUT")" "WARN:"

TYPED="$T_SCRATCH/typed.json"
printf '{"TargetFolder": "%s", "CutoffDays": "seven", "IgnoreFolders": []}' "$T" > "$TYPED"
# invalid CutoffDays warns and falls back to default 7: a 10-day file lists.
make_old "$T/old10.tmp" 10
run_case TempCleanup --config-path "$TYPED" --dry-run
assert_eq "cfg-badint-exit" "0" "$T_RC"
assert_contains "cfg-badint-warns" "$(cat "$T_OUT")" "WARN:"
assert_contains "cfg-badint-default-cutoff-applies" "$(cat "$T_OUT")" "old10.tmp"
