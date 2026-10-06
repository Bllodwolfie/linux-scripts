#!/usr/bin/env bash
# Generates a self-contained HTML system health report (system info, CPU, GPU,
# memory, storage, network, top processes, recent journal errors, package
# updates, temperatures) with a Catppuccin Mocha/Latte theme toggle.
#
# The honest Linux counterpart of SystemHealthReport.ps1: same single-file
# layout, same cards/tables/sidebar/fade-in JS, same palette-from-config
# mechanism — but every collector is Linux-native (/proc, lscpu, df, ip,
# ps, journalctl, apt, sensors). Documented differences from Windows:
# Update section shows PENDING apt updates (no "last checked" COM API);
# theme defaults to Mocha unless Plasma reports a light scheme; mascots are
# embedded only when the PNGs resolve (repo assets/ or ~/Downloads fallback).
# Read-only: no dry-run/items, like the Windows version (supportsDryRun=false).
#
# Usage: SystemHealthReport.sh [--config-path FILE]
# Exit 0 on success; exit 1 on hard failures (uncreatable dir, folder-as-file,
# unwritable report).

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

# --- config (palettes ride along from DefaultConfigs via seeding; the shipped
# --- DefaultConfigs file is the fallback when the user config lacks them) ---
OUTPUT_DIR="$HOME/Documents"
OUTPUT_FILE="System_Health_Report.html"
CAT_DARK="cat_bobber-dark.png"
CAT_LIGHT="cat_bobber-light.png"
MAX_PROCS=10
MAX_ERRORS=20
ERR_HOURS=24
RISK_GREEN=60
RISK_YELLOW=80
if [[ -n "$CONFIG_PATH" && -f "$CONFIG_PATH" ]]; then
    if ! cfg_usable "$CONFIG_PATH"; then
        warn "SystemHealthReport config is corrupt or unreadable at $CONFIG_PATH — using defaults (OutputDir=$HOME/Documents, OutputFile=$OUTPUT_FILE). Check or reset the config via Settings."
    else
        OUTPUT_DIR=$(cfg_str "$CONFIG_PATH" "OutputDir" "$HOME/Documents")
        [[ -z "$OUTPUT_DIR" ]] && OUTPUT_DIR="$HOME/Documents"
        OUTPUT_FILE=$(cfg_str "$CONFIG_PATH" "OutputFile" "System_Health_Report.html")
        [[ -z "$OUTPUT_FILE" ]] && OUTPUT_FILE="System_Health_Report.html"
        CAT_DARK=$(cfg_str "$CONFIG_PATH" "CatImageDark" "cat_bobber-dark.png")
        CAT_LIGHT=$(cfg_str "$CONFIG_PATH" "CatImageLight" "cat_bobber-light.png")
        for spec in "MaxTopProcesses:MAX_PROCS:10" "MaxErrorEvents:MAX_ERRORS:20" "ErrorWindowHours:ERR_HOURS:24" "RiskGreen:RISK_GREEN:60" "RiskYellow:RISK_YELLOW:80"; do
            key="${spec%%:*}"; rest="${spec#*:}"; var="${rest%%:*}"; def="${rest##*:}"
            val=""; ok=0
            val=$(cfg_int "$CONFIG_PATH" "$key" "$def") && ok=1 || val="$def"
            [[ "$ok" -eq 0 ]] && warn "$key value in $CONFIG_PATH is invalid — using default $def."
            printf -v "$var" '%s' "$val"
        done
    fi
fi
OUTPUT_DIR=$(expand_user_path "$OUTPUT_DIR")

if [[ "$OUTPUT_DIR" != /* ]]; then
    error "Path must be absolute: $OUTPUT_DIR"
    exit 1
fi
if [[ -e "$OUTPUT_DIR" && ! -d "$OUTPUT_DIR" ]]; then
    error "OutputDir is not a folder (is a file): $OUTPUT_DIR"
    exit 1
fi
if ! mkdir -p -- "$OUTPUT_DIR" 2>/dev/null; then
    error "Failed to create output folder '$OUTPUT_DIR'."
    exit 1
fi
OUTPUT_PATH="$OUTPUT_DIR/$OUTPUT_FILE"
if [[ -e "$OUTPUT_PATH" && -d "$OUTPUT_PATH" ]]; then
    error "OutputFile is a folder, not a file: $OUTPUT_PATH"
    exit 1
fi

# Palette -> CSS vars (toolbarBg gets the --toolbar-bg name, like Write-Palette).
palette_css() { # Mocha|Latte
    local src="$CONFIG_PATH" block=""
    if [[ -n "$src" && -f "$src" ]] && cfg_usable "$src"; then
        block=$(jq -r --arg k "$1" '.[$k] // {} | to_entries[] | "--\(if .key == "toolbarBg" then "toolbar-bg" else .key end): \(.value);"' "$src" 2>/dev/null)
    fi
    if [[ -z "$block" ]]; then
        local shipped=""
        for cand in "$HERE/../DefaultConfigs/SystemHealthReport.json" "/usr/lib/scriptsuite/DefaultConfigs/SystemHealthReport.json"; do
            [[ -f "$cand" ]] && { shipped="$cand"; break; }
        done
        if [[ -n "$shipped" ]]; then
            block=$(jq -r --arg k "$1" '.[$k] // {} | to_entries[] | "--\(if .key == "toolbarBg" then "toolbar-bg" else .key end): \(.value);"' "$shipped" 2>/dev/null)
        fi
    fi
    [[ -z "$block" ]] && block="--base: #1e1e2e; --text: #cdd6f4; --green: #a6e3a1; --yellow: #f9e2af; --red: #f38ba8; --mauve: #cba6f7;"
    printf '%s\n' "$block" | sed 's/^/    /'
}

risk_color() { # pct -> css var
    if [[ "$1" -lt "$RISK_GREEN" ]]; then printf 'var(--green)'
    elif [[ "$1" -lt "$RISK_YELLOW" ]]; then printf 'var(--yellow)'
    else printf 'var(--red)'; fi
}

# Theme: follow Plasma light schemes when detectable, else Mocha (app default).
THEME="mocha"; THEME_NAME="Mocha"
if command -v kreadconfig6 >/dev/null 2>&1; then
    scheme=$(kreadconfig6 --file kdeglobals --group General --key ColorScheme 2>/dev/null || true)
    [[ "$scheme" =~ [Ll]ight ]] && { THEME="latte"; THEME_NAME="Latte"; }
fi

# Mascots: repo assets/ beside scripts, else ~/Downloads fallback (Windows
# parity); images embedded only when they actually copy.
HAS_CATS=0
for assetdir in "$HERE/../../assets" "$HOME/Downloads"; do
    if [[ -f "$assetdir/Cat_bobber-dark.png" && -f "$assetdir/Cat_bobber-light.png" ]]; then
        if cp -f -- "$assetdir/Cat_bobber-dark.png" "$OUTPUT_DIR/$CAT_DARK" 2>/dev/null \
        && cp -f -- "$assetdir/Cat_bobber-light.png" "$OUTPUT_DIR/$CAT_LIGHT" 2>/dev/null; then
            HAS_CATS=1; break
        fi
    fi
done

# ----- collectors (every value best-effort; Unknown beats failure) -----
HOST=$(hostname 2>/dev/null || echo Unknown)
OS_PRETTY=$(awk -F= '/^PRETTY_NAME=/{gsub(/"/,"",$2); print $2}' /etc/os-release 2>/dev/null || echo Unknown)
[[ -z "$OS_PRETTY" ]] && OS_PRETTY="Unknown"
KERNEL=$(uname -r 2>/dev/null || echo Unknown)
ARCH=$(uname -m 2>/dev/null || echo Unknown)
INSTALL_DATE=$(stat -c %w / 2>/dev/null | cut -d' ' -f1)
[[ -z "$INSTALL_DATE" || "$INSTALL_DATE" == "-" || "$INSTALL_DATE" == "1970-01-01" ]] && INSTALL_DATE="Unknown"
LAST_BOOT=$(uptime -s 2>/dev/null || echo Unknown)
UPTIME_SEC=$(cut -d' ' -f1 /proc/uptime 2>/dev/null || echo 0); UPTIME_SEC="${UPTIME_SEC%%.*}"
UP_D=$(( UPTIME_SEC / 86400 )); UP_H=$(( UPTIME_SEC % 86400 / 3600 )); UP_M=$(( UPTIME_SEC % 3600 / 60 ))
VENDOR=$(cat /sys/class/dmi/id/sys_vendor 2>/dev/null || echo Unknown)
MODEL=$(cat /sys/class/dmi/id/product_name 2>/dev/null || echo Unknown)

CPU_MODEL=$(lscpu 2>/dev/null | awk -F: '/^Model name:/{sub(/^ +/,"",$2); print $2; exit}')
[[ -z "$CPU_MODEL" ]] && CPU_MODEL=$(awk -F: '/^model name/{sub(/^ +/,"",$2); print $2; exit}' /proc/cpuinfo 2>/dev/null)
[[ -z "$CPU_MODEL" ]] && CPU_MODEL="Unknown"
CPU_LOGICAL=$(lscpu 2>/dev/null | awk -F: '/^CPU\(s\):/{gsub(/ /,"",$2); print $2; exit}')
CPU_PER_SOCK=$(lscpu 2>/dev/null | awk -F: '/^Core\(s\) per socket:/{gsub(/ /,"",$2); print $2; exit}')
CPU_SOCKS=$(lscpu 2>/dev/null | awk -F: '/^Socket\(s\):/{gsub(/ /,"",$2); print $2; exit}')
CPU_PHYSICAL="Unknown"
[[ "$CPU_PER_SOCK" =~ ^[0-9]+$ && "$CPU_SOCKS" =~ ^[0-9]+$ ]] && CPU_PHYSICAL=$(( CPU_PER_SOCK * CPU_SOCKS ))
[[ -z "$CPU_LOGICAL" ]] && CPU_LOGICAL="Unknown"
CPU_MHZ=$(lscpu 2>/dev/null | awk -F: '/^CPU max MHz:/{gsub(/ /,"",$2); print $2; exit}')
[[ -z "$CPU_MHZ" ]] && CPU_MHZ="Unknown"
CPU_L2=$(lscpu 2>/dev/null | awk -F: '/^L2 cache:/{sub(/^ +/,"",$2); print $2; exit}')
[[ -z "$CPU_L2" ]] && CPU_L2="Unknown"
CPU_L3=$(lscpu 2>/dev/null | awk -F: '/^L3 cache:/{sub(/^ +/,"",$2); print $2; exit}')
[[ -z "$CPU_L3" ]] && CPU_L3="Unknown"

MEM_KB=$(awk '/^MemTotal:/{print $2}' /proc/meminfo 2>/dev/null || echo 0)
MEM_AV=$(awk '/^MemAvailable:/{print $2}' /proc/meminfo 2>/dev/null || echo 0)
MEM_TOTAL_GB=$(LC_ALL=C awk -v k="$MEM_KB" 'BEGIN{printf "%.1f", k/1048576}')
MEM_USED_GB=$(LC_ALL=C awk -v t="$MEM_KB" -v a="$MEM_AV" 'BEGIN{printf "%.1f", (t-a)/1048576}')
MEM_PCT=0; [[ "$MEM_KB" -gt 0 ]] && MEM_PCT=$(( (MEM_KB - MEM_AV) * 100 / MEM_KB ))
MEM_COLOR=$(risk_color "$MEM_PCT")

DISK_DATA=$(df -B1 --output=source,size,used,pcent,target -x tmpfs -x devtmpfs -x efivarfs -x squashfs -x overlay 2>/dev/null | tail -n +2)
SUMMARY_CARDS=""; DISK_ROWS=""
if [[ -n "$DISK_DATA" ]]; then
    while read -r src size used pcent mnt; do
        [[ -z "$src" ]] && continue
        total_gb=$(LC_ALL=C awk -v b="$size" 'BEGIN{printf "%.1f", b/1073741824}')
        used_gb=$(LC_ALL=C awk -v b="$used" 'BEGIN{printf "%.1f", b/1073741824}')
        pct="${pcent%\%}"; [[ "$pct" =~ ^[0-9]+$ ]] || pct=0
        color=$(risk_color "$pct")
        label="$mnt"
        SUMMARY_CARDS+="<div class=\"card\" style=\"border-left: 4px solid $color;\"><span class=\"card-label\">$(html_escape "$label")</span><span class=\"card-value\">$used_gb / $total_gb GB</span><div class=\"bar\"><div class=\"bar-fill\" style=\"width:${pct}%;background:$color;\"></div></div></div>"
        DISK_ROWS+="<tr><td class=\"key\">$(html_escape "$label")</td><td><div class=\"bar\" style=\"max-width:300px\"><div class=\"bar-fill\" style=\"width:${pct}%;background:$color;\"></div></div> $used_gb / $total_gb GB ($pct%)</td></tr>"
    done <<< "$DISK_DATA"
else
    DISK_ROWS='<tr><td colspan="2" style="text-align:center;color:var(--subtext0);">Storage information unavailable</td></tr>'
fi

NET_ROWS=""
if command -v ip >/dev/null 2>&1 && command -v jq >/dev/null 2>&1; then
    while IFS=$'\037' read -r ifname addrs; do
        [[ -z "$ifname" ]] && continue
        [[ -z "$addrs" ]] && addrs="Connected"
        NET_ROWS+="<tr><td class=\"key\">$(html_escape "$ifname")</td><td>$(html_escape "$addrs")</td></tr>"
    done < <(ip -j addr 2>/dev/null | jq -r '.[] | select(.operstate == "UP" and ((.addr_info // []) | length > 0)) | "\(.ifname)\u001f\([.addr_info[] | "\(.local)/\(.prefixlen)"] | join(", "))"' 2>/dev/null)
fi
[[ -z "$NET_ROWS" ]] && NET_ROWS='<tr><td colspan="2" style="text-align:center;color:var(--subtext0);">Network information unavailable</td></tr>'

GPU_ROWS=""; GPU_N=0
if command -v lspci >/dev/null 2>&1; then
    while IFS= read -r g; do
        GPU_N=$(( GPU_N + 1 ))
        vram="Unknown"; driver="Unknown"
        if [[ "$g" =~ [Nn][Vv][Ii][Dd][Ii][Aa] ]] && command -v nvidia-smi >/dev/null 2>&1; then
            vram=$(nvidia-smi --query-gpu=memory.total --format=csv,noheader 2>/dev/null | head -1 | grep -oE '[0-9]+' | head -1)
            [[ -n "$vram" ]] && vram=$(LC_ALL=C awk -v m="$vram" 'BEGIN{printf "%.1f GB", m/1024}') || vram="Unknown"
            driver=$(nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>/dev/null | head -1)
            [[ -z "$driver" ]] && driver="Unknown"
        fi
        [[ "$GPU_N" -gt 1 ]] && GPU_ROWS+='<tr><td colspan="2" style="padding:0;border-bottom:2px solid var(--surface1);"></td></tr>'
        GPU_ROWS+="<tr><td class=\"key\">GPU $GPU_N</td><td>$(html_escape "$g")</td></tr><tr><td class=\"key\">VRAM $GPU_N</td><td>$(html_escape "$vram")</td></tr><tr><td class=\"key\">Driver $GPU_N</td><td>$(html_escape "$driver")</td></tr>"
    done < <(lspci -nn 2>/dev/null | grep -iE 'vga|3d controller|display controller' | sed 's/^[^ ]* //' | cut -c1-120)
fi

PROC_ROWS=""; PROC_I=0
while read -r pid comm args rss; do
    PROC_I=$(( PROC_I + 1 ))
    mb=$(LC_ALL=C awk -v r="$rss" 'BEGIN{printf "%.1f", r/1024}')
    PROC_ROWS+="<tr><td>$PROC_I</td><td>$(html_escape "$comm")</td><td>$(html_escape "${args:0:100}")</td><td>$mb MB</td><td>$pid</td></tr>"
done < <(ps -eo pid,comm,args,rss --sort=-rss --no-headers 2>/dev/null | head -"$MAX_PROCS")

ERR_ROWS=""
if ERR_OUT=$(journalctl -p 0..3 --since "-${ERR_HOURS} hours" -o short --no-pager 2>/dev/null | awk '!seen[$0]++' | head -"$MAX_ERRORS"); then
    while IFS= read -r line; do
        [[ -z "$line" || "$line" == "--"* ]] && continue
        level="Error"; color="var(--yellow)"
        if [[ "$line" =~ [Cc]rit|[Aa]lert|[Ee]merg ]]; then level="Critical"; color="var(--red)"; fi
        tm=$(printf '%s' "$line" | awk '{print $1, $2, $3}')
        src=$(printf '%s' "$line" | awk '{print $5}' | sed 's/:$//;s/\[[0-9]*\]$//')
        msg=$(printf '%s' "$line" | cut -d' ' -f6- | cut -c1-150)
        ERR_ROWS+="<tr><td style=\"color:$color\">$level</td><td>$(html_escape "$tm")</td><td>$(html_escape "${src:-unknown}")</td><td>$(html_escape "$msg")</td></tr>"
    done <<< "$ERR_OUT"
fi
[[ -z "$ERR_ROWS" ]] && ERR_ROWS="<tr><td colspan=\"4\" style=\"text-align:center;color:var(--subtext0);\">No critical errors in the last $ERR_HOURS hours</td></tr>"

UPD_ROWS=""
if command -v apt >/dev/null 2>&1; then
    UPD_LIST=$(apt list --upgradable 2>/dev/null | tail -n +2 | cut -d/ -f1)
    UPD_N=$(printf '%s' "$UPD_LIST" | grep -c . || true)
    UPD_ROWS="<tr><td class=\"key\">Pending updates</td><td>$UPD_N package(s)</td></tr>"
    while IFS= read -r u; do
        [[ -z "$u" ]] && continue
        UPD_ROWS+="<tr><td class=\"key\">Update</td><td>$(html_escape "$u")</td></tr>"
    done <<< "$(printf '%s' "$UPD_LIST" | head -10)"
else
    UPD_ROWS='<tr><td colspan="2" style="text-align:center;color:var(--subtext0);">Package manager information unavailable</td></tr>'
fi

TEMP_ROWS=""
if command -v sensors >/dev/null 2>&1 && SENSORS_OUT=$(sensors 2>/dev/null | grep -E '°C' | head -10) && [[ -n "$SENSORS_OUT" ]]; then
    while IFS= read -r t; do
        label="${t%%:*}"; val=$(printf '%s' "$t" | grep -oE '[+-]?[0-9.]+°C' | head -1)
        TEMP_ROWS+="<tr><td class=\"key\">$(html_escape "$(printf '%s' "$label" | xargs)")</td><td>$(html_escape "$val")</td></tr>"
    done <<< "$SENSORS_OUT"
else
    TEMP_ROWS='<tr><td colspan="2" style="text-align:center;color:var(--subtext0);">Temperature sensors unavailable (lm-sensors)</td></tr>'
fi

UPTIME_TXT="$UP_D days, $UP_H hours, $UP_M minutes"
BOOT_SHORT=$(date -d "$LAST_BOOT" '+%b %d, %H:%M' 2>/dev/null || printf '%s' "$LAST_BOOT")
GEN_DATE=$(date '+%d/%m/%Y %H:%M')

# ----- assembly -----
TMP_HTML=$(mktemp) || { error "Cannot create temp file."; exit 1; }
trap 'rm -f -- "$TMP_HTML"' EXIT
{
cat <<EOF
<!DOCTYPE html>
<html lang="en" data-theme="$THEME">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>System Health Report - $(html_escape "$HOST")</title>
<style>
:root {
    --radius-md: 16px; --radius-lg: 20px;
$(palette_css Mocha)
}
[data-theme="latte"] {
$(palette_css Latte)
}
* { margin: 0; padding: 0; box-sizing: border-box; }
body { font-family: sans-serif; background: var(--base); color: var(--text); padding: 20px; }
.header { display: flex; align-items: center; padding: 12px 20px; margin-bottom: 28px; background: var(--mantle); border-radius: var(--radius-lg); position: sticky; top: 0; z-index: 100; }
.header-body { flex: 1; text-align: center; }
h1 { font-size: 34px; font-weight: 700; }
.subtitle { color: var(--subtext0); font-size: 15px; margin-top: 6px; }
h2 { font-size: 20px; margin: 32px 0 14px; padding-bottom: 8px; border-bottom: 2px solid var(--surface0); }
.summary { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; margin-bottom: 8px; }
.card { background: var(--mantle); border-radius: var(--radius-md); padding: 16px 18px; }
.card-label { display: block; font-size: 13px; text-transform: uppercase; letter-spacing: 0.6px; color: var(--subtext0); margin-bottom: 4px; }
.card-value { display: block; font-size: 17px; color: var(--text); }
.card-sub { display: block; font-size: 15px; color: var(--overlay1); margin-top: 2px; }
.bar { height: 6px; background: var(--surface0); border-radius: 4px; margin-top: 6px; overflow: hidden; }
.bar-fill { height: 100%; border-radius: 4px; }
table { width: 100%; border-collapse: collapse; background: var(--mantle); border-radius: var(--radius-md); overflow: hidden; }
th { background: var(--surface0); color: var(--subtext1); font-size: 13px; text-transform: uppercase; padding: 11px 14px; text-align: left; }
td { padding: 9px 14px; border-bottom: 1px solid var(--surface0); font-size: 16px; vertical-align: top; }
.key { color: var(--subtext0); white-space: nowrap; width: 180px; }
.fade-section { opacity: 0; transform: translateY(20px); transition: opacity 0.6s, transform 0.6s; }
.fade-section.visible { opacity: 1; transform: translateY(0); }
.footer { margin-top: 40px; padding: 20px; border-top: 1px solid var(--surface0); font-size: 12px; color: var(--overlay0); text-align: center; }
.intro { height: 100vh; display: flex; flex-direction: column; align-items: center; justify-content: center; text-align: center; gap: 16px; }
.intro-cat { width: 200px; height: auto; }
.intro-cat-mocha { display: block; } .intro-cat-latte { display: none; }
[data-theme="latte"] .intro-cat-mocha { display: none; }
[data-theme="latte"] .intro-cat-latte { display: block; }
.header .sidebar-toggle, .header .theme-toggle { width: 44px; height: 44px; border-radius: 50%; background: var(--toolbar-bg); border: 2px solid var(--surface0); color: var(--text); cursor: pointer; display: flex; align-items: center; justify-content: center; }
.theme-toggle .icon-moon { display: flex; } .theme-toggle .icon-sun { display: none; }
[data-theme="latte"] .theme-toggle .icon-moon { display: none; }
[data-theme="latte"] .theme-toggle .icon-sun { display: flex; }
.sidebar { position: fixed; top: 0; left: 0; height: 100%; z-index: 9998; width: 220px; background: var(--mantle); border-right: 1px solid var(--surface0); padding: 16px 0 56px; transform: translateX(-100%); transition: transform 0.35s; overflow-y: auto; }
.sidebar.open { transform: translateX(0); }
.sidebar a { display: block; padding: 8px 16px; color: var(--subtext1); text-decoration: none; font-size: 13px; }
.sidebar a:hover { background: var(--surface0); color: var(--text); }
</style>
</head>
<body>
<div class="intro">
EOF
if [[ "$HAS_CATS" -eq 1 ]]; then
    printf "    <img src='%s' alt='Cat' class='intro-cat intro-cat-mocha'>\n" "$(html_escape "$CAT_DARK")"
    printf "    <img src='%s' alt='Cat' class='intro-cat intro-cat-latte'>\n" "$(html_escape "$CAT_LIGHT")"
fi
cat <<EOF
    <h1>Hello $(html_escape "$USER")</h1>
    <p>Ready to see today's system report?</p>
</div>
<nav class="sidebar" id="sidebar">
    <div style="padding:16px 16px 8px;font-size:12px;text-transform:uppercase;color:var(--overlay0);">Jump to</div>
    <a href='#sysinfo'>System Information</a>
    <a href='#cpu'>CPU</a>
    <a href='#gpu'>Graphics</a>
    <a href='#memory'>Memory</a>
    <a href='#storage'>Storage</a>
    <a href='#network'>Network</a>
    <a href='#processes'>Top Processes</a>
    <a href='#errors'>Recent Errors</a>
    <a href='#temps'>Temperatures</a>
    <a href='#updates'>Package Updates</a>
</nav>
<div class="header" id="header">
<button class="sidebar-toggle" id="sidebarToggle" aria-label="Menu">☰</button>
<div class="header-body">
<h1>System Health Report</h1>
<div class="subtitle">$(html_escape "$HOST") &middot; $GEN_DATE &middot; Catppuccin $THEME_NAME</div>
</div>
<button class="theme-toggle" id="themeToggle" aria-label="Toggle theme"><span class="icon-moon">◐</span><span class="icon-sun">◑</span></button>
</div>

<div class="summary">
    <div class="card" style="border-left: 4px solid var(--mauve);"><span class="card-label">OS</span><span class="card-value">$(html_escape "$OS_PRETTY")</span><span class="card-sub">$(html_escape "$KERNEL")</span></div>
    <div class="card" style="border-left: 4px solid var(--blue);"><span class="card-label">Uptime</span><span class="card-value">${UP_D}d ${UP_H}h</span><span class="card-sub">since $(html_escape "$BOOT_SHORT")</span></div>
    <div class="card" style="border-left: 4px solid var(--peach);"><span class="card-label">CPU</span><span class="card-value">$(html_escape "$CPU_MODEL")</span><span class="card-sub">$CPU_PHYSICAL cores / $CPU_LOGICAL threads</span></div>
    <div class="card" style="border-left: 4px solid $MEM_COLOR;"><span class="card-label">RAM</span><span class="card-value">$MEM_USED_GB / $MEM_TOTAL_GB GB</span><div class="bar"><div class="bar-fill" style="width:${MEM_PCT}%;background:$MEM_COLOR;"></div></div></div>
    $SUMMARY_CARDS
</div>

<div class="fade-section"><h2 id="sysinfo">System Information</h2><table><tbody>
<tr><td class="key">Hostname</td><td>$(html_escape "$HOST")</td></tr>
<tr><td class="key">OS</td><td>$(html_escape "$OS_PRETTY")</td></tr>
<tr><td class="key">Kernel</td><td>$(html_escape "$KERNEL")</td></tr>
<tr><td class="key">Architecture</td><td>$(html_escape "$ARCH")</td></tr>
<tr><td class="key">Install Date</td><td>$(html_escape "$INSTALL_DATE")</td></tr>
<tr><td class="key">Last Boot</td><td>$(html_escape "$LAST_BOOT")</td></tr>
<tr><td class="key">Uptime</td><td>$(html_escape "$UPTIME_TXT")</td></tr>
<tr><td class="key">Vendor</td><td>$(html_escape "$VENDOR")</td></tr>
<tr><td class="key">Model</td><td>$(html_escape "$MODEL")</td></tr>
</tbody></table></div>

<div class="fade-section"><h2 id="cpu">CPU</h2><table><tbody>
<tr><td class="key">Processor</td><td>$(html_escape "$CPU_MODEL")</td></tr>
<tr><td class="key">Cores</td><td>$CPU_PHYSICAL physical / $CPU_LOGICAL logical</td></tr>
<tr><td class="key">Max Clock</td><td>$(html_escape "$CPU_MHZ") MHz</td></tr>
<tr><td class="key">L2 Cache</td><td>$(html_escape "$CPU_L2")</td></tr>
<tr><td class="key">L3 Cache</td><td>$(html_escape "$CPU_L3")</td></tr>
</tbody></table></div>

<div class="fade-section"><h2 id="gpu">Graphics</h2><table><tbody>
$GPU_ROWS
</tbody></table></div>

<div class="fade-section"><h2 id="memory">Memory</h2><table><tbody>
<tr><td class="key">Total</td><td>$MEM_TOTAL_GB GB</td></tr>
<tr><td class="key">Used</td><td>$MEM_USED_GB GB</td></tr>
<tr><td class="key">Usage</td><td><div class="bar" style="max-width:300px"><div class="bar-fill" style="width:${MEM_PCT}%;background:$MEM_COLOR;"></div></div> $MEM_PCT%</td></tr>
</tbody></table></div>

<div class="fade-section"><h2 id="storage">Storage</h2><table><tbody>
$DISK_ROWS
</tbody></table></div>

<div class="fade-section"><h2 id="network">Network</h2><table><tbody>
$NET_ROWS
</tbody></table></div>

<div class="fade-section"><h2 id="processes">Top Processes (by memory)</h2><table><thead><tr><th>#</th><th>Name</th><th>File</th><th>Memory</th><th>PID</th></tr></thead><tbody>
$PROC_ROWS
</tbody></table></div>

<div class="fade-section"><h2 id="errors">Recent Errors (last $ERR_HOURS h)</h2><table><thead><tr><th>Level</th><th>Time</th><th>Source</th><th>Message</th></tr></thead><tbody>
$ERR_ROWS
</tbody></table></div>

<div class="fade-section"><h2 id="temps">Temperatures</h2><table><tbody>
$TEMP_ROWS
</tbody></table></div>

<div class="fade-section"><h2 id="updates">Package Updates</h2><table><tbody>
$UPD_ROWS
</tbody></table></div>

<div class="footer">Generated by SystemHealthReport.sh &middot; Catppuccin $THEME_NAME</div>

<script>
const observer = new IntersectionObserver((entries) => {
  entries.forEach(entry => { if (entry.isIntersecting) entry.target.classList.add('visible'); });
}, { threshold: 0.1 });
document.querySelectorAll('.fade-section').forEach(el => observer.observe(el));
document.querySelectorAll('.bar-fill').forEach(bar => {
  const width = bar.style.width; bar.style.width = '0%';
  setTimeout(() => { bar.style.width = width; }, 100);
});
const toggle = document.getElementById('themeToggle');
const html = document.documentElement;
toggle.addEventListener('click', () => {
    const cur = html.getAttribute('data-theme');
    html.setAttribute('data-theme', cur === 'mocha' ? 'latte' : 'mocha');
});
const sidebar = document.getElementById('sidebar');
document.addEventListener('click', (e) => {
    if (e.target.closest('#sidebarToggle')) sidebar.classList.toggle('open');
    else if (!sidebar.contains(e.target)) sidebar.classList.remove('open');
});
</script>
</body>
</html>
EOF
} > "$TMP_HTML"

SECTIONS=10
if ! cp -- "$TMP_HTML" "$OUTPUT_PATH" 2>/dev/null; then
    error "SystemHealthReport: Failed to write report to '$OUTPUT_PATH'."
    exit 1
fi
trap - EXIT
rm -f -- "$TMP_HTML"

printf '%s\n' "System health report written to $OUTPUT_PATH ($SECTIONS sections)."
exit 0
