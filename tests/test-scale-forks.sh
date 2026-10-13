#!/usr/bin/env bash
# D-SC scale cliffs: per-file loops must not fork jq/awk per file.
# Counting shims for jq/awk log every exec; a 200-file dry-run may only
# touch jq at startup (config) and must never touch awk. Special-char
# names (quote, backslash, newline) must survive as valid JSON DRYRUN.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd jq awk || exit 0
S="$T_SCRATCH/scale-forks"; mkdir -p "$S"
SHIM="$S/shim"; mkdir -p "$SHIM"
export SCALE_COUNT="$S/counts.log"
: > "$SCALE_COUNT"

mkshim() { # name realbin
    cat > "$SHIM/$1" <<EOF
#!/usr/bin/env bash
printf 'x\n' >> "\$SCALE_COUNT"
exec "$2" "\$@"
EOF
    chmod +x "$SHIM/$1"
}
mkshim jq /usr/bin/jq
mkshim awk /usr/bin/awk

count_of() { grep -c . "$SCALE_COUNT" 2>/dev/null || echo 0; }
# NOTE: shims count jointly (jq+awk share one log); awk_only runs reset it.

# --- DownloadsCleanup dry-run: fork count must not grow with file count ---
# Small run (20 files) establishes the startup-only baseline; the 200-file
# run must fork exactly as many times (all config reads, zero per-file).
DS="$S/dlsmall"; mkdir -p "$DS"
i=0; while [[ "$i" -lt 20 ]]; do make_old "$DS/f$(printf '%04d' "$i").tmp" 5; i=$(( i + 1 )); done
DSCFG=$(make_config ScaleDlSmall '{"SourceDir":"'"$DS"'","CutoffDays":1,"LogDir":"'"$S"'/logs","LogFile":"small.log","DeleteExts":[".tmp"],"IgnorePatterns":[],"AdvancedRules":{},"Categories":{}}')
: > "$SCALE_COUNT"
PATH="$SHIM:$PATH" bash "$(script_path DownloadsCleanup)" --config-path "$DSCFG" --dry-run > "$T_SCRATCH/out-small.log" 2>&1
SMALL_N=$(count_of)
D="$S/dl"; mkdir -p "$D"
i=0; while [[ "$i" -lt 200 ]]; do make_old "$D/f$(printf '%04d' "$i").tmp" 5; i=$(( i + 1 )); done
make_old "$D/qu\"ote.tmp" 5
make_old "$D/back\\slash.tmp" 5
make_old "$D/new
line.tmp" 5
DCFG=$(make_config ScaleDl '{"SourceDir":"'"$D"'","CutoffDays":1,"LogDir":"'"$S"'/logs","LogFile":"dl.log","DeleteExts":[".tmp"],"IgnorePatterns":[],"AdvancedRules":{},"Categories":{}}')
: > "$SCALE_COUNT"
T_OUT="$T_SCRATCH/out-scaledl.log"
PATH="$SHIM:$PATH" bash "$(script_path DownloadsCleanup)" --config-path "$DCFG" --dry-run > "$T_OUT" 2>&1
T_RC=$?
BIG_N=$(count_of)
assert_eq "fork-dl-exit" "0" "$T_RC"
assert_eq "fork-dl-count" "203" "$(dryrun_count Delete)"
# DRYRUN lines must be valid JSON even with quote/backslash/newline names.
BADLINES=0
while IFS= read -r dl; do
    /usr/bin/jq -e . <<< "${dl#DRYRUN: }" >/dev/null 2>&1 || BADLINES=$(( BADLINES + 1 ))
done < <(grep '^DRYRUN: ' "$T_OUT")
assert_eq "fork-dl-json-valid" "0" "$BADLINES"
assert_contains "fork-dl-quote-name" "$(cat "$T_OUT")" 'qu\"ote.tmp'
assert_contains "fork-dl-backslash-name" "$(cat "$T_OUT")" 'back\\slash.tmp'
assert_contains "fork-dl-newline-name" "$(cat "$T_OUT")" 'new\nline.tmp'
assert_eq "fork-dl-flat" "$SMALL_N" "$BIG_N"

# --- EmptyTrash dry-run: fork count flat from 12 to 120 items ---
export XDG_DATA_HOME="$S/trashdata"
mkdir -p "$XDG_DATA_HOME/Trash/files" "$XDG_DATA_HOME/Trash/info"
seed_trash() { # prefix count
    local j=0
    while [[ "$j" -lt "$2" ]]; do
        local n="$1$(printf '%04d' "$j").txt"
        echo "data" > "$XDG_DATA_HOME/Trash/files/$n"
        printf '[Trash Info]\nPath=/home/somewhere/%s\nDeletionDate=%s\n' "$n" \
            "$(date -d '5 days ago' +%Y-%m-%dT%H:%M:%S)" > "$XDG_DATA_HOME/Trash/info/$n.trashinfo"
        j=$(( j + 1 ))
    done
}
seed_trash "s" 12
TCFG=$(make_config ScaleTrash '{"MinAgeDays": 1}')
: > "$SCALE_COUNT"
PATH="$SHIM:$PATH" bash "$(script_path EmptyTrash)" --config-path "$TCFG" --dry-run > "$T_SCRATCH/out-scaletrash-small.log" 2>&1
TRASH_SMALL=$(count_of)
seed_trash "t" 108
: > "$SCALE_COUNT"
T_OUT="$T_SCRATCH/out-scaletrash.log"
PATH="$SHIM:$PATH" bash "$(script_path EmptyTrash)" --config-path "$TCFG" --dry-run > "$T_OUT" 2>&1
T_RC=$?
TRASH_BIG=$(count_of)
assert_eq "fork-trash-exit" "0" "$T_RC"
assert_eq "fork-trash-count" "120" "$(dryrun_count Delete)"
assert_eq "fork-trash-flat" "$TRASH_SMALL" "$TRASH_BIG"
