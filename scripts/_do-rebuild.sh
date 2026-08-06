#!/bin/bash
set -euo pipefail
cd /Users/jack/Developer/openconnect-gui-v2
export PATH="$HOME/.dotnet:$PATH"
chmod +x scripts/package-macos-dmg.sh scripts/rebuild-macos-app.sh
./scripts/package-macos-dmg.sh osx-arm64
echo DONE > /tmp/ocg-rebuild-done
ls -lh dist/dmg-stage-osx-arm64/OpenConnect\ Gui.app/Contents/MacOS/libSkiaSharp.dylib
ls -lh dist/*.zip dist/*.dmg 2>/dev/null || true
