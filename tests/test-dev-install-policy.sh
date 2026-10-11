#!/usr/bin/env bash
# dev-install-policy.sh (H-2): no fixed /tmp output name, sed-safe BIN path.
# Runs the helper from a repo-shaped tree whose path contains sed-replacement
# metacharacters (& and |) and asserts the generated policy names the real
# binary byte-for-byte.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd mktemp sed || exit 0

EVILROOT="$T_SCRATCH/evil&we|ird/repo"
mkdir -p "$EVILROOT/scripts" "$EVILROOT/packaging/polkit" "$EVILROOT/src/ScriptSuite/bin/Release/net8.0/linux-x64"
cp "$(repo_root)/scripts/dev-install-policy.sh" "$EVILROOT/scripts/"
cp "$(repo_root)/packaging/polkit/org.scriptsuite.run-elevated.policy" "$EVILROOT/packaging/polkit/"
: > "$EVILROOT/src/ScriptSuite/bin/Release/net8.0/linux-x64/ScriptSuite"
chmod +x "$EVILROOT/src/ScriptSuite/bin/Release/net8.0/linux-x64/ScriptSuite"
WANTBIN="$(canonical_path "$EVILROOT/src/ScriptSuite/bin/Release/net8.0/linux-x64/ScriptSuite")"

OUT1=""; OUT2=""
pushd "$EVILROOT" >/dev/null
OUT1=$(bash "$EVILROOT/scripts/dev-install-policy.sh" 2>&1 | sed -n 's/^Wrote dev policy: //p')
RC1=$?
OUT2=$(bash "$EVILROOT/scripts/dev-install-policy.sh" 2>&1 | sed -n 's/^Wrote dev policy: //p')
popd >/dev/null

assert_eq "devpolicy-exit" "0" "$RC1"
# mktemp: two runs must not report the same path (fixed /tmp name = H-2).
if [[ -n "$OUT1" && -n "$OUT2" && "$OUT1" != "$OUT2" ]]; then t_pass "devpolicy-unique-out"
else t_fail "devpolicy-unique-out" "out1=[$OUT1] out2=[$OUT2]"; fi
# The hostile BIN path must appear literally (unquoted & would expand to the
# matched pattern text; unquoted | would break the s||| command).
if [[ -n "$OUT1" && -f "$OUT1" ]] && grep -qF "$WANTBIN" "$OUT1" \
    && ! grep -qF '/usr/lib/scriptsuite/ScriptSuite' "$OUT1"; then t_pass "devpolicy-literal-bin"
else t_fail "devpolicy-literal-bin" "want literal [$WANTBIN] in [$OUT1]"; fi
rm -f "$OUT1" "$OUT2"
