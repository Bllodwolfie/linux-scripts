#!/usr/bin/env bash
# B-1: DownloadsCleanup must never clobber on move. A destination clash
# renames to "name (2)" ("name (3)", ...) with a WARN — the existing file
# keeps its bytes. Scratch dirs only.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd gio jq || exit 0
D="$(mktemp -d "$T_SCRATCH/noclob-XXXXXX")"
SORTED="$D/sorted"; DEST2="$D/dest2"; mkdir -p "$SORTED" "$DEST2"

# Categories clash: source song.mp3 vs existing sorted/song.mp3.
printf 'MOVED-CONTENT' > "$D/song.mp3"; touch -d '5 days ago' "$D/song.mp3"
printf 'KEEP-CONTENT' > "$SORTED/song.mp3"
# Advanced MoveTo clash: source arc.iso vs existing dest2/arc.iso.
printf 'MOVED-ISO' > "$D/arc.iso"; touch -d '5 days ago' "$D/arc.iso"
printf 'KEEP-ISO' > "$DEST2/arc.iso"

CFG=$(make_config NoClobber '{
  "SourceDir": "'"$D"'", "CutoffDays": 1,
  "LogDir": "'"$T_SCRATCH"'/logs", "LogFile": "noclob.log",
  "DeleteExts": [], "IgnorePatterns": [],
  "AdvancedRules": {".iso": {"Action": "MoveTo", "Destination": "'"$DEST2"'"}},
  "Categories": {"'"$SORTED"'": [".mp3"]}}')

# Preview names the rename before it happens.
run_case DownloadsCleanup --config-path "$CFG" --dry-run
assert_eq "noclob-dry-exit" "0" "$T_RC"
assert_contains "noclob-dry-cat-rename" "$(cat "$T_OUT")" "song (2).mp3"
assert_contains "noclob-dry-adv-rename" "$(cat "$T_OUT")" "arc (2).iso"

run_case DownloadsCleanup --config-path "$CFG"
assert_eq "noclob-real-exit" "0" "$T_RC"
assert_contains "noclob-real-warn" "$(cat "$T_OUT")" "avoid overwriting"
# Pre-existing destinations keep their bytes.
assert_eq "noclob-cat-kept" "KEEP-CONTENT" "$(cat "$SORTED/song.mp3")"
assert_eq "noclob-adv-kept" "KEEP-ISO" "$(cat "$DEST2/arc.iso")"
# Moved files land under suffixed names with their bytes.
assert_eq "noclob-cat-moved" "MOVED-CONTENT" "$(cat "$SORTED/song (2).mp3")"
assert_eq "noclob-adv-moved" "MOVED-ISO" "$(cat "$DEST2/arc (2).iso")"
assert_file_gone "noclob-cat-source-gone" "$D/song.mp3"
assert_file_gone "noclob-adv-source-gone" "$D/arc.iso"

# Pre-seeded (2) rolls over to (3).
printf 'MOVED2' > "$D/song.mp3"; touch -d '5 days ago' "$D/song.mp3"
run_case DownloadsCleanup --config-path "$CFG"
assert_eq "noclob-rollover" "MOVED2" "$(cat "$SORTED/song (3).mp3")"
