#!/usr/bin/env bash
# Deletes regular files under the target folder that are older than CutoffDays.
# Files only — directories are never removed. Deletions are PERMANENT (rm),
# never routed through the XDG Trash: the default target /tmp is tmpfs and
# trashing it to ~/.local/share/Trash would copy across filesystems and
# defeat the purpose. (Linux counterpart of TempCleanup.ps1; the Windows
# version recycled via SHFileOperationW, the dialog-proofing saga in its
# comments does not apply here — rm -f has no UI by construction.)
#
# Usage: TempCleanup.sh [--dry-run] [--config-path FILE] [--include-only-file FILE]
#   --dry-run            emit DRYRUN: items, change nothing
#   --config-path        JSON config (TargetFolder, CutoffDays, IgnoreFolders)
#   --include-only-file  newline-separated canonical paths; when non-empty,
#                        only listed files are touched (preview confirmation)
#
# Exit 0 on completed runs (outcome comes from WARN/ERROR lines, as on
# Windows); exit 1 on hard failures (relative target, dangerous root).

set -u

HERE=$(dirname -- "$(readlink -f -- "$0")")
# Layout is scripts/TempCleanup/TempCleanup.sh + scripts/lib/common.sh both in
# the repo and in the shipped app output, so one relative path covers both.
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

# --- config (defaults mirror the shipped DefaultConfigs/TempCleanup.json) ---
TARGET="/tmp"
CUTOFF=7
IGNORE_RAW=()

if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]]; then
    if ! cfg_usable "$CONFIG_PATH"; then
        warn "TempCleanup config is corrupt or unreadable at $CONFIG_PATH — using defaults (TargetFolder=/tmp, CutoffDays=7). Check or reset the config via Settings."
    else
        TARGET=$(cfg_str "$CONFIG_PATH" "TargetFolder" "/tmp")
        [[ -z "$TARGET" ]] && TARGET="/tmp"
        if ! CUTOFF=$(cfg_int "$CONFIG_PATH" "CutoffDays" "7"); then
            CUTOFF=7
            warn "CutoffDays value in $CONFIG_PATH is invalid — using default 7."
        fi
        if ! mapfile -t IGNORE_RAW < <(cfg_list "$CONFIG_PATH" "IgnoreFolders"); then
            IGNORE_RAW=()
            warn "IgnoreFolders in $CONFIG_PATH is not a list — ignoring it."
        fi
    fi
fi

# --- scope checks (Windows Test-PathScope equivalent, plus a Linux guard) ---
if ! is_absolute "$TARGET"; then
    error "Path must be absolute: $TARGET"
    exit 1
fi
CANON_TARGET=$(canonical "$TARGET")
if ! guard_dangerous_root "$CANON_TARGET"; then
    error "Refusing to clean system-critical root: $CANON_TARGET"
    exit 1
fi

if [[ ! -d "$CANON_TARGET" ]]; then
    warn "Target folder '$TARGET' does not exist or is not accessible — skipping."
    if [[ "$DRY_RUN" -eq 0 ]]; then
        printf '%s\n' "Temp cleanup: 0 deleted, 0 skipped (locked or in use)."
    fi
    exit 0
fi

# --- ignore list: canonical, exact-or-subtree match (ignore wins over age
# --- and over --include-only, as on Windows; matching is case-sensitive:
# --- Linux filesystems are case-sensitive, unlike Windows) ---
declare -A IGNORE_MAP=()
for raw in ${IGNORE_RAW[@]+"${IGNORE_RAW[@]}"}; do
    [[ -z "${raw// }" ]] && continue
    IGNORE_MAP["$(canonical "$raw")"]=1
done

is_ignored() { # canonical-path
    local p="$1" ig
    [[ ${#IGNORE_MAP[@]} -eq 0 ]] && return 1
    [[ -n "${IGNORE_MAP[$p]:-}" ]] && return 0
    for ig in "${!IGNORE_MAP[@]}"; do
        [[ "$p" == "$ig"/* ]] && return 0
    done
    return 1
}

# --- include-only allow-list (preview confirmation) ---
declare -A ONLY_MAP=()
ONLY_ACTIVE=0
if [[ -n "$INCLUDE_ONLY_FILE" && -f "$INCLUDE_ONLY_FILE" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ -z "$line" ]] && continue
        ONLY_MAP["$(canonical "$line")"]=1
    done < "$INCLUDE_ONLY_FILE"
    [[ ${#ONLY_MAP[@]} -gt 0 ]] && ONLY_ACTIVE=1
fi

# --- enumerate: regular files (-type f; symlinks skipped, safer) older than
# --- the cutoff. -mmin +N matches "strictly older than N days" like the
# --- Windows LastWriteTime < now-CutoffDays comparison. Negative cutoff
# --- matches nothing (same as Windows: a future cutoff excludes everything).
deleted=0
skipped=0
if [[ "$CUTOFF" -ge 0 ]]; then
    cutoff_min=$(( CUTOFF * 1440 ))
    while IFS= read -r -d '' f; do
        canon=$(canonical "$f")
        if is_ignored "$canon"; then continue; fi
        if [[ "$ONLY_ACTIVE" -eq 1 && -z "${ONLY_MAP[$canon]:-}" ]]; then continue; fi
        if [[ "$DRY_RUN" -eq 1 ]]; then
            size=$(stat -c %s -- "$f" 2>/dev/null || echo 0)
            mdate=$(stat -c %y -- "$f" 2>/dev/null | cut -d' ' -f1 || echo unknown)
            mb=$(LC_ALL=C awk -v s="$size" 'BEGIN{printf "%.1f", s/1048576}')
            dryrun_item "Delete" "$canon" "$mb MB, last modified $mdate"
        else
            if rm -f -- "$f" 2>/dev/null; then
                deleted=$(( deleted + 1 ))
            else
                warn "Skipped locked file: $canon"
                skipped=$(( skipped + 1 ))
            fi
        fi
    done < <(find "$CANON_TARGET" -type f -mmin +"$cutoff_min" -print0 2>/dev/null)
fi

if [[ "$DRY_RUN" -eq 0 ]]; then
    printf '%s\n' "Temp cleanup: $deleted deleted, $skipped skipped (locked or in use)."
fi
exit 0
