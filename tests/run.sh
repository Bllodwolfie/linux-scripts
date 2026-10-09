#!/usr/bin/env bash
# tests/run.sh — regression suite for the 8 Bash scripts.
# Every test runs under ONE scratch root (same filesystem, so gio trash works):
# HOME, XDG_CONFIG_HOME, XDG_DATA_HOME, XDG_RUNTIME_DIR all point inside it.
# Usage: tests/run.sh [--keep] [filter-substring]
#   --keep   leave the scratch root behind and print its path (debugging)
#   filter   only run test files whose name contains the substring
# Exit 1 when any check FAILs. No sudo, no hardcoded paths, no writes outside
# the scratch root (scripts trash into the scratch Trash, vacuum nothing —
# ClearJournal is dry-run only by construction of its test file).
set -u

HERE="$(dirname "$0")"
KEEP=0
FILTER=""
for a in "$@"; do
    case "$a" in
        --keep) KEEP=1 ;;
        *) FILTER="$a" ;;
    esac
done

export T_SCRATCH
T_SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/scriptsuite-tests-XXXXXX")"
export HOME="$T_SCRATCH/home"
export XDG_CONFIG_HOME="$T_SCRATCH/config"
export XDG_DATA_HOME="$T_SCRATCH/data"
export XDG_RUNTIME_DIR="$T_SCRATCH/run"
mkdir -p "$HOME" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_RUNTIME_DIR"

cleanup() {
    if [[ "$KEEP" -eq 1 ]]; then
        printf 'kept scratch: %s\n' "$T_SCRATCH"
    else
        rm -rf -- "$T_SCRATCH"
    fi
}
trap cleanup EXIT

# shellcheck disable=SC1091
source "$HERE/lib.sh"

pass=0; fail=0; skip=0
while IFS= read -r f; do
    if [[ -n "$FILTER" && "$(basename "$f")" != *"$FILTER"* ]]; then continue; fi
    printf -- '--- %s ---\n' "$(basename "$f")"
    while IFS= read -r line; do
        printf '%s\n' "$line"
        case "$line" in
            PASS*) pass=$(( pass + 1 )) ;;
            FAIL*) fail=$(( fail + 1 )) ;;
            SKIP*) skip=$(( skip + 1 )) ;;
        esac
    done < <(bash "$f")
done < <(find "$HERE" -maxdepth 1 -name 'test-*.sh' | sort)

printf 'TOTAL pass=%d fail=%d skip=%d scratch=%s\n' "$pass" "$fail" "$skip" "$T_SCRATCH"
[[ "$fail" -eq 0 ]]
