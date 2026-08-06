#!/bin/bash
# OpenConnect Gui privileged runner → /Library/OpenConnectGui/oc-run
# v8: connect snapshot must not abort on networksetup pipefail
set -eu
# 不用 pipefail：networksetup/scutil 管道失败不应中断 connect

HELPER_VERSION=8
INSTALL_DIR="/Library/OpenConnectGui"
OCG_VPNC="$INSTALL_DIR/ocg-vpnc-script"

OPENCONNECT_CANDIDATES=(
  /opt/homebrew/bin/openconnect
  /usr/local/bin/openconnect
)

find_oc() {
  for p in "${OPENCONNECT_CANDIDATES[@]}"; do
    [[ -x "$p" ]] && { echo "$p"; return 0; }
  done
  return 1
}

capture_phys() {
  local gw ifc line
  gw="$(route -n get default 2>/dev/null | awk '/gateway:/{print $2; exit}' || true)"
  ifc="$(route -n get default 2>/dev/null | awk '/interface:/{print $2; exit}' || true)"
  if [[ -z "$gw" || -z "$ifc" ]]; then
    line="$(netstat -rn -f inet 2>/dev/null | awk '$1=="default"{print $2,$NF; exit}' || true)"
    gw="$(echo "$line" | awk '{print $1}')"
    ifc="$(echo "$line" | awk '{print $2}')"
  fi
  if [[ -z "$gw" || "$ifc" == utun* ]]; then
    echo "cannot detect physical default gateway" >&2
    return 1
  fi
  echo "$gw $ifc"
}

network_service_for_if() {
  local ifc="$1"
  local uuid name
  uuid="$(echo "show State:/Network/Global/IPv4" | scutil 2>/dev/null | \
    grep -oE '[a-fA-F0-9]{8}-([a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}' | head -1 || true)"
  if [[ -n "$uuid" ]]; then
    name="$(echo "show Setup:/Network/Service/$uuid/Interface" | scutil 2>/dev/null | \
      awk -F' : ' '/UserDefinedName/{print $2; exit}' || true)"
    if [[ -n "$name" ]]; then
      echo "$name"
      return 0
    fi
  fi
  networksetup -listnetworkserviceorder 2>/dev/null | \
    awk -v iface="$ifc" '
      /^\([0-9]+\)/ { name=$0; sub(/^\([0-9]+\) /, "", name) }
      /Device: / {
        dev=$0; sub(/.*Device: /, "", dev); sub(/\).*/, "", dev);
        if (dev == iface) { print name; exit }
      }' || true
}

primary_service_uuid() {
  echo "show State:/Network/Global/IPv4" | scutil 2>/dev/null | \
    grep -oE '[a-fA-F0-9]{8}-([a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}' | head -1 || true
}

clear_one_utun_scutil() {
  local u="$1"
  [[ -z "$u" ]] && return 0
  scutil >/dev/null 2>&1 <<-EOF || true
		open
		remove State:/Network/Service/$u/IPv4
		remove State:/Network/Service/$u/DNS
		close
	EOF
}

clear_all_utun_scutil() {
  local tundev="${1:-}" u
  [[ -n "$tundev" ]] && clear_one_utun_scutil "$tundev"
  for u in utun0 utun1 utun2 utun3 utun4 utun5 utun6 utun7 utun8 utun9 \
           utun10 utun11 utun12 utun13 utun14 utun15 utun16 utun17 utun18 utun19 utun20; do
    [[ -n "$tundev" && "$u" == "$tundev" ]] && continue
    clear_one_utun_scutil "$u"
  done
}

clear_vpn_half_routes() {
  route -n delete -net 0.0.0.0/1 2>/dev/null || true
  route -n delete -net 128.0.0.0/1 2>/dev/null || true
  local u
  for u in utun0 utun1 utun2 utun3 utun4 utun5 utun6 utun7 utun8 utun9 \
           utun10 utun11 utun12 utun13 utun14 utun15 utun16 utun17 utun18 utun19 utun20; do
    route -n delete default -interface "$u" 2>/dev/null || true
  done
}

backup_network_state() {
  local session="$1"
  local dir="$session/net-backup"
  mkdir -p "$dir" || return 0

  route -n get default > "$dir/route-default.txt" 2>/dev/null || true

  local phys gw ifc
  phys="$(capture_phys 2>/dev/null || true)"
  gw="$(echo "$phys" | awk '{print $1}')"
  ifc="$(echo "$phys" | awk '{print $2}')"
  echo "${gw:-}" > "$dir/phys-gw.txt"
  echo "${ifc:-}" > "$dir/phys-if.txt"
  network_service_for_if "${ifc:-}" > "$dir/network-service.txt" 2>/dev/null || true

  local list="$dir/services.list"
  networksetup -listallnetworkservices > "$list" 2>/dev/null || true
  : > "$dir/services.tsv"

  local s key vals
  while IFS= read -r s || [[ -n "${s:-}" ]]; do
    # 禁用服务以 * 开头；首行说明含 asterisk
    [[ -z "$s" ]] && continue
    [[ "$s" == \** ]] && continue
    [[ "$s" == *"asterisk"* ]] && continue
    key="$(printf '%s' "$s" | /usr/bin/shasum -a 256 2>/dev/null | awk '{print $1}' || true)"
    [[ -z "$key" ]] && key="$(printf '%s' "$s" | /usr/bin/cksum 2>/dev/null | awk '{print $1}' || true)"
    [[ -z "$key" ]] && key="svc"
    vals="$(networksetup -getdnsservers "$s" 2>/dev/null || true)"
    printf '%s\t%s\n' "$key" "$s" >> "$dir/services.tsv"
    if echo "$vals" | grep -qi "aren't any"; then
      echo "EMPTY" > "$dir/$key.dns"
    else
      echo "$vals" | awk 'NF' > "$dir/$key.dns" || true
      [[ -s "$dir/$key.dns" ]] || echo "EMPTY" > "$dir/$key.dns"
    fi
  done < <(tail -n +2 "$list" 2>/dev/null || true)

  local uuid
  uuid="$(primary_service_uuid || true)"
  echo "${uuid:-}" > "$dir/primary-service-uuid.txt"
  if [[ -n "$uuid" ]]; then
    echo "show State:/Network/Service/$uuid/DNS" | scutil 2>/dev/null | \
      awk '/ServerAddresses/{p=1;next} /:/ && p && $1 !~ /^[0-9]+$/{p=0} p && /:/{print $3}' \
      > "$dir/scutil-primary-dns-addrs.txt" || true
  fi

  rm -rf "$session/dns-backup" 2>/dev/null || true
  ln -sfn "net-backup" "$session/dns-backup" 2>/dev/null || true
  chmod -R a+r "$dir" 2>/dev/null || true
  return 0
}

_restore_one_service_dns() {
  local dir="$1" key="$2" s="$3" f servers
  f="$dir/$key.dns"
  if [[ ! -f "$f" ]] || grep -qx "EMPTY" "$f" 2>/dev/null; then
    networksetup -setdnsservers "$s" Empty 2>/dev/null || true
  else
    servers="$(awk 'NF{printf "%s ", $0}' "$f" | sed 's/[[:space:]]*$//' || true)"
    if [[ -z "$servers" ]]; then
      networksetup -setdnsservers "$s" Empty 2>/dev/null || true
    else
      # shellcheck disable=SC2086
      networksetup -setdnsservers "$s" $servers 2>/dev/null || true
    fi
  fi
}

restore_network_state() {
  local session="${1:-}"
  local dir="" tundev="" gw="" uuid=""

  if [[ -n "$session" && -d "$session/net-backup" ]]; then
    dir="$session/net-backup"
  elif [[ -n "$session" && -d "$session/dns-backup" ]]; then
    dir="$session/dns-backup"
  fi

  if [[ -n "$session" && -f "$session/tundev.txt" ]]; then
    tundev="$(cat "$session/tundev.txt" 2>/dev/null || true)"
  fi

  clear_all_utun_scutil "$tundev"

  if [[ -z "$dir" || ! -f "$dir/services.tsv" ]]; then
    return 0
  fi

  gw="$(cat "$dir/phys-gw.txt" 2>/dev/null || true)"
  uuid="$(cat "$dir/primary-service-uuid.txt" 2>/dev/null || true)"

  local key s
  while IFS=$'\t' read -r key s || [[ -n "${s:-}" ]]; do
    [[ -z "$s" ]] && continue
    _restore_one_service_dns "$dir" "$key" "$s"
  done < "$dir/services.tsv"

  if [[ -n "$uuid" && -f "$dir/scutil-primary-dns-addrs.txt" ]]; then
    local addrs
    addrs="$(awk 'NF{printf "%s ", $0}' "$dir/scutil-primary-dns-addrs.txt" | sed 's/[[:space:]]*$//' || true)"
    if [[ -n "$addrs" ]]; then
      # shellcheck disable=SC2086
      scutil >/dev/null 2>&1 <<-EOF || true
				open
				d.init
				d.add ServerAddresses * $addrs
				set State:/Network/Service/$uuid/DNS
				close
			EOF
    fi
  fi

  clear_vpn_half_routes
  if [[ -n "$gw" ]]; then
    local ifc_now
    ifc_now="$(route -n get default 2>/dev/null | awk '/interface:/{print $2; exit}' || true)"
    if [[ "$ifc_now" == utun* ]] || [[ -z "$ifc_now" ]]; then
      route -n add default "$gw" 2>/dev/null || \
        route -n change default "$gw" 2>/dev/null || true
    fi
  fi

  dscacheutil -flushcache 2>/dev/null || true
  killall -HUP mDNSResponder 2>/dev/null || true
}

fail() {
  local session="${1:-}" msg="$2"
  echo "$msg" >&2
  if [[ -n "$session" && -d "$session" ]]; then
    echo "$msg" > "$session/helper.err" 2>/dev/null || true
    chmod 644 "$session/helper.err" 2>/dev/null || true
  fi
  exit 1
}

cmd="${1:-}"
case "$cmd" in
  version)
    echo "$HELPER_VERSION"
    exit 0
    ;;
  ping)
    echo "ok"
    exit 0
    ;;
  disconnect)
    pid="${2:-}"
    session="${3:-}"
    restore_network_state "$session"
    if [[ -n "$pid" ]]; then
      kill -KILL "$pid" 2>/dev/null || true
    fi
    pkill -KILL -x openconnect 2>/dev/null || true
    if [[ -n "$session" && -f "$session/vpngateway.txt" ]]; then
      route -n delete -host "$(cat "$session/vpngateway.txt")" 2>/dev/null || true
    fi
    restore_network_state "$session"
    (
      if [[ -n "$session" && -f "$session/chnroutes.path" ]]; then
        chn_file="$(cat "$session/chnroutes.path" 2>/dev/null || true)"
        if [[ -n "$chn_file" && -f "$chn_file" ]]; then
          ncpu="$(sysctl -n hw.logicalcpu 2>/dev/null || echo 8)"
          [[ "$ncpu" -gt 16 ]] && ncpu=16
          grep -vE '^[[:space:]]*(#|$)' "$chn_file" | \
            awk '{
              gsub(/[[:space:]]/, "", $1)
              if ($1 ~ /^[0-9]+(\.[0-9]+){3}\/[0-9]+$/) print $1
            }' | \
            xargs -P "$ncpu" -n 1 -I{} \
              route -n delete -net {} 2>/dev/null || true
        fi
      fi
    ) >/dev/null 2>&1 &
    echo "disconnected"
    exit 0
    ;;
  connect)
    session="${2:-}"
    if [[ -z "$session" || ! -d "$session" ]]; then
      fail "" "missing session dir"
    fi
    case "$session" in
      /tmp/ocg-*|/private/tmp/ocg-*) ;;
      *) fail "$session" "refusing session path: $session" ;;
    esac

    meta="$session/meta.env"
    pass="$session/pass.txt"
    log="$session/openconnect.log"
    pidf="$session/openconnect.pid"
    if [[ ! -f "$meta" || ! -f "$pass" ]]; then
      fail "$session" "missing meta.env or pass.txt"
    fi

    # shellcheck disable=SC1090
    source "$meta"
    : "${OCG_USER:?}"
    : "${OCG_URL:?}"
    OCG_SPLIT="${OCG_SPLIT:-0}"
    OCG_CHNROUTES="${OCG_CHNROUTES:-}"

    OC="$(find_oc)" || fail "$session" "openconnect not found"

    export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/sbin"
    export LANG=C LC_ALL=C

    phys="$(capture_phys)" || fail "$session" "cannot detect physical default gateway"
    OCG_PHYS_GW="$(echo "$phys" | awk '{print $1}')"
    OCG_PHYS_IF="$(echo "$phys" | awk '{print $2}')"
    OCG_NETWORK_SERVICE="$(network_service_for_if "$OCG_PHYS_IF" || true)"
    echo "$OCG_PHYS_GW" > "$session/phys-gw.txt"
    echo "$OCG_PHYS_IF" > "$session/phys-if.txt"
    echo "$OCG_NETWORK_SERVICE" > "$session/network-service.txt"
    chmod 644 "$session/phys-gw.txt" "$session/phys-if.txt" "$session/network-service.txt" 2>/dev/null || true

    backup_network_state "$session" || true

    SCRIPT=""
    if [[ "$OCG_SPLIT" == "1" ]]; then
      [[ -f "$OCG_VPNC" ]] || fail "$session" "split script missing: $OCG_VPNC (reinstall helper)"
      [[ -n "$OCG_CHNROUTES" && -f "$OCG_CHNROUTES" ]] || fail "$session" "chnroutes file missing"
      OCG_ROUTE_LIST="$session/routes.list"
      : > "$OCG_ROUTE_LIST"
      export OCG_SPLIT OCG_CHNROUTES OCG_PHYS_GW OCG_PHYS_IF OCG_ROUTE_LIST
      export OCG_SESSION_DIR="$session"
      SCRIPT="$OCG_VPNC"
      echo "split mode phys_gw=$OCG_PHYS_GW if=$OCG_PHYS_IF svc=$OCG_NETWORK_SERVICE" >> "$log"
    else
      if [[ -f "$OCG_VPNC" ]]; then
        export OCG_SPLIT=0
        export OCG_SESSION_DIR="$session"
        export OCG_PHYS_GW OCG_PHYS_IF
        SCRIPT="$OCG_VPNC"
      else
        for p in /opt/homebrew/etc/vpnc/vpnc-script /usr/local/etc/vpnc/vpnc-script /etc/vpnc/vpnc-script; do
          [[ -f "$p" ]] && { SCRIPT="$p"; break; }
        done
      fi
    fi

    ARGS=(--protocol=anyconnect --user="$OCG_USER" --passwd-on-stdin --non-inter
          --useragent="AnyConnect-compatible OpenConnect VPN Agent" --verbose)
    if [[ -n "${SCRIPT:-}" ]]; then
      ARGS+=(--script="$SCRIPT")
    fi
    ARGS+=("$OCG_URL")

    pkill -KILL -x openconnect 2>/dev/null || true

    if [[ "$OCG_SPLIT" == "1" ]]; then
      nohup env OCG_SPLIT=1 \
        OCG_CHNROUTES="$OCG_CHNROUTES" \
        OCG_PHYS_GW="$OCG_PHYS_GW" \
        OCG_PHYS_IF="$OCG_PHYS_IF" \
        OCG_ROUTE_LIST="$OCG_ROUTE_LIST" \
        OCG_SESSION_DIR="$session" \
        "$OC" "${ARGS[@]}" < "$pass" > "$log" 2>&1 &
    else
      nohup env OCG_SPLIT=0 OCG_SESSION_DIR="$session" \
        OCG_PHYS_GW="$OCG_PHYS_GW" OCG_PHYS_IF="$OCG_PHYS_IF" \
        "$OC" "${ARGS[@]}" < "$pass" > "$log" 2>&1 &
    fi
    echo $! > "$pidf"
    chmod 644 "$pidf" "$log" 2>/dev/null || true
    rm -f "$pass"
    disown 2>/dev/null || true
    echo "started $(cat "$pidf") split=$OCG_SPLIT"
    exit 0
    ;;
  *)
    echo "usage: oc-run version|ping|connect <dir>|disconnect [pid] [session]" >&2
    exit 1
    ;;
esac
