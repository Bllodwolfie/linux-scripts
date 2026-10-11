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

# Privacy (Windows parity: adapter name + state only — no IPs or MACs
# anywhere in the HTML). Patterns validated against real reports.
HTML="$T_SCRATCH/health/rep.html"
assert_contains "health-network-connected-only" "$(grep -A5 'id="network"' "$HTML")" "<td>Connected</td>"
assert_no_ere "health-no-ipv4" "$HTML" '\b([0-9]{1,3}\.){3}[0-9]{1,3}\b'
assert_no_ere "health-no-mac" "$HTML" '\b([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\b'
assert_no_ere "health-no-ipv6-compressed" "$HTML" '::'
assert_no_ere "health-no-ipv6-expanded" "$HTML" '\b([0-9a-fA-F]{1,4}:){3,\}'
