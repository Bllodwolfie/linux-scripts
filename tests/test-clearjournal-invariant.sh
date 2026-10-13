#!/usr/bin/env bash
# ClearJournal backup/vacuum invariant (S1): everything the vacuum can delete
# must be in the pre-vacuum export first.
#
# Scratch journal directory only: copies of readable archived journal files
# plus a journalctl shim injecting --directory (Scope=user dodges the root
# gate). No pkexec, the real journal is never touched, setpriv runs as self.
# Entry identity is the sha256 of each export-format record; removal sets are
# diffed with comm: removed(before-after) must be a subset of the export.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

need_cmd journalctl || exit 0
need_cmd python3 || exit 0
need_cmd setpriv || exit 0
need_cmd jq || exit 0

REAL_JCTL="$(command -v journalctl)"
SRC_DIR="/var/log/journal/$(cat /etc/machine-id 2>/dev/null || echo none)"
mapfile -t SEED < <(find "$SRC_DIR" -maxdepth 1 -name '*.journal' -readable -printf '%T@ %p\n' 2>/dev/null | sort -n | head -n 3 | cut -d' ' -f2-)
if [[ ${#SEED[@]} -lt 2 ]]; then t_skip "clearjournal-invariant" "need 2+ readable journal files"; exit 0; fi

JDIR="$T_SCRATCH/jdir"; BK="$T_SCRATCH/backups"; SHIM="$T_SCRATCH/shim"
mkdir -p "$SHIM" "$BK"
cat > "$SHIM/journalctl" <<EOF
#!/usr/bin/env bash
# Transparent shim: same argv, scratch directory (drops --user: irrelevant to
# the export/vacuum logic under test, incompatible with --directory).
args=()
for a in "\$@"; do [[ "\$a" == "--user" ]] || args+=("\$a"); done
exec "$REAL_JCTL" --directory="$JDIR" "\${args[@]}"
EOF
chmod +x "$SHIM/journalctl"

# sha256 of every export-format record on stdin -> sorted list file ($1).
SHAS_PY='import sys, hashlib
data = sys.stdin.buffer.read()
recs = data.split(b"\n__CURSOR=")
out = []
for i, r in enumerate(recs):
    if i > 0: r = b"__CURSOR=" + r
    r = r.strip(b"\n")
    if r: out.append(hashlib.sha256(r).hexdigest())
open(sys.argv[1], "w").write("\n".join(sorted(out)) + ("\n" if out else ""))'

full_shas() { "$REAL_JCTL" --directory="$JDIR" -o export 2>/dev/null | python3 -c "$SHAS_PY" "$1"; }
rec_shas() { python3 -c "$SHAS_PY" "$2" < "$1"; }

reseed() { # fresh scratch journal + clean backup dir
    rm -rf "$JDIR" "$BK"; mkdir -p "$JDIR" "$BK"
    cp -a "${SEED[@]}" "$JDIR"/
}

mkcfg() { # vtime vsize -> config path (Scope=user: no root gate, self setpriv)
    make_config "CJInv$1-$2" '{"Scope":"user","VacuumTime":"'"$1"'","VacuumSize":"'"$2"'","BackupDir":"'"$BK"'"}'
}

run_real() { # cfg [include-file]; sets T_RC/T_OUT/EXPORT
    local cfg="$1" inc="${2:-}"
    T_OUT="$T_SCRATCH/out-$(date +%s%N).log"
    if [[ -n "$inc" ]]; then
        PATH="$SHIM:$PATH" bash "$(script_path ClearJournal)" --config-path "$cfg" --include-only-file "$inc" > "$T_OUT" 2>&1
    else
        PATH="$SHIM:$PATH" bash "$(script_path ClearJournal)" --config-path "$cfg" > "$T_OUT" 2>&1
    fi
    T_RC=$?
    EXPORT="$(echo "$BK"/*.export)"
}

all_targets() { # cfg -> include file with every dry-run target
    local cfg="$1"
    PATH="$SHIM:$PATH" bash "$(script_path ClearJournal)" --config-path "$cfg" --dry-run 2>/dev/null \
        | grep '^DRYRUN: ' | sed 's/^DRYRUN: //' | jq -r .target > "$T_SCRATCH/all.targets"
    printf '%s' "$T_SCRATCH/all.targets"
}

assert_subset() { # name removed-list exported-list (sorted sha files)
    if comm -23 "$2" "$3" | grep -q .; then
        t_fail "$1" "$(comm -23 "$2" "$3" | wc -l) removed record(s) missing from the export"
    else
        t_pass "$1"
    fi
}

assert_removed_nonempty() { # name removed-list: the case must prove something
    if [[ -s "$1" ]]; then t_pass "$2-setup-removed-nonempty"; else t_fail "$2-setup-removed-nonempty" "vacuum removed nothing; case proves nothing"; fi
}

# --- Case A: retention excludes everything + all-Skip selection.
# --- Must export nothing and vacuum nothing (the 10:42 incident shape).
reseed
CFGA="$(mkcfg 1000d "")"
INCA="$(all_targets "$CFGA")"
full_shas "$T_SCRATCH/a.before"
run_real "$CFGA" "$INCA"
assert_eq "inv-a-exit" "0" "$T_RC"
rec_shas "$EXPORT" "$T_SCRATCH/a.export"
assert_eq "inv-a-export-empty" "0" "$(grep -c . "$T_SCRATCH/a.export" || true)"
full_shas "$T_SCRATCH/a.after"
if cmp -s "$T_SCRATCH/a.before" "$T_SCRATCH/a.after"; then t_pass "inv-a-journal-untouched"; else t_fail "inv-a-journal-untouched" "vacuum changed the journal with nothing older than retention"; fi

# --- Case B: subset selection, retention covers old entries.
# --- Removed-by-vacuum must be a subset of the export.
reseed
CFGB="$(mkcfg 5d "")"
INCB="$T_SCRATCH/one.target"; head -n 1 "$INCA" > "$INCB"
full_shas "$T_SCRATCH/b.before"
run_real "$CFGB" "$INCB"
assert_eq "inv-b-exit" "0" "$T_RC"
rec_shas "$EXPORT" "$T_SCRATCH/b.export"
full_shas "$T_SCRATCH/b.after"
comm -23 "$T_SCRATCH/b.before" "$T_SCRATCH/b.after" > "$T_SCRATCH/b.removed"
assert_removed_nonempty "$T_SCRATCH/b.removed" "inv-b"
assert_subset "inv-b-removed-in-export" "$T_SCRATCH/b.removed" "$T_SCRATCH/b.export"

# --- Case C: no selection, mid retention (guards the --until path).
reseed
CFGC="$(mkcfg 5d "")"
full_shas "$T_SCRATCH/c.before"
run_real "$CFGC"
assert_eq "inv-c-exit" "0" "$T_RC"
rec_shas "$EXPORT" "$T_SCRATCH/c.export"
full_shas "$T_SCRATCH/c.after"
comm -23 "$T_SCRATCH/c.before" "$T_SCRATCH/c.after" > "$T_SCRATCH/c.removed"
assert_removed_nonempty "$T_SCRATCH/c.removed" "inv-c"
assert_subset "inv-c-removed-in-export" "$T_SCRATCH/c.removed" "$T_SCRATCH/c.export"

# --- Case D: size cap with retention excluding everything.
# --- Size vacuum deletes oldest-first; the export must cover it.
reseed
CFGD="$(mkcfg 1000d 1M)"
full_shas "$T_SCRATCH/d.before"
run_real "$CFGD"
assert_eq "inv-d-exit" "0" "$T_RC"
rec_shas "$EXPORT" "$T_SCRATCH/d.export"
full_shas "$T_SCRATCH/d.after"
comm -23 "$T_SCRATCH/d.before" "$T_SCRATCH/d.after" > "$T_SCRATCH/d.removed"
assert_removed_nonempty "$T_SCRATCH/d.removed" "inv-d"
assert_subset "inv-d-removed-in-export" "$T_SCRATCH/d.removed" "$T_SCRATCH/d.export"

# --- Case E: retention covers everything, no selection (full-clear path).
reseed
CFGE="$(mkcfg 1d "")"
full_shas "$T_SCRATCH/e.before"
run_real "$CFGE"
assert_eq "inv-e-exit" "0" "$T_RC"
rec_shas "$EXPORT" "$T_SCRATCH/e.export"
full_shas "$T_SCRATCH/e.after"
comm -23 "$T_SCRATCH/e.before" "$T_SCRATCH/e.after" > "$T_SCRATCH/e.removed"
assert_removed_nonempty "$T_SCRATCH/e.removed" "inv-e"
assert_subset "inv-e-removed-in-export" "$T_SCRATCH/e.removed" "$T_SCRATCH/e.export"
