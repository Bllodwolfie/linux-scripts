#!/usr/bin/env bash
# Cross-uid elevation handoff (CI-only): the ROOT child must hand its
# rendezvous files to the pkexec caller (fchown on the open fd), so the
# unprivileged parent can read them back. Needs passwordless sudo — the
# GitHub ubuntu-24.04 runners have it; anywhere else this file prints an
# explicit SKIP and exits 0 (local runs: sudo needs a password).
#
# Shape mirrors a real pkexec run: euid 0 + PKEXEC_UID set, caller-owned
# rendezvous dir, caller config + consent, user-scope no-op retention
# (36500d provably deletes nothing). Asserts exit 0, caller (not root)
# ownership, mode 0600, and a parseable payload — i.e. the exact 10:42
# failure (root-0600 result, parent EACCES) cannot recur.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

# Direct invocation (ci.yml calls this file, not run.sh): scratch env when
# the runner did not provide one. Never delete a provided scratch dir.
if [[ -z "${T_SCRATCH:-}" ]]; then
    T_SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/scriptsuite-crossuid-XXXXXX")"
    CLEAN_SCRATCH=1
fi

need_cmd dotnet || exit 0
need_cmd sudo || exit 0
need_cmd python3 || exit 0
DLL="$(repo_root)/src/ScriptSuite/bin/Release/net8.0/ScriptSuite.dll"
[[ -f "$DLL" ]] || { t_skip "cross-uid" "no Release build"; exit 0; }
sudo -n true 2>/dev/null || { t_skip "cross-uid" "needs passwordless sudo (CI-only)"; exit 0; }

ME="$(id -u)"
MEHOME="$(getent passwd "$ME" | cut -d: -f6)"
DOTNET_BIN="$(command -v dotnet)"
CFGDIR="$MEHOME/.config/scriptsuite"
CFG="$CFGDIR/cj-crossuid.json"
CONSENT="$CFGDIR/risk-consents.json"
# Validator-confined rendezvous dir: explicitly under /tmp (owned by the
# caller), independent of whatever TMPDIR/run.sh chose for T_SCRATCH.
WORK="$(mktemp -d /tmp/scriptsuite-crossuid-XXXXXX)"
GUID="$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
RES="$WORK/scriptsuite_${GUID}.json"
LIVE="$RES.live.log"
INC="$RES.include.json"

cleanup() {
    rm -rf -- "$WORK" "$CFG"
    if [[ -n "${CONSENT_BAK:-}" && -f "$CONSENT_BAK" ]]; then
        mv -f -- "$CONSENT_BAK" "$CONSENT"
    elif [[ "${CONSENT_SEEDED:-0}" == "1" ]]; then
        rm -f -- "$CONSENT"
    fi
    if [[ "${CLEAN_SCRATCH:-0}" == "1" ]]; then rm -rf -- "$T_SCRATCH"; fi
}
trap cleanup EXIT

# Caller config (must live under the caller's own config dir, G-5) +
# caller consent (re-checked by the child, G-1).
mkdir -p -- "$CFGDIR"
printf '{"Scope":"user","VacuumTime":"36500d","VacuumSize":"","BackupDir":"%s/backups"}' "$WORK" > "$CFG"
if [[ -f "$CONSENT" ]]; then
    CONSENT_BAK="$WORK/risk-consents.bak"; cp -a -- "$CONSENT" "$CONSENT_BAK"
    python3 - "$CONSENT" <<'EOF'
import sys, json
p = sys.argv[1]
try: d = json.load(open(p))
except Exception: d = {}
d["ClearJournal"] = True
json.dump(d, open(p, "w"))
EOF
else
    printf '{"ClearJournal": true}' > "$CONSENT"
    CONSENT_SEEDED=1
fi
printf '[]' > "$INC"

T_OUT="$WORK/child-out.log"
# shellcheck disable=SC2024
sudo -n "PKEXEC_UID=$ME" "HOME=$MEHOME" "$DOTNET_BIN" "$DLL" --elevated-run ClearJournal \
    --config-path "$CFG" --result-path "$RES" --live-log "$LIVE" --include-only "$INC" \
    > "$T_OUT" 2>&1
T_RC=$?
if [[ "$T_RC" -eq 0 ]]; then t_pass "cross-uid-exit-zero"; else t_fail "cross-uid-exit-zero" "rc=$T_RC out=[$(cat "$T_OUT")]"; fi

for f in "$RES" "$LIVE"; do
    if [[ "$(stat -c %u -- "$f")" == "$ME" ]]; then t_pass "cross-uid-owned-by-caller $(basename "$f")";
    else t_fail "cross-uid-owned-by-caller $(basename "$f")" "owner=$(stat -c %u -- "$f") want=$ME"; fi
    if [[ "$(stat -c %a -- "$f")" == "600" ]]; then t_pass "cross-uid-mode-0600 $(basename "$f")";
    else t_fail "cross-uid-mode-0600 $(basename "$f")" "mode=$(stat -c %a -- "$f")"; fi
done
# Readable by the caller (this shell IS the caller) + well-formed payload.
if python3 - "$RES" <<'EOF'; then
import sys, json
d = json.load(open(sys.argv[1]))
assert d.get("ExitCode") == 0, d
assert d.get("Outcome") in ("Success", "Warning"), d.get("Outcome")
assert isinstance(d.get("Logs"), list) and len(d["Logs"]) > 0, "empty logs"
EOF
    t_pass "cross-uid-parent-reads-payload"
else
    t_fail "cross-uid-parent-reads-payload" "unreadable or malformed result"
fi
if [[ -s "$LIVE" ]]; then t_pass "cross-uid-live-log-written"; else t_fail "cross-uid-live-log-written" "empty"; fi
