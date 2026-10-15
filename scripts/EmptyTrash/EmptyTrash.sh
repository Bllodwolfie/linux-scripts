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
# so an EMPTY epoch field survives intact. (A tab delimiter cannot be used:
# tab is IFS whitespace, bash collapses the empty field, the size shifts
# into the epoch slot, and dateless items look ~20733 days old.)
SEP=$(printf '\037')

# Split one NUL-collected row into ROW_* fields (A-1). Plain `read` cannot
# be used here: read is line-oriented and stops at the first newline, which
# re-splits rows whose base/target legitimately contain newlines.
split_row() { # row
    local rest="$1"
    ROW_BASE="${rest%%"$SEP"*}"; rest="${rest#*"$SEP"}"
    ROW_TARGET="${rest%%"$SEP"*}"; rest="${rest#*"$SEP"}"
    ROW_EPOCH="${rest%%"$SEP"*}"; rest="${rest#*"$SEP"}"
    ROW_SIZE="$rest"
}

# Item fields for one info file: base, target, epoch-or-empty, size.
# D-SC: pure-bash parse (was: basename + 2x awk + du + cut + date forks per
# item; date -d and du stay — real syscalls, not shell-outs-per-line).
read_trash_item() { # info-file
    local info="$1" base encoded target dated epoch size payload line
    base="${info##*/}"; base="${base%.trashinfo}"
    encoded=""; dated=""; local seen_path=0 seen_date=0
    while IFS= read -r line || [[ -n "$line" ]]; do
        if [[ "$seen_path" -eq 0 && "$line" == Path=* ]]; then
            encoded="${line#Path=}"; seen_path=1
        elif [[ "$seen_date" -eq 0 && "$line" == DeletionDate=* ]]; then
            dated="${line#DeletionDate=}"; seen_date=1
        fi
        [[ "$seen_path" -eq 1 && "$seen_date" -eq 1 ]] && break
    done < "$info" 2>/dev/null
    [[ -n "$encoded" ]] || return 1
    target=$(urldecode "$encoded")
    epoch=""
    if [[ -n "$dated" ]]; then
        epoch=$(date -d "$dated" +%s 2>/dev/null) || epoch=""
    fi
    payload="$FILES_DIR/$base"
    size=0
    if [[ -e "$payload" || -L "$payload" ]]; then
        size=$(du -sb -- "$payload" 2>/dev/null); size="${size%%$'\t'*}"
        [[ "$size" =~ ^[0-9]+$ ]] || size=0
    fi
    # NUL-terminated (A-1): base/target may contain newlines (Path= %0A);
    # newline-terminated rows split and caused false counts + orphans.
    printf '%s%s%s%s%s%s%s\0' "$base" "$SEP" "$target" "$SEP" "$epoch" "$SEP" "$size"
}

# Old-enough test. Missing date: old enough only when MinAgeDays==0
# (delete-everything preserves empty-the-whole-trash; filtered runs
# conservatively skip dateless items — same rule as Windows).
# D-SC: integer arithmetic (was: one awk fork per tested item).
is_old() { # epoch-or-empty
    if [[ -z "$1" ]]; then
        [[ "$MIN_AGE" -eq 0 ]]
        return
    fi
    [[ "$1" =~ ^[0-9]+$ ]] || return 1
    (( NOW_EPOCH - $1 >= MIN_AGE * 86400 ))
}

age_days_fmt() { # epoch-or-empty decimals
    if [[ -z "$1" ]]; then printf 'age unknown'; return; fi
    local ds=$(( NOW_EPOCH - $1 ))
    if [[ "$2" == "0" ]]; then printf '%d days old' $(( (ds + 43200) / 86400 )); return; fi
    local t=$(( (ds * 10 + 43200) / 86400 ))
    printf '%d.%d days old' $(( t / 10 )) $(( t % 10 ))
}

# Collect items (sorted for stable output/preview order). NUL-delimited end
# to end (A-1): mapfile -d '' never splits a row, and split_row (not the
# line-oriented read) parses fields, so embedded newlines survive whole.
mapfile -d '' -t ITEMS < <(
    if [[ -d "$INFO_DIR" ]]; then
        for info in "$INFO_DIR"/*.trashinfo; do
            [[ -e "$info" ]] || continue
            read_trash_item "$info" || continue
        done | LC_ALL=C sort -z
    fi
)

in_only() { # canonical-target
    [[ "$ONLY_ACTIVE" -eq 0 ]] && return 0
    [[ -n "${ONLY_MAP[$1]:-}" ]]
}

# Batch indexes (D-SC): canonical target + epoch per base, and the reverse
# base per canonical target — built ONCE after collection. The confirmed-
# subset run nested every selection inside every row with a canonical fork
# per comparison (O(K×N) realpath execs); the dry-run re-canonicalized each
# row on every scan. All loops below are hash lookups: zero forks.
declare -A CANON_BY_BASE=() BASE_BY_CANON=() EPOCH_BY_BASE=()
for row in ${ITEMS[@]+"${ITEMS[@]}"}; do
    split_row "$row"
    _canon=$(canonical "$ROW_TARGET")
    CANON_BY_BASE["$ROW_BASE"]="$_canon"
    EPOCH_BY_BASE["$ROW_BASE"]="$ROW_EPOCH"
    [[ -z "${BASE_BY_CANON[$_canon]:-}" ]] && BASE_BY_CANON["$_canon"]="$ROW_BASE"
done

if [[ "$DRY_RUN" -eq 1 ]]; then
    del_n=0; skip_n=0; del_sz=0; skip_sz=0
    for row in ${ITEMS[@]+"${ITEMS[@]}"}; do
        split_row "$row"; base="$ROW_BASE"; target="$ROW_TARGET"; epoch="$ROW_EPOCH"; size="$ROW_SIZE"
        canon="${CANON_BY_BASE[$base]:-}"
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
# DELETE_ERR holds rm's stderr on failure for the WARN label (A-2).
DELETE_ERR=""
delete_pair() { # base -> 0 when both halves are gone
    local base="$1" ferr ierr
    DELETE_ERR=""
    # Security: refuse empty/unsafe base (e.g. a planted ".trashinfo" info
    # file would otherwise yield base="" and delete $FILES_DIR itself).
    # Bases come from info-file names and must never contain a path separator.
    [[ -n "$base" ]] || return 1
    case "$base" in
        *"/"*|"."|"..") return 1 ;;
    esac
    ferr=$(rm -rf -- "$FILES_DIR/$base" 2>&1) || DELETE_ERR+="files: $ferr. "
    ierr=$(rm -f -- "$INFO_DIR/$base.trashinfo" 2>&1) || DELETE_ERR+="info: $ierr. "
    if [[ ! -e "$FILES_DIR/$base" && ! -L "$FILES_DIR/$base" && ! -e "$INFO_DIR/$base.trashinfo" ]]; then
        DELETE_ERR=""
        return 0
    fi
    return 1
}
# Short cause label for a failed delete_pair (A-2): Linux rm failures are
# permissions, read-only mounts or busy files — never "locked" (no such
# thing for rm). Unknown errors print verbatim (locale-proof details).
delete_cause() {
    local e="${DELETE_ERR,,}"
    case "$e" in
        *"permission denied"*) printf 'Permission denied' ;;
        *"read-only file system"*) printf 'Read-only filesystem' ;;
        *"device or resource busy"*) printf 'In use (busy)' ;;
        *) printf 'Failed (%s)' "$(head -n 1 <<< "${DELETE_ERR:-unknown error}" | cut -c1-120)" ;;
    esac
}

warned=0
deleted=0
if [[ "$ONLY_ACTIVE" -eq 1 ]]; then
    # Confirmed subset: delete exactly the listed targets — but re-derive the
    # age verdict (S1): a checked Skip row (younger than MinAgeDays) must
    # never be deleted just because it was listed.
    for key in "${!ONLY_MAP[@]}"; do
        base="${BASE_BY_CANON[$key]:-}"
        if [[ -z "$base" ]]; then
            warn "Skipped trash item (no longer in the Trash): $key"
            warned=$(( warned + 1 ))
            continue
        fi
        foundepoch="${EPOCH_BY_BASE[$base]}"
        if ! is_old "$foundepoch"; then
            warn "Skipped trash item (younger than $MIN_AGE days — preview marked it Skip): $key"
            warned=$(( warned + 1 ))
            continue
        fi
        if delete_pair "$base"; then
            deleted=$(( deleted + 1 ))
        else
            warn "Could not permanently delete $key: $(delete_cause)"
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
        split_row "$row"; base="$ROW_BASE"; target="$ROW_TARGET"; epoch="$ROW_EPOCH"; size="$ROW_SIZE"
        if ! is_old "$epoch"; then continue; fi
        matched=$(( matched + 1 ))
        if delete_pair "$base"; then
            deleted=$(( deleted + 1 ))
        else
            warn "Could not permanently delete $target: $(delete_cause)"
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
