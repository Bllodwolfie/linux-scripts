#!/usr/bin/env bash
# Permanently empties the XDG Trash (~/.local/share/Trash). This is a real,
# irreversible delete — files emptied here cannot be recovered. In dry-run
# mode nothing is deleted; the script instead lists what is in the Trash and
# would be permanently lost, as DRYRUN: items for the app's preview UI.
#
# Enumeration reads info/*.trashinfo (Path= original location, DeletionDate=
# trash timestamp) and sizes files/<name> — the direct counterpart of the
# Windows version's Shell.Application Namespace(10) +
# System.Recycle.{DateDeleted,DeletedFrom} properties. All deletions remove
# the files/<name> + info/<name>.trashinfo pair directly, data first then
# metadata (like the Windows $R-then-$I ordering), for whole-trash AND
# filtered runs alike. There is deliberately no `gio trash --empty` fast
# path: on this stack (GLib 2.80) it exits 0 while deleting nothing — a
# silent no-op that would report false success. Each pair is verified gone
# afterwards and warned otherwise. `gio` remains the contract for
# trashing/listing/restoring (all proven working); Dolphin re-reads the
# same directories, so direct pair removal stays coherent.
#
# Home trash only (XDG "home trash"); per-volume trash dirs
# (/.Trash-$UID) are out of scope for v1 and documented as such.
#
# Usage: EmptyTrash.sh [--dry-run] [--config-path FILE] [--include-only-file FILE]
#
# Exit 0 on completed runs (outcome comes from WARN/ERROR lines, as on
# Windows); exit 1 on hard failures (jq missing).

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
# NOTE: no gio requirement — this script never shells out to gio (see header).

# --- config: MinAgeDays (default 0 = everything), negative clamps to 0 ---
MIN_AGE=0
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]]; then
    if ! cfg_usable "$CONFIG_PATH"; then
        warn "EmptyTrash config is corrupt or unreadable at $CONFIG_PATH — using defaults (MinAgeDays=0)."
    else
        if ! MIN_AGE=$(cfg_int "$CONFIG_PATH" "MinAgeDays" "0"); then
            MIN_AGE=0
            warn "MinAgeDays value in $CONFIG_PATH is invalid — using default 0."
        fi
    fi
fi
if [[ "$MIN_AGE" -lt 0 ]]; then
    warn "MinAgeDays is negative ($MIN_AGE) — treating as 0 (delete everything)."
    MIN_AGE=0
fi

# --- trash locations (XDG home trash; spec fallback when $XDG_DATA_HOME unset) ---
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
TRASH_DIR="$DATA_HOME/Trash"
FILES_DIR="$TRASH_DIR/files"
INFO_DIR="$TRASH_DIR/info"

# --- include-only allow-list (preview confirmation), canonicalized ---
declare -A ONLY_MAP=()
ONLY_ACTIVE=0
if [[ -n "$INCLUDE_ONLY_FILE" && -f "$INCLUDE_ONLY_FILE" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ -z "$line" ]] && continue
        ONLY_MAP["$(canonical "$line")"]=1
    done < "$INCLUDE_ONLY_FILE"
    [[ ${#ONLY_MAP[@]} -gt 0 ]] && ONLY_ACTIVE=1
fi

# trashinfo Path= is URL-encoded per spec (%20 etc.).
urldecode() {
    local s="${1//+/ }"
    printf '%b' "${s//%/\\x}" 2>/dev/null || printf '%s' "$1"
}

NOW_EPOCH=$(date +%s)

# Internal item separator: ASCII unit separator (octal 037, non-whitespace),
# so an EMPTY epoch field survives `read` intact. A tab delimiter cannot be
# used here: tab is IFS whitespace, bash collapses the empty field, the size
# shifts into the epoch slot, and dateless items look ~20733 days old.
SEP=$(printf '\037')

# Item fields for one info file: base, target, epoch-or-empty, size.
read_trash_item() { # info-file
    local info="$1" base encoded target dated epoch size payload
    base=$(basename -- "$info" .trashinfo)
    encoded=$(awk '/^Path=/{sub(/^Path=/,""); print; exit}' "$info" 2>/dev/null)
    [[ -z "$encoded" ]] && return 1
    target=$(urldecode "$encoded")
    dated=$(awk '/^DeletionDate=/{sub(/^DeletionDate=/,""); print; exit}' "$info" 2>/dev/null)
    epoch=""
    if [[ -n "$dated" ]]; then
        epoch=$(date -d "$dated" +%s 2>/dev/null) || epoch=""
    fi
    payload="$FILES_DIR/$base"
    size=0
    if [[ -e "$payload" || -L "$payload" ]]; then
        size=$(du -sb -- "$payload" 2>/dev/null | cut -f1) || size=0
        [[ "$size" =~ ^[0-9]+$ ]] || size=0
    fi
    printf '%s%s%s%s%s%s%s\n' "$base" "$SEP" "$target" "$SEP" "$epoch" "$SEP" "$size"
}

# Old-enough test. Missing date: old enough only when MinAgeDays==0
# (delete-everything preserves empty-the-whole-trash; filtered runs
# conservatively skip dateless items — same rule as Windows).
is_old() { # epoch-or-empty
    if [[ -z "$1" ]]; then
        [[ "$MIN_AGE" -eq 0 ]]
        return
    fi
    awk -v now="$NOW_EPOCH" -v e="$1" -v m="$MIN_AGE" \
        'BEGIN{exit !((now - e) / 86400 >= m)}'
}

age_days_fmt() { # epoch-or-empty decimals
    if [[ -z "$1" ]]; then printf 'age unknown'; return; fi
    LC_ALL=C awk -v now="$NOW_EPOCH" -v e="$1" -v d="$2" \
        'BEGIN{printf "%.*f days old", d, (now - e) / 86400}'
}

# Collect items (sorted for stable output/preview order).
mapfile -t ITEMS < <(
    if [[ -d "$INFO_DIR" ]]; then
        for info in "$INFO_DIR"/*.trashinfo; do
            [[ -e "$info" ]] || continue
            read_trash_item "$info" || continue
        done | LC_ALL=C sort
    fi
)

in_only() { # canonical-target
    [[ "$ONLY_ACTIVE" -eq 0 ]] && return 0
    [[ -n "${ONLY_MAP[$1]:-}" ]]
}

if [[ "$DRY_RUN" -eq 1 ]]; then
    del_n=0; skip_n=0; del_sz=0; skip_sz=0
    for row in ${ITEMS[@]+"${ITEMS[@]}"}; do
        IFS="$SEP" read -r base target epoch size <<< "$row"
        canon=$(canonical "$target")
        if ! in_only "$canon"; then continue; fi
        sz=$(fmt_size "$size")
        if is_old "$epoch"; then
            del_n=$(( del_n + 1 )); del_sz=$(( del_sz + size ))
            if [[ -z "$epoch" ]]; then age="age unknown"
            else age=$(age_days_fmt "$epoch" 0); fi
            dryrun_item "Delete" "$target" "$sz, $age, would be permanently deleted"
        else
            skip_n=$(( skip_n + 1 )); skip_sz=$(( skip_sz + size ))
            if [[ -z "$epoch" ]]; then age="age unknown"
            else age=$(age_days_fmt "$epoch" 1); fi
            dryrun_item "Skip" "$target" "$sz, $age — younger than $MIN_AGE days, would be skipped"
        fi
    done
    if [[ "$del_n" -gt 0 || "$skip_n" -gt 0 ]]; then
        printf '%s\n' "Trash dry-run: $del_n item(s) ($(fmt_size "$del_sz")) would be permanently deleted, $skip_n skipped (younger than $MIN_AGE days, $(fmt_size "$skip_sz"))."
    fi
    exit 0
fi

# --- real run: every deletion removes the files/ + info/ pair directly
# --- (data first, then metadata) and verifies it is actually gone, so a run
# --- can never report deletions that did not happen.
delete_pair() { # base -> 0 when both halves are gone
    local base="$1"
    rm -rf -- "$FILES_DIR/$base" 2>/dev/null
    rm -f -- "$INFO_DIR/$base.trashinfo" 2>/dev/null
    [[ ! -e "$FILES_DIR/$base" && ! -L "$FILES_DIR/$base" && ! -e "$INFO_DIR/$base.trashinfo" ]]
}

warned=0
deleted=0
if [[ "$ONLY_ACTIVE" -eq 1 ]]; then
    # Confirmed subset: delete exactly the listed targets.
    for key in "${!ONLY_MAP[@]}"; do
        found=""
        for row in ${ITEMS[@]+"${ITEMS[@]}"}; do
            IFS="$SEP" read -r base target epoch size <<< "$row"
            if [[ "$(canonical "$target")" == "$key" ]]; then found="$base"; break; fi
        done
        if [[ -z "$found" ]]; then
            warn "Skipped trash item (no longer in the Trash): $key"
            warned=$(( warned + 1 ))
            continue
        fi
        if delete_pair "$found"; then
            deleted=$(( deleted + 1 ))
        else
            warn "Skipped locked trash item: $key"
            warned=$(( warned + 1 ))
        fi
    done
else
    # Whole-trash (optionally age-gated): MinAgeDays==0 matches everything
    # including dateless items, so no special case is needed.
    if [[ ${#ITEMS[@]} -eq 0 ]]; then
        printf '%s\n' "Trash is already empty — nothing to delete."
        exit 0
    fi
    matched=0
    for row in ${ITEMS[@]+"${ITEMS[@]}"}; do
        IFS="$SEP" read -r base target epoch size <<< "$row"
        if ! is_old "$epoch"; then continue; fi
        matched=$(( matched + 1 ))
        if delete_pair "$base"; then
            deleted=$(( deleted + 1 ))
        else
            warn "Skipped locked trash item: $target"
            warned=$(( warned + 1 ))
        fi
    done
    if [[ "$matched" -eq 0 ]]; then
        printf '%s\n' "Trash: 0 items older than $MIN_AGE days — nothing to delete."
        exit 0
    fi
fi

if [[ "$warned" -gt 0 ]]; then
    warn "$warned trash item(s) could not be permanently deleted."
else
    printf '%s\n' "Trash: $deleted item(s) permanently deleted."
fi
exit 0
