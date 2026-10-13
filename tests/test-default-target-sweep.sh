#!/usr/bin/env bash
# Default-target sweep (step-1 follow-up): every script's preview AND real run
# against REAL-shaped /home/<user> paths — not only /tmp scratch. Any
# "protected system path" refusal on a legitimate home target is a false
# positive (the old guard refused all of /home/*). Real runs use fresh empty
# dirs (no-op by construction) or read-only scripts; nothing real is touched.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

export HOME=/home/timj USER=timj
SWEEP=/home/timj/.cache/ss-sweep-$$
mkdir -p "$SWEEP"
trap 'rm -rf -- "$SWEEP" /home/timj/ss-sweep-dl-$$ /home/timj/ss-sweep-sh-$$ /home/timj/ss-sweep-ef-$$' EXIT

no_refusal() { # name outfile
    if grep -Eq "protected system path|system-critical root" "$2"; then t_fail "$1" "false positive on real-shaped path"; else t_pass "$1"; fi
    return 0
}

# TempCleanup: dry + real on a fresh real-shaped dir.
CFG=$(make_config SweepTC '{"TargetFolder":"'"$SWEEP"'/tc","CutoffDays":7,"IgnoreFolders":[]}')
mkdir -p "$SWEEP/tc"
run_case TempCleanup --config-path "$CFG" --dry-run
assert_eq "sweep-tc-dry-exit" "0" "$T_RC"; no_refusal "sweep-tc-dry-no-refusal" "$T_OUT"
run_case TempCleanup --config-path "$CFG"
assert_eq "sweep-tc-real-exit" "0" "$T_RC"; no_refusal "sweep-tc-real-no-refusal" "$T_OUT"

# DownloadsCleanup: dry on the REAL ~/Downloads (dry-run: zero writes).
CFG=$(make_config SweepDL '{"SourceDir":"/home/timj/Downloads","CutoffDays":365,"IgnorePatterns":[],"Categories":{},"AdvancedRules":{}}')
run_case DownloadsCleanup --config-path "$CFG" --dry-run
assert_eq "sweep-dl-dry-exit" "0" "$T_RC"; no_refusal "sweep-dl-dry-no-refusal" "$T_OUT"
# ...plus real on a fresh real-shaped dir.
rm -rf /home/timj/ss-sweep-dl-$$; mkdir -p /home/timj/ss-sweep-dl-$$
CFG=$(make_config SweepDLR '{"SourceDir":"/home/timj/ss-sweep-dl-$$","CutoffDays":1,"IgnorePatterns":[],"Categories":{},"AdvancedRules":{}}')
run_case DownloadsCleanup --config-path "$CFG"
assert_eq "sweep-dl-real-exit" "0" "$T_RC"; no_refusal "sweep-dl-real-no-refusal" "$T_OUT"

# ScreenshotsCleanup: dry on real ~/Pictures/Screenshots if present, else fresh.
if [[ -d /home/timj/Pictures/Screenshots ]]; then SD=/home/timj/Pictures/Screenshots; else SD="$SWEEP/shots"; mkdir -p "$SD"; fi
CFG=$(make_config SweepSH '{"TargetFolder":"'"$SD"'","CutoffDays":365,"IgnoreFolders":[]}')
run_case ScreenshotsCleanup --config-path "$CFG" --dry-run
assert_eq "sweep-sh-dry-exit" "0" "$T_RC"; no_refusal "sweep-sh-dry-no-refusal" "$T_OUT"
rm -rf /home/timj/ss-sweep-sh-$$; mkdir -p /home/timj/ss-sweep-sh-$$
CFG=$(make_config SweepSHR '{"TargetFolder":"/home/timj/ss-sweep-sh-$$","CutoffDays":1,"IgnoreFolders":[]}')
run_case ScreenshotsCleanup --config-path "$CFG"
assert_eq "sweep-sh-real-exit" "0" "$T_RC"; no_refusal "sweep-sh-real-no-refusal" "$T_OUT"

# EmptyFolderCleanup: dry + real on fresh real-shaped dirs.
rm -rf /home/timj/ss-sweep-ef-$$; mkdir -p /home/timj/ss-sweep-ef-$$/empty-sub
CFG=$(make_config SweepEF '{"TargetFolder":"/home/timj/ss-sweep-ef-$$","CutoffDays":1,"IgnoreFolders":[]}')
run_case EmptyFolderCleanup --config-path "$CFG" --dry-run
assert_eq "sweep-ef-dry-exit" "0" "$T_RC"; no_refusal "sweep-ef-dry-no-refusal" "$T_OUT"
run_case EmptyFolderCleanup --config-path "$CFG"
assert_eq "sweep-ef-real-exit" "0" "$T_RC"; no_refusal "sweep-ef-real-no-refusal" "$T_OUT"

# EmptyTrash: real-shaped XDG root under /home/timj, one old file, dry + real.
export XDG_DATA_HOME=/home/timj/.cache/ss-sweep-xdg-$$
mkdir -p "$XDG_DATA_HOME"
: > "$SWEEP/old.txt"; touch -d '5 days ago' -- "$SWEEP/old.txt"
export XDG_DATA_HOME="$T_SCRATCH/data"
if command -v gio >/dev/null 2>&1; then
    export XDG_DATA_HOME=/home/timj/.cache/ss-sweep-xdg-$$
    gio trash -- "$SWEEP/old.txt" 2>/dev/null || true
    export XDG_DATA_HOME="$T_SCRATCH/data"
    CFG=$(make_config SweepTR '{"MinAgeDays":1}')
    XDG_DATA_HOME=/home/timj/.cache/ss-sweep-xdg-$$ run_case EmptyTrash --config-path "$CFG" --dry-run
    assert_eq "sweep-tr-dry-exit" "0" "$T_RC"; no_refusal "sweep-tr-dry-no-refusal" "$T_OUT"
    XDG_DATA_HOME=/home/timj/.cache/ss-sweep-xdg-$$ run_case EmptyTrash --config-path "$CFG"
    assert_eq "sweep-tr-real-exit" "0" "$T_RC"; no_refusal "sweep-tr-real-no-refusal" "$T_OUT"
    rm -rf /home/timj/.cache/ss-sweep-xdg-$$
else
    t_skip "sweep-trash" "missing command: gio"
fi

# ClearJournal: dry with the EXACT default BackupDir literal under real $HOME.
CFG=$(make_config SweepCJ '{"Scope":"user","VacuumTime":"7d","VacuumSize":"","BackupDir":"$HOME/Documents/Script_Logs/JournalBackups"}')
run_case ClearJournal --config-path "$CFG" --dry-run
assert_eq "sweep-cj-dry-exit" "0" "$T_RC"; no_refusal "sweep-cj-dry-no-refusal" "$T_OUT"

# SoftwareInventory + SystemHealthReport: real runs, real-shaped output dirs.
CFG=$(make_config SweepSI '{"OutputFile":"'"$SWEEP"'/inv/i.txt"}')
run_case SoftwareInventory --config-path "$CFG"
assert_eq "sweep-si-real-exit" "0" "$T_RC"; no_refusal "sweep-si-real-no-refusal" "$T_OUT"
CFG=$(make_config SweepHR '{"OutputDir":"'"$SWEEP"'/health","OutputFile":"r.html","MaxTopProcesses":3,"MaxErrorEvents":3,"ErrorWindowHours":1}')
run_case SystemHealthReport --config-path "$CFG"
assert_eq "sweep-hr-real-exit" "0" "$T_RC"; no_refusal "sweep-hr-real-no-refusal" "$T_OUT"
