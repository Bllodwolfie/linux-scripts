#!/usr/bin/env bash
# Generates a DEV polkit policy for the locally built ScriptSuite binary and
# prints the one sudo copy needed to activate it.
#
# Why sudo: polkit only reads vendor action dirs (/usr/share/polkit-1/actions).
# Per-user ~/.local/share/polkit-1/actions/ is silently ignored (verified with
# pkaction on KDE Neon) — there is no rootless dev-install path for actions.
# The .deb (Stage 7) installs the packaged policy with the /usr/lib path.
#
# Usage: bash scripts/dev-install-policy.sh   (run from the repo root)

set -u

SRC="$(dirname -- "$(readlink -f -- "$0")")/../packaging/polkit/org.scriptsuite.run-elevated.policy"
BIN="$(readlink -f -- src/ScriptSuite/bin/Release/net8.0/linux-x64/ScriptSuite)"
OUT="/tmp/org.scriptsuite.run-elevated.policy"

if [[ ! -x "$BIN" ]]; then
    echo "Build the app first: dotnet build src/ScriptSuite -c Release -r linux-x64" >&2
    exit 1
fi

sed "s|/usr/lib/scriptsuite/ScriptSuite|$BIN|" "$SRC" > "$OUT"
echo "Wrote dev policy: $OUT"
echo "Activate with:"
echo "  sudo cp $OUT /usr/share/polkit-1/actions/ && pkaction --action-id org.scriptsuite.run-elevated -v | head -12"
