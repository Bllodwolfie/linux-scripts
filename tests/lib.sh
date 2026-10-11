#!/usr/bin/env bash
# tests/lib.sh — shared helpers for the linux-scripts regression suite.
# Sourced by every test-*.sh after run.shexports the scratch environment.
# Protocol: print `PASS <name>`, `FAIL <name> [detail]`, `SKIP <name> (reason)`.
# Never `set -e` here or in tests: a failed assert must not abort the file.
set -u

# --- result printers -------------------------------------------------------
t_pass() { printf 'PASS %s\n' "$1"; }
t_fail() { printf 'FAIL %s %s\n' "$1" "${2:-}"; }
t_skip() { printf 'SKIP %s (%s)\n' "$1" "${2:-no reason}"; }

# --- asserts (print PASS/FAIL, always return 0) -----------------------------
assert_eq() { # name expected actual
    if [[ "$2" == "$3" ]]; then t_pass "$1"; else t_fail "$1" "expected=[$2] actual=[$3]"; fi
    return 0
}

assert_contains() { # name haystack needle
    if [[ "$2" == *"$3"* ]]; then t_pass "$1"; else t_fail "$1" "missing [$3]"; fi
    return 0
}

assert_file_exists() { # name path
    if [[ -e "$2" || -L "$2" ]]; then t_pass "$1"; else t_fail "$1" "missing $2"; fi
    return 0
}

assert_file_gone() { # name path
    if [[ ! -e "$2" && ! -L "$2" ]]; then t_pass "$1"; else t_fail "$1" "still present: $2"; fi
    return 0
}

assert_no_ere() { # name file ere-pattern — fails if the pattern matches anywhere
    if grep -Eq "$3" "$2"; then t_fail "$1" "matched [$3] in $2"; else t_pass "$1"; fi
    return 0
}

# --- fixtures ---------------------------------------------------------------
# Repo root (tests/ lives at the root).
repo_root() { canonical_path "$(dirname "${BASH_SOURCE[0]}")/.."; }

canonical_path() {
    if command -v realpath >/dev/null 2>&1; then realpath -m -- "$1"
    else readlink -m -- "$1"; fi
}

script_path() { # scriptId -> absolute .sh path
    printf '%s/scripts/%s/%s.sh' "$(repo_root)" "$1" "$1"
}

# Scratch config file with the given JSON body; prints the path.
make_config() { # name json-body
    local p="$T_SCRATCH/configs/$1.json"
    mkdir -p "$(dirname "$p")"
    printf '%s\n' "$2" > "$p"
    printf '%s' "$p"
}

# File with mtime N days ago (default 5).
make_old() { # path [days]
    mkdir -p "$(dirname "$1")"
    : > "$1"
    touch -d "${2:-5} days ago" -- "$1"
}

make_fresh() { # path
    mkdir -p "$(dirname "$1")"
    : > "$1"
}

# --- execution ---------------------------------------------------------------
# Run one script, capturing stdout to $T_OUT and exit code to $T_RC.
run_case() { # scriptId args...
    local id="$1"; shift
    T_OUT="$T_SCRATCH/out-$(date +%s%N).log"
    bash "$(script_path "$id")" "$@" > "$T_OUT" 2>&1
    T_RC=$?
}

# DRYRUN: line count, optionally filtered by action (Delete/Move/Skip/Clear).
dryrun_count() { # [action]
    if [[ $# -eq 0 ]]; then grep -c '^DRYRUN: ' "$T_OUT" || true
    else grep '^DRYRUN: ' "$T_OUT" | grep -c "\"action\":\"$1\"" || true; fi
}

# Skip the whole file when a command is missing.
need_cmd() { # name cmd...
    local c
    for c in "$@"; do
        if ! command -v "$c" >/dev/null 2>&1; then
            t_skip "$1" "missing command: $c"
            return 1
        fi
    done
    return 0
}
