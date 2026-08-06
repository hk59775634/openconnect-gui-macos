#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAGE="$ROOT/dist/dmg-stage-osx-arm64"
DMG="$ROOT/dist/OpenConnectGui-2.0.0-macos-arm64.dmg"
rm -f "$DMG"
hdiutil create -volname "OpenConnect Gui" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
ls -lh "$DMG"
open "$ROOT/dist"
