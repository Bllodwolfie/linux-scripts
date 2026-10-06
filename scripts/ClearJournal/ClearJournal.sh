#!/usr/bin/env bash
# Vacuums the systemd journal after a best-effort export-before-vacuum.
#
# This is the honest Linux counterpart of ClearEventLogs.ps1, not a literal
# translation: journald has no per-log backup/restore (wevtutil epl), so
# "backup" here means a `journalctl -o export` snapshot for inspection — it is
# NOT a restorable log database, and the help text says so. Like the Windows
# version's "never clear what you couldn't back up" rule, a failed export
# aborts the run BEFORE any vacuum. Vacuum itself is retention-global
# (--vacuum-time/--vacuum-size); there is no per-boot deletion primitive, so
# --include-only narrows the EXPORT only, with one WARN saying so.
#
# Scope=user vacuums the user's own journal (no privilege needed).
# Scope=system (default, like the Windows "all logs" default) requires root:
# run via pkexec, e.g. pkexec <app> --elevated-run ClearJournal ... — the
# script refuses system scope as non-root rather than failing obscurely.
#
# Usage: ClearJournal.sh [--dry-run] [--config-path FILE] [--include-only-file FILE]
# Config keys: Scope (user|system, default system), VacuumTime (default 7d),
#   VacuumSize (default empty = skip), BackupDir (default
#   $HOME/Documents/Script_Logs/JournalBackups).
#
# Exit 0 on completed runs (outcome from WARN/ERROR lines, as on Windows);
# exit 1 on hard failures (export failed, vacuum refused, no retention set).

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

# --- config (deviates from ClearEventLogs.ps1 in one place: corrupt config
# --- warns + falls back instead of throwing, like our other scripts) ---
SCOPE="system"
VTIME="7d"
VSIZE=""
BACKUP_DIR="$HOME/Documents/Script_Logs/JournalBackups"
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]]; then
    if ! cfg_usable "$CONFIG_PATH"; then
        warn "ClearJournal config is corrupt or unreadable at $CONFIG_PATH — using defaults (Scope=system, VacuumTime=7d). Check or reset the config via Settings."
    else
        SCOPE=$(cfg_str "$CONFIG_PATH" "Scope" "system")
        VTIME=$(cfg_str "$CONFIG_PATH" "VacuumTime" "7d")
        VSIZE=$(cfg_str "$CONFIG_PATH" "VacuumSize" "")
        BACKUP_DIR=$(cfg_str "$CONFIG_PATH" "BackupDir" "$HOME/Documents/Script_Logs/JournalBackups")
    fi
fi

# Minimal env expansion for config paths (~/$HOME/$USER only — no eval).
expand_user_path() {
    local p="$1"
    [[ "$p" == "~"* ]] && p="$HOME${p:1}"
    p="${p//\$HOME/$HOME}"; p="${p//\$\{HOME\}/$HOME}"
    p="${p//\$USER/$USER}"; p="${p//\$\{USER\}/$USER}"
    printf '%s' "$p"
}
BACKUP_DIR=$(expand_user_path "$BACKUP_DIR")

if [[ "$SCOPE" != "user" && "$SCOPE" != "system" ]]; then
    warn "Scope '$SCOPE' is invalid (want user|system) — using default system."
    SCOPE="system"
fi

# VacuumTime -> retention days (float). Units d/w/m/y/h accepted; anything
# else warns + falls back. The RAW string goes to journalctl (it parses the
# same units natively); the day count only classifies boots for preview.
parse_days() { # raw default -> prints days
    local raw="$1" def="$2" num unit
    if [[ "$raw" =~ ^([0-9]+(\.[0-9]+)?)[[:space:]]*([a-zA-Z]*)$ ]]; then
        num="${BASH_REMATCH[1]}"; unit="${BASH_REMATCH[3],,}"
        case "$unit" in
            ""|d|day|days) LC_ALL=C awk -v n="$num" 'BEGIN{print n}'; return 0 ;;
            h|hour|hours) LC_ALL=C awk -v n="$num" 'BEGIN{print n/24}'; return 0 ;;
            w|week|weeks) LC_ALL=C awk -v n="$num" 'BEGIN{print n*7}'; return 0 ;;
            m|month|months) LC_ALL=C awk -v n="$num" 'BEGIN{print n*30}'; return 0 ;;
            y|year|years) LC_ALL=C awk -v n="$num" 'BEGIN{print n*365}'; return 0 ;;
        esac
    fi
    printf '%s' "$def"; return 1
}
RET_DAYS=""
if ! RET_DAYS=$(parse_days "$VTIME" "7"); then
    RET_DAYS="7"
    warn "VacuumTime '$VTIME' is invalid — using default 7d."
    VTIME="7d"
fi
if [[ -n "$VSIZE" && ! "$VSIZE" =~ ^[0-9]+[KMGT]?$ ]]; then
    warn "VacuumSize '$VSIZE' is invalid (want e.g. 500M) — skipping size vacuum."
    VSIZE=""
fi
if [[ -z "$VTIME" && -z "$VSIZE" ]]; then
    error "No retention set (VacuumTime and VacuumSize both empty) — nothing to do."
    exit 1
fi

JCTL=(journalctl)
[[ "$SCOPE" == "user" ]] && JCTL+=(--user)

if [[ "$SCOPE" == "system" && "$EUID" -ne 0 && "$DRY_RUN" -eq 0 ]]; then
    error "System journal vacuum requires root — run via pkexec (the app elevates automatically)."
    exit 1
fi
if [[ "$SCOPE" == "system" && ! -d /var/log/journal ]]; then
    warn "No persistent system journal (/var/log/journal missing — Storage=volatile?) — vacuum will be a no-op."
fi

disk_usage() {
    local line size
    line=$("${JCTL[@]}" --disk-usage 2>/dev/null | head -1) || { printf 'unknown'; return; }
    size=$(printf '%s' "$line" | grep -oE '[0-9.]+[KMGT]?B?' | head -1)
    [[ -n "$size" ]] && printf '%s' "$size" || printf 'unknown'
}

# Boot list: id, first-entry epoch. Unparseable dates are skipped with a
# single WARN (conservative: never delete what you cannot date).
mapfile -t BOOTS < <(
    "${JCTL[@]}" --list-boots --no-pager 2>/dev/null | awk 'NF>=4 && $1 != "IDX" {print $2, $3, $4}' |
    while read -r id d t; do
        ep=$(date -d "$d $t" +%s 2>/dev/null) || { echo "BAD $id"; continue; }
        printf '%s %s\n' "$id" "$ep"
    done
)
BAD_BOOTS=0
CLEAN_BOOTS=()
for b in ${BOOTS[@]+"${BOOTS[@]}"}; do
    if [[ "$b" == BAD* ]]; then BAD_BOOTS=$(( BAD_BOOTS + 1 )); else CLEAN_BOOTS+=("$b"); fi
done
if [[ "$BAD_BOOTS" -gt 0 ]]; then
    warn "$BAD_BOOTS boot(s) with unreadable dates — skipped conservatively."
fi
NOW_EPOCH=$(date +%s)
CUTOFF_EPOCH=$(LC_ALL=C awk -v n="$NOW_EPOCH" -v d="$RET_DAYS" 'BEGIN{printf "%d", n - d*86400}')
CUTOFF_STR=$(date -d "@$CUTOFF_EPOCH" '+%Y-%m-%d %H:%M:%S')

boot_date() { date -d "@$1" '+%Y-%m-%d' 2>/dev/null || printf 'unknown'; }
is_old_boot() { # epoch
    LC_ALL=C awk -v n="$NOW_EPOCH" -v e="$1" -v d="$RET_DAYS" 'BEGIN{exit !((n - e)/86400 >= d)}'
}

# Include-only narrows the EXPORT (per-boot `journalctl -b`); the vacuum
# itself is retention-global and cannot honor deselection. One WARN, once.
ONLY_ACTIVE=0
ONLY_LIST=()
if [[ -n "$INCLUDE_ONLY_FILE" && -f "$INCLUDE_ONLY_FILE" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ -z "$line" ]] && continue
        ONLY_LIST+=("$line")
    done < "$INCLUDE_ONLY_FILE"
    [[ ${#ONLY_LIST[@]} -gt 0 ]] && ONLY_ACTIVE=1
fi
in_only() { # target
    local t="$1" o
    for o in ${ONLY_LIST[@]+"${ONLY_LIST[@]}"}; do [[ "$o" == "$t" ]] && return 0; done
    return 1
}

DU_BEFORE=$(disk_usage)

if [[ "$DRY_RUN" -eq 1 ]]; then
    del_n=0; skip_n=0
    for b in ${CLEAN_BOOTS[@]+"${CLEAN_BOOTS[@]}"}; do
        id="${b%% *}"; ep="${b##* }"
        target="boot $id ($(boot_date "$ep"))"
        if [[ "$ONLY_ACTIVE" -eq 1 ]] && ! in_only "$target"; then continue; fi
        if is_old_boot "$ep"; then
            del_n=$(( del_n + 1 ))
            dryrun_item "Clear" "$target" "entries before $CUTOFF_STR; would be exported to $BACKUP_DIR, then vacuumed"
        else
            skip_n=$(( skip_n + 1 ))
            dryrun_item "Skip" "$target" "entries within retention ($VTIME) — would be kept"
        fi
    done
    printf '%s\n' "Journal dry-run: $del_n boot(s) older than $VTIME would be vacuumed (disk usage: $DU_BEFORE)."
    exit 0
fi

# --- real run: export first (Windows "never clear what you couldn't back up"),
# --- vacuum second. Export scope: retention window, or selected boots only.
if [[ "$ONLY_ACTIVE" -eq 1 ]]; then
    warn "Vacuum applies retention-globally to all boots; the selection scoped the export backup only."
fi
if ! mkdir -p -- "$BACKUP_DIR" 2>/dev/null; then
    error "Cannot create BackupDir $BACKUP_DIR — aborting before any vacuum."
    exit 1
fi
STAMP=$(date '+%Y%m%d_%H%M%S')
EXPORT="$BACKUP_DIR/journal-$SCOPE-$STAMP.export"
if [[ "$ONLY_ACTIVE" -eq 1 ]]; then
    : > "$EXPORT" || { error "Cannot write export $EXPORT — aborting before any vacuum."; exit 1; }
    for t in ${ONLY_LIST[@]+"${ONLY_LIST[@]}"}; do
        bid="${t#boot }"; bid="${bid%% *}"
        "${JCTL[@]}" -b "$bid" -o export >> "$EXPORT" 2>/dev/null || \
            warn "Export of $t failed — continuing with the remaining selection."
    done
else
    if ! "${JCTL[@]}" --until "$CUTOFF_STR" -o export > "$EXPORT" 2>/dev/null; then
        error "Pre-vacuum export failed — aborting before any vacuum."
        rm -f -- "$EXPORT"
        exit 1
    fi
fi
# An empty export is legitimate (nothing in scope to preserve) but must never
# look like a silent failure: say so explicitly.
if [[ ! -s "$EXPORT" ]]; then
    warn "Pre-vacuum export is empty — no entries in scope exist to preserve."
fi

VAC_ARGS=(--vacuum-time="$VTIME")
[[ -n "$VSIZE" ]] && VAC_ARGS+=(--vacuum-size="$VSIZE")
VAC_OUT=""
if ! VAC_OUT=$("${JCTL[@]}" "${VAC_ARGS[@]}" 2>&1); then
    error "Vacuum failed: $VAC_OUT"
    exit 1
fi
# journalctl exits 0 even when it deleted nothing due to permissions
# ("Failed to delete archived journal ...: Permission denied" on stdout —
# user journal files under /var/log/journal are root-owned on stock Neon).
# Promote those lines to warnings: a run that vacuumed nothing it intended
# to vacuum must never report plain Success. (Same class of lie as the
# Windows note about never trusting ps.HadErrors.)
if grep -qiE 'failed to delete|permission denied' <<< "$VAC_OUT"; then
    while IFS= read -r vl; do
        if grep -qiE 'failed to delete|permission denied' <<< "$vl"; then
            warn "vacuum blocked: $vl"
        fi
    done <<< "$VAC_OUT"
fi

DU_AFTER=$(disk_usage)
printf '%s\n' "Journal cleanup: vacuumed to $VTIME ($DU_BEFORE → $DU_AFTER)."
exit 0
