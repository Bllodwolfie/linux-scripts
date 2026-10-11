#!/usr/bin/env bash
# guard_dangerous_root branch coverage (step-1 follow-up): the old guard
# refused ALL of /home/* and /run/* by prefix and its $home line was dead
# code nobody noticed. Every branch below names the code branch it pins.
# Cases marked FAIL-OLD fail on the pre-fix guard; PURE-GUARD cases refuse
# on both (regression locks, labelled as such in the report).
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"
# shellcheck disable=SC1091
source "$(repo_root)/scripts/lib/common.sh"

export HOME=/home/timj USER=timj
ME=1000

gpass() { # name branch path [allowed]
    if [[ -z "${4:-}" ]]; then guard_dangerous_root "$3"; else guard_dangerous_root "$3" "$4"; fi
    if [[ $? -eq 0 ]]; then t_pass "$1"; else t_fail "$1" "branch $2 refused [$3]"; fi
    return 0
}
grefuse() { # name branch path [allowed]
    if [[ -z "${4:-}" ]]; then guard_dangerous_root "$3"; else guard_dangerous_root "$3" "$4"; fi
    if [[ $? -eq 0 ]]; then t_fail "$1" "branch $2 allowed [$3]"; else t_pass "$1"; fi
    return 0
}

# --- allow-list wins (FAIL-OLD: old code refused /home/* and /run/*) ---
gpass "guard-home-subdir"            "3:under-allowed"      "/home/timj/Documents"
gpass "guard-home-nested-missing"    "3:under-allowed"      "/home/timj/a/b/c/missing-leaf"
gpass "guard-run-user"               "4:skip-run"           "/run/user/1000/scriptsuite_x"
gpass "guard-override-allows-other"  "3:under-allowed+arg"  "/home/other/x" "/home/other"
gpass "guard-tmp-scratch"            "6:unlisted"           "$T_SCRATCH/whatever"

# symlinked Documents inside $HOME canonicalizes back under $HOME (FAIL-OLD).
if [[ -d /home/timj/Documents ]]; then
    ln -sfn /home/timj/Documents "$T_SCRATCH/doclink"
    gpass "guard-symlink-into-home" "3:canonical-then-allowed" "$(canonical "$T_SCRATCH/doclink/sub")"
else
    t_skip "guard-symlink-into-home" "no /home/timj/Documents"
fi

# --- genuine system roots: exact + nested for EVERY prefix entry (branch 4) ---
for d in /etc /usr /bin /sbin /lib /lib32 /lib64 /libx32 /var /boot /proc /sys /dev /opt /srv /root; do
    tag="sys-$(printf '%s' "$d" | tr '/' '_')"
    grefuse "guard$tag-exact"  "4:prefix" "$d"
    grefuse "guard$tag-nested" "4:prefix" "$d/sub/dir"
done

# --- negatives (branch-labelled) ---
ln -sfn /etc "$T_SCRATCH/etclink"
grefuse "guard-etc-cron"      "4:prefix"          "/etc/cron.d"            # FAIL-OLD (old was exact-only)
grefuse "guard-root-nested"   "4:prefix"          "/root/x"                # FAIL-OLD (old was exact-only)
grefuse "guard-symlink-to-etc" "4:canonical-first" "$(canonical "$T_SCRATCH/etclink/sub")"
grefuse "guard-traversal"     "4:canonical-first" "$(canonical "/home/timj/a/../../etc")"
grefuse "guard-home-exact"    "2:own-home"        "/home/timj"             # PURE-GUARD
grefuse "guard-root-fs"       "1:fs-root"         "/"                      # PURE-GUARD
grefuse "guard-home-bare"     "5:home-parent"     "/home"                  # PURE-GUARD
grefuse "guard-other-home"    "5:other-user"      "/home/otheruser/x"      # PURE-GUARD
grefuse "guard-override-refuses-first" "5:other-user+arg" "/home/timj/x" "/home/other"  # PURE-GUARD
