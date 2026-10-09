#!/usr/bin/env bash
# SystemHealthReport: HTML report written to the output dir.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

CFG=$(make_config SystemHealthReport '{
  "OutputDir": "'"$T_SCRATCH"'/health",
  "OutputFile": "rep.html",
  "MaxTopProcesses": 5, "MaxErrorEvents": 5, "ErrorWindowHours": 24}')

run_case SystemHealthReport --config-path "$CFG"
assert_eq "health-exit" "0" "$T_RC"
assert_file_exists "health-report-written" "$T_SCRATCH/health/rep.html"
assert_contains "health-report-html" "$(cat "$T_SCRATCH/health/rep.html")" "<html"
