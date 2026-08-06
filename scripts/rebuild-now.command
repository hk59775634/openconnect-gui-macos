#!/bin/bash
cd /Users/jack/Developer/openconnect-gui-v2
export PATH="$HOME/.dotnet:$PATH"
./scripts/package-macos-dmg.sh osx-arm64
echo DONE; sleep 3
