#!/usr/bin/env bash
# Generates a text report of all installed software and writes it to
# OutputFile. Sources (the honest Linux counterpart of the Windows Uninstall
# registry keys — Get-Package was never used there either):
#   dpkg-query (system .debs — the source of truth on Ubuntu-based Neon),
#   snap list (if present), flatpak list (if present).
# Columns are Name / Version / Arch / Source — registry fields like Publisher
# and InstallDate have no dpkg equivalent and are not emulated.
# Read-only: no dry-run/items, like the Windows version (supportsDryRun=false).
#
# Usage: SoftwareInventory.sh [--config-path FILE]
# Exit 0 on success; exit 1 on hard failures (relative path, folder-as-file,
# unwritable output, dpkg missing).

set -u

HERE=$(dirname -- "$(readlink -f -- "$0")")
if [[ ! -f "$HERE/../lib/common.sh" ]]; then
    echo "ERROR: cannot locate scripts/lib/common.sh" >&2; exit 1
fi
# shellcheck disable=SC1091
. "$HERE/../lib/common.sh"

CONFIG_PATH=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --config-path) CONFIG_PATH="${2:-}"; shift 2 ;;
        *) error "Unknown argument: $1"; exit 1 ;;
    esac
done

require_jq || exit 1
if ! command -v dpkg-query >/dev/null 2>&1; then
    error "dpkg-query is required but not installed."
    exit 1
fi

# --- config ---
OUTPUT_FILE="$HOME/Documents/Script_Logs/Software_Inventory.txt"
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]]; then
    if ! cfg_usable "$CONFIG_PATH"; then
        warn "SoftwareInventory config is corrupt or unreadable at $CONFIG_PATH — using defaults (OutputFile=$HOME/Documents/Script_Logs/Software_Inventory.txt). Check or reset the config via Settings."
    else
        OUTPUT_FILE=$(cfg_str "$CONFIG_PATH" "OutputFile" "$HOME/Documents/Script_Logs/Software_Inventory.txt")
        [[ -z "$OUTPUT_FILE" ]] && OUTPUT_FILE="$HOME/Documents/Script_Logs/Software_Inventory.txt"
    fi
fi
OUTPUT_FILE=$(expand_user_path "$OUTPUT_FILE")

if [[ "$OUTPUT_FILE" != /* ]]; then
    error "Path must be absolute: $OUTPUT_FILE"
    exit 1
fi

# B6 parity: file-where-folder-expected and folder-where-file-expected fail
# clearly instead of silently.
OUT_DIR=$(dirname -- "$OUTPUT_FILE")
if [[ -e "$OUT_DIR" && ! -d "$OUT_DIR" ]]; then
    error "OutputFile parent is not a folder (is a file): $OUT_DIR"
    exit 1
fi
if [[ -e "$OUTPUT_FILE" && -d "$OUTPUT_FILE" ]]; then
    error "OutputFile is a folder, not a file: $OUTPUT_FILE"
    exit 1
fi
if ! mkdir -p -- "$OUT_DIR" 2>/dev/null; then
    error "Failed to create output folder for '$OUTPUT_FILE'."
    exit 1
fi

TMP_REPORT=$(mktemp) || { error "Cannot create temp file."; exit 1; }
trap 'rm -f -- "$TMP_REPORT"' EXIT

OS_PRETTY=$(awk -F= '/^PRETTY_NAME=/{gsub(/"/,"",$2); print $2}' /etc/os-release 2>/dev/null || echo Unknown)
{
    printf 'Software Inventory — %s\n' "$(hostname)"
    printf 'Generated: %s | %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$OS_PRETTY"
    printf '%s\n' "--------------------------------------------------------------------------------"
} > "$TMP_REPORT"

# dpkg: installed packages only (Status ... ok installed), sorted by name.
DPKG_COUNT=$(dpkg-query -W -f='${Package}\t${Version}\t${Architecture}\t${Status}\n' 2>/dev/null \
    | awk -F'\t' '$4 ~ /ok installed/ {print $1"\t"$2"\t"$3}' | LC_ALL=C sort > "$TMP_REPORT.dpkg" 2>/dev/null; wc -l < "$TMP_REPORT.dpkg" | tr -d ' ')
{
    printf '\n== dpkg (%s packages) ==\n' "$DPKG_COUNT"
    printf '%-50s %-30s %-10s\n' "Name" "Version" "Arch"
    awk -F'\t' '{printf "%-50.50s %-30.30s %-10.10s\n", $1, $2, $3}' "$TMP_REPORT.dpkg"
    rm -f -- "$TMP_REPORT.dpkg"
} >> "$TMP_REPORT"

# snap (appendix, when present).
SNAP_COUNT=0
if command -v snap >/dev/null 2>&1; then
    SNAP_COUNT=$(snap list 2>/dev/null | tail -n +2 | wc -l | tr -d ' ')
    {
        printf '\n== snap (%s packages) ==\n' "$SNAP_COUNT"
        snap list 2>/dev/null | tail -n +2
    } >> "$TMP_REPORT"
fi

# flatpak apps (appendix, when present).
FLAT_COUNT=0
if command -v flatpak >/dev/null 2>&1; then
    FLAT_COUNT=$(flatpak list --app --columns=name,version,branch 2>/dev/null | tail -n +2 | wc -l | tr -d ' ')
    {
        printf '\n== flatpak apps (%s packages) ==\n' "$FLAT_COUNT"
        flatpak list --app --columns=name,version,branch 2>/dev/null
    } >> "$TMP_REPORT"
fi

TOTAL=$(( DPKG_COUNT + SNAP_COUNT + FLAT_COUNT ))
printf '\nTotal: %s packages (%s dpkg, %s snap, %s flatpak).\n' "$TOTAL" "$DPKG_COUNT" "$SNAP_COUNT" "$FLAT_COUNT" >> "$TMP_REPORT"

if ! cp -- "$TMP_REPORT" "$OUTPUT_FILE" 2>/dev/null; then
    error "Failed to write report to '$OUTPUT_FILE'."
    exit 1
fi
trap - EXIT
rm -f -- "$TMP_REPORT"

printf '%s\n' "Software inventory written to $OUTPUT_FILE ($TOTAL packages: $DPKG_COUNT dpkg, $SNAP_COUNT snap, $FLAT_COUNT flatpak)."
exit 0
