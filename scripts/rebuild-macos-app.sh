#!/bin/bash
# Rebuild .app (fix Skia), add icon, make zip/dmg
set -euo pipefail
cd /Users/jack/Developer/openconnect-gui-v2
export PATH="$HOME/.dotnet:$PATH"

# Icon
ICONSET=SslVpnClient.Mac/Assets/AppIcon.iconset
ICNS=SslVpnClient.Mac/Assets/AppIcon.icns
if [[ ! -f "$ICNS" ]]; then
  iconutil -c icns "$ICONSET" -o "$ICNS" || \
    sips -s format icns SslVpnClient.Mac/Assets/app-icon.png --out "$ICNS" || true
fi
# Ensure app-icon.png exists
[[ -f SslVpnClient.Mac/Assets/app-icon.png ]] || \
  sips -z 1024 1024 /tmp/ocg-icon-1024.png --out SslVpnClient.Mac/Assets/app-icon.png 2>/dev/null || true

./scripts/package-macos-dmg.sh osx-arm64
echo
echo "测试打开 App："
open "dist/dmg-stage-osx-arm64/OpenConnect Gui.app"
echo "完成"
