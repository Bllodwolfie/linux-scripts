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

# Human size: "4.2 KB" below 1 MiB, "12.3 MB" at/above (1-decimal,
# invariant culture — mirrors the Windows N1 KB/MB reporting).
fmt_size() { # bytes
    LC_ALL=C awk -v s="$1" 'BEGIN{if (s < 1048576) printf "%.1f KB", s/1024; else printf "%.1f MB", s/1048576}'
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
