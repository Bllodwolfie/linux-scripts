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
# per-boot selections are IGNORED (warned): the export is always one stream
# covering everything the vacuum can delete (--until cutoff; whole journal
# when a size cap is set, since oldest-first deletion order is
# journald-internal).
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

# --- caller identity (privileged tier): $HOME and ~ in BackupDir (and its
# --- default) expand from the INVOKING user's passwd entry — never root's
# --- HOME, which is all an elevated process sees. Unprivileged tier uses our
# --- own $HOME. Unresolvable uid fails closed before any config is trusted.
CALLER_HOME="$HOME"
CALLER_USER="${USER:-}"
if [[ -n "${PKEXEC_UID:-}" ]]; then
    CALLER_HOME="$(caller_home_for_uid "$PKEXEC_UID")" || {
        error "Cannot resolve home directory for uid $PKEXEC_UID — aborting before any vacuum."
        exit 1
    }
    CALLER_USER="$(caller_name_for_uid "$PKEXEC_UID")"
fi

# --- config (deviates from ClearEventLogs.ps1 in one place: corrupt config
# --- warns + falls back instead of throwing, like our other scripts) ---
SCOPE="system"
VTIME="7d"
VSIZE=""
BACKUP_DIR="$CALLER_HOME/Documents/Script_Logs/JournalBackups"
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]]; then
    if ! cfg_usable "$CONFIG_PATH"; then
        warn "ClearJournal config is corrupt or unreadable at $CONFIG_PATH — using defaults (Scope=system, VacuumTime=7d). Check or reset the config via Settings."
    else
        SCOPE=$(cfg_str "$CONFIG_PATH" "Scope" "system")
        VTIME=$(cfg_str "$CONFIG_PATH" "VacuumTime" "")
        # Present-but-empty is not the silent-default case (missing key):
        # say so loudly, then fall back like any other invalid value.
        if [[ -z "$VTIME" ]]; then
            if jq -e --arg k "VacuumTime" 'has($k)' -- "$CONFIG_PATH" >/dev/null 2>&1; then
                warn "VacuumTime is empty — using default 7 days."
            fi
            VTIME="7 days"
        fi
        VSIZE=$(cfg_str "$CONFIG_PATH" "VacuumSize" "")
        BACKUP_DIR=$(cfg_str "$CONFIG_PATH" "BackupDir" "$CALLER_HOME/Documents/Script_Logs/JournalBackups")
    fi
fi

# Minimal env expansion for config paths lives in common.sh
# (expand_user_path: leading ~ plus $HOME/$USER, no eval — from the caller
# identity above, never root's environment when elevated).
BACKUP_DIR=$(expand_user_path "$BACKUP_DIR" "$CALLER_HOME" "$CALLER_USER")

# --- BackupDir hardening (G-4/G-5): this path is root-wielded when elevated
# --- but read from a user-writable config, so it is validated BEFORE any
# --- root/vacuum logic. Canonicalise, refuse protected roots, refuse
# --- symlinked or foreign-owned existing components (expected owner = the
# --- pkexec caller when elevated, else ourselves).
EXPECTED_OWNER="${PKEXEC_UID:-$EUID}"
BACKUP_DIR="${BACKUP_DIR%/}"
CANON_BACKUP=$(canonical "$BACKUP_DIR")
if ! guard_dangerous_root "$CANON_BACKUP" "$CALLER_HOME"; then
    error "BackupDir $BACKUP_DIR resolves to a protected system path — refusing (it would run as root when elevated)."
    exit 1
fi
# Component walk on the UNRESOLVED path: canonical() resolves symlinks away,
# which would hide a planted link. Every existing prefix must itself be a
# non-symlink owned by the invoking user or root; non-existent tails are
# skipped (mkdir -p creates them as EUID below).
_rest="$BACKUP_DIR"
while [[ -n "$_rest" && "$_rest" != "/" && "$_rest" != "." ]]; do
    if [[ -L "$_rest" ]]; then
        error "BackupDir $BACKUP_DIR contains a symlinked component ($_rest) — refusing."
        exit 1
    fi
    if [[ -e "$_rest" ]]; then
        _owner=$(stat -c %u -- "$_rest" 2>/dev/null || echo "")
        if [[ "$_owner" != "$EXPECTED_OWNER" && "$_owner" != "0" ]]; then
            error "BackupDir $BACKUP_DIR is not owned by the invoking user (uid $_owner at $_rest) — refusing."
            exit 1
        fi
    fi
    _rest=$(dirname -- "$_rest")
done

if [[ "$SCOPE" != "user" && "$SCOPE" != "system" ]]; then
    warn "Scope '$SCOPE' is invalid (want user|system) — using default system."
    SCOPE="system"
fi

# VacuumTime -> retention days (float). Unit table mirrors journalctl
# --vacuum-time (systemd.time) EXACTLY — including month=30.44d and
# year=365.25d — so the preview/export math and the vacuum agree on every
# value journalctl accepts. Bare numbers are days here (normalized + warned
# at the call site; journalctl would read seconds). Two deliberate
# departures, both loud: capital M is months for journalctl (lowercase m is
# minutes — checked before folding case); sub-second units below ms are
# rejected (vacuum granularity).
parse_days() { # raw default -> prints days
    local raw="$1" def="$2" num unit
    if [[ "$raw" =~ ^([0-9]+(\.[0-9]+)?)[[:space:]]*([a-zA-Z]*)$ ]]; then
        num="${BASH_REMATCH[1]}"; unit="${BASH_REMATCH[3]}"
        if [[ "$unit" == "M" ]]; then unit="month"; else unit="${unit,,}"; fi
        case "$unit" in
            ""|d|day|days) LC_ALL=C awk -v n="$num" 'BEGIN{print n}'; return 0 ;;
            us|usec) LC_ALL=C awk -v n="$num" 'BEGIN{print n/86400000000}'; return 0 ;;
            ms|msec) LC_ALL=C awk -v n="$num" 'BEGIN{print n/86400000}'; return 0 ;;
            s|sec|second|seconds) LC_ALL=C awk -v n="$num" 'BEGIN{print n/86400}'; return 0 ;;
            m|min|minute|minutes) LC_ALL=C awk -v n="$num" 'BEGIN{print n/1440}'; return 0 ;;
            h|hr|hour|hours) LC_ALL=C awk -v n="$num" 'BEGIN{print n/24}'; return 0 ;;
            w|week|weeks) LC_ALL=C awk -v n="$num" 'BEGIN{print n*7}'; return 0 ;;
            month|months) LC_ALL=C awk -v n="$num" 'BEGIN{print n*30.44}'; return 0 ;;
            y|year|years) LC_ALL=C awk -v n="$num" 'BEGIN{print n*365.25}'; return 0 ;;
        esac
    fi
    printf '%s' "$def"; return 1
}
RET_DAYS=""
if ! RET_DAYS=$(parse_days "$VTIME" "7"); then
    RET_DAYS="7"
    warn "VacuumTime '$VTIME' is invalid — using default 7 days."
    VTIME="7 days"
fi
# Bare numbers are days for the preview/export math but seconds for
# journalctl: normalize to an explicit unit (warn EVERY run) so the vacuum
# receives exactly what the preview computed.
VTIME_BARE_NOTE=""
if [[ "$VTIME" =~ ^[0-9]+(\.[0-9]+)?$ ]]; then
    warn "VacuumTime '$VTIME' has no unit — reading as $VTIME days."
    VTIME_BARE_NOTE=" (bare number read as days)"
    VTIME="$VTIME days"
fi
# Zero / absurd retentions never reach journalctl: --vacuum-time=0 lifts the
# time limit silently, and far-future values are almost surely typos.
if ! LC_ALL=C awk -v d="$RET_DAYS" 'BEGIN{exit !(d > 0 && d <= 36525)}'; then
    warn "VacuumTime '$VTIME' is out of range (need more than 0 days, at most 100 years) — using default 7 days."
    RET_DAYS="7"
    VTIME="7 days"
    VTIME_BARE_NOTE=""
fi
if [[ -n "$VSIZE" && ! "$VSIZE" =~ ^[0-9]+[KMGT]?$ ]]; then
    warn "VacuumSize '$VSIZE' is invalid (want e.g. 500M) — skipping size vacuum."
    VSIZE=""
fi
# Explicit rule line, derived from the SAME normalized values the vacuum
# receives: bare VacuumTime was rewritten to "<N> days" above, and a bare
# VacuumSize is bytes in both worlds (journalctl suffixes are base-1024).
RULE_LINE="Rule: keep entries newer than $VTIME$VTIME_BARE_NOTE"
if [[ -n "$VSIZE" ]]; then
    if [[ "$VSIZE" =~ ^[0-9]+$ ]]; then RULE_LINE+="; size cap $VSIZE bytes"
    else RULE_LINE+="; size cap $VSIZE"; fi
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
# D-SC: integer compare against the precomputed cutoff (was: one awk fork
# per boot). Same instant the export --until uses, so preview and export
# can never disagree by a float-truncation second.
is_old_boot() { # epoch
    [[ "$1" -le "$CUTOFF_EPOCH" ]]
}

# Include-only is accepted for CLI compatibility but NEVER honored here:
# the effect is retention-global (there is no per-boot deletion primitive),
# so any selection would lie. The app no longer sends one for this script.
if [[ -n "$INCLUDE_ONLY_FILE" && -s "$INCLUDE_ONLY_FILE" ]]; then
    warn "Per-boot selections are not honored for ClearJournal (the vacuum is retention-global); running the full retention export."
fi

DU_BEFORE=$(disk_usage)

# Export plan (preview honesty, S1): stream exactly what the real run would
# export and count entries + bytes in one pass, discarded. A size cap forces
# a whole-journal backup (see the real branch: oldest-first deletion order
# is journald-internal and cannot be replicated exactly). Never fails the
# preview: unreadable journals degrade to "unknown", never to a lie.
EXP_FILTER=(--until "$CUTOFF_STR")
EXP_SCOPE_NOTE=""
if [[ -n "$VSIZE" ]]; then
    EXP_FILTER=()
    EXP_SCOPE_NOTE=" (size cap $VSIZE set: whole-journal backup)"
fi
EXP_N="unknown"; EXP_MB="unknown"
if EXP_TMP_N=$(mktemp 2>/dev/null) && EXP_TMP_B=$(mktemp 2>/dev/null); then
    if "${JCTL[@]}" "${EXP_FILTER[@]}" -o export 2>/dev/null \
        | tee >(grep -a -c '^__REALTIME_TIMESTAMP=' >"$EXP_TMP_N" 2>/dev/null) 2>/dev/null \
        | wc -c >"$EXP_TMP_B" 2>/dev/null; then
        wait 2>/dev/null
        EXP_N=$(cat -- "$EXP_TMP_N" 2>/dev/null || echo unknown)
        EXP_B=$(cat -- "$EXP_TMP_B" 2>/dev/null || echo unknown)
        [[ "$EXP_N" =~ ^[0-9]+$ ]] || EXP_N="unknown"
        if [[ "$EXP_B" =~ ^[0-9]+$ ]]; then
            EXP_MB=$(LC_ALL=C awk -v b="$EXP_B" 'BEGIN{printf "%.1f", b/1048576}')
        fi
    fi
    rm -f -- "$EXP_TMP_N" "$EXP_TMP_B"
fi

if [[ "$DRY_RUN" -eq 1 ]]; then
    del_n=0; skip_n=0
    for b in ${CLEAN_BOOTS[@]+"${CLEAN_BOOTS[@]}"}; do
        id="${b%% *}"; ep="${b##* }"
        target="boot $id ($(boot_date "$ep"))"
        if is_old_boot "$ep"; then
            del_n=$(( del_n + 1 ))
            dryrun_item "Clear" "$target" "entries before $CUTOFF_STR; would be exported to $BACKUP_DIR, then vacuumed"
        else
            skip_n=$(( skip_n + 1 ))
            dryrun_item "Skip" "$target" "entries within retention ($VTIME) — would be kept"
        fi
    done
    printf '%s\n' "Journal dry-run: $del_n boot(s) older than $VTIME would be vacuumed (disk usage: $DU_BEFORE)."
    printf '%s\n' "$RULE_LINE; vacuum would delete older archived journals."
    printf '%s\n' "Export destination: $BACKUP_DIR/journal-$SCOPE-<timestamp>-<pid>-<random>.export"
    printf '%s\n' "Export plan: $EXP_N entries (~$EXP_MB MB)$EXP_SCOPE_NOTE; then vacuum deletes archived journals older than $CUTOFF_STR."
    if [[ "$del_n" -eq 0 && -z "$VSIZE" ]]; then
        printf '%s\n' "Nothing older than $VTIME: nothing would be exported or deleted."
    elif [[ "$del_n" -eq 0 ]]; then
        printf '%s\n' "No entries older than $VTIME, but the size cap $VSIZE may still vacuum oldest archived journals (covered by the whole-journal backup)."
    fi
    exit 0
fi

# --- real run: export first (Windows "never clear what you couldn't back up"),
# --- vacuum second. S1 INVARIANT: ONE export covering everything the vacuum
# --- can delete — the same --until cutoff for the time vacuum. There is no
# --- per-boot deletion primitive, so per-boot selections are ignored (warned
# --- above): exporting a subset while vacuuming globally deleted data the
# --- preview marked Skip (2026-10-12: a 215 MB full-journal export at a
# --- 365-day cutoff that matched nothing).
# --- Size cap: --vacuum-size drops oldest archived journals first down to the
# --- cap — an order set by journald internals (file seqnums/rotation state)
# --- the script cannot replicate exactly. The only computable superset that
# --- provably covers the deletion set is the whole journal, so a size cap
# --- forces a whole-journal backup (stated loudly, cost and all).
STAMP=$(date '+%Y%m%d_%H%M%S')
# Unpredictable name (H-7: seconds-only STAMP let an attacker pre-plant the
# export symlink).
EXPORT="$BACKUP_DIR/journal-$SCOPE-$STAMP-$$-$RANDOM.export"

# --- dropped-privilege writer (G-4): root NEVER opens the user path — not
# --- even to create it. All export bytes flow journalctl -> pipe -> setpriv
# --- (uid:gid of the pkexec caller, else ourselves) -> cat, which opens the
# --- file exactly once with noclobber (O_EXCL: plants abort). The upfront
# --- BackupDir validation stays as defence in depth (clear errors) plus a
# --- mkdir backstop; setpriv is util-linux (Priority: required), hard fail
# --- if missing. --clear-groups needs privilege (setgroups EPERM as user,
# --- verified), so unprivileged runs keep their own groups instead.
WRITE_UID="${PKEXEC_UID:-$EUID}"
if ! command -v setpriv >/dev/null 2>&1; then
    error "setpriv is required for the privilege-dropping export writer — aborting before any vacuum."
    exit 1
fi
WRITE_GID="$(id -g -- "$WRITE_UID" 2>/dev/null)" || {
    error "Cannot resolve gid for uid $WRITE_UID — aborting before any vacuum."
    exit 1
}
SETPRIV_FLAGS=(--reuid="$WRITE_UID" --regid="$WRITE_GID" --no-new-privs)
if [[ "$EUID" -eq 0 ]]; then SETPRIV_FLAGS+=(--clear-groups); else SETPRIV_FLAGS+=(--keep-groups); fi
run_as_writer() { setpriv "${SETPRIV_FLAGS[@]}" -- "$@"; }

if ! run_as_writer mkdir -p -- "$BACKUP_DIR" 2>/dev/null; then
    error "Cannot create BackupDir $BACKUP_DIR — aborting before any vacuum."
    exit 1
fi
# One writer stream for the whole export: the path is opened exactly once,
# so a mid-run BackupDir swap cannot redirect later writes. Export failure
# (journalctl OR writer — PIPESTATUS keeps the two apart) aborts before any
# vacuum: a missing or partial backup must never precede deletion.
if [[ -n "$VSIZE" ]]; then
    warn "VacuumSize $VSIZE caps total size (oldest archived journals first); backing up the whole journal so the vacuum cannot delete anything unexported."
    EXP_FILTER=()
else
    EXP_FILTER=(--until "$CUTOFF_STR")
fi
"${JCTL[@]}" "${EXP_FILTER[@]}" -o export 2>/dev/null | run_as_writer sh -c 'set -C; exec cat > "$1"' _ "$EXPORT" 2>/dev/null
pl=("${PIPESTATUS[@]}")
if [[ "${pl[1]}" -ne 0 ]]; then
    error "Cannot create export $EXPORT exclusively (already exists?) — aborting before any vacuum."
    exit 1
fi
if [[ "${pl[0]}" -ne 0 ]]; then
    error "Pre-vacuum export failed — aborting before any vacuum."
    rm -f -- "$EXPORT"
    exit 1
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
printf '%s\n' "$RULE_LINE; vacuum deleted older archived journals."
exit 0
