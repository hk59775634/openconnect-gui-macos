#!/usr/bin/env bash
# Build a macOS Installer .pkg (AnyConnect-style) + DMG that contains the PKG.
# Installer prompts for admin password and installs:
#   /Applications/OpenConnect Gui.app
#   /Library/OpenConnectGui/{oc-run,ocg-vpnhost,vpnhost/,lib/,...}
#
# Usage:
#   ./scripts/package-macos-pkg.sh              # osx-arm64
#   ./scripts/package-macos-pkg.sh osx-x64
#
# Optional signing / notarization (production distribution):
#   export OCG_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
#   export OCG_INSTALLER_IDENTITY="Developer ID Installer: Your Name (TEAMID)"
#   export OCG_NOTARIZE=1
#   export APPLE_ID="you@example.com"
#   export APPLE_TEAM_ID="TEAMID"
#   export APPLE_APP_SPECIFIC_PASSWORD="xxxx-xxxx-xxxx-xxxx"
#
# Without Developer ID, an unsigned/ad-hoc package is still produced for local testing.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

RID="${1:-osx-arm64}"
VERSION="${OCG_VERSION:-2.2.0}"
APP_NAME="OpenConnect Gui"
BUNDLE_ID="com.openconnectgui.client"
PKG_ID="com.openconnectgui.client"
ARCH_SUFFIX="${RID#osx-}"

DIST="$ROOT/dist"
APP_STAGE="$DIST/dmg-stage-$RID/${APP_NAME}.app"
VPNHHOST_OUT="$DIST/vpnhost-$RID"
PKG_ROOT="$DIST/pkg-root-$RID"
PKG_SCRIPTS="$DIST/pkg-scripts-$RID"
PKG_RES="$DIST/pkg-resources-$RID"
COMPONENT_PKG="$DIST/OpenConnectGui-component-${VERSION}-${ARCH_SUFFIX}.pkg"
PRODUCT_PKG="$DIST/OpenConnectGui-${VERSION}-macos-${ARCH_SUFFIX}.pkg"
DMG_OUT="$DIST/OpenConnectGui-${VERSION}-macos-${ARCH_SUFFIX}-Installer.dmg"

SIGN_APP="${OCG_SIGN_IDENTITY:-}"
SIGN_PKG="${OCG_INSTALLER_IDENTITY:-}"

echo "==> 0/6 Ensure .app + vpnhost for $RID (via package-macos-dmg.sh)"
export OCG_VERSION="$VERSION"
"$ROOT/scripts/package-macos-dmg.sh" "$RID"
[[ -d "$APP_STAGE" ]] || { echo "missing $APP_STAGE" >&2; exit 1; }
[[ -d "$VPNHHOST_OUT" ]] || { echo "missing $VPNHHOST_OUT" >&2; exit 1; }

echo "==> 1/6 Stage package payload (Applications + Library)"
rm -rf "$PKG_ROOT" "$PKG_SCRIPTS" "$PKG_RES"
mkdir -p "$PKG_ROOT/Applications" \
         "$PKG_ROOT/Library/OpenConnectGui/lib" \
         "$PKG_ROOT/Library/OpenConnectGui/vpnhost" \
         "$PKG_SCRIPTS" \
         "$PKG_RES"

# App
ditto "$APP_STAGE" "$PKG_ROOT/Applications/${APP_NAME}.app"

# Helper scripts + stock vpnc
NATIVE="$ROOT/SslVpnClient.Mac/Native"
cp "$NATIVE/oc-run.sh" "$PKG_ROOT/Library/OpenConnectGui/oc-run"
cp "$NATIVE/ocg-vpnc-script.sh" "$PKG_ROOT/Library/OpenConnectGui/ocg-vpnc-script"
if [[ -f "$NATIVE/vpnc-script" ]]; then
  cp "$NATIVE/vpnc-script" "$PKG_ROOT/Library/OpenConnectGui/vpnc-script"
elif [[ -f "$APP_STAGE/Contents/MacOS/Native/vpnc-script" ]]; then
  cp "$APP_STAGE/Contents/MacOS/Native/vpnc-script" "$PKG_ROOT/Library/OpenConnectGui/vpnc-script"
else
  echo "missing vpnc-script" >&2
  exit 1
fi

# dylibs (RID-matched)
if [[ -d "$NATIVE/lib/$RID" ]]; then
  find "$NATIVE/lib/$RID" -maxdepth 1 -type f -name '*.dylib' \
    -exec cp -f {} "$PKG_ROOT/Library/OpenConnectGui/lib/" \;
fi
# also flat Native leftovers (same arch after package-macos-dmg flatten)
find "$NATIVE" -maxdepth 1 -type f -name '*.dylib' \
  -exec cp -f {} "$PKG_ROOT/Library/OpenConnectGui/lib/" \; 2>/dev/null || true

# vpnhost publish tree
rsync -a --delete \
  --exclude '*.pdb' --exclude '*.dbg' \
  "$VPNHHOST_OUT/" "$PKG_ROOT/Library/OpenConnectGui/vpnhost/"

# Thin wrapper at classic path
cat > "$PKG_ROOT/Library/OpenConnectGui/ocg-vpnhost" <<'EOF'
#!/bin/bash
set -euo pipefail
SESSION="${1:-}"
[[ -n "$SESSION" ]] || { echo "usage: ocg-vpnhost <session>" >&2; exit 2; }
LIB="/Library/OpenConnectGui/lib"
APP="/Library/OpenConnectGui/vpnhost/ocg-vpnhost"
[[ -x "$APP" ]] || { echo "missing $APP — reinstall package" >&2; exit 3; }
export DYLD_LIBRARY_PATH="$LIB${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
export DYLD_FALLBACK_LIBRARY_PATH="$LIB${DYLD_FALLBACK_LIBRARY_PATH:+:$DYLD_FALLBACK_LIBRARY_PATH}"
export OCG_VPN_WORKER=1
export OCG_VPN_SESSION="$SESSION"
exec "$APP" --vpn-worker "$SESSION"
EOF

chmod 755 \
  "$PKG_ROOT/Library/OpenConnectGui/oc-run" \
  "$PKG_ROOT/Library/OpenConnectGui/ocg-vpnc-script" \
  "$PKG_ROOT/Library/OpenConnectGui/vpnc-script" \
  "$PKG_ROOT/Library/OpenConnectGui/ocg-vpnhost" \
  "$PKG_ROOT/Library/OpenConnectGui/vpnhost/ocg-vpnhost"
chmod 755 "$PKG_ROOT/Library/OpenConnectGui/lib"/*.dylib 2>/dev/null || true

# Strip AppleDouble / Finder junk so Installer doesn't ship ._* files
export COPYFILE_DISABLE=1
find "$PKG_ROOT" \( -name '._*' -o -name '.DS_Store' \) -delete 2>/dev/null || true

echo "==> 2/6 Codesign app + helper binaries"
sign_one() {
  local target="$1"
  if [[ -n "$SIGN_APP" ]]; then
    codesign --force --options runtime --timestamp --sign "$SIGN_APP" "$target"
  else
    codesign --force --sign - "$target" 2>/dev/null || true
  fi
}

# Deep-sign .app
if [[ -n "$SIGN_APP" ]]; then
  codesign --force --deep --options runtime --timestamp --sign "$SIGN_APP" \
    "$PKG_ROOT/Applications/${APP_NAME}.app"
else
  codesign --force --deep --sign - "$PKG_ROOT/Applications/${APP_NAME}.app" 2>/dev/null || true
  echo "WARN: no OCG_SIGN_IDENTITY — ad-hoc signed .app (Gatekeeper will warn on other Macs)"
fi

# Helper dylibs + binaries
while IFS= read -r -d '' f; do
  sign_one "$f"
done < <(find "$PKG_ROOT/Library/OpenConnectGui/lib" -type f -name '*.dylib' -print0 2>/dev/null)
sign_one "$PKG_ROOT/Library/OpenConnectGui/vpnhost/ocg-vpnhost"
# scripts are shell — no codesign required

echo "==> 3/6 Build component + product PKG"
cp "$ROOT/scripts/pkg/preinstall" "$PKG_SCRIPTS/preinstall"
cp "$ROOT/scripts/pkg/postinstall" "$PKG_SCRIPTS/postinstall"
chmod 755 "$PKG_SCRIPTS/preinstall" "$PKG_SCRIPTS/postinstall"

COMPONENT_SHORT="$DIST/OpenConnectGui-component.pkg"
rm -f "$COMPONENT_SHORT" "$COMPONENT_PKG" "$PRODUCT_PKG"

pkgbuild \
  --root "$PKG_ROOT" \
  --scripts "$PKG_SCRIPTS" \
  --component-plist "$ROOT/scripts/pkg/component.plist" \
  --identifier "$PKG_ID" \
  --version "$VERSION" \
  --install-location "/" \
  --ownership recommended \
  "$COMPONENT_SHORT"

# pkgbuild still may emit <relocate>; strip it so Installer NEVER redirects the
# .app into a leftover build-tree copy with the same bundle id.
EXPAND_FIX="$DIST/pkg-expand-fix-$RID"
rm -rf "$EXPAND_FIX"
pkgutil --expand "$COMPONENT_SHORT" "$EXPAND_FIX"
python3 - "$EXPAND_FIX/PackageInfo" <<'PY'
import pathlib, re, sys
p = pathlib.Path(sys.argv[1])
text = p.read_text(encoding="utf-8")
text2 = re.sub(r"<relocate\b[^>]*>.*?</relocate>\s*", "", text, flags=re.S)
text2 = re.sub(r"<relocate\s*/>\s*", "", text2)
text2 = re.sub(r'relocatable="true"', 'relocatable="false"', text2)
p.write_text(text2, encoding="utf-8")
print("PackageInfo relocate stripped")
PY
pkgutil --flatten "$EXPAND_FIX" "$COMPONENT_SHORT"
rm -rf "$EXPAND_FIX"

# Distribution UI resources
sed "s/__VERSION__/${VERSION}/g" "$ROOT/scripts/pkg/distribution.xml.in" > "$PKG_RES/distribution.xml"
sed "s/__VERSION__/${VERSION}/g" "$ROOT/scripts/pkg/welcome.html.in" > "$PKG_RES/welcome.html"
sed "s/__VERSION__/${VERSION}/g" "$ROOT/scripts/pkg/conclusion.html.in" > "$PKG_RES/conclusion.html"

productbuild \
  --distribution "$PKG_RES/distribution.xml" \
  --resources "$PKG_RES" \
  --package-path "$DIST" \
  "$PRODUCT_PKG"

cp -f "$COMPONENT_SHORT" "$COMPONENT_PKG"

if [[ -n "$SIGN_PKG" ]]; then
  echo "==> productsign with Developer ID Installer"
  productsign --sign "$SIGN_PKG" "$PRODUCT_PKG" "$PRODUCT_PKG.signed"
  mv -f "$PRODUCT_PKG.signed" "$PRODUCT_PKG"
else
  echo "WARN: no OCG_INSTALLER_IDENTITY — PKG unsigned (Installer still asks admin password)"
fi

echo "==> 4/6 Optional notarization"
if [[ "${OCG_NOTARIZE:-0}" == "1" ]]; then
  : "${APPLE_ID:?set APPLE_ID}"
  : "${APPLE_TEAM_ID:?set APPLE_TEAM_ID}"
  : "${APPLE_APP_SPECIFIC_PASSWORD:?set APPLE_APP_SPECIFIC_PASSWORD}"
  xcrun notarytool submit "$PRODUCT_PKG" \
    --apple-id "$APPLE_ID" \
    --team-id "$APPLE_TEAM_ID" \
    --password "$APPLE_APP_SPECIFIC_PASSWORD" \
    --wait
  xcrun stapler staple "$PRODUCT_PKG"
  echo "Notarized + stapled: $PRODUCT_PKG"
fi

echo "==> 5/6 Wrap PKG in Installer DMG"
DMG_STAGE="$DIST/pkg-dmg-stage-$RID"
rm -rf "$DMG_STAGE"
mkdir -p "$DMG_STAGE"
cp "$PRODUCT_PKG" "$DMG_STAGE/"
cat > "$DMG_STAGE/安装说明.txt" <<EOF
OpenConnect Gui ${VERSION}（macOS 安装包）

1. 双击「OpenConnectGui-${VERSION}-macos-${ARCH_SUFFIX}.pkg」
2. 按提示输入 Mac 管理员密码完成安装
3. 在「应用程序」中打开 OpenConnect Gui

本安装会同时安装 VPN 权限助手，连接时一般无需再次输入管理员密码。
EOF

VOLNAME="OpenConnect Gui Installer"
TMP_DMG="$DIST/OpenConnectGui-installer-rw-${ARCH_SUFFIX}.dmg"
rm -f "$TMP_DMG" "$DMG_OUT"
for v in "/Volumes/${VOLNAME}" "/Volumes/${VOLNAME} 1"; do
  [[ -d "$v" ]] && hdiutil detach "$v" -force 2>/dev/null || true
done
# size: app+helper ~200MB compressed later
hdiutil create -ov -size 400m -fs HFS+ -volname "$VOLNAME" "$TMP_DMG"
hdiutil attach -readwrite -noverify -noautoopen "$TMP_DMG" >/dev/null
MOUNT="/Volumes/${VOLNAME}"
for _ in $(seq 1 30); do [[ -d "$MOUNT" ]] && break; sleep 0.2; done
ditto "$DMG_STAGE/" "$MOUNT/"
sync
hdiutil detach "$MOUNT" -force
hdiutil convert "$TMP_DMG" -format UDZO -imagekey zlib-level=9 -o "$DMG_OUT"
rm -f "$TMP_DMG"

echo "==> 6/6 Done"
ls -lh "$PRODUCT_PKG" "$DMG_OUT" "$COMPONENT_PKG"
echo
echo "Install (will prompt for password): open \"$PRODUCT_PKG\""
echo "Or open DMG: open \"$DMG_OUT\""
if [[ -z "$SIGN_PKG" || -z "$SIGN_APP" ]]; then
  cat <<'TIP'

── 对外正式分发还需 ──
本机目前只有 Apple Development 证书。请在 Apple Developer 后台创建并安装：
  1) Developer ID Application  —— 签名 .app / dylib / vpnhost
  2) Developer ID Installer    —— 签名 .pkg
然后：
  export OCG_SIGN_IDENTITY="Developer ID Application: 你的名字 (TEAMID)"
  export OCG_INSTALLER_IDENTITY="Developer ID Installer: 你的名字 (TEAMID)"
公证（推荐）：
  export OCG_NOTARIZE=1
  export APPLE_ID="苹果账号邮箱"
  export APPLE_TEAM_ID="TEAMID"
  export APPLE_APP_SPECIFIC_PASSWORD="app专用密码"
  ./scripts/package-macos-pkg.sh

在 Xcode → Settings → Accounts 可查看 Team ID；钥匙串「我的证书」应出现 Developer ID 两项。
TIP
fi
