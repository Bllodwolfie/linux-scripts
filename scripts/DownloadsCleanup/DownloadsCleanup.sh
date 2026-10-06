#!/usr/bin/env bash
# Sorts and cleans the Downloads folder:
#   - Files older than CutoffDays with an extension in DeleteExts are trashed.
#   - Files older than CutoffDays with an extension under Categories are moved
#     into the matching destination folder (created if missing).
#   - Files with an unrecognized extension are left in place (Skip).
# Non-recursive: subfolders are never touched. All real-run actions are
# appended to LogFile for auditing; dry-run writes no logs at all.
#
# Rule precedence per file (faithful to DownloadsCleanup.ps1): IgnorePatterns
# (filename glob, case-insensitive) wins over everything, then the preview
# IncludeOnly gate, then AdvancedRules (Ignore|Delete|MoveTo — always wins over
# DeleteExts/Categories when present), then DeleteExts, then Categories.
# A MoveTo rule with an empty destination is a Skip (validated, not executed).
# Moves overwrite existing destinations (mirrors Move-Item -Force).
# Deletes go through `gio trash` (recoverable), never `rm`.
#
# Usage: DownloadsCleanup.sh [--dry-run] [--config-path FILE] [--include-only-file FILE]
# Exit 0 on completed runs (outcome from WARN/ERROR lines); exit 1 on hard
# failures (relative source dir).

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
if ! command -v gio >/dev/null 2>&1; then
    error "gio is required but not installed (package libglib2.0-bin)."
    exit 1
fi

# --- config ---
SOURCE="$HOME/Downloads"
CUTOFF=7
LOGDIR="$HOME/Documents/Script_Logs"
LOGFILE_NAME="CleanupLog.txt"
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]]; then
    if ! cfg_usable "$CONFIG_PATH"; then
        warn "DownloadsCleanup config is corrupt or unreadable at $CONFIG_PATH — using defaults (SourceDir=$HOME/Downloads, CutoffDays=7). Check or reset the config via Settings."
    else
        SOURCE=$(cfg_str "$CONFIG_PATH" "SourceDir" "$HOME/Downloads")
        [[ -z "$SOURCE" ]] && SOURCE="$HOME/Downloads"
        if ! CUTOFF=$(cfg_int "$CONFIG_PATH" "CutoffDays" "7"); then
            CUTOFF=7
            warn "CutoffDays value in $CONFIG_PATH is invalid — using default 7."
        fi
        LOGDIR=$(cfg_str "$CONFIG_PATH" "LogDir" "$HOME/Documents/Script_Logs")
        [[ -z "$LOGDIR" ]] && LOGDIR="$HOME/Documents/Script_Logs"
        LOGFILE_NAME=$(cfg_str "$CONFIG_PATH" "LogFile" "CleanupLog.txt")
        [[ -z "$LOGFILE_NAME" ]] && LOGFILE_NAME="CleanupLog.txt"
    fi
fi
SOURCE=$(expand_user_path "$SOURCE")
LOGDIR=$(expand_user_path "$LOGDIR")

if [[ "$SOURCE" != /* ]]; then
    error "Path must be absolute: $SOURCE"
    exit 1
fi
CANON_SOURCE=$(canonical "$SOURCE")
if ! guard_dangerous_root "$CANON_SOURCE"; then
    error "Refusing to clean system-critical root: $CANON_SOURCE"
    exit 1
fi
EXPECTED_SCOPE=$(canonical "$HOME/Downloads")
if [[ "$CANON_SOURCE" != "$EXPECTED_SCOPE" && "$CANON_SOURCE" != "$EXPECTED_SCOPE"/* ]]; then
    warn "Path '$SOURCE' is outside expected scope (expected under $HOME/Downloads) — proceeding anyway."
fi

# Extension lists (lowercased; rule keys gain a leading dot when missing).
mapfile -t DELETE_EXTS < <(
    if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]] && cfg_usable "$CONFIG_PATH"; then
        cfg_list "$CONFIG_PATH" "DeleteExts" 2>/dev/null | tr '[:upper:]' '[:lower:]'
    else
        printf '%s\n' .zip .rar .7z .ttf .otf .exe .msi
    fi
)
declare -A DELETE_MAP=()
for e in ${DELETE_EXTS[@]+"${DELETE_EXTS[@]}"}; do
    [[ -z "$e" ]] && continue
    [[ "$e" != .* ]] && e=".$e"
    DELETE_MAP["$e"]=1
done

# Categories: destination -> [exts], flattened to ext -> destination.
declare -A EXT_MAP=()
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]] && cfg_usable "$CONFIG_PATH"; then
    while IFS=$'\037' read -r ext dest; do
        [[ -z "$ext" ]] && continue
        EXT_MAP["$ext"]="$dest"
    done < <(jq -r '.Categories // {} | to_entries[] | .key as $d | (.value // [])[] | "\(. | tostring | ascii_downcase)\u001f\($d)"' "$CONFIG_PATH" 2>/dev/null)
else
    while IFS=$'\037' read -r ext dest; do
        EXT_MAP["$ext"]="$dest"
    done < <(printf '%s\037%s\n' \
        ".mp3" "$HOME/Music/Misc" ".flac" "$HOME/Music/Misc" ".ogg" "$HOME/Music/Misc" ".wav" "$HOME/Music/Misc" ".m4a" "$HOME/Music/Misc" \
        ".mp4" "$HOME/Videos/Misc" ".mkv" "$HOME/Videos/Misc" ".avi" "$HOME/Videos/Misc" ".mov" "$HOME/Videos/Misc" ".webm" "$HOME/Videos/Misc" \
        ".png" "$HOME/Pictures/Misc" ".jpg" "$HOME/Pictures/Misc" ".jpeg" "$HOME/Pictures/Misc" ".gif" "$HOME/Pictures/Misc" ".bmp" "$HOME/Pictures/Misc" ".svg" "$HOME/Pictures/Misc" ".webp" "$HOME/Pictures/Misc" \
        ".blend" "$HOME/Documents/Modeling" ".obj" "$HOME/Documents/Modeling" ".stl" "$HOME/Documents/Modeling" ".fbx" "$HOME/Documents/Modeling" \
        ".csv" "$HOME/Documents/Spreadsheets" ".xls" "$HOME/Documents/Spreadsheets" ".xlsx" "$HOME/Documents/Spreadsheets" ".ods" "$HOME/Documents/Spreadsheets" \
        ".ppt" "$HOME/Documents/Presentations" ".pptx" "$HOME/Documents/Presentations" ".odp" "$HOME/Documents/Presentations" \
        ".txt" "$HOME/Documents/Text" ".md" "$HOME/Documents/Text" ".pdf" "$HOME/Documents/Text" ".doc" "$HOME/Documents/Text" ".docx" "$HOME/Documents/Text" ".epub" "$HOME/Documents/Text" ".log" "$HOME/Documents/Text" ".rtf" "$HOME/Documents/Text" ".odt" "$HOME/Documents/Text" \
        ".json" "$HOME/Documents/Configuration" ".xml" "$HOME/Documents/Configuration" ".yaml" "$HOME/Documents/Configuration" ".yml" "$HOME/Documents/Configuration" ".toml" "$HOME/Documents/Configuration" ".ini" "$HOME/Documents/Configuration" ".cfg" "$HOME/Documents/Configuration" ".conf" "$HOME/Documents/Configuration" ".sql" "$HOME/Documents/Configuration")
fi

# AdvancedRules: ext -> "action \x1f destination".
declare -A ADV_MAP=()
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]] && cfg_usable "$CONFIG_PATH"; then
    while IFS=$'\037' read -r ext action dest; do
        [[ -z "$ext" ]] && continue
        ADV_MAP["$ext"]="$action"$'\037'"$dest"
    done < <(jq -r '.AdvancedRules // {} | to_entries[] | [(.key | tostring | ascii_downcase | if startswith(".") then . else "." + . end), ((.value.Action // "") | tostring), ((.value.Destination // "") | tostring)] | join("\u001f")' "$CONFIG_PATH" 2>/dev/null)
fi

# IgnorePatterns: trimmed, empties dropped; matched against basename only.
IGNORE_PATS=()
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]] && cfg_usable "$CONFIG_PATH"; then
    while IFS= read -r pat; do
        pat="${pat#"${pat%%[![:space:]]*}"}"; pat="${pat%"${pat##*[![:space:]]}"}"
        [[ -z "$pat" ]] && continue
        IGNORE_PATS+=("$pat")
    done < <(cfg_list "$CONFIG_PATH" "IgnorePatterns" 2>/dev/null)
fi

# Include-only allow-list (preview confirmation), canonicalized.
declare -A ONLY_MAP=()
ONLY_ACTIVE=0
if [[ -n "$INCLUDE_ONLY_FILE" && -f "$INCLUDE_ONLY_FILE" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ -z "$line" ]] && continue
        ONLY_MAP["$(canonical "$line")"]=1
    done < "$INCLUDE_ONLY_FILE"
    [[ ${#ONLY_MAP[@]} -gt 0 ]] && ONLY_ACTIVE=1
fi

LOGFILE="$LOGDIR/$LOGFILE_NAME"
mkdir -p -- "$LOGDIR" 2>/dev/null
NOW_EPOCH=$(date +%s)

if [[ "$DRY_RUN" -eq 0 ]]; then
    write_log "$LOGFILE" "Cleanup started"
fi

if [[ ! -d "$CANON_SOURCE" ]]; then
    warn "Source folder '$SOURCE' does not exist or is not accessible — skipping."
    if [[ "$DRY_RUN" -eq 0 ]]; then
        write_log "$LOGFILE" "SKIPPED : Source folder not found: $SOURCE"
        write_log "$LOGFILE" "Cleanup finished"
    fi
    exit 0
fi

deleted=0
skipped=0
while IFS= read -r -d '' f; do
    name=$(basename -- "$f")
    canon=$(canonical "$f")
    # File age first: young files are left alone regardless of rules.
    if ! is_old_file "$f" "$CUTOFF" "$NOW_EPOCH"; then continue; fi

    # IgnorePatterns wins over everything (age already applied above).
    if [[ ${#IGNORE_PATS[@]} -gt 0 ]] && ignored_by=$(matches_pattern_list "$name" "${IGNORE_PATS[@]}"); then
        if [[ "$DRY_RUN" -eq 1 ]]; then
            dryrun_item "Skip" "$canon" "ignored per ignore pattern: $ignored_by"
        else
            write_log "$LOGFILE" "SKIPPED : $name (ignored per ignore pattern: $ignored_by)"
        fi
        continue
    fi

    if [[ "$ONLY_ACTIVE" -eq 1 && -z "${ONLY_MAP[$canon]:-}" ]]; then continue; fi

    base="${name##*/}"
    if [[ "$base" == *.* ]]; then ext=".${base##*.}"; ext="${ext,,}"
    else ext=""; fi

    # AdvancedRules override (Ignore | Delete | MoveTo).
    if [[ -n "$ext" && -n "${ADV_MAP[$ext]:-}" ]]; then
        IFS=$'\037' read -r adv_action adv_dest <<< "${ADV_MAP[$ext]}"
        case "$adv_action" in
            Ignore)
                if [[ "$DRY_RUN" -eq 1 ]]; then
                    dryrun_item "Skip" "$canon" "ignored per advanced rule: $ext"
                else
                    write_log "$LOGFILE" "SKIPPED : $name (ignored per advanced rule: $ext)"
                fi
                continue
                ;;
            Delete)
                if [[ "$DRY_RUN" -eq 1 ]]; then
                    dryrun_item "Delete" "$canon" "$(file_detail "$f") [advanced rule]"
                else
                    if trash_file "$f"; then
                        write_log "$LOGFILE" "DELETED : $name [advanced rule]"
                        deleted=$(( deleted + 1 ))
                    else
                        rc=$?
                        trash_warn "$rc" "$canon" "$name" "$LOGFILE" " [advanced rule]"
                        skipped=$(( skipped + 1 ))
                    fi
                fi
                continue
                ;;
            MoveTo)
                if [[ -z "${adv_dest// }" ]]; then
                    if [[ "$DRY_RUN" -eq 1 ]]; then
                        dryrun_item "Skip" "$canon" "advanced MoveTo missing destination: $ext [advanced rule]"
                    else
                        write_log "$LOGFILE" "SKIPPED : $name (advanced MoveTo missing destination: $ext)"
                    fi
                    continue
                fi
                dest=$(expand_user_path "$adv_dest")
                if [[ "$DRY_RUN" -eq 1 ]]; then
                    dryrun_item "Move" "$canon" "to $dest [advanced rule]"
                else
                    if mkdir -p -- "$dest" 2>/dev/null && mv -f -- "$f" "$dest/" 2>/dev/null; then
                        write_log "$LOGFILE" "MOVED   : $name -> $dest [advanced rule]"
                    else
                        write_log "$LOGFILE" "ERROR   : Failed to move $name to $dest [advanced rule]"
                    fi
                fi
                continue
                ;;
        esac
        # Unknown action word: fall through to the standard rules below.
    fi

    # DeleteExts -> trash.
    if [[ -n "$ext" && -n "${DELETE_MAP[$ext]:-}" ]]; then
        if [[ "$DRY_RUN" -eq 1 ]]; then
            dryrun_item "Delete" "$canon" "$(file_detail "$f")"
        else
            if trash_file "$f"; then
                write_log "$LOGFILE" "DELETED : $name"
                deleted=$(( deleted + 1 ))
            else
                rc=$?
                trash_warn "$rc" "$canon" "$name" "$LOGFILE" ""
                skipped=$(( skipped + 1 ))
            fi
        fi
        continue
    fi

    # Categories -> move (mkdir -p first, overwrite like Move-Item -Force).
    if [[ -n "$ext" && -n "${EXT_MAP[$ext]:-}" ]]; then
        dest=$(expand_user_path "${EXT_MAP[$ext]}")
        if [[ "$DRY_RUN" -eq 1 ]]; then
            dryrun_item "Move" "$canon" "to $dest"
        else
            if mkdir -p -- "$dest" 2>/dev/null && mv -f -- "$f" "$dest/" 2>/dev/null; then
                write_log "$LOGFILE" "MOVED   : $name -> $dest"
            else
                write_log "$LOGFILE" "ERROR   : Failed to move $name to $dest"
            fi
        fi
        continue
    fi

    # Unrecognized extension -> no action.
    if [[ "$DRY_RUN" -eq 1 ]]; then
        dryrun_item "Skip" "$canon" "no action (unrecognized extension: $ext)"
    else
        write_log "$LOGFILE" "SKIPPED : $name (unrecognized extension: $ext)"
    fi
done < <(find "$CANON_SOURCE" -maxdepth 1 -type f -print0 2>/dev/null)

if [[ "$DRY_RUN" -eq 0 ]]; then
    write_log "$LOGFILE" "Cleanup finished"
    printf '%s\n' "DownloadsCleanup: $deleted deleted, $skipped skipped (locked or in use)."
fi
exit 0
