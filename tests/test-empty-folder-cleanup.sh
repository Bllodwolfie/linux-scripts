#!/usr/bin/env bash
# EmptyFolderCleanup: nested empties go, root stays, ignored subtrees stay.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

T="$(mktemp -d "$T_SCRATCH/emptyroot-XXXXXX")"
mkdir -p "$T/a/b/c" "$T/keepme/deep" "$T/full"
echo x > "$T/full/file.txt"
CFG=$(make_config EmptyFolderCleanup '{"TargetFolder": "'"$T"'", "IgnoreFolders": ["'"$T"'/keepme"]}')

run_case EmptyFolderCleanup --config-path "$CFG" --dry-run
assert_eq "emptyfolder-dryrun-exit" "0" "$T_RC"
assert_contains "emptyfolder-dryrun-lists-nested" "$(cat "$T_OUT")" "a/b/c"
assert_file_exists "emptyfolder-dryrun-changes-nothing" "$T/a/b/c"

# include-only is exact-match on canonical targets (leaf selection, same as
# the Run window's checkbox narrowing): the deepest dir alone lists just it.
printf '%s\n' "$T/a/b/c" > "$T_SCRATCH/include.txt"
run_case EmptyFolderCleanup --config-path "$CFG" --dry-run --include-only-file "$T_SCRATCH/include.txt"
assert_eq "emptyfolder-include-narrows" "1" "$(dryrun_count Delete)"
assert_contains "emptyfolder-include-names-deepest" "$(cat "$T_OUT")" "$T/a/b/c"

run_case EmptyFolderCleanup --config-path "$CFG"
assert_eq "emptyfolder-real-exit" "0" "$T_RC"
assert_file_gone "emptyfolder-real-removes-nested" "$T/a"
assert_file_exists "emptyfolder-real-keeps-root" "$T"
assert_file_exists "emptyfolder-real-keeps-ignored" "$T/keepme/deep"
assert_file_exists "emptyfolder-real-keeps-nonempty" "$T/full/file.txt"
