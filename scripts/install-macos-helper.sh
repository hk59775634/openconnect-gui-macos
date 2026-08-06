#!/bin/bash
# Install helper v13: oc-run + vpnc + self-contained ocg-vpnhost (NO Avalonia)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

ARCH="$(uname -m)"
if [[ "$ARCH" == "arm64" ]]; then RID=osx-arm64; else RID=osx-x64; fi

SRC_RUN="$ROOT/SslVpnClient.Mac/Native/oc-run.sh"
SRC_VPNC="$ROOT/SslVpnClient.Mac/Native/ocg-vpnc-script.sh"
SRC_STOCK="$ROOT/SslVpnClient.Mac/Native/vpnc-script"
NATIVE="$ROOT/SslVpnClient.Mac/Native"
VPNHHOST_OUT="$ROOT/dist/vpnhost-$RID"

INSTALL_DIR="/Library/OpenConnectGui"
HELPER="$INSTALL_DIR/oc-run"
VPNC="$INSTALL_DIR/ocg-vpnc-script"
STOCK="$INSTALL_DIR/vpnc-script"
HOST_WRAP="$INSTALL_DIR/ocg-vpnhost"
HOST_APP_DIR="$INSTALL_DIR/vpnhost"
SUDOERS="/etc/sudoers.d/openconnect-gui"

[[ -f "$SRC_RUN" && -f "$SRC_VPNC" ]] || { echo "missing native scripts" >&2; exit 1; }
if [[ ! -f "$SRC_STOCK" ]] || ! ls "$NATIVE"/*.dylib >/dev/null 2>&1; then
  echo "==> vendoring native libs…"
  "$ROOT/scripts/vendor-macos-native.sh" "$RID"
fi

echo "==> Publish headless VpnHost ($RID, no Avalonia)…"
dotnet publish "$ROOT/SslVpnClient.Mac.VpnHost/SslVpnClient.Mac.VpnHost.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:NuGetAudit=false \
  -o "$VPNHHOST_OUT"
[[ -x "$VPNHHOST_OUT/ocg-vpnhost" ]] || { echo "missing published ocg-vpnhost" >&2; exit 1; }

TMP="$(mktemp -d /tmp/ocg-helper-XXXXXX)"
cp "$SRC_RUN" "$TMP/oc-run"
cp "$SRC_VPNC" "$TMP/ocg-vpnc-script"
cp "$SRC_STOCK" "$TMP/vpnc-script"
mkdir -p "$TMP/lib" "$TMP/vpnhost"
cp -f "$NATIVE"/*.dylib "$TMP/lib/" 2>/dev/null || true
cp -f "$NATIVE"/lib/"$RID"/*.dylib "$TMP/lib/" 2>/dev/null || true
rsync -a --delete "$VPNHHOST_OUT/" "$TMP/vpnhost/"

# Thin wrapper kept at classic path; real binary is Avalonia-free
cat > "$TMP/ocg-vpnhost" <<'EOF'
#!/bin/bash
set -euo pipefail
SESSION="${1:-}"
[[ -n "$SESSION" ]] || { echo "usage: ocg-vpnhost <session>" >&2; exit 2; }
LIB="/Library/OpenConnectGui/lib"
APP="/Library/OpenConnectGui/vpnhost/ocg-vpnhost"
[[ -x "$APP" ]] || { echo "missing $APP — reinstall helper" >&2; exit 3; }
export DYLD_LIBRARY_PATH="$LIB${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
export DYLD_FALLBACK_LIBRARY_PATH="$LIB${DYLD_FALLBACK_LIBRARY_PATH:+:$DYLD_FALLBACK_LIBRARY_PATH}"
export OCG_VPN_WORKER=1
export OCG_VPN_SESSION="$SESSION"
# Never launch the Avalonia GUI binary.
exec "$APP" --vpn-worker "$SESSION"
EOF
chmod 755 "$TMP/oc-run" "$TMP/ocg-vpnc-script" "$TMP/vpnc-script" "$TMP/ocg-vpnhost"

# Bump helper script version stamp is inside oc-run.sh (expect 13)
cat > "$TMP/sudoers" <<EOF
Defaults!$VPNC !env_reset,!secure_path
ALL ALL=(root) NOPASSWD:SETENV: $VPNC
ALL ALL=(root) NOPASSWD: $HELPER
EOF
chmod 440 "$TMP/sudoers"

cat > "$TMP/install.sh" <<EOF
#!/bin/bash
set -euo pipefail
mkdir -p "$INSTALL_DIR/lib" "$HOST_APP_DIR"
cp "$TMP/oc-run" "$HELPER"
cp "$TMP/ocg-vpnc-script" "$VPNC"
cp "$TMP/vpnc-script" "$STOCK"
cp "$TMP/ocg-vpnhost" "$HOST_WRAP"
rsync -a --delete "$TMP/vpnhost/" "$HOST_APP_DIR/"
cp -f "$TMP/lib"/*.dylib "$INSTALL_DIR/lib/" 2>/dev/null || true
# remove stale GUI host.env that pointed at Avalonia app
rm -f "$INSTALL_DIR/host.env"
chown -R root:wheel "$INSTALL_DIR"
chmod 755 "$HELPER" "$VPNC" "$STOCK" "$HOST_WRAP"
chmod 755 "$HOST_APP_DIR/ocg-vpnhost"
chmod 755 "$INSTALL_DIR/lib"/*.dylib 2>/dev/null || true
cp "$TMP/sudoers" "$SUDOERS"
chown root:wheel "$SUDOERS"
chmod 440 "$SUDOERS"
/usr/sbin/visudo -cf "$SUDOERS"
echo installed
EOF
chmod 755 "$TMP/install.sh"

echo "==> Installing OpenConnect Gui helper v13 (admin once)…"
osascript -e "do shell script \"/bin/bash $(printf %q "$TMP/install.sh")\" with administrator privileges"
rm -rf "$TMP"

ver="$(sudo -n "$HELPER" version 2>/dev/null || echo 0)"
if [[ "$ver" -ge 13 ]]; then
  echo "OK: helper v$ver (Avalonia-free vpnhost)"
else
  echo "WARN: version=$ver (expected 13) — update oc-run.sh HELPER_VERSION" >&2
  exit 1
fi

echo "Smoke: headless worker must not open UI"
sudo -n "$HOST_WRAP" /tmp/ocg-does-not-exist 2>&1 | head -3 || true
