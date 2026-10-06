#!/usr/bin/env bash
# Deletes screenshots older than CutoffDays from the target folder.
# Non-recursive: only files directly in TargetFolder. If the folder does not
# exist yet (fresh profile that never took a screenshot), logs a skip and
# exits cleanly instead of erroring. Log lines carry the self-identifying
# "ScreenshotsCleanup ..." prefix (the shared CleanupLog.txt gives no other
# way to tell which script wrote a line later).
# Deletes go through `gio trash` (recoverable), never `rm`.
#
# Usage: ScreenshotsCleanup.sh [--dry-run] [--config-path FILE] [--include-only-file FILE]
# Exit 0 on completed runs (outcome from WARN/ERROR lines); exit 1 on hard
# failures (relative target dir).

set -u

HERE=$(dirname -- "$(readlink -f -- "$0")")
if [[ ! -f "$HERE/../lib/common.sh" ]]; then
    echo "ERROR: cannot locate scripts/lib/common.sh" >&2; exit 1
fi
# shellcheck disable=SC1091
. "$HERE/../lib/common.sh"

DRY_RUN=0
CONFIG_PATH=""
INCLUDE_ONLY_FILE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dry-run) DRY_RUN=1; shift ;;
        --config-path) CONFIG_PATH="${2:-}"; shift 2 ;;
        --include-only-file) INCLUDE_ONLY_FILE="${2:-}"; shift 2 ;;
        *) error "Unknown argument: $1"; exit 1 ;;
    esac
done

require_jq || exit 1
if ! command -v gio >/dev/null 2>&1; then
    error "gio is required but not installed (package libglib2.0-bin)."
    exit 1
fi

# --- config ---
TARGET="$HOME/Pictures/Screenshots"
CUTOFF=7
LOGDIR="$HOME/Documents/Script_Logs"
LOGFILE_NAME="CleanupLog.txt"
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]]; then
    if ! cfg_usable "$CONFIG_PATH"; then
        warn "ScreenshotsCleanup config is corrupt or unreadable at $CONFIG_PATH — using defaults (TargetFolder=$HOME/Pictures/Screenshots, CutoffDays=7). Check or reset the config via Settings."
    else
        TARGET=$(cfg_str "$CONFIG_PATH" "TargetFolder" "$HOME/Pictures/Screenshots")
        [[ -z "$TARGET" ]] && TARGET="$HOME/Pictures/Screenshots"
        if ! CUTOFF=$(cfg_int "$CONFIG_PATH" "CutoffDays" "7"); then
            CUTOFF=7
            warn "CutoffDays value in $CONFIG_PATH is invalid — using default 7."
        fi
        LOGDIR=$(cfg_str "$CONFIG_PATH" "LogDir" "$HOME/Documents/Script_Logs")
        [[ -z "$LOGDIR" ]] && LOGDIR="$HOME/Documents/Script_Logs"
        LOGFILE_NAME=$(cfg_str "$CONFIG_PATH" "LogFile" "CleanupLog.txt")
        [[ -z "$LOGFILE_NAME" ]] && LOGFILE_NAME="CleanupLog.txt"
    fi
fi
TARGET=$(expand_user_path "$TARGET")
LOGDIR=$(expand_user_path "$LOGDIR")

if [[ "$TARGET" != /* ]]; then
    error "Path must be absolute: $TARGET"
    exit 1
fi
CANON_TARGET=$(canonical "$TARGET")
if ! guard_dangerous_root "$CANON_TARGET"; then
    error "Refusing to clean system-critical root: $CANON_TARGET"
    exit 1
fi
EXPECTED_SCOPE=$(canonical "$HOME/Pictures/Screenshots")
if [[ "$CANON_TARGET" != "$EXPECTED_SCOPE" && "$CANON_TARGET" != "$EXPECTED_SCOPE"/* ]]; then
    warn "Path '$TARGET' is outside expected scope (expected under $HOME/Pictures/Screenshots) — proceeding anyway."
fi

# IgnorePatterns: trimmed, empties dropped; matched against basename only.
IGNORE_PATS=()
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]] && cfg_usable "$CONFIG_PATH"; then
    while IFS= read -r pat; do
        pat="${pat#"${pat%%[![:space:]]*}"}"; pat="${pat%"${pat##*[![:space:]]}"}"
        [[ -z "$pat" ]] && continue
        IGNORE_PATS+=("$pat")
    done < <(cfg_list "$CONFIG_PATH" "IgnorePatterns" 2>/dev/null)
fi

# Include-only allow-list (preview confirmation), canonicalized.
declare -A ONLY_MAP=()
ONLY_ACTIVE=0
if [[ -n "$INCLUDE_ONLY_FILE" && -f "$INCLUDE_ONLY_FILE" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ -z "$line" ]] && continue
        ONLY_MAP["$(canonical "$line")"]=1
    done < "$INCLUDE_ONLY_FILE"
    [[ ${#ONLY_MAP[@]} -gt 0 ]] && ONLY_ACTIVE=1
fi

LOGFILE="$LOGDIR/$LOGFILE_NAME"
mkdir -p -- "$LOGDIR" 2>/dev/null
NOW_EPOCH=$(date +%s)

if [[ "$DRY_RUN" -eq 0 ]]; then
    write_log "$LOGFILE" "ScreenshotsCleanup started"
fi

if [[ ! -d "$CANON_TARGET" ]]; then
    if [[ "$DRY_RUN" -eq 0 ]]; then
        write_log "$LOGFILE" "SKIPPED : Screenshots folder not found: $TARGET"
        write_log "$LOGFILE" "ScreenshotsCleanup finished"
    fi
    exit 0
fi

removed=0
skipped=0
while IFS= read -r -d '' f; do
    name=$(basename -- "$f")
    canon=$(canonical "$f")
    if ! is_old_file "$f" "$CUTOFF" "$NOW_EPOCH"; then continue; fi

    # IgnorePatterns wins over age and over IncludeOnly.
    if [[ ${#IGNORE_PATS[@]} -gt 0 ]] && ignored_by=$(matches_pattern_list "$name" "${IGNORE_PATS[@]}"); then
        if [[ "$DRY_RUN" -eq 1 ]]; then
            dryrun_item "Skip" "$canon" "ignored per ignore pattern: $ignored_by"
        else
            write_log "$LOGFILE" "SKIPPED : $name (ignored per ignore pattern: $ignored_by)"
        fi
        continue
    fi
    if [[ "$ONLY_ACTIVE" -eq 1 && -z "${ONLY_MAP[$canon]:-}" ]]; then continue; fi

    if [[ "$DRY_RUN" -eq 1 ]]; then
        dryrun_item "Delete" "$canon" "$(file_detail "$f")"
        continue
    fi
    if trash_file "$f"; then
        write_log "$LOGFILE" "DELETED : $name"
        removed=$(( removed + 1 ))
    else
        write_log "$LOGFILE" "ERROR   : Failed to delete $name"
        warn "Skipped locked file: $canon"
        skipped=$(( skipped + 1 ))
    fi
done < <(find "$CANON_TARGET" -maxdepth 1 -type f -print0 2>/dev/null)

if [[ "$DRY_RUN" -eq 0 ]]; then
    write_log "$LOGFILE" "ScreenshotsCleanup removed $removed file(s), skipped $skipped locked file(s)"
    write_log "$LOGFILE" "ScreenshotsCleanup finished"
    printf '%s\n' "ScreenshotsCleanup: $removed removed, $skipped skipped (locked or in use)."
fi
exit 0
