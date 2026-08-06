#!/bin/bash
# OpenConnect Gui vpnc-script wrapper (helper v9 / libopenconnect).
# 非 root 时经 sudo -n -E 提权；优先使用内置 stock vpnc-script。
set -euo pipefail

SELF="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
if [[ "${EUID:-0}" -ne 0 ]]; then
  exec /usr/bin/sudo -n -E "$SELF" "$@"
fi

STOCK_CANDIDATES=(
  /Library/OpenConnectGui/vpnc-script
  /opt/homebrew/etc/vpnc/vpnc-script
  /usr/local/etc/vpnc/vpnc-script
  /etc/vpnc/vpnc-script
)

find_stock() {
  for p in "${STOCK_CANDIDATES[@]}"; do
    [[ -x "$p" || -f "$p" ]] && { echo "$p"; return 0; }
  done
  return 1
}

STOCK="$(find_stock)" || {
  echo "ocg-vpnc-script: stock vpnc-script not found" >&2
  exit 1
}

REASON="${reason:-}"
SPLIT="${OCG_SPLIT:-0}"
ROUTE_LIST="${OCG_ROUTE_LIST:-}"
CHNROUTES="${OCG_CHNROUTES:-}"
PHYS_GW="${OCG_PHYS_GW:-}"
PHYS_IF="${OCG_PHYS_IF:-}"
SESSION="${OCG_SESSION_DIR:-}"

# 从 session 文件回退（openconnect 会清掉父进程环境变量）
if [[ -z "$SESSION" && -f "${0:-}" ]]; then
  # 若直接被当成 wrap 同目录调用
  _d="$(cd "$(dirname "$0")" && pwd)"
  [[ -f "$_d/ocg.env" || -f "$_d/meta.env" ]] && SESSION="$_d"
fi
if [[ -n "$SESSION" && -f "$SESSION/ocg.env" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "$SESSION/ocg.env"
  set +a
  SESSION="${OCG_SESSION_DIR:-$SESSION}"
  SPLIT="${OCG_SPLIT:-$SPLIT}"
  ROUTE_LIST="${OCG_ROUTE_LIST:-$ROUTE_LIST}"
  CHNROUTES="${OCG_CHNROUTES:-$CHNROUTES}"
  PHYS_GW="${OCG_PHYS_GW:-$PHYS_GW}"
  PHYS_IF="${OCG_PHYS_IF:-$PHYS_IF}"
fi
if [[ -z "$SPLIT" || "$SPLIT" == "0" ]] && [[ -n "$SESSION" && -f "$SESSION/split.flag" ]]; then
  SPLIT="$(tr -d '[:space:]' < "$SESSION/split.flag" || true)"
fi
if [[ -z "$PHYS_GW" && -n "$SESSION" && -f "$SESSION/phys-gw.txt" ]]; then
  PHYS_GW="$(tr -d '[:space:]' < "$SESSION/phys-gw.txt" || true)"
fi
if [[ -z "$PHYS_IF" && -n "$SESSION" && -f "$SESSION/phys-if.txt" ]]; then
  PHYS_IF="$(tr -d '[:space:]' < "$SESSION/phys-if.txt" || true)"
fi
if [[ -z "$CHNROUTES" && -n "$SESSION" && -f "$SESSION/chnroutes-v4" ]]; then
  CHNROUTES="$SESSION/chnroutes-v4"
fi
if [[ -z "$ROUTE_LIST" && -n "$SESSION" ]]; then
  ROUTE_LIST="$SESSION/routes.list"
fi

log() {
  echo "ocg-vpnc: $*" >&2
  if [[ -n "${SESSION:-}" && -d "$SESSION" ]]; then
    echo "[ocg-vpnc] $*" >> "$SESSION/openconnect.log" 2>/dev/null || true
  fi
}

log "reason=$REASON split=$SPLIT session=${SESSION:-} phys_gw=${PHYS_GW:-} chn=${CHNROUTES:-}"

record_session() {
  [[ -n "$SESSION" && -d "$SESSION" ]] || return 0
  if [[ -n "${TUNDEV:-}" ]]; then
    echo "$TUNDEV" > "$SESSION/tundev.txt"
  fi
  if [[ -n "${VPNGATEWAY:-}" ]]; then
    echo "$VPNGATEWAY" > "$SESSION/vpngateway.txt"
  fi
  if [[ -n "$CHNROUTES" && -f "$CHNROUTES" ]]; then
    # 断开时批量删除用
    echo "$CHNROUTES" > "$SESSION/chnroutes.path"
  fi
  chmod 644 "$SESSION/tundev.txt" "$SESSION/vpngateway.txt" 2>/dev/null || true
}

# 管道重定向批量添加：一份命令流 → 单个 sh 执行（比 bash while+route 快很多）
batch_route_add_nets() {
  local file="$1"
  local gw="$2"
  local parallel="${3:-0}"

  if [[ "$parallel" -gt 1 ]]; then
    # 并行：每行一个 route，xargs -P 加速（路由套接字可并行）
    grep -vE '^[[:space:]]*(#|$)' "$file" | \
      awk '{
        gsub(/[[:space:]]/, "", $1)
        if ($1 ~ /^[0-9]+(\.[0-9]+){3}\/[0-9]+$/) print $1
      }' | \
      xargs -P "$parallel" -n 1 -I{} \
        route -n add -net {} "$gw" 2>/dev/null || true
  else
    grep -vE '^[[:space:]]*(#|$)' "$file" | \
      awk -v gw="$gw" '{
        gsub(/[[:space:]]/, "", $1)
        if ($1 ~ /^[0-9]+(\.[0-9]+){3}\/[0-9]+$/)
          printf "route -n add -net %s %s 2>/dev/null || true\n", $1, gw
      }' | /bin/sh
  fi
}

batch_route_del_nets() {
  local file="$1"
  local parallel="${2:-8}"

  if [[ ! -f "$file" ]]; then
    return 0
  fi

  if [[ "$parallel" -gt 1 ]]; then
    grep -vE '^[[:space:]]*(#|$)' "$file" | \
      awk '{
        gsub(/[[:space:]]/, "", $1)
        if ($1 ~ /^[0-9]+(\.[0-9]+){3}\/[0-9]+$/) print $1
      }' | \
      xargs -P "$parallel" -n 1 -I{} \
        route -n delete -net {} 2>/dev/null || true
  else
    grep -vE '^[[:space:]]*(#|$)' "$file" | \
      awk '{
        gsub(/[[:space:]]/, "", $1)
        if ($1 ~ /^[0-9]+(\.[0-9]+){3}\/[0-9]+$/)
          printf "route -n delete -net %s 2>/dev/null || true\n", $1
      }' | /bin/sh
  fi
}

apply_split_routes() {
  local tundev="${TUNDEV:-}"
  local vpngw="${VPNGATEWAY:-}"
  local ncpu

  if [[ -z "$tundev" || -z "$vpngw" ]]; then
    log "missing TUNDEV or VPNGATEWAY"
    return 1
  fi
  if [[ -z "$PHYS_GW" ]]; then
    log "missing OCG_PHYS_GW"
    return 1
  fi
  if [[ -z "$CHNROUTES" || ! -f "$CHNROUTES" ]]; then
    log "missing chnroutes file"
    return 1
  fi

  : > "${ROUTE_LIST:-/dev/null}"

  # stock 常写成「default via VPN 内网网关」，仅 -interface 删不掉
  route -n delete default -interface "$tundev" 2>/dev/null || true
  route -n delete default 2>/dev/null || true
  route -n delete -net 0.0.0.0/1 -interface "$tundev" 2>/dev/null || true
  route -n delete -net 128.0.0.0/1 -interface "$tundev" 2>/dev/null || true
  # 先恢复物理默认，再挂 half-defaults（外网走 VPN，国内走物理）
  if route -n add default "$PHYS_GW" 2>/dev/null; then
    log "phys default -> $PHYS_GW"
  else
    route -n change default "$PHYS_GW" 2>/dev/null || true
    log "phys default change -> $PHYS_GW"
  fi

  if route -n add -host "$vpngw" "$PHYS_GW" 2>/dev/null; then
    echo "host $vpngw" >> "${ROUTE_LIST:-/dev/null}"
  else
    route -n change -host "$vpngw" "$PHYS_GW" 2>/dev/null || true
    echo "host $vpngw" >> "${ROUTE_LIST:-/dev/null}"
  fi
  log "host route $vpngw -> $PHYS_GW"

  ncpu="$(sysctl -n hw.logicalcpu 2>/dev/null || echo 4)"
  [[ "$ncpu" -lt 1 ]] && ncpu=4
  [[ "$ncpu" -gt 16 ]] && ncpu=16

  local count
  count="$(grep -cE '^[0-9]' "$CHNROUTES" 2>/dev/null || echo 0)"
  log "batch add $count chnroutes via pipe (P=$ncpu)…"
  local t0 t1
  t0="$(date +%s)"
  batch_route_add_nets "$CHNROUTES" "$PHYS_GW" "$ncpu"
  t1="$(date +%s)"
  log "chnroutes added in $((t1 - t0))s"

  # half defaults last
  {
    echo "route -n add -net 0.0.0.0/1 -interface $tundev 2>/dev/null || route -n change -net 0.0.0.0/1 -interface $tundev 2>/dev/null || true"
    echo "route -n add -net 128.0.0.0/1 -interface $tundev 2>/dev/null || route -n change -net 128.0.0.0/1 -interface $tundev 2>/dev/null || true"
  } | /bin/sh
  echo "net 0.0.0.0/1 iface" >> "${ROUTE_LIST:-/dev/null}"
  echo "net 128.0.0.0/1 iface" >> "${ROUTE_LIST:-/dev/null}"
  log "half-defaults -> $tundev"
}

# 分流：stock 会改物理 DNS；连接后立刻按「连接前快照」写回（原样，不写死）。
restore_phys_dns_from_backup() {
  local dir key s f servers
  if [[ -n "$SESSION" && -d "$SESSION/net-backup" ]]; then
    dir="$SESSION/net-backup"
  elif [[ -n "$SESSION" && -d "$SESSION/dns-backup" ]]; then
    dir="$SESSION/dns-backup"
  else
    return 0
  fi
  [[ -f "$dir/services.tsv" ]] || return 0
  while IFS=$'\t' read -r key s; do
    [[ -z "$s" ]] && continue
    f="$dir/$key.dns"
    if [[ ! -f "$f" ]] || grep -qx "EMPTY" "$f" 2>/dev/null; then
      networksetup -setdnsservers "$s" Empty 2>/dev/null || true
    else
      servers="$(awk 'NF{printf "%s ", $0}' "$f" | sed 's/[[:space:]]*$//')"
      if [[ -z "$servers" ]]; then
        networksetup -setdnsservers "$s" Empty 2>/dev/null || true
      else
        # shellcheck disable=SC2086
        networksetup -setdnsservers "$s" $servers 2>/dev/null || true
      fi
    fi
  done < "$dir/services.tsv"
  dscacheutil -flushcache 2>/dev/null || true
  killall -HUP mDNSResponder 2>/dev/null || true
  log "restored pre-connect DNS after stock (split)"
}

case "$REASON" in
  connect)
    /bin/bash "$STOCK"
    record_session
    if [[ "$SPLIT" == "1" ]]; then
      restore_phys_dns_from_backup || true
      apply_split_routes || log "apply_split_routes failed"
    fi
    ;;
  disconnect|reconnect|attempt-reconnect)
    # kill -KILL 时通常不会走到这里；若走到，只清关键路由，DNS 由 oc-run 负责
    route -n delete -net 0.0.0.0/1 2>/dev/null || true
    route -n delete -net 128.0.0.0/1 2>/dev/null || true
    if [[ -n "${VPNGATEWAY:-}" ]]; then
      route -n delete -host "$VPNGATEWAY" 2>/dev/null || true
    fi
    ;;
  *)
    /bin/bash "$STOCK"
    ;;
esac

exit 0
