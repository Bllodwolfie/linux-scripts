#!/usr/bin/env bash
# D-SC quadratic collection: EmptyFolder re-scanned the whole tree per
# pass (a 25-deep chain = 25 full finds + O(D^2) per-dir finds) and
# EmptyTrash matched K selections against N items with a canonical fork
# per comparison. Counting shims prove single-pass batching: find and
# realpath totals must stay within linear budgets at 125 dirs / 250 items.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd find realpath || exit 0
S="$T_SCRATCH/scale-quad"; mkdir -p "$S"
SHIM="$S/shim"; mkdir -p "$SHIM"
export QUAD_COUNT="$S/counts.log"
: > "$QUAD_COUNT"

mkshim() { # name realbin
    cat > "$SHIM/$1" <<EOF
#!/usr/bin/env bash
printf 'x\n' >> "\$QUAD_COUNT"
exec "$2" "\$@"
EOF
    chmod +x "$SHIM/$1"
}
mkshim find /usr/bin/find
mkshim realpath /usr/bin/realpath
count_of() { grep -c . "$QUAD_COUNT" 2>/dev/null || echo 0; }

# --- EmptyFolder: 25-deep chain + 100 wide dirs, one real run ---
R="$S/folders"
chain="$R/chain"; d="$chain"
i=0; while [[ "$i" -lt 25 ]]; do d="$d/l$(printf '%02d' "$i")"; i=$(( i + 1 )); done
mkdir -p "$d"
i=0; while [[ "$i" -lt 100 ]]; do mkdir -p "$R/w$(printf '%03d' "$i")"; i=$(( i + 1 )); done
FCFG=$(make_config ScaleFolders '{"TargetFolder":"'"$R"'","LogDir":"'"$S"'/logs","LogFile":"folders.log","IgnoreFolders":[]}')
: > "$QUAD_COUNT"
T_OUT="$T_SCRATCH/out-scalefolders.log"
PATH="$SHIM:$PATH" bash "$(script_path EmptyFolderCleanup)" --config-path "$FCFG" > "$T_OUT" 2>&1
T_RC=$?
FINDS=$(count_of)
assert_eq "quad-folders-exit" "0" "$T_RC"
assert_eq "quad-folders-all-gone" "0" "$(/usr/bin/find "$R" -mindepth 1 2>/dev/null | wc -l)"
assert_eq "quad-folders-finds-linear" "1" "$([ "$FINDS" -le 300 ] && echo 1 || echo 0)"

# --- EmptyTrash: 250 items, 30 selected, realpath budget linear ---
export XDG_DATA_HOME="$S/trashdata"
mkdir -p "$XDG_DATA_HOME/Trash/files" "$XDG_DATA_HOME/Trash/info"
i=0; while [[ "$i" -lt 250 ]]; do
    n="q$(printf '%04d' "$i").txt"
    echo "data" > "$XDG_DATA_HOME/Trash/files/$n"
    printf '[Trash Info]\nPath=/home/somewhere/%s\nDeletionDate=%s\n' "$n" \
        "$(date -d '5 days ago' +%Y-%m-%dT%H:%M:%S)" > "$XDG_DATA_HOME/Trash/info/$n.trashinfo"
    i=$(( i + 1 ))
done
# Select the LAST 30 (worst case for a nested scan: match at the end).
: > "$S/selected.txt"
i=220; while [[ "$i" -lt 250 ]]; do printf '/home/somewhere/q%04d.txt\n' "$i" >> "$S/selected.txt"; i=$(( i + 1 )); done
QCFG=$(make_config ScaleTrashQ '{"MinAgeDays": 1}')
: > "$QUAD_COUNT"
T_OUT="$T_SCRATCH/out-scaletrashq.log"
PATH="$SHIM:$PATH" bash "$(script_path EmptyTrash)" --config-path "$QCFG" --include-only-file "$S/selected.txt" > "$T_OUT" 2>&1
T_RC=$?
RPS=$(count_of)
assert_eq "quad-trash-exit" "0" "$T_RC"
assert_file_gone "quad-trash-selected-gone" "$XDG_DATA_HOME/Trash/files/q0249.txt"
assert_file_exists "quad-trash-rest-kept" "$XDG_DATA_HOME/Trash/files/q0000.txt"
assert_eq "quad-trash-rest-count" "220" "$(ls "$XDG_DATA_HOME/Trash/files" | wc -l)"
assert_eq "quad-trash-rp-linear" "1" "$([ "$RPS" -le 350 ] && echo 1 || echo 0)"
