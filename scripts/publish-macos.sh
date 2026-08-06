#!/usr/bin/env bash
# Publish self-contained macOS app (no .NET install required on target Mac).
# Do NOT use PublishSingleFile — Avalonia needs libSkiaSharp.dylib beside the binary.
# Prefer: ./scripts/package-macos-dmg.sh  (builds .app + zip/dmg)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

RID="${1:-osx-arm64}"
OUT="$ROOT/dist/$RID"

echo "==> Publishing self-contained $RID → $OUT"
dotnet publish "$ROOT/SslVpnClient.Mac/SslVpnClient.Mac.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:NuGetAudit=false \
  -o "$OUT"

[[ -f "$OUT/libSkiaSharp.dylib" ]] || {
  echo "ERROR: libSkiaSharp.dylib missing" >&2
  exit 1
}

echo "==> Done: $OUT/OpenConnectGui (+ native dylibs)"
echo "For .app / DMG: ./scripts/package-macos-dmg.sh $RID"
echo "Prerequisite: brew install openconnect"
