#!/usr/bin/env bash
# S3 hardening: private-by-default outputs (umask 077 — logs, exports,
# reports land 0600, never group/other-readable), option-injection-safe
# config reads (jq -- terminator, so a dash-leading config path cannot be
# parsed as flags; head -n -- with numeric clamp), and sibling mktemp
# (report temps in the output dir, same filesystem, no /tmp plant).
# Scratch only.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd gio jq || exit 0
S="$T_SCRATCH/s3"; mkdir -p "$S/logs"

mode_of() { stat -c %a -- "$1" 2>/dev/null || echo "missing"; }

# --- DownloadsCleanup log file is owner-only ---
D="$S/dl"; mkdir -p "$D"
make_old "$D/old.zip" 5
DCFG=$(make_config S3Dl '{"SourceDir":"'"$D"'","CutoffDays":1,"LogDir":"'"$S"'/logs","LogFile":"dl.log","DeleteExts":[".zip"],"IgnorePatterns":[],"AdvancedRules":{},"Categories":{}}')
run_case DownloadsCleanup --config-path "$DCFG"
assert_eq "s3-dl-exit" "0" "$T_RC"
assert_eq "s3-dl-log-0600" "600" "$(mode_of "$S/logs/dl.log")"

# --- SoftwareInventory report is owner-only ---
RCFG=$(make_config S3Rep '{"OutputFile":"'"$S"'/report.txt"}')
run_case SoftwareInventory --config-path "$RCFG"
assert_eq "s3-rep-exit" "0" "$T_RC"
assert_file_exists "s3-rep-written" "$S/report.txt"
assert_eq "s3-rep-0600" "600" "$(mode_of "$S/report.txt")"

# --- ClearJournal stubbed export is owner-only (real setpriv writer) ---
STUBBIN="$S/stubbin"; mkdir -p "$STUBBIN"
cat > "$STUBBIN/journalctl" <<'EOF'
#!/usr/bin/env bash
if [[ "$*" == *--disk-usage* ]]; then echo "Journals take up 1M."; exit 0; fi
if [[ "$*" == *--list-boots* ]]; then printf ' 0 deadbeef00000000000000000000000 Mon 2020-01-01 00:00:00 CET\n'; exit 0; fi
echo "STUB_EXPORT_LINE_1"
exit 0
EOF
chmod +x "$STUBBIN/journalctl"
JCFG=$(make_config S3Journal '{"Scope":"user","VacuumTime":"7d","VacuumSize":"","BackupDir":"'"$S"'/bk"}')
PATH="$STUBBIN:$PATH" run_case ClearJournal --config-path "$JCFG"
assert_eq "s3-export-exit" "0" "$T_RC"
EXPORT=$(echo "$S"/bk/journal-user-*.export)
assert_eq "s3-export-0600" "600" "$(mode_of "$EXPORT")"

# --- dash-leading config path is data, not flags (jq --) ---
DW="$S/dash"; mkdir -p "$DW"
printf '{"TargetFolder":"%s","CutoffDays":1,"IgnoreFolders":[]}' "$DW/t" > "$DW/-weird.json"
mkdir -p "$DW/t"
make_old "$DW/t/old.tmp" 5
T_OUT="$T_SCRATCH/out-dash.log"
TEMPSH="$(script_path TempCleanup)"
(cd "$DW" && bash "$TEMPSH" --config-path "-weird.json" --dry-run > "$T_OUT" 2>&1)
T_RC=$?
assert_eq "s3-dash-exit" "0" "$T_RC"
assert_no_ere "s3-dash-no-corrupt-fallback" "$T_OUT" "corrupt or unreadable"
assert_contains "s3-dash-reads-config" "$(cat "$T_OUT")" "old.tmp"

# --- dash-leading config reaches raw-jq sites (Categories/AdvancedRules) ---
DC="$S/dashcat"; mkdir -p "$DC/src"
make_old "$DC/src/old.foo" 5
printf '{"SourceDir":"%s/src","CutoffDays":1,"LogDir":"%s/logs","LogFile":"cat.log","DeleteExts":[],"IgnorePatterns":[],"AdvancedRules":{},"Categories":{"%s/catdest":[".foo"]}}' "$DC" "$S" "$DC" > "$DC/-cat.json"
T_OUT="$T_SCRATCH/out-dashcat.log"
DLSH="$(script_path DownloadsCleanup)"
(cd "$DC" && bash "$DLSH" --config-path "-cat.json" --dry-run > "$T_OUT" 2>&1)
T_RC=$?
assert_eq "s3-dashcat-exit" "0" "$T_RC"
assert_no_ere "s3-dashcat-no-corrupt" "$T_OUT" "corrupt or unreadable"
assert_contains "s3-dashcat-categorized" "$(cat "$T_OUT")" "catdest"

# --- numeric counts never reach head as options (clamp + head -n --) ---
HC="$S/headclamp"; mkdir -p "$HC"
printf '{"OutputDir":"%s","OutputFile":"rep.html","MaxTopProcesses":"--help","MaxErrorEvents":"-5","ErrorWindowHours":"evil"}' "$HC" > "$HC/clamp.json"
T_OUT="$T_SCRATCH/out-clamp.log"
HSH="$(script_path SystemHealthReport)"
bash "$HSH" --config-path "$HC/clamp.json" > "$T_OUT" 2>&1
T_RC=$?
assert_eq "s3-clamp-exit" "0" "$T_RC"
assert_file_exists "s3-clamp-written" "$HC/rep.html"
assert_contains "s3-clamp-warned" "$(cat "$T_OUT")" "invalid — using default"

# --- negative count is clamped (old: silent head misparse, no warning) ---
NC="$S/negclamp"; mkdir -p "$NC"
printf '{"OutputDir":"%s","OutputFile":"neg.html","MaxTopProcesses":"-2"}' "$NC" > "$NC/neg.json"
T_OUT="$T_SCRATCH/out-neg.log"
bash "$HSH" --config-path "$NC/neg.json" > "$T_OUT" 2>&1
T_RC=$?
assert_eq "s3-neg-exit" "0" "$T_RC"
assert_file_exists "s3-neg-written" "$NC/neg.html"
assert_contains "s3-neg-warned" "$(cat "$T_OUT")" "MAX_PROCS value '-2' is invalid"
assert_no_ere "s3-neg-no-head-err" "$T_OUT" "unrecognized option|invalid number"

# --- sibling mktemp: reports build even with TMPDIR broken ---
BR="$S/brokentmp"; mkdir -p "$BR"
printf '{"OutputFile":"%s/sib.txt"}' "$BR" > "$BR/sib.json"
T_OUT="$T_SCRATCH/out-sib.log"
TMPDIR="$BR/nonexistent" bash "$(script_path SoftwareInventory)" --config-path "$BR/sib.json" > "$T_OUT" 2>&1
T_RC=$?
assert_eq "s3-sib-exit" "0" "$T_RC"
assert_file_exists "s3-sib-written" "$BR/sib.txt"
assert_eq "s3-sib-0600" "600" "$(mode_of "$BR/sib.txt")"
