#!/usr/bin/env bash
# Shared helpers for linux-scripts maintenance scripts.
#
# Log protocol (stdout, consumed by BashScriptExecutor — mirrors the
# OUTPUT:/INFO:/WARN:/ERROR: convention of the Windows PowerShell version):
#   INFO:  <msg>    informational line
#   WARN:  <msg>    non-fatal problem; marks the run Warning, never aborts
#   ERROR: <msg>    fatal problem for this item/run
#   DRYRUN: {...}   one JSON dry-run item {action,target,detail}
# Anything else on stdout is kept as plain output.
#
# Config files are JSON (same shape as the app writes). Parsed with jq,
# which is a runtime dependency of the suite (packaging declares it).
# Missing config file  -> silent defaults (first run before seeding).
# Present but corrupt  -> caller warns, then defaults (never abort).

# Never `set -e`: a per-file failure must warn-and-continue, not kill the run.
set -u

# S3: private by default. Logs, exports, reports and walk temps land 0600 —
# journal contents and file paths are not group/other-readable. Every script
# sources this file, so one line covers all of them (including the setpriv
# export writer, which inherits this umask through pkexec).
umask 077

info()  { printf 'INFO: %s\n' "$*"; }
warn()  { printf 'WARN: %s\n' "$*"; }
error() { printf 'ERROR: %s\n' "$*"; }

# DRYRUN item emitter. Pure-bash JSON escaping (D-SC): the old jq -n fork
# cost one process per previewed file (200 files = 200+ jq execs). Output
# stays byte-compatible with jq -c {action,target,detail} for the app's
# System.Text.Json parser (key order preserved).
json_escape() { # text -> escaped for a double-quoted JSON string
    local s="$1"
    case "$s" in
        *'"'*|*'\'*|*[[:cntrl:]]*) ;;
        *) printf '%s' "$s"; return 0 ;;
    esac
    local out="" i ch ord
    local LC_ALL=C
    for (( i=0; i<${#s}; i++ )); do
        ch="${s:$i:1}"
        case "$ch" in
            '"') out+='\"' ;;
            '\') out+='\\' ;;
            $'\n') out+='\n' ;;
            $'\t') out+='\t' ;;
            $'\r') out+='\r' ;;
            [[:cntrl:]]) printf -v ord '%d' "'$ch"; printf -v ch '\\u%04x' "$ord"; out+="$ch" ;;
            *) out+="$ch" ;;
        esac
    done
    printf '%s' "$out"
}
dryrun_item() { # action target detail
    local a t d
    a=$(json_escape "$1"); t=$(json_escape "$2"); d=$(json_escape "$3")
    printf 'DRYRUN: {"action":"%s","target":"%s","detail":"%s"}\n' "$a" "$t" "$d"
}

require_jq() {
    if ! command -v jq >/dev/null 2>&1; then
        error "jq is required but not installed (sudo apt install jq)."
        return 1
    fi
}

# True when the file exists and parses as JSON.
cfg_usable() { # file
    [[ -f "$1" ]] && jq -e . -- "$1" >/dev/null 2>&1
}

# String value for key, or default when missing/null/wrong-type.
cfg_str() { # file key default
    local v
    v=$(jq -r --arg k "$2" '.[$k] // empty' -- "$1" 2>/dev/null) || { printf '%s' "$3"; return 0; }
    if [[ -z "$v" ]]; then printf '%s' "$3"; else printf '%s' "$v"; fi
}

# Integer value for key, or default. Returns 1 when present-but-invalid
# (caller warns and keeps the default).
cfg_int() { # file key default
    local v
    v=$(jq -r --arg k "$2" 'if .[$k] == null then "" elif .[$k] | type == "number" then (.[$k] | tostring) else (.[$k] | tostring) end' -- "$1" 2>/dev/null) || { printf '%s' "$3"; return 0; }
    if [[ -z "$v" ]]; then printf '%s' "$3"; return 0; fi
    if [[ "$v" =~ ^-?[0-9]+$ ]]; then printf '%s' "$v"; return 0; fi
    printf '%s' "$3"; return 1
}

# Array elements for key, one per line. Empty when missing. Returns 1 when
# present-but-not-an-array (caller warns).
cfg_list() { # file key
    if ! jq -e --arg k "$2" 'has($k)' -- "$1" >/dev/null 2>&1; then return 0; fi
    if ! jq -e --arg k "$2" '.[$k] | type == "array"' -- "$1" >/dev/null 2>&1; then return 1; fi
    jq -r --arg k "$2" '.[$k][]? | if type == "string" then . else tostring end' -- "$1" 2>/dev/null
}

# Minimal env expansion for config paths (leading ~ plus $HOME/$USER in
# either $VAR or ${VAR} form — no eval). Optional home/user override the
# process env (privileged tier expands from the pkexec caller's identity,
# never root's).
expand_user_path() { # path [home [user]]
    local p="$1" _home="${2:-${HOME:-/root}}" _user="${3:-${USER:-}}"
    [[ "$p" == "~"* ]] && p="$_home${p:1}"
    p="${p//\$HOME/$_home}"; p="${p//\$\{HOME\}/$_home}"
    p="${p//\$USER/$_user}"; p="${p//\$\{USER\}/$_user}"
    printf '%s' "$p"
}

# Home directory for a uid (getent primary, /etc/passwd fallback).
# Fails closed (return 1) when unresolvable — callers must abort, not guess.
caller_home_for_uid() { # uid -> home
    local h
    h=$(getent passwd "$1" 2>/dev/null | cut -d: -f6)
    if [[ -z "$h" ]]; then
        h=$(awk -F: -v u="$1" '$3==u {print $6; exit}' /etc/passwd 2>/dev/null)
    fi
    [[ -n "$h" ]] && printf '%s' "$h" || return 1
}

# Login name for a uid (same resolution order; empty string when unknown —
# usernames only decorate paths, so callers fall back to the uid).
caller_name_for_uid() { # uid -> name (may be empty)
    local n
    n=$(getent passwd "$1" 2>/dev/null | cut -d: -f1)
    if [[ -z "$n" ]]; then
        n=$(awk -F: -v u="$1" '$3==u {print $1; exit}' /etc/passwd 2>/dev/null)
    fi
    printf '%s' "$n"
}

# Human size: "4.2 KB" below 1 MiB, "12.3 MB" at/above (1-decimal,
# invariant culture — mirrors the Windows N1 KB/MB reporting).
# D-SC: pure-bash tenths (the old awk fork ran once per listed file).
fmt_size() { # bytes
    local s="$1" t
    [[ "$s" =~ ^[0-9]+$ ]] || { printf '%s' "$s"; return 0; }
    if [[ "$s" -lt 1048576 ]]; then
        t=$(( (s * 10 + 512) / 1024 ))
        printf '%d.%d KB' $(( t / 10 )) $(( t % 10 ))
    else
        t=$(( (s * 10 + 524288) / 1048576 ))
        printf '%d.%d MB' $(( t / 10 )) $(( t % 10 ))
    fi
}

# "%.1f MB" for a byte count (TempCleanup dry-run, file_detail below).
fmt_mb1() { # bytes
    local t=$(( ($1 * 10 + 524288) / 1048576 ))
    printf '%d.%d' $(( t / 10 )) $(( t % 10 ))
}

# "N.N MB, last modified yyyy-MM-dd" detail string (Windows dry-run format).
# D-SC: one stat call (was: two stats + one awk per file).
file_detail() { # path -> 0 ok; 1 when stat failed (detail says so)
    local out sz dy
    out=$(stat -c '%s %y' -- "$1" 2>/dev/null) || {
        printf 'size unknown, last modified unknown (could not stat)'
        return 1
    }
    sz="${out%% *}"; dy="${out#* }"; dy="${dy%% *}"
    [[ "$sz" =~ ^[0-9]+$ && -n "$dy" ]] || {
        printf 'size unknown, last modified unknown (could not stat)'
        return 1
    }
    printf '%s MB, last modified %s' "$(fmt_mb1 "$sz")" "$dy"
}

# True (0) when the file's mtime is strictly older than cutoff_days before
# now_epoch (mirrors LastWriteTime < now-AddDays; negative cutoff matches
# nothing, as on Windows). Returns 2 when the file cannot be statted at all
# (unreadable path — NOT young: callers must report and skip conservatively,
# never silently treat as young). 1 means young.
is_old_file() { # path cutoff_days now_epoch
    [[ "$2" -lt 0 ]] && return 1
    local mt
    mt=$(stat -c %Y -- "$1" 2>/dev/null) || return 2
    [[ "$mt" -lt $(( $3 - $2 * 86400 )) ]]
}

# Partial-walk accounting for one run (A-3): process substitution swallows
# find's stderr AND exit code, so walks go through these. walk_begin creates
# the per-run stderr accumulator; walk_find runs find into a list file (exit
# in WALK_RC, stderr appended to the accumulator); walk_finish warns once —
# Warning outcome, never silent Success, never a hard abort — and removes
# the accumulator. Call walk_finish before every exit after walk_begin.
WALK_ACC=""
WALK_RC=0
walk_begin() {
    WALK_ACC=$(mktemp) || { error "Cannot create temp file for walk errors — aborting."; exit 1; }
}
walk_find() { # listfile dir find-args... (no print action; -print0 added)
    local listfile="$1"; shift
    WALK_RC=0
    find "$@" -print0 2>>"$WALK_ACC" >"$listfile" || WALK_RC=$?
}
walk_find_text() { # listfile dir find-args... (no print action; -print -quit added)
    local listfile="$1"; shift
    WALK_RC=0
    find "$@" -print -quit 2>>"$WALK_ACC" >"$listfile" || WALK_RC=$?
}
walk_finish() { # context-label
    if [[ -n "$WALK_ACC" ]]; then
        if [[ -s "$WALK_ACC" ]]; then
            warn "Partial walk during $1 (some entries unreadable — results may be incomplete): $(head -n 2 -- "$WALK_ACC" 2>/dev/null | tr '\n' ';')"
        fi
        rm -f -- "$WALK_ACC"
        WALK_ACC=""
    fi
}

# First glob pattern (PowerShell -like semantics) matching name, or false.
# Case-insensitive, filename-only matching is the caller's job: pass patterns
# already trimmed with empties dropped.
matches_pattern_list() { # name pat...
    local name="$1"; shift
    local pat restore
    restore=$(shopt -p nocasematch)
    shopt -s nocasematch
    for pat in "$@"; do
        # shellcheck disable=SC2053
        if [[ "$name" == $pat ]]; then
            eval "$restore"
            printf '%s' "$pat"
            return 0
        fi
    done
    eval "$restore"
    return 1
}

# Trash one file via gio (recoverable delete for user data). Returns 0 only
# when the file is actually gone afterwards — never report false success.
# Exit 2 specifically means cross-filesystem: gio cannot trash to the home
# Trash from another mount (proven with /tmp tmpfs), so callers can say so
# instead of misreporting "locked". Exit 1 is any other failure.
trash_file() { # path
    if gio trash -- "$1" 2>/dev/null; then
        [[ ! -e "$1" && ! -L "$1" ]]
        return
    fi
    local fd td trashdir="${XDG_DATA_HOME:-$HOME/.local/share}/Trash"
    fd=$(stat -c %d -- "$1" 2>/dev/null) || return 1
    td=$(stat -c %d -- "$trashdir" 2>/dev/null) || return 1
    [[ "$fd" != "$td" ]] && return 2
    return 1
}

# Shared failure reporting for trash_file's exit code (1 = locked/other,
# 2 = cross-filesystem). The tag (e.g. " [advanced rule]") appends to the log
# line, mirroring the Windows per-rule log suffixes.
trash_warn() { # rc canon name logfile tag
    local rc="$1" canon="$2" name="$3" logfile="$4" tag="$5"
    if [[ "$rc" -eq 2 ]]; then
        write_log "$logfile" "ERROR   : Cannot trash $name (cross-filesystem — gio limitation)$tag"
        warn "Skipped (cannot trash across filesystems): $canon"
    else
        write_log "$logfile" "ERROR   : Failed to delete $name$tag"
        warn "Skipped locked file: $canon"
    fi
}

# HTML-escape a string (& < > " ') for report output.
# NOTE (bash patsub_replacement): a bare & in the replacement expands to the
# matched text, so the literal & must be written \& — "${s//</&lt;}" would
# emit "<lt;", not "&lt;".
html_escape() { # text
    local s="$1"
    s="${s//&/\&amp;}"
    s="${s//</\&lt;}"
    s="${s//>/\&gt;}"
    s="${s//\"/\&quot;}"
    s="${s//\'/\&#39;}"
    printf '%s' "$s"
}

# Append "[yyyy-MM-dd HH:mm:ss] message" to a log file (shared CleanupLog.txt
# convention). Log-dir creation is the caller's job (mkdir -p, best effort).
write_log() { # logfile message
    local f="$1"; shift
    printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >> "$f" 2>/dev/null
}

# Canonical path (resolves ., .., duplicate slashes; -m tolerates missing tails).
canonical() { # path
    if command -v realpath >/dev/null 2>&1; then realpath -m -- "$1"
    else readlink -m -- "$1"; fi
}

is_absolute() { # path
    [[ "$1" = /* ]]
}

# Refuse targets that would endanger the system. Returns 0 when safe.
# Allow-list model: anything strictly under allowed-home (default: our own
# $HOME) is the caller's own tree and passes — after canonicalization, so a
# symlink under $HOME pointing into /etc still resolves outside and refuses.
# The home root itself and / are exact-match refusals (never wipe $HOME).
# Genuine system roots match by prefix; /run is deliberately NOT listed
# (scripts legitimately use /run/user/$UID); other users' homes refuse via
# the /home rule. Unlisted paths (/tmp, …) are allowed.
guard_dangerous_root() { # canonical-target [allowed-home]
    local t="$1" allowed="${2:-${HOME:-/root}}" d
    [[ "$t" != "/" ]] && t="${t%/}"
    [[ "$allowed" != "/" ]] && allowed="${allowed%/}"
    [[ -z "$t" ]] && t="/"
    [[ -z "$allowed" ]] && allowed="/"
    # 1. filesystem root is never a target.
    [[ "$t" == "/" ]] && return 1
    # 2. your own home root is never a target.
    [[ "$t" == "$allowed" ]] && return 1
    # 3. anything strictly under your own home is yours (allow-list wins).
    [[ "$t" == "$allowed"/* ]] && return 0
    # 4. genuine system roots, by prefix.
    for d in /root /etc /usr /bin /sbin /lib /lib32 /lib64 /libx32 /var /boot /proc /sys /dev /opt /srv; do
        [[ "$t" == "$d" || "$t" == "$d"/* ]] && return 1
    done
    # 5. other users' homes ($allowed/* already returned above).
    if [[ "$t" == /home || "$t" == /home/* ]]; then return 1; fi
    return 0
}
