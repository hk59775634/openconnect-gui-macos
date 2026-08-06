#!/bin/bash
# Emergency: purge leftover VPN half-routes / chnroutes via helper.
set -euo pipefail
HELPER="/Library/OpenConnectGui/oc-run"
SESSION="${1:-}"

if [[ ! -x "$HELPER" ]]; then
  echo "helper missing: $HELPER" >&2
  exit 1
fi

echo "==> Purging VPN residual routes…"
if [[ -n "$SESSION" ]]; then
  sudo -n "$HELPER" purge-routes "$SESSION" || sudo "$HELPER" purge-routes "$SESSION"
else
  # latest session under /tmp
  latest="$(ls -1dt /tmp/ocg-* 2>/dev/null | head -1 || true)"
  if [[ -n "$latest" ]]; then
    echo "using session: $latest"
    sudo -n "$HELPER" purge-routes "$latest" || sudo "$HELPER" purge-routes "$latest"
  else
    sudo -n "$HELPER" purge-routes || sudo "$HELPER" purge-routes
  fi
fi

echo "==> Default route now:"
route -n get default 2>&1 | head -8
echo "Done."
