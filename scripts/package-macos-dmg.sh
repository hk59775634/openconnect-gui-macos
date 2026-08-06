#!/usr/bin/env bash
# Publish self-contained macOS .app (NOT single-file — Avalonia needs libSkiaSharp.dylib)
# and pack into DMG + zip.
# Usage:
#   ./scripts/package-macos-dmg.sh           # osx-arm64
#   ./scripts/package-macos-dmg.sh osx-x64
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

RID="${1:-osx-arm64}"
VERSION="${OCG_VERSION:-2.1.0}"
APP_NAME="OpenConnect Gui"
BUNDLE_ID="com.openconnectgui.client"
PUBLISH="$ROOT/dist/$RID"
VPNHHOST_OUT="$ROOT/dist/vpnhost-$RID"
STAGE="$ROOT/dist/dmg-stage-$RID"
APP_DIR="$STAGE/${APP_NAME}.app"
DMG_OUT="$ROOT/dist/OpenConnectGui-${VERSION}-macos-${RID#osx-}.dmg"
ZIP_OUT="${DMG_OUT%.dmg}.zip"
ICNS_SRC="$ROOT/SslVpnClient.Mac/Assets/AppIcon.icns"

echo "==> 0/5 Vendor libopenconnect for $RID"
"$ROOT/scripts/vendor-macos-native.sh" "$RID"

echo "==> 1/5 Publish Avalonia-free VpnHost ($RID)"
rm -rf "$VPNHHOST_OUT"
dotnet publish "$ROOT/SslVpnClient.Mac.VpnHost/SslVpnClient.Mac.VpnHost.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:NuGetAudit=false \
  -o "$VPNHHOST_OUT"
[[ -x "$VPNHHOST_OUT/ocg-vpnhost" || -f "$VPNHHOST_OUT/ocg-vpnhost" ]] || {
  echo "missing $VPNHHOST_OUT/ocg-vpnhost" >&2
  exit 1
}
chmod +x "$VPNHHOST_OUT/ocg-vpnhost"

echo "==> 2/5 Publish self-contained GUI $RID (multi-file, includes native dylibs)"
rm -rf "$PUBLISH"
dotnet publish "$ROOT/SslVpnClient.Mac/SslVpnClient.Mac.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:NuGetAudit=false \
  -o "$PUBLISH"

BIN="$PUBLISH/OpenConnectGui"
[[ -f "$BIN" ]] || { echo "missing $BIN" >&2; exit 1; }
chmod +x "$BIN"
[[ -f "$PUBLISH/libSkiaSharp.dylib" ]] || {
  echo "ERROR: libSkiaSharp.dylib missing — UI will not start" >&2
  ls -la "$PUBLISH" | head -40
  exit 1
}

echo "==> 3/5 Build ${APP_NAME}.app"
rm -rf "$STAGE"
mkdir -p "$APP_DIR/Contents/MacOS" \
         "$APP_DIR/Contents/Resources/Native"

# Copy entire publish output into MacOS (runtime + Avalonia native libs)
# Exclude huge PDB if any
rsync -a --delete \
  --exclude '*.pdb' \
  --exclude '*.dbg' \
  "$PUBLISH/" "$APP_DIR/Contents/MacOS/"

chmod +x "$APP_DIR/Contents/MacOS/OpenConnectGui"

# Bundle headless vpnhost next to GUI binary (helper install copies from here)
rsync -a --delete \
  --exclude '*.pdb' \
  --exclude '*.dbg' \
  "$VPNHHOST_OUT/" "$APP_DIR/Contents/MacOS/vpnhost/"
chmod +x "$APP_DIR/Contents/MacOS/vpnhost/ocg-vpnhost"

# Drop other-RID native trees (csproj copies whole Native/lib/**)
OTHER_RID="osx-x64"
[[ "$RID" == "osx-x64" ]] && OTHER_RID="osx-arm64"
rm -rf "$APP_DIR/Contents/MacOS/Native/lib/$OTHER_RID"
# Flat Native/*.dylib must match this RID (dev machine may have host-arch leftovers)
if [[ -d "$ROOT/SslVpnClient.Mac/Native/lib/$RID" ]]; then
  find "$APP_DIR/Contents/MacOS/Native" -maxdepth 1 -type f -name '*.dylib' -delete
  find "$ROOT/SslVpnClient.Mac/Native/lib/$RID" -maxdepth 1 -type f -name '*.dylib' \
    -exec cp -f {} "$APP_DIR/Contents/MacOS/Native/" \;
fi

# Native helper scripts
for f in oc-run.sh ocg-vpnc-script.sh; do
  src="$ROOT/SslVpnClient.Mac/Native/$f"
  [[ -f "$src" ]] || src="$PUBLISH/Native/$f"
  [[ -f "$src" ]] || { echo "missing $f" >&2; exit 1; }
  mkdir -p "$APP_DIR/Contents/MacOS/Native" "$APP_DIR/Contents/Resources/Native"
  cp "$src" "$APP_DIR/Contents/MacOS/Native/$f"
  cp "$src" "$APP_DIR/Contents/Resources/Native/$f"
  chmod 755 "$APP_DIR/Contents/MacOS/Native/$f" "$APP_DIR/Contents/Resources/Native/$f"
done
# stock vpnc-script (for helper install)
if [[ -f "$ROOT/SslVpnClient.Mac/Native/vpnc-script" ]]; then
  cp "$ROOT/SslVpnClient.Mac/Native/vpnc-script" "$APP_DIR/Contents/MacOS/Native/vpnc-script"
  chmod 755 "$APP_DIR/Contents/MacOS/Native/vpnc-script"
fi

# App icon
if [[ -f "$ICNS_SRC" ]]; then
  cp "$ICNS_SRC" "$APP_DIR/Contents/Resources/AppIcon.icns"
fi

cat > "$APP_DIR/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>zh_CN</string>
  <key>CFBundleExecutable</key>
  <string>OpenConnectGui</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundleIdentifier</key>
  <string>${BUNDLE_ID}</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>${APP_NAME}</string>
  <key>CFBundleDisplayName</key>
  <string>${APP_NAME}</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${VERSION}</string>
  <key>CFBundleVersion</key>
  <string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key>
  <true/>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
</dict>
</plist>
EOF

# Ad-hoc sign
if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$APP_DIR" 2>/dev/null || true
fi
xattr -cr "$APP_DIR" 2>/dev/null || true

# Smoke-test: process must stay alive >1s (Skia load)
# Skip when cross-arch (e.g. arm64 host packaging osx-x64) — Rosetta may be absent.
echo "==> Smoke-test launch…"
HOST_ARCH="$(uname -m)"
NEED_ROSETTA=0
if [[ "$RID" == "osx-x64" && "$HOST_ARCH" == "arm64" ]]; then NEED_ROSETTA=1; fi
if [[ "$RID" == "osx-arm64" && "$HOST_ARCH" == "x86_64" ]]; then NEED_ROSETTA=1; fi
if [[ "$NEED_ROSETTA" -eq 1 ]] && ! arch -x86_64 /usr/bin/true 2>/dev/null; then
  echo "SKIP: cannot run $RID binary on $HOST_ARCH (no Rosetta)"
else
  "$APP_DIR/Contents/MacOS/OpenConnectGui" >/tmp/ocg-smoke.out 2>/tmp/ocg-smoke.err &
  SPID=$!
  sleep 2
  if kill -0 "$SPID" 2>/dev/null; then
    echo "OK: process running pid=$SPID"
    kill "$SPID" 2>/dev/null || true
    wait "$SPID" 2>/dev/null || true
  else
    echo "FAIL: app exited immediately:" >&2
    cat /tmp/ocg-smoke.err >&2 || true
    exit 1
  fi
fi

ln -s /Applications "$STAGE/Applications"
cat > "$STAGE/安装说明.txt" <<EOF
OpenConnect Gui ${VERSION}（macOS）

安装：
1. 将「${APP_NAME}」拖到 Applications（应用程序）
2. 若提示无法打开：右键 → 打开，或：
     xattr -cr "/Applications/${APP_NAME}.app"
3. 已内置 libopenconnect，目标机无需 brew install openconnect
4. 首次连接会提示输入一次 Mac 密码安装权限助手

说明：配置目录 ~/Library/Application Support/OpenConnectGui/
EOF

echo "==> 4/5 Create zip + DMG"
rm -f "$ZIP_OUT" "$DMG_OUT"
ditto -c -k --sequesterRsrc --keepParent "$APP_DIR" "$ZIP_OUT"
echo "ZIP: $ZIP_OUT ($(du -h "$ZIP_OUT" | awk '{print $1}'))"

# Write MAKE-DMG helper next to artifacts (RID-specific)
ARCH_SUFFIX="${RID#osx-}"
MAKE_DMG="$(dirname "$DMG_OUT")/MAKE-DMG-${ARCH_SUFFIX}.sh"
cat > "$MAKE_DMG" <<MAKE
#!/bin/bash
set -euo pipefail
cd "\$(dirname "\$0")"
STAGE="dmg-stage-${RID}"
APP="OpenConnect Gui.app"
DMG="OpenConnectGui-${VERSION}-macos-${ARCH_SUFFIX}.dmg"
TMP_DMG="OpenConnectGui-rw-temp-${ARCH_SUFFIX}.dmg"
VOLNAME="OpenConnect Gui"
[[ -d "\$STAGE/\$APP" ]] || { echo "missing \$STAGE/\$APP" >&2; exit 1; }
for v in "/Volumes/\${VOLNAME}" "/Volumes/\${VOLNAME} 1"; do
  [[ -d "\$v" ]] && hdiutil detach "\$v" -force 2>/dev/null || true
done
rm -f "\$TMP_DMG" "\$DMG"
hdiutil create -ov -size 300m -fs HFS+ -volname "\$VOLNAME" "\$TMP_DMG"
hdiutil attach -readwrite -noverify -noautoopen "\$TMP_DMG" >/dev/null
MOUNT="/Volumes/\${VOLNAME}"
for _ in \$(seq 1 20); do [[ -d "\$MOUNT" ]] && break; sleep 0.2; done
ditto "\$STAGE/\$APP" "\$MOUNT/\$APP"
ln -sf /Applications "\$MOUNT/Applications"
[[ -f "\$STAGE/安装说明.txt" ]] && cp "\$STAGE/安装说明.txt" "\$MOUNT/安装说明.txt" || true
sync
hdiutil detach "\$MOUNT" -force
hdiutil convert "\$TMP_DMG" -format UDZO -imagekey zlib-level=9 -o "\$DMG"
rm -f "\$TMP_DMG"
ls -lh "\$DMG"
MAKE
chmod +x "$MAKE_DMG"

# Try DMG; may fail in restricted environments
bash "$MAKE_DMG" && echo "DMG ready" || echo "WARN: run $MAKE_DMG manually"

echo "==> 5/5 Done"
ls -lh "$ZIP_OUT" "$DMG_OUT" 2>/dev/null || ls -lh "$ZIP_OUT"
echo "App: $APP_DIR"
echo "Open: open \"$(dirname "$DMG_OUT")\""
