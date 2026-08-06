#!/bin/bash
# Install App helper only (oc-run + ocg-vpnc-script). No watchdog. No DNS/route changes.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC_RUN="$ROOT/SslVpnClient.Mac/Native/oc-run.sh"
SRC_VPNC="$ROOT/SslVpnClient.Mac/Native/ocg-vpnc-script.sh"

INSTALL_DIR="/Library/OpenConnectGui"
HELPER="$INSTALL_DIR/oc-run"
VPNC="$INSTALL_DIR/ocg-vpnc-script"
SUDOERS="/etc/sudoers.d/openconnect-gui"
WD_PLIST="/Library/LaunchDaemons/com.openconnectgui.network-watchdog.plist"
WD_LABEL="com.openconnectgui.network-watchdog"

[[ -f "$SRC_RUN" && -f "$SRC_VPNC" ]] || { echo "missing native scripts" >&2; exit 1; }

TMP="$(mktemp -d /tmp/ocg-helper-XXXXXX)"
cp "$SRC_RUN" "$TMP/oc-run"
cp "$SRC_VPNC" "$TMP/ocg-vpnc-script"
chmod 755 "$TMP/oc-run" "$TMP/ocg-vpnc-script"

cat > "$TMP/sudoers" <<EOF
ALL ALL=(root) NOPASSWD: $HELPER
EOF
chmod 440 "$TMP/sudoers"

cat > "$TMP/install.sh" <<EOF
#!/bin/bash
set -euo pipefail
launchctl bootout system/$WD_LABEL 2>/dev/null || true
pkill -f network-watchdog.sh 2>/dev/null || true
rm -f "$WD_PLIST" "$INSTALL_DIR/network-watchdog.sh" 2>/dev/null || true
rm -rf "$INSTALL_DIR/last-good-net" 2>/dev/null || true
mkdir -p "$INSTALL_DIR"
cp "$TMP/oc-run" "$HELPER"
cp "$TMP/ocg-vpnc-script" "$VPNC"
chown root:wheel "$HELPER" "$VPNC"
chmod 755 "$HELPER" "$VPNC"
cp "$TMP/sudoers" "$SUDOERS"
chown root:wheel "$SUDOERS"
chmod 440 "$SUDOERS"
/usr/sbin/visudo -cf "$SUDOERS"
echo installed
EOF
chmod 755 "$TMP/install.sh"

echo "==> Installing OpenConnect Gui helper v8 (admin once)…"
osascript -e "do shell script \"/bin/bash $(printf %q "$TMP/install.sh")\" with administrator privileges"
rm -rf "$TMP"

ver="$(sudo -n "$HELPER" version 2>/dev/null || echo 0)"
if [[ "$ver" -ge 8 ]]; then
  echo "OK: helper v$ver"
else
  echo "WARN: version=$ver (expected 8)" >&2
  exit 1
fi
