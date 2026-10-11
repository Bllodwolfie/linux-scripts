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

# Hostile config + fixtures (H-4 palette breakout, H-5 img-src breakout,
# hostile journal lines): nothing attacker-controlled may appear unescaped.
mkdir -p "$HOME/Downloads"
: > "$HOME/Downloads/Cat_bobber-dark.png"
: > "$HOME/Downloads/Cat_bobber-light.png"
FAKEBIN="$T_SCRATCH/fakebin"; mkdir -p "$FAKEBIN"
printf '#!/usr/bin/env bash\nprintf "Oct 11 17:00:00 <script>alert(9)</script> proc[1]: x'"'"' onerror='"'"'y amp & tail \\"q\\"\\n"\n' > "$FAKEBIN/journalctl"
chmod +x "$FAKEBIN/journalctl"
EVILCFG=$(make_config HealthEvil '{
  "OutputDir": "'"$T_SCRATCH"'/health-evil",
  "OutputFile": "evil.html",
  "CatImageDark": "x'"'"' onerror='"'"'alert(1)",
  "CatImageLight": "ok-light.png",
  "Mocha": {"base": "</style><script>alert(1)</script>", "text": "#cdd6f4"},
  "Latte": {"base": "red'"'"', x"},
  "MaxTopProcesses": 3, "MaxErrorEvents": 5, "ErrorWindowHours": 1
}')
PATH="$FAKEBIN:$PATH" run_case SystemHealthReport --config-path "$EVILCFG"
EVILHTML="$T_SCRATCH/health-evil/evil.html"
assert_eq "evil-exit" "0" "$T_RC"
assert_file_exists "evil-written" "$EVILHTML"
assert_no_ere "evil-no-palette-breakout" "$EVILHTML" '</style><script>alert\(1\)</script>'
assert_no_ere "evil-no-imgsrc-breakout" "$EVILHTML" "x' onerror='alert\(1\)"
assert_no_ere "evil-no-journal-script" "$EVILHTML" '<script>alert\(9\)</script>'
assert_no_ere "evil-no-journal-onerror" "$EVILHTML" "x' onerror='y"
assert_contains "evil-warns-palette" "$(cat "$T_OUT")" "not a #rgb hex color"
assert_contains "evil-warns-filename" "$(cat "$T_OUT")" "not a plain filename"
assert_contains "evil-defaults-kept" "$(cat "$EVILHTML")" "--base: #1e1e2e;"
