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

info()  { printf 'INFO: %s\n' "$*"; }
warn()  { printf 'WARN: %s\n' "$*"; }
error() { printf 'ERROR: %s\n' "$*"; }

# DRYRUN item emitter. Builds JSON via jq so targets with quotes/backslashes
# can never break the protocol line.
dryrun_item() { # action target detail
    local json
    json=$(jq -n -c --arg a "$1" --arg t "$2" --arg d "$3" \
        '{action:$a,target:$t,detail:$d}') || return 1
    printf 'DRYRUN: %s\n' "$json"
}

require_jq() {
    if ! command -v jq >/dev/null 2>&1; then
        error "jq is required but not installed (sudo apt install jq)."
        return 1
    fi
}

# True when the file exists and parses as JSON.
cfg_usable() { # file
    [[ -f "$1" ]] && jq -e . "$1" >/dev/null 2>&1
}

# String value for key, or default when missing/null/wrong-type.
cfg_str() { # file key default
    local v
    v=$(jq -r --arg k "$2" '.[$k] // empty' "$1" 2>/dev/null) || { printf '%s' "$3"; return 0; }
    if [[ -z "$v" ]]; then printf '%s' "$3"; else printf '%s' "$v"; fi
}

# Integer value for key, or default. Returns 1 when present-but-invalid
# (caller warns and keeps the default).
cfg_int() { # file key default
    local v
    v=$(jq -r --arg k "$2" 'if .[$k] == null then "" elif .[$k] | type == "number" then (.[$k] | tostring) else (.[$k] | tostring) end' "$1" 2>/dev/null) || { printf '%s' "$3"; return 0; }
    if [[ -z "$v" ]]; then printf '%s' "$3"; return 0; fi
    if [[ "$v" =~ ^-?[0-9]+$ ]]; then printf '%s' "$v"; return 0; fi
    printf '%s' "$3"; return 1
}

# Array elements for key, one per line. Empty when missing. Returns 1 when
# present-but-not-an-array (caller warns).
cfg_list() { # file key
    if ! jq -e --arg k "$2" 'has($k)' "$1" >/dev/null 2>&1; then return 0; fi
    if ! jq -e --arg k "$2" '.[$k] | type == "array"' "$1" >/dev/null 2>&1; then return 1; fi
    jq -r --arg k "$2" '.[$k][]? | if type == "string" then . else tostring end' "$1" 2>/dev/null
}

# Minimal env expansion for config paths (leading ~ plus $HOME/$USER in
# either $VAR or ${VAR} form — no eval).
expand_user_path() { # path
    local p="$1"
    [[ "$p" == "~"* ]] && p="$HOME${p:1}"
    p="${p//\$HOME/$HOME}"; p="${p//\$\{HOME\}/$HOME}"
    p="${p//\$USER/$USER}"; p="${p//\$\{USER\}/$USER}"
    printf '%s' "$p"
}

# Human size: "4.2 KB" below 1 MiB, "12.3 MB" at/above (1-decimal,
# invariant culture — mirrors the Windows N1 KB/MB reporting).
fmt_size() { # bytes
    LC_ALL=C awk -v s="$1" 'BEGIN{if (s < 1048576) printf "%.1f KB", s/1024; else printf "%.1f MB", s/1048576}'
}

# "N.N MB, last modified yyyy-MM-dd" detail string (Windows dry-run format).
file_detail() { # path
    local sz d
    sz=$(stat -c %s -- "$1" 2>/dev/null || echo 0)
    d=$(stat -c %y -- "$1" 2>/dev/null | cut -d' ' -f1)
    LC_ALL=C awk -v s="$sz" -v d="${d:-unknown}" 'BEGIN{printf "%.1f MB, last modified %s", s/1048576, d}'
}

# True when the file's mtime is strictly older than cutoff_days before
# now_epoch (mirrors LastWriteTime < now-AddDays; negative cutoff matches
# nothing, as on Windows).
is_old_file() { # path cutoff_days now_epoch
    [[ "$2" -lt 0 ]] && return 1
    local mt
    mt=$(stat -c %Y -- "$1" 2>/dev/null) || return 1
    [[ "$mt" -lt $(( $3 - $2 * 86400 )) ]]
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

# HTML-escape a string (& < > ") for report output.
html_escape() { # text
    local s="$1"
    s="${s//&/&amp;}"
    s="${s//</&lt;}"
    s="${s//>/&gt;}"
    s="${s//\"/&quot;}"
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
guard_dangerous_root() { # canonical-target
    local t="$1" home="${HOME:-/root}"
    case "$t" in
        /|/home|/root|/etc|/usr|/bin|/sbin|/var|/boot|/proc|/sys|/dev|/run|"$home")
            return 1 ;;
    esac
    return 0
}
