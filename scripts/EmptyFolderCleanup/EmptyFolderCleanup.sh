#!/usr/bin/env bash
# Recursively removes empty folders under TargetFolder.
# Multi-pass loop: deleting a folder can leave its now-empty parent behind,
# so passes repeat until one removes nothing (a pass that deletes nothing
# cannot make progress — the loop stops instead of spinning forever).
# Folders go through `rmdir` (non-recursive): a folder that turned non-empty
# between scan and delete fails instead of silently losing new contents.
# Permanent deletion, never Trash — but only genuinely EMPTY folders are ever
# passed here, and an empty folder carries no data, so nothing recoverable is
# lost (same justification as the Windows version). The root itself is never
# removed. Hidden entries count (a folder containing dotfiles is not empty).
# Log lines carry the FULL folder path (bare names would be ambiguous across
# nested subfolders).
#
# IgnoreFolders (exact-or-subtree, case-sensitive: Linux filesystems are)
# protects a folder and everything under it. Ignore wins over IncludeOnly.
#
# Usage: EmptyFolderCleanup.sh [--dry-run] [--config-path FILE] [--include-only-file FILE]
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

# --- config ---
TARGET="$HOME/Downloads"
LOGDIR="$HOME/Documents/Script_Logs"
LOGFILE_NAME="CleanupLog.txt"
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]]; then
    if ! cfg_usable "$CONFIG_PATH"; then
        warn "EmptyFolderCleanup config is corrupt or unreadable at $CONFIG_PATH — using defaults (TargetFolder=$HOME/Downloads). Check or reset the config via Settings."
    else
        TARGET=$(cfg_str "$CONFIG_PATH" "TargetFolder" "$HOME/Downloads")
        [[ -z "$TARGET" ]] && TARGET="$HOME/Downloads"
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
    error "Refusing to scan system-critical root: $CANON_TARGET"
    exit 1
fi

# IgnoreFolders: canonical exact-or-subtree set (ignore wins over age and
# over IncludeOnly, in dry-run simulation and on disk alike).
declare -A IGNORE_MAP=()
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]] && cfg_usable "$CONFIG_PATH"; then
    while IFS= read -r raw; do
        [[ -z "${raw// }" ]] && continue
        IGNORE_MAP["$(canonical "$(expand_user_path "$raw")")"]=1
    done < <(cfg_list "$CONFIG_PATH" "IgnoreFolders" 2>/dev/null)
fi
is_ignored() { # canonical-path
    local p="$1" ig
    [[ ${#IGNORE_MAP[@]} -eq 0 ]] && return 1
    [[ -n "${IGNORE_MAP[$p]:-}" ]] && return 0
    for ig in "${!IGNORE_MAP[@]}"; do
        [[ "$p" == "$ig"/* ]] && return 0
    done
    return 1
}

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
is_selected() { # canonical-path
    [[ "$ONLY_ACTIVE" -eq 0 ]] && return 0
    [[ -n "${ONLY_MAP[$1]:-}" ]]
}

# Empty = zero entries at any depth-1 listing (hidden entries count).
is_empty_dir() { # path
    [[ -z $(find "$1" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null) ]]
}

LOGFILE="$LOGDIR/$LOGFILE_NAME"
mkdir -p -- "$LOGDIR" 2>/dev/null

# Missing target: nothing to do (silent, like the Windows Test-Path guard).
[[ -d "$CANON_TARGET" ]] || exit 0

if [[ "$DRY_RUN" -eq 1 ]]; then
    # Simulate the multi-pass loop against a removed-set (not the disk) so
    # parents emptied by deleted children preview exactly as the real run
    # would delete them.
    declare -A SIM_REMOVED=()
    sim_empty() { # path -> 0 when empty ignoring simulated-removed entries
        local e
        while IFS= read -r -d '' e; do
            [[ -n "${SIM_REMOVED[$(canonical "$e")]:-}" ]] && continue
            return 1
        done < <(find "$1" -mindepth 1 -maxdepth 1 -print0 2>/dev/null)
        return 0
    }
    changed=1
    while [[ "$changed" -eq 1 ]]; do
        changed=0
        while IFS= read -r -d '' d; do
            canon=$(canonical "$d")
            [[ -n "${SIM_REMOVED[$canon]:-}" ]] && continue
            is_ignored "$canon" && continue
            is_selected "$canon" || continue
            if sim_empty "$d"; then
                SIM_REMOVED["$canon"]=1
                changed=1
                dryrun_item "Delete" "$canon" "empty folder"
            fi
        done < <(find "$CANON_TARGET" -mindepth 1 -type d -print0 2>/dev/null)
    done
    exit 0
fi

write_log "$LOGFILE" "Cleanup started"
while :; do
    deleted_anything=0
    declare -A UNDEL=()
    while IFS= read -r -d '' d; do
        canon=$(canonical "$d")
        is_ignored "$canon" && continue
        is_selected "$canon" || continue
        is_empty_dir "$d" || continue
        # Non-recursive rmdir: a folder that turned non-empty since the scan
        # fails here instead of losing new contents (Windows $false parity).
        rmdir -- "$d" 2>/dev/null
        if [[ -e "$d" ]]; then
            UNDEL["$canon"]=1
        else
            deleted_anything=1
            write_log "$LOGFILE" "DELETED : $canon"
        fi
    done < <(find "$CANON_TARGET" -mindepth 1 -type d -print0 2>/dev/null)
    for u in ${!UNDEL[@]+"${!UNDEL[@]}"}; do
        warn "Could not remove empty folder: $u"
    done
    [[ "$deleted_anything" -eq 0 ]] && break
done
# Ignored folders stay empty on disk by design — exclude them here too, or a
# run that correctly kept them would still report Warning.
remaining=0
while IFS= read -r -d '' d; do
    canon=$(canonical "$d")
    is_ignored "$canon" && continue
    if is_empty_dir "$d"; then remaining=$(( remaining + 1 )); fi
done < <(find "$CANON_TARGET" -mindepth 1 -type d -print0 2>/dev/null)
if [[ "$remaining" -gt 0 ]]; then
    warn "$remaining empty folder(s) could not be removed (locked or protected)"
fi
write_log "$LOGFILE" "Cleanup finished"
exit 0
